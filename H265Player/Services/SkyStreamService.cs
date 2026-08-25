using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using H265Player.Models;

namespace H265Player.Services;

public sealed class SkyStreamService : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> Commands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["power"] = "Power",
        ["home"] = "Home",
        ["tvguide"] = "AccessMenu",
        ["search"] = "Search",
        ["dismiss"] = "Dismiss",
        ["backup"] = "Backspace",
        ["up"] = "ArrowUp",
        ["down"] = "ArrowDown",
        ["left"] = "ArrowLeft",
        ["right"] = "ArrowRight",
        ["select"] = "Enter",
        ["i"] = "Info",
        ["channelup"] = "ChannelUp",
        ["channeldown"] = "ChannelDown",
        ["play"] = "MediaPlay",
        ["pause"] = "MediaPlay",
        ["stop"] = "Dismiss",
        ["record"] = "MediaRecord",
        ["fastforward"] = "MediaFastForward",
        ["rewind"] = "MediaRewind",
        ["red"] = "Red",
        ["green"] = "Green",
        ["yellow"] = "Yellow",
        ["blue"] = "Blue",
        ["0"] = "Digit0",
        ["1"] = "Digit1",
        ["2"] = "Digit2",
        ["3"] = "Digit3",
        ["4"] = "Digit4",
        ["5"] = "Digit5",
        ["6"] = "Digit6",
        ["7"] = "Digit7",
        ["8"] = "Digit8",
        ["9"] = "Digit9"
    };

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<Task<SkyStreamClient>>> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cachePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly ILogger<SkyStreamService> _logger;
    private readonly LocalSetupStore _setupStore;
    private SkyStreamScanResponse _cachedScan;
    private DateTimeOffset? _lastScanAt;
    private static readonly TimeSpan ScanCacheDuration = TimeSpan.FromHours(1);

    public SkyStreamService(IHostEnvironment environment, LocalSetupStore setupStore, ILogger<SkyStreamService> logger)
    {
        _setupStore = setupStore;
        _logger = logger;
        _cachePath = AppPaths.File("sky-stream-cache.json");
        _cachedScan = LoadCachedScan();
        _lastScanAt = _cachedScan.LastScanAt;
    }

    public async Task<SkyStreamScanResponse> GetScanAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _cachedScan.Devices.Count > 0)
        {
            return _cachedScan;
        }

        if (!forceRefresh && _lastScanAt is not null && DateTimeOffset.UtcNow - _lastScanAt < ScanCacheDuration)
        {
            return _cachedScan;
        }

        return await ForceRefreshAsync(cancellationToken);
    }

    public async Task<SkyStreamScanResponse> ForceRefreshAsync(CancellationToken cancellationToken)
    {
        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            var scan = await ScanInternalAsync(cancellationToken);
            _cachedScan = scan with { LastScanAt = DateTimeOffset.UtcNow };
            _lastScanAt = _cachedScan.LastScanAt;
            await File.WriteAllTextAsync(_cachePath, JsonSerializer.Serialize(_cachedScan, _jsonOptions), cancellationToken);
            return _cachedScan;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    public async Task<SkyQCommandResult> SendCommandAsync(string host, string command, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(host, out var address) ||
            !PrivateIpv4.IsAllowedTarget(address, _setupStore.Get().ExtraScanNetworks))
        {
            return new SkyQCommandResult(false, host, command, "Sky Stream control is limited to private IPv4 addresses.", []);
        }

        if (!Commands.TryGetValue(command, out var key))
        {
            return new SkyQCommandResult(false, host, command, $"Unknown Sky Stream command '{command}'.", []);
        }

        var logs = new List<string>
        {
            $"Target={host}:{SkyStreamCredentials.Port}",
            $"Command={command}",
            $"Key={key}"
        };

        try
        {
            var response = await SendKeyWithRetryAsync(host.Trim(), key, logs, cancellationToken);
            var ok = response.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.True;
            logs.Add(ok ? "Key accepted." : $"Box response: {response}");
            return new SkyQCommandResult(ok, host, command, ok ? "Command sent." : "Sky Stream rejected the key.", logs);
        }
        catch (Exception ex)
        {
            logs.Add($"{ex.GetType().Name}: {ex.Message}");
            return new SkyQCommandResult(false, host, command, ex.Message, logs);
        }
    }

    private async Task<System.Text.Json.JsonElement> SendKeyWithRetryAsync(
        string host,
        string key,
        List<string> logs,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = await GetClientAsync(host, logs, cancellationToken);
            return await client.SendKeyAsync(key, cancellationToken);
        }
        catch (Exception first)
        {
            logs.Add($"First attempt failed: {first.Message}");
            await DropSessionAsync(host);
            var client = await GetClientAsync(host, logs, cancellationToken);
            return await client.SendKeyAsync(key, cancellationToken);
        }
    }

    private async Task<SkyStreamClient> GetClientAsync(string host, List<string> logs, CancellationToken cancellationToken)
    {
        var lazy = _sessions.GetOrAdd(host, _ => new Lazy<Task<SkyStreamClient>>(() => OpenAsync(host, logs, cancellationToken)));
        try
        {
            return await lazy.Value;
        }
        catch
        {
            _sessions.TryRemove(host, out _);
            throw;
        }
    }

    private async Task<SkyStreamClient> OpenAsync(string host, List<string> logs, CancellationToken cancellationToken)
    {
        TryWake(host, logs);
        logs.Add("Opening mTLS WebSocket to /iptarget.");
        var client = new SkyStreamClient(host);
        await client.ConnectAndBindAsync(cancellationToken);
        logs.Add(string.IsNullOrWhiteSpace(client.DeviceName)
            ? "Paired and bound."
            : $"Paired and bound to {client.DeviceName}.");
        return client;
    }

    private void TryWake(string host, List<string> logs)
    {
        var device = _cachedScan.Devices.FirstOrDefault(item => string.Equals(item.Host, host, StringComparison.OrdinalIgnoreCase));
        if (device is null || string.IsNullOrWhiteSpace(device.MacAddress))
        {
            return;
        }

        try
        {
            SendWakeOnLan(device.MacAddress);
            logs.Add($"Sent Wake-on-LAN to {device.MacAddress}.");
        }
        catch (Exception ex)
        {
            logs.Add($"Wake-on-LAN skipped: {ex.Message}");
        }
    }

    private async Task DropSessionAsync(string host)
    {
        if (_sessions.TryRemove(host, out var lazy))
        {
            try
            {
                if (lazy.IsValueCreated)
                {
                    var client = await lazy.Value;
                    await client.DisposeAsync();
                }
            }
            catch
            {
            }
        }
    }

    private async Task<SkyStreamScanResponse> ScanInternalAsync(CancellationToken cancellationToken)
    {
        var extraScanNetworks = _setupStore.Get().ExtraScanNetworks;
        var interfaceScan = PrivateIpv4.GetInterfaces(_logger);
        var extras = PrivateIpv4.ExtraScanTargets(extraScanNetworks);
        var extraOnly = extras
            .Where(extra => interfaceScan.Interfaces.All(local =>
                !string.Equals(local.Cidr, extra.Cidr, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var skipped = interfaceScan.Messages.ToList();
        var networks = interfaceScan.Interfaces.Select(item => item.Cidr)
            .Concat(extras.Select(item => item.Cidr))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (interfaceScan.Interfaces.Count == 0 && extras.Count == 0)
        {
            if (skipped.Count == 0)
            {
                skipped.Add("No connected private IPv4 interfaces were detected on this machine.");
            }

            return new SkyStreamScanResponse(networks, skipped, [], null);
        }

        var candidates = new Dictionary<string, SkyStreamDevice>(StringComparer.OrdinalIgnoreCase);
        if (interfaceScan.Interfaces.Count > 0)
        {
            foreach (var found in await SkyStreamMdns.QueryAsync(interfaceScan.Interfaces, cancellationToken))
            {
                candidates[found.Host] = new SkyStreamDevice(found.Host, found.Name, found.Name, found.MacAddress, found.Port);
            }
        }
        else
        {
            skipped.Add("No local private NIC; scanning additional networks only.");
        }

        if (candidates.Count == 0 && interfaceScan.Interfaces.Count > 0)
        {
            foreach (var open in await ProbePortAsync(interfaceScan.Interfaces, cancellationToken))
            {
                candidates[open] = new SkyStreamDevice(open, string.Empty, "Sky Stream", string.Empty, SkyStreamCredentials.Port);
            }
        }

        if (extraOnly.Count > 0)
        {
            foreach (var open in await ProbePortAsync(extraOnly, cancellationToken))
            {
                candidates[open] = new SkyStreamDevice(open, string.Empty, "Sky Stream", string.Empty, SkyStreamCredentials.Port);
            }
        }

        var devices = candidates.Values
            .OrderBy(device => device.Host)
            .ToList();

        if (devices.Count == 0)
        {
            skipped.Add(extras.Count == 0
                ? "No Sky Stream boxes answered mDNS (_rdk-rics._tcp) or TCP port 8091."
                : "No Sky Stream boxes answered mDNS (_rdk-rics._tcp) or TCP port 8091 on connected or additional networks.");
        }

        return new SkyStreamScanResponse(networks, skipped, devices, null);
    }

    private static async Task<HashSet<string>> ProbePortAsync(
        IReadOnlyList<PrivateIpv4.PrivateInterface> interfaces,
        CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hosts = await PrivateIpv4.ProbeOpenTcpAsync(
            interfaces.SelectMany(PrivateIpv4.EnumerateHosts),
            SkyStreamCredentials.Port,
            TimeSpan.FromMilliseconds(250),
            32,
            cancellationToken);
        foreach (var host in hosts)
        {
            found.Add(host.ToString());
        }

        return found;
    }

    private static void SendWakeOnLan(string macAddress)
    {
        var hex = new string(macAddress.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12)
        {
            throw new ArgumentException("MAC address must contain 12 hex digits.", nameof(macAddress));
        }

        var mac = Convert.FromHexString(hex);
        var packet = new byte[102];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++)
        {
            Buffer.BlockCopy(mac, 0, packet, 6 + (i * 6), 6);
        }

        using var udp = new UdpClient { EnableBroadcast = true };
        udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
    }

    private SkyStreamScanResponse LoadCachedScan()
    {
        if (!File.Exists(_cachePath))
        {
            return new SkyStreamScanResponse([], ["No cached Sky Stream scan found."], [], null);
        }

        try
        {
            var cached = JsonSerializer.Deserialize<SkyStreamScanResponse>(File.ReadAllText(_cachePath));
            if (cached is not null)
            {
                return cached;
            }
        }
        catch
        {
        }

        return new SkyStreamScanResponse([], ["Sky Stream cache file could not be read."], [], null);
    }

    public void Dispose()
    {
        foreach (var host in _sessions.Keys.ToArray())
        {
            _ = DropSessionAsync(host);
        }

        _scanLock.Dispose();
    }
}
