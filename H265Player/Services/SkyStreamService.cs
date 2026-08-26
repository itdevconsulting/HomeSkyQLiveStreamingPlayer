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
        ["backup"] = "Dismiss",
        ["back"] = "Dismiss",
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

    private static readonly (string Command, int SettleMs)[] GuideStrokes =
    [
        ("home", 2800),
        ("down", 500),
        ("down", 1000),
        ("select", 900),
        ("", 900),
        ("", 900),
        ("right", 800),
        ("down", 800)
    ];

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _lastWasDigit = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SkyStreamClient> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cachePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly ILogger<SkyStreamService> _logger;
    private readonly LocalSetupStore _setupStore;
    private SkyStreamScanResponse _cachedScan;
    private DateTimeOffset? _lastScanAt;
    private static readonly TimeSpan DigitKeyGap = TimeSpan.FromMilliseconds(550);
    private static readonly TimeSpan FirstDigitGap = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan OtherKeyGap = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ScanCacheDuration = TimeSpan.FromHours(1);

    public SkyStreamService(IHostEnvironment environment, LocalSetupStore setupStore, ILogger<SkyStreamService> logger)
    {
        _setupStore = setupStore;
        _logger = logger;
        _cachePath = AppPaths.File("sky-stream-cache.json");
        _cachedScan = MergeKnownHosts(LoadCachedScan(), _setupStore.Get().SkyStreamKnownHosts);
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

    public Task<SkyQCommandResult> SendCommandAsync(string host, string command, CancellationToken cancellationToken) =>
        Task.FromResult(QueueCommand(host, command, replaceMacro: true));

    public Task<SkyQCommandResult> OpenGuideAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult(QueueGuide(host));

    public Task<SkyQCommandResult> TuneLiveChannelAsync(string host, int channelNumber, CancellationToken cancellationToken)
    {
        if (channelNumber is < 1 or > 9999)
        {
            return Task.FromResult(new SkyQCommandResult(false, host, "livetv", $"Invalid channel number {channelNumber}.", []));
        }

        var guide = QueueGuide(host);
        if (!guide.Success)
        {
            return Task.FromResult(new SkyQCommandResult(false, host, "livetv", guide.Message, guide.Logs));
        }

        QueueCommand(host, "", replaceMacro: false, settleMs: 2000);
        var firstDigit = true;
        foreach (var digit in channelNumber.ToString())
        {
            QueueCommand(host, digit.ToString(), replaceMacro: false, settleMs: (int)(firstDigit ? FirstDigitGap : DigitKeyGap).TotalMilliseconds);
            firstDigit = false;
        }

        QueueCommand(host, "select", replaceMacro: false);
        QueueCommand(host, "select", replaceMacro: false);
        return Task.FromResult(new SkyQCommandResult(
            true,
            host,
            "livetv",
            $"Tuning live TV {channelNumber}.",
            [$"Target={host}:{SkyStreamCredentials.Port}", $"Live TV channel={channelNumber}", "Queued Guide, then channel number, OK, OK."]));
    }

    private SkyQCommandResult QueueGuide(string host)
    {
        SkyQCommandResult? last = null;
        var first = true;
        foreach (var stroke in GuideStrokes)
        {
            last = QueueCommand(host, stroke.Command, replaceMacro: first, settleMs: stroke.SettleMs);
            first = false;
            if (!last.Success)
            {
                return last;
            }
        }

        return last ?? new SkyQCommandResult(false, host, "tvguide", "Guide sequence is empty.", []);
    }

    private SkyQCommandResult QueueCommand(string host, string command, bool replaceMacro, int? settleMs = null)
    {
        if (!TryHost(host, out _))
        {
            return new SkyQCommandResult(false, host, command, "Sky Stream control is limited to private IPv4 addresses.", []);
        }

        if (string.Equals(command, "tvguide", StringComparison.OrdinalIgnoreCase))
        {
            return QueueGuide(host);
        }

        var hostKey = host.Trim();
        if (string.IsNullOrEmpty(command))
        {
            GetClient(hostKey).Enqueue(null, settleMs ?? 0, replaceMacro);
            return new SkyQCommandResult(true, host, "wait", "Queued.", []);
        }

        if (!Commands.TryGetValue(command, out var key))
        {
            return new SkyQCommandResult(false, host, command, $"Unknown Sky Stream command '{command}'.", []);
        }

        var isDigit = command.Length == 1 && char.IsDigit(command[0]);
        var continuingDigits = isDigit && _lastWasDigit.TryGetValue(hostKey, out var previousDigit) && previousDigit;
        var settle = settleMs ?? (int)(isDigit
            ? (continuingDigits ? DigitKeyGap : FirstDigitGap)
            : OtherKeyGap).TotalMilliseconds;
        GetClient(hostKey).Enqueue(key, settle, replaceMacro);
        _lastWasDigit[hostKey] = isDigit;
        return new SkyQCommandResult(
            true,
            host,
            command,
            "Queued.",
            [$"Target={host}:{SkyStreamCredentials.Port}", $"Command={command}", $"Key={key}"]);
    }

    public Task WarmAsync(string host, CancellationToken cancellationToken)
    {
        if (!TryHost(host, out _))
        {
            return Task.CompletedTask;
        }

        GetClient(host.Trim()).Enqueue(null, 0, replaceMacro: false);
        return Task.CompletedTask;
    }

    private bool TryHost(string host, out IPAddress address) =>
        IPAddress.TryParse(host, out address) &&
        PrivateIpv4.IsAllowedTarget(address, _setupStore.Get().ExtraScanNetworks);

    private SkyStreamClient GetClient(string host) =>
        _sessions.GetOrAdd(host, key =>
        {
            var client = new SkyStreamClient(key, log: message => _logger.LogInformation("Sky Stream {Host}: {Message}", key, message));
            client.Start();
            return client;
        });

    public async Task<SkyQCommandResult> WakeAsync(string host, string? macAddress, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(host, out var address) ||
            !PrivateIpv4.IsAllowedTarget(address, _setupStore.Get().ExtraScanNetworks))
        {
            return new SkyQCommandResult(false, host, "wake", "Sky Stream wake is limited to private IPv4 addresses.", []);
        }

        var logs = new List<string> { $"Target={host}:{SkyStreamCredentials.Port}" };
        if (!string.IsNullOrWhiteSpace(macAddress))
        {
            await RememberHostAsync(host, macAddress, cancellationToken);
        }

        var awake = await WakeIfNeededAsync(host.Trim(), macAddress, waitForPort: true, logs, cancellationToken);
        if (awake)
        {
            await ForceRefreshAsync(cancellationToken);
        }

        return new SkyQCommandResult(
            awake,
            host,
            "wake",
            awake
                ? "Puck is awake and TCP 8091 is open."
                : "Sent Wake-on-LAN but TCP 8091 did not open. Confirm the MAC and that magic packets can reach the puck.",
            logs);
    }

    private async Task<bool> WakeIfNeededAsync(
        string host,
        string? macAddress,
        bool waitForPort,
        List<string> logs,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        if (await PrivateIpv4.WaitForOpenTcpAsync(address, SkyStreamCredentials.Port, TimeSpan.FromMilliseconds(400), cancellationToken))
        {
            logs.Add("TCP 8091 already open.");
            return true;
        }

        var mac = ResolveMac(host, macAddress);
        if (string.IsNullOrWhiteSpace(mac))
        {
            logs.Add("No Wake-on-LAN MAC stored for this host. Enter the puck MAC, then Wake.");
            return false;
        }

        var broadcast = DirectedBroadcastFor(address);
        var targets = WakeOnLan.Send(mac, address, broadcast);
        logs.Add($"Sent Wake-on-LAN to {mac} via {string.Join(", ", targets)}.");

        if (!waitForPort)
        {
            return false;
        }

        logs.Add("Waiting for TCP 8091 after magic packet.");
        var awake = await PrivateIpv4.WaitForOpenTcpAsync(
            address,
            SkyStreamCredentials.Port,
            TimeSpan.FromSeconds(12),
            cancellationToken);
        logs.Add(awake ? "TCP 8091 is open." : "TCP 8091 did not open after Wake-on-LAN.");
        return awake;
    }

    public async Task RememberHostAsync(string host, string macAddress, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(host, out var address) || !PrivateIpv4.IsPrivateLike(address))
        {
            throw new InvalidOperationException("Sky Stream hosts must be private IPv4 addresses.");
        }

        var mac = WakeOnLan.NormalizeMac(macAddress);
        var current = _setupStore.Get();
        var hosts = current.SkyStreamKnownHosts
            .Where(item => !string.Equals(item.Host, address.ToString(), StringComparison.OrdinalIgnoreCase))
            .Append(new SkyStreamKnownHost(address.ToString(), mac))
            .ToList();
        await _setupStore.SaveAsync(current with { SkyStreamKnownHosts = hosts }, cancellationToken);

        _cachedScan = MergeKnownHosts(_cachedScan, hosts);
    }

    private string ResolveMac(string host, string? macAddress)
    {
        if (WakeOnLan.TryNormalizeMac(macAddress, out var provided))
        {
            return provided;
        }

        var known = _setupStore.Get().SkyStreamKnownHosts
            .FirstOrDefault(item => string.Equals(item.Host, host, StringComparison.OrdinalIgnoreCase));
        if (known is not null)
        {
            return known.MacAddress;
        }

        var cached = _cachedScan.Devices.FirstOrDefault(item =>
            string.Equals(item.Host, host, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(item.MacAddress));
        return cached?.MacAddress ?? string.Empty;
    }

    private IPAddress? DirectedBroadcastFor(IPAddress host)
    {
        foreach (var extra in PrivateIpv4.ExtraScanTargets(_setupStore.Get().ExtraScanNetworks))
        {
            if (PrivateIpv4.Contains(extra, host))
            {
                return PrivateIpv4.DirectedBroadcast(extra.Network, extra.PrefixLength);
            }
        }

        return PrivateIpv4.DirectedBroadcast(host, 24);
    }

    private void DropSession(string host)
    {
        if (_sessions.TryRemove(host, out var client))
        {
            _ = client.DisposeAsync();
        }
    }

    private async Task<SkyStreamScanResponse> ScanInternalAsync(CancellationToken cancellationToken)
    {
        var extraScanNetworks = _setupStore.Get().ExtraScanNetworks;
        var interfaceScan = PrivateIpv4.GetInterfaces(_logger);
        var extras = PrivateIpv4.ExtraScanTargets(extraScanNetworks);
        var extraOnly = PrivateIpv4.ExtraProbeTargets(interfaceScan.Interfaces, extras);
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
            foreach (var open in await ProbePortAsync(interfaceScan.Interfaces, TimeSpan.FromMilliseconds(250), cancellationToken))
            {
                candidates[open] = new SkyStreamDevice(open, string.Empty, "Sky Stream", string.Empty, SkyStreamCredentials.Port);
            }
        }

        if (extraOnly.Count > 0)
        {
            skipped.AddRange(await WakeKnownExtraHostsAsync(extraOnly, cancellationToken));

            foreach (var found in await SkyStreamMdns.QueryHostsAsync(
                         extraOnly.Where(item => item.PrefixLength == 32).SelectMany(PrivateIpv4.EnumerateHosts),
                         cancellationToken))
            {
                candidates[found.Host] = new SkyStreamDevice(found.Host, found.Name, found.Name, found.MacAddress, found.Port);
            }

            foreach (var open in await ProbePortAsync(extraOnly, TimeSpan.FromSeconds(2), cancellationToken))
            {
                candidates.TryAdd(open, new SkyStreamDevice(open, string.Empty, "Sky Stream", string.Empty, SkyStreamCredentials.Port));
            }

            skipped.AddRange(await DescribeUnreachableExtraHostsAsync(
                extraOnly,
                candidates.Keys,
                cancellationToken));
        }

        var knownHosts = _setupStore.Get().SkyStreamKnownHosts;
        foreach (var known in knownHosts)
        {
            if (candidates.TryGetValue(known.Host, out var existing))
            {
                if (string.IsNullOrWhiteSpace(existing.MacAddress))
                {
                    candidates[known.Host] = existing with { MacAddress = known.MacAddress };
                }

                continue;
            }

            candidates[known.Host] = new SkyStreamDevice(
                known.Host,
                string.Empty,
                "Sky Stream",
                known.MacAddress,
                SkyStreamCredentials.Port,
                Asleep: true);
        }

        foreach (var extraHost in extraOnly.Where(item => item.PrefixLength == 32).SelectMany(PrivateIpv4.EnumerateHosts))
        {
            var host = extraHost.ToString();
            if (candidates.ContainsKey(host))
            {
                continue;
            }

            candidates[host] = new SkyStreamDevice(
                host,
                string.Empty,
                "Sky Stream",
                ResolveMac(host, null),
                SkyStreamCredentials.Port,
                Asleep: true);
        }

        await PersistDiscoveredMacsAsync(candidates.Values, cancellationToken);

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
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hosts = await PrivateIpv4.ProbeOpenTcpAsync(
            interfaces.SelectMany(PrivateIpv4.EnumerateHosts),
            SkyStreamCredentials.Port,
            timeout,
            32,
            cancellationToken);
        foreach (var host in hosts)
        {
            found.Add(host.ToString());
        }

        return found;
    }

    private async Task<List<string>> WakeKnownExtraHostsAsync(
        IReadOnlyList<PrivateIpv4.PrivateInterface> extras,
        CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var sleepy = extras
            .Where(item => item.PrefixLength == 32)
            .SelectMany(PrivateIpv4.EnumerateHosts)
            .Select(host => (Address: host, Mac: ResolveMac(host.ToString(), null)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Mac))
            .ToList();

        foreach (var item in sleepy)
        {
            var open = await PrivateIpv4.WaitForOpenTcpAsync(
                item.Address,
                SkyStreamCredentials.Port,
                TimeSpan.FromMilliseconds(350),
                cancellationToken);
            if (open)
            {
                continue;
            }

            var broadcast = DirectedBroadcastFor(item.Address);
            var targets = WakeOnLan.Send(item.Mac, item.Address, broadcast);
            messages.Add($"Waking {item.Address} ({item.Mac}) via {string.Join(", ", targets)}.");
        }

        if (messages.Count > 0)
        {
            messages.Add("Waiting a few seconds for TCP 8091 after Wake-on-LAN.");
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
        }

        return messages;
    }

    private async Task PersistDiscoveredMacsAsync(IEnumerable<SkyStreamDevice> devices, CancellationToken cancellationToken)
    {
        var discovered = devices
            .Where(device => WakeOnLan.TryNormalizeMac(device.MacAddress, out _) && IPAddress.TryParse(device.Host, out _))
            .ToList();
        if (discovered.Count == 0)
        {
            return;
        }

        var current = _setupStore.Get();
        var hosts = current.SkyStreamKnownHosts.ToDictionary(item => item.Host, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var device in discovered)
        {
            var mac = WakeOnLan.NormalizeMac(device.MacAddress);
            if (hosts.TryGetValue(device.Host, out var existing) &&
                string.Equals(existing.MacAddress, mac, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            hosts[device.Host] = new SkyStreamKnownHost(device.Host, mac);
            changed = true;
        }

        if (changed)
        {
            await _setupStore.SaveAsync(current with { SkyStreamKnownHosts = hosts.Values.ToList() }, cancellationToken);
        }
    }

    private static SkyStreamScanResponse MergeKnownHosts(
        SkyStreamScanResponse scan,
        IReadOnlyList<SkyStreamKnownHost> knownHosts)
    {
        var devices = scan.Devices.ToDictionary(item => item.Host, StringComparer.OrdinalIgnoreCase);
        foreach (var known in knownHosts)
        {
            if (devices.TryGetValue(known.Host, out var existing))
            {
                if (string.IsNullOrWhiteSpace(existing.MacAddress))
                {
                    devices[known.Host] = existing with { MacAddress = known.MacAddress };
                }
            }
            else
            {
                devices[known.Host] = new SkyStreamDevice(
                    known.Host,
                    string.Empty,
                    "Sky Stream",
                    known.MacAddress,
                    SkyStreamCredentials.Port,
                    Asleep: true);
            }
        }

        return scan with { Devices = devices.Values.OrderBy(item => item.Host).ToList() };
    }

    private async Task<List<string>> DescribeUnreachableExtraHostsAsync(
        IReadOnlyList<PrivateIpv4.PrivateInterface> extras,
        IReadOnlyCollection<string> foundHosts,
        CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        foreach (var host in extras.Where(item => item.PrefixLength == 32).SelectMany(PrivateIpv4.EnumerateHosts))
        {
            var text = host.ToString();
            if (foundHosts.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var pingable = await PrivateIpv4.IsReachableAsync(host, 1000, cancellationToken);
            var mac = ResolveMac(text, null);
            if (pingable && string.IsNullOrWhiteSpace(mac))
            {
                messages.Add($"{text} answers ping but TCP {SkyStreamCredentials.Port} is closed or filtered. nmap filtered means the host is up while Sky Remote is still firewalled — Home cannot be sent until 8091 is open. Sleeping pucks also need a Wake-on-LAN MAC.");
            }
            else if (pingable)
            {
                messages.Add($"{text} answers ping but TCP {SkyStreamCredentials.Port} stayed closed or filtered after Wake-on-LAN. If this host is already on the puck’s LAN, wake it with the Sky remote until 8091 is open. Tailscale subnet routes usually forward ICMP and drop TCP 8091.");
            }
            else
            {
                messages.Add($"{text} did not answer mDNS or TCP {SkyStreamCredentials.Port}.");
            }
        }

        return messages;
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
            DropSession(host);
        }

        _scanLock.Dispose();
    }
}
