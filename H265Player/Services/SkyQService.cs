using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using H265Player.Models;

namespace H265Player.Services;

public sealed class SkyQService : IDisposable
{
    private static readonly IReadOnlyDictionary<string, int> Commands = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["power"] = 0,
        ["select"] = 1,
        ["backup"] = 2,
        ["dismiss"] = 2,
        ["channelup"] = 6,
        ["channeldown"] = 7,
        ["interactive"] = 8,
        ["sidebar"] = 8,
        ["help"] = 9,
        ["services"] = 10,
        ["search"] = 10,
        ["tvguide"] = 11,
        ["home"] = 11,
        ["i"] = 14,
        ["text"] = 15,
        ["up"] = 16,
        ["down"] = 17,
        ["left"] = 18,
        ["right"] = 19,
        ["red"] = 32,
        ["green"] = 33,
        ["yellow"] = 34,
        ["blue"] = 35,
        ["0"] = 48,
        ["1"] = 49,
        ["2"] = 50,
        ["3"] = 51,
        ["4"] = 52,
        ["5"] = 53,
        ["6"] = 54,
        ["7"] = 55,
        ["8"] = 56,
        ["9"] = 57,
        ["play"] = 64,
        ["pause"] = 65,
        ["stop"] = 66,
        ["record"] = 67,
        ["fastforward"] = 69,
        ["rewind"] = 71,
        ["boxoffice"] = 240,
        ["sky"] = 241
    };

    private const int JsonPort = 9006;
    private const int RemotePort = 49160;
    private static readonly string[] SsdpSearchTargets =
    [
        "ssdp:all",
        "urn:schemas-upnp-org:device:MediaServer:1",
        "urn:schemas-nds-com:service:SkyPlay:2",
        "urn:schemas-nds-com:device:SkyServer:1"
    ];

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly string _cachePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SkyQService> _logger;
    private SkyQScanResponse _cachedScan;
    private DateTimeOffset? _lastScanAt;
    private static readonly TimeSpan ScanCacheDuration = TimeSpan.FromHours(1);

    public SkyQService(
        IHttpClientFactory httpClientFactory,
        IHostEnvironment environment,
        ILogger<SkyQService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cachePath = AppPaths.File("skyq-cache.json");
        _cachedScan = LoadCachedScan();
        _lastScanAt = _cachedScan.LastScanAt;
    }

    public async Task<SkyQScanResponse> GetScanAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _cachedScan.Devices.Count > 0)
        {
            return _cachedScan;
        }

        if (!forceRefresh && _lastScanAt is not null && DateTimeOffset.UtcNow - _lastScanAt < ScanCacheDuration)
        {
            return _cachedScan;
        }

        return await RefreshCacheAsync(cancellationToken);
    }

    public async Task<SkyQScanResponse> RefreshCacheAsync(CancellationToken cancellationToken)
    {
        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            var scan = await ScanInternalAsync(cancellationToken);
            _cachedScan = scan with { LastScanAt = DateTimeOffset.UtcNow };
            _lastScanAt = _cachedScan.LastScanAt;
            await SaveCachedScanAsync(_cachedScan, cancellationToken);
            return _cachedScan;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    public async Task<SkyQScanResponse> ForceRefreshAsync(CancellationToken cancellationToken)
    {
        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            var scan = await ScanInternalAsync(cancellationToken);
            _cachedScan = scan with { LastScanAt = DateTimeOffset.UtcNow };
            _lastScanAt = _cachedScan.LastScanAt;
            await SaveCachedScanAsync(_cachedScan, cancellationToken);
            return _cachedScan;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task<SkyQScanResponse> ScanInternalAsync(CancellationToken cancellationToken)
    {
        var interfaceScan = GetPrivateInterfaces();
        var interfaces = interfaceScan.Interfaces;
        var skippedNetworks = interfaceScan.Messages.ToList();
        var networks = interfaces.Select(item => item.Cidr).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

        if (interfaces.Count == 0)
        {
            if (skippedNetworks.Count == 0)
            {
                skippedNetworks.Add("No connected private IPv4 interfaces were detected on this machine.");
            }

            return new SkyQScanResponse(networks, skippedNetworks, [], null);
        }

        var candidates = await DiscoverCandidatesAsync(interfaces, cancellationToken);

        var semaphore = new SemaphoreSlim(16);
        var tasks = candidates.Select(async address =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await ProbeAsync(address, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var devices = (await Task.WhenAll(tasks))
            .Where(device => device is not null)
            .Cast<SkyQDevice>()
            .OrderBy(device => ToSortKey(device.Host))
            .ToList();

        return new SkyQScanResponse(
            networks,
            candidates.Count == 0
                ? [.. skippedNetworks, "No SSDP responders found on connected private interfaces."]
                : skippedNetworks,
            devices,
            null);
    }

    public async Task<SkyQCommandResult> SendCommandAsync(string host, string command, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(host, out var address) || !IsPrivateAddress(address))
        {
            return new SkyQCommandResult(false, host, command, "Sky Q control is limited to private IPv4 addresses.", []);
        }

        if (!Commands.TryGetValue(command, out var code))
        {
            return new SkyQCommandResult(false, host, command, $"Unknown Sky Q command '{command}'.", []);
        }

        var logs = new List<string> { $"Target={host}:{RemotePort}", $"Command={command}", $"KeyCode={code}" };

        try
        {
            using var client = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(address, RemotePort, connectCts.Token);
            logs.Add("TCP connect succeeded.");

            using var stream = client.GetStream();
            stream.ReadTimeout = 2000;
            stream.WriteTimeout = 2000;
            logs.Add("Network stream opened.");

            var commandBytes = new byte[]
            {
                4, 1, 0, 0, 0, 0,
                (byte)Math.Floor(224 + (code / 16.0)),
                (byte)(code % 16)
            };

            var buffer = new byte[1024];
            var timeoutAt = DateTime.UtcNow.AddSeconds(2);
            var sent = false;
            var echoLength = 12;

            while (DateTime.UtcNow < timeoutAt)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readCts.CancelAfter(TimeSpan.FromMilliseconds(500));
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token);
                if (read <= 0)
                {
                    logs.Add("Remote closed the socket before handshake completed.");
                    break;
                }

                logs.Add($"Read {read} bytes from Sky Q.");

                if (read < 24)
                {
                    var bytesToEcho = Math.Min(echoLength, read);
                    logs.Add($"Echoing {bytesToEcho} bytes as part of handshake.");
                    await stream.WriteAsync(buffer.AsMemory(0, bytesToEcho), cancellationToken);
                    echoLength = 1;
                    continue;
                }

                logs.Add("Handshake threshold reached, sending key press.");
                await stream.WriteAsync(commandBytes, cancellationToken);
                commandBytes[1] = 0;
                logs.Add("Sending key release.");
                await stream.WriteAsync(commandBytes, cancellationToken);
                sent = true;
                break;
            }

            return sent
                ? new SkyQCommandResult(true, host, command, "Command sent.", logs)
                : new SkyQCommandResult(false, host, command, "Sky Q did not complete the remote socket handshake.", logs);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logs.Add("Timed out waiting for handshake data from the Sky Q box.");
            return new SkyQCommandResult(false, host, command, ex.Message, logs);
        }
        catch (Exception ex)
        {
            logs.Add($"{ex.GetType().Name}: {ex.Message}");
            return new SkyQCommandResult(false, host, command, ex.Message, logs);
        }
    }

    private async Task<SkyQDevice?> ProbeAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("skyq");
        client.Timeout = TimeSpan.FromSeconds(1.5);

        try
        {
            using var deviceInfoResponse = await client.GetAsync($"http://{address}:{JsonPort}/as/system/deviceinformation", cancellationToken);
            if (!deviceInfoResponse.IsSuccessStatusCode)
            {
                return null;
            }

            using var systemInfoResponse = await client.GetAsync($"http://{address}:{JsonPort}/as/system/information", cancellationToken);
            if (!systemInfoResponse.IsSuccessStatusCode)
            {
                return null;
            }

            using var deviceInfoJson = JsonDocument.Parse(await deviceInfoResponse.Content.ReadAsStringAsync(cancellationToken));
            using var systemInfoJson = JsonDocument.Parse(await systemInfoResponse.Content.ReadAsStringAsync(cancellationToken));

            var manufacturer = GetString(systemInfoJson.RootElement, "manufacturer");
            var model = GetString(deviceInfoJson.RootElement, "modelNumber");
            var hardware = GetString(deviceInfoJson.RootElement, "hardwareName");
            var serial = GetString(deviceInfoJson.RootElement, "serialNumber");
            var deviceType = GetString(systemInfoJson.RootElement, "deviceType");

            var isSky = string.Equals(manufacturer, "Sky", StringComparison.OrdinalIgnoreCase) ||
                        !string.IsNullOrWhiteSpace(model) ||
                        !string.IsNullOrWhiteSpace(hardware);
            if (!isSky)
            {
                return null;
            }

            return new SkyQDevice(
                address.ToString(),
                await TryResolveShortHostNameAsync(address),
                manufacturer ?? "Sky",
                model ?? hardware ?? "Sky Q",
                hardware ?? string.Empty,
                serial ?? string.Empty,
                deviceType ?? string.Empty,
                GetBool(deviceInfoJson.RootElement, "gateway") || GetBool(systemInfoJson.RootElement, "gateway"),
                GetString(systemInfoJson.RootElement, "wakeReason") ?? string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private async Task<HashSet<IPAddress>> DiscoverCandidatesAsync(
        IReadOnlyList<PrivateInterface> interfaces,
        CancellationToken cancellationToken)
    {
        var discovered = new HashSet<IPAddress>();
        var tasks = interfaces.Select(item => DiscoverViaSsdpAsync(item.Address, cancellationToken));
        var results = await Task.WhenAll(tasks);

        foreach (var group in results)
        {
            foreach (var address in group)
            {
                discovered.Add(address);
            }
        }

        return discovered;
    }

    private async Task<HashSet<IPAddress>> DiscoverViaSsdpAsync(IPAddress localAddress, CancellationToken cancellationToken)
    {
        var results = new HashSet<IPAddress>();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(localAddress, 0));
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.MulticastLoopback = false;

            var endpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            foreach (var target in SsdpSearchTargets)
            {
                var request = BuildSsdpRequest(target);
                var bytes = Encoding.ASCII.GetBytes(request);
                await udp.SendAsync(bytes, bytes.Length, endpoint);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1500));

            while (!timeoutCts.IsCancellationRequested)
            {
                UdpReceiveResult response;
                try
                {
                    response = await udp.ReceiveAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }

                if (IsPrivateAddress(response.RemoteEndPoint.Address))
                {
                    results.Add(response.RemoteEndPoint.Address);
                }

                var location = TryGetLocationHost(Encoding.UTF8.GetString(response.Buffer));
                if (location is not null && IsPrivateAddress(location))
                {
                    results.Add(location);
                }
            }
        }
        catch
        {
        }

        return results;
    }

    private static string BuildSsdpRequest(string searchTarget) =>
        $"M-SEARCH * HTTP/1.1\r\n" +
        "HOST: 239.255.255.250:1900\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 1\r\n" +
        $"ST: {searchTarget}\r\n\r\n";

    private static IPAddress? TryGetLocationHost(string responseText)
    {
        foreach (var line in responseText.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "location:";
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[prefix.Length..].Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && IPAddress.TryParse(uri.Host, out var address))
            {
                return address;
            }
        }

        return null;
    }

    private PrivateInterfaceScanResult GetPrivateInterfaces()
    {
        var result = new List<PrivateInterface>();
        var messages = new List<string>();
        NetworkInterface[] networkInterfaces;

        try
        {
            networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException ex)
        {
            _logger.LogWarning(ex, "Sky Q scan could not enumerate local network interfaces.");
            string? fallbackError = null;

            if (OperatingSystem.IsLinux() && TryGetPrivateInterfacesFromLinuxIpCommand(out var fallbackInterfaces, out fallbackError))
            {
                _logger.LogInformation("Sky Q scan is using the Linux 'ip' command fallback for interface discovery.");
                return new PrivateInterfaceScanResult(fallbackInterfaces, messages);
            }

            if (!string.IsNullOrWhiteSpace(fallbackError))
            {
                _logger.LogWarning("Linux 'ip' command fallback failed during Sky Q scan: {Message}", fallbackError);
            }

            messages.Add($"Unable to enumerate local network interfaces: {ex.Message}.");
            return new PrivateInterfaceScanResult(result, messages);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sky Q scan failed before interface enumeration completed.");
            messages.Add($"Unable to inspect local network interfaces: {ex.Message}.");
            return new PrivateInterfaceScanResult(result, messages);
        }

        foreach (var nic in networkInterfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException ex)
            {
                _logger.LogDebug(ex, "Skipping interface {InterfaceName} during Sky Q scan.", nic.Name);
                messages.Add($"Skipped interface '{nic.Name}': {ex.Message}.");
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask is null ||
                    !IsPrivateAddress(unicast.Address))
                {
                    continue;
                }

                var cidr = BuildCidr(unicast.Address, unicast.IPv4Mask);
                if (result.All(existing => !existing.Address.Equals(unicast.Address)))
                {
                    result.Add(new PrivateInterface(unicast.Address, cidr));
                }
            }
        }

        return new PrivateInterfaceScanResult(result, messages);
    }

    private static bool TryGetPrivateInterfacesFromLinuxIpCommand(
        out List<PrivateInterface> interfaces,
        out string? errorMessage)
    {
        interfaces = [];
        errorMessage = null;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ip",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add("-o");
            process.StartInfo.ArgumentList.Add("-4");
            process.StartInfo.ArgumentList.Add("addr");
            process.StartInfo.ArgumentList.Add("show");
            process.StartInfo.ArgumentList.Add("up");

            if (!process.Start())
            {
                errorMessage = "Failed to start the Linux 'ip' command.";
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                errorMessage = "Timed out while reading interface data from the Linux 'ip' command.";
                return false;
            }

            if (process.ExitCode != 0)
            {
                errorMessage = string.IsNullOrWhiteSpace(error)
                    ? $"The Linux 'ip' command exited with code {process.ExitCode}."
                    : error.Trim();
                return false;
            }

            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length < 4 || !string.Equals(tokens[2], "inet", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var cidrParts = tokens[3].Split('/', StringSplitOptions.TrimEntries);
                if (cidrParts.Length != 2 ||
                    !IPAddress.TryParse(cidrParts[0], out var address) ||
                    address.AddressFamily != AddressFamily.InterNetwork ||
                    !int.TryParse(cidrParts[1], out var prefixLength) ||
                    prefixLength < 0 ||
                    prefixLength > 32 ||
                    !IsPrivateAddress(address))
                {
                    continue;
                }

                if (interfaces.All(existing => !existing.Address.Equals(address)))
                {
                    interfaces.Add(new PrivateInterface(address, BuildCidr(address, prefixLength)));
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool GetBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;

    private static bool IsPrivateAddress(IPAddress address)
    {
        var ipv4 = address.MapToIPv4();
        var bytes = ipv4.GetAddressBytes();

        return bytes.Length == 4 && (
            bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168));
    }

    private static uint ToSortKey(string host)
    {
        if (!IPAddress.TryParse(host, out var address))
        {
            return uint.MaxValue;
        }

        var bytes = address.MapToIPv4().GetAddressBytes();
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    private static async Task<string> TryResolveShortHostNameAsync(IPAddress address)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address);
            var hostName = entry.HostName;
            if (string.IsNullOrWhiteSpace(hostName))
            {
                return string.Empty;
            }

            var firstLabel = hostName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return firstLabel ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildCidr(IPAddress address, IPAddress mask)
    {
        var bytes = address.MapToIPv4().GetAddressBytes();
        var maskBytes = mask.MapToIPv4().GetAddressBytes();
        var network = new IPAddress(new[]
        {
            (byte)(bytes[0] & maskBytes[0]),
            (byte)(bytes[1] & maskBytes[1]),
            (byte)(bytes[2] & maskBytes[2]),
            (byte)(bytes[3] & maskBytes[3])
        });

        var prefixLength = 0;
        foreach (var maskByte in maskBytes)
        {
            var value = maskByte;
            while (value != 0)
            {
                prefixLength += value & 1;
                value >>= 1;
            }
        }

        return $"{network}/{prefixLength}";
    }

    private static string BuildCidr(IPAddress address, int prefixLength)
    {
        var bytes = address.MapToIPv4().GetAddressBytes();
        var ipValue = ((uint)bytes[0] << 24) |
                      ((uint)bytes[1] << 16) |
                      ((uint)bytes[2] << 8) |
                      bytes[3];

        var maskValue = prefixLength == 0
            ? 0u
            : uint.MaxValue << (32 - prefixLength);
        var networkValue = ipValue & maskValue;
        var network = new IPAddress(
        [
            (byte)((networkValue >> 24) & 0xff),
            (byte)((networkValue >> 16) & 0xff),
            (byte)((networkValue >> 8) & 0xff),
            (byte)(networkValue & 0xff)
        ]);

        return $"{network}/{prefixLength}";
    }

    private sealed record PrivateInterface(IPAddress Address, string Cidr);
    private sealed record PrivateInterfaceScanResult(IReadOnlyList<PrivateInterface> Interfaces, IReadOnlyList<string> Messages);

    private SkyQScanResponse LoadCachedScan()
    {
        if (!File.Exists(_cachePath))
        {
            return new SkyQScanResponse([], ["No cached Sky Q scan found."], [], null);
        }

        try
        {
            var json = File.ReadAllText(_cachePath);
            var cached = JsonSerializer.Deserialize<SkyQScanResponse>(json);
            if (cached is not null)
            {
                return cached;
            }
        }
        catch
        {
        }

        return new SkyQScanResponse([], ["Sky Q cache file could not be read."], [], null);
    }

    private async Task SaveCachedScanAsync(SkyQScanResponse scan, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(scan, _jsonOptions);
        await File.WriteAllTextAsync(_cachePath, json, cancellationToken);
    }

    public void Dispose()
    {
        _scanLock.Dispose();
    }
}
