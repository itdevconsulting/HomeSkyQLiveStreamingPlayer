using System.Net;
using System.Net.Sockets;
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
    private readonly LocalSetupStore _setupStore;
    private SkyQScanResponse _cachedScan;
    private DateTimeOffset? _lastScanAt;
    private static readonly TimeSpan ScanCacheDuration = TimeSpan.FromHours(1);

    public SkyQService(
        IHttpClientFactory httpClientFactory,
        IHostEnvironment environment,
        LocalSetupStore setupStore,
        ILogger<SkyQService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _setupStore = setupStore;
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
        var extraScanNetworks = _setupStore.Get().ExtraScanNetworks;
        var interfaceScan = PrivateIpv4.GetInterfaces(_logger);
        var extras = PrivateIpv4.ExtraScanTargets(extraScanNetworks);
        var extraOnly = extras
            .Where(extra => interfaceScan.Interfaces.All(local =>
                !string.Equals(local.Cidr, extra.Cidr, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var skippedNetworks = interfaceScan.Messages.ToList();
        var networks = interfaceScan.Interfaces.Select(item => item.Cidr)
            .Concat(extras.Select(item => item.Cidr))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (interfaceScan.Interfaces.Count == 0 && extras.Count == 0)
        {
            if (skippedNetworks.Count == 0)
            {
                skippedNetworks.Add("No connected private IPv4 interfaces were detected on this machine.");
            }

            return new SkyQScanResponse(networks, skippedNetworks, [], null);
        }

        var candidates = new HashSet<IPAddress>();
        if (interfaceScan.Interfaces.Count > 0)
        {
            foreach (var address in await DiscoverCandidatesAsync(interfaceScan.Interfaces, cancellationToken))
            {
                candidates.Add(address);
            }
        }
        else
        {
            skippedNetworks.Add("No local private NIC; scanning additional networks only.");
        }

        if (extraOnly.Count > 0)
        {
            var extraHosts = extraOnly.SelectMany(PrivateIpv4.EnumerateHosts);
            foreach (var address in await PrivateIpv4.ProbeOpenTcpAsync(
                         extraHosts,
                         JsonPort,
                         TimeSpan.FromMilliseconds(250),
                         32,
                         cancellationToken))
            {
                candidates.Add(address);
            }
        }

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

        if (candidates.Count == 0)
        {
            skippedNetworks.Add(extras.Count == 0
                ? "No SSDP responders found on connected private interfaces."
                : "No Sky Q JSON API found on connected or additional networks.");
        }

        return new SkyQScanResponse(networks, skippedNetworks, devices, null);
    }

    public async Task<SkyQCommandResult> SendCommandAsync(string host, string command, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(host, out var address) ||
            !PrivateIpv4.IsAllowedTarget(address, _setupStore.Get().ExtraScanNetworks))
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
        IReadOnlyList<PrivateIpv4.PrivateInterface> interfaces,
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

                if (PrivateIpv4.IsPrivateLike(response.RemoteEndPoint.Address))
                {
                    results.Add(response.RemoteEndPoint.Address);
                }

                var location = TryGetLocationHost(Encoding.UTF8.GetString(response.Buffer));
                if (location is not null && PrivateIpv4.IsPrivateLike(location))
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

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool GetBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;

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
