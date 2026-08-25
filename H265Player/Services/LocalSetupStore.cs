using System.Net;
using System.Text.Json;
using H265Player.Models;

namespace H265Player.Services;

public sealed class LocalSetupStore
{
    private readonly object _gate = new();
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private LocalSetupSettings _settings;

    public LocalSetupStore(IHostEnvironment environment)
    {
        _settingsPath = AppPaths.File("local-settings.json");
        _settings = Load();
    }

    public LocalSetupSettings Get()
    {
        lock (_gate)
        {
            return _settings;
        }
    }

    public bool IsConfigured()
    {
        lock (_gate)
        {
            return _settings.IsConfigured;
        }
    }

    public async Task SaveAsync(LocalSetupSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(settings);

        lock (_gate)
        {
            _settings = normalized;
        }

        var json = JsonSerializer.Serialize(normalized, _jsonOptions);
        await File.WriteAllTextAsync(_settingsPath, json, cancellationToken);
    }

    private LocalSetupSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return Empty();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<LocalSetupSettings>(File.ReadAllText(_settingsPath));
            return loaded is null ? Empty() : Normalize(loaded);
        }
        catch
        {
            return Empty();
        }
    }

    public static LocalSetupSettings LoadFromPath(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            return Empty();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<LocalSetupSettings>(File.ReadAllText(settingsPath));
            return loaded is null ? Empty() : Normalize(loaded);
        }
        catch
        {
            return Empty();
        }
    }

    private static LocalSetupSettings Normalize(LocalSetupSettings settings) =>
        WithDetectedFfmpeg(new LocalSetupSettings
        {
            FfmpegPath = settings.FfmpegPath.Trim(),
            DefaultHttpStreamUrl = settings.DefaultHttpStreamUrl.Trim(),
            DefaultRtspStreamUrl = settings.DefaultRtspStreamUrl.Trim(),
            EnableUnauthenticatedPort = settings.EnableUnauthenticatedPort,
            UnauthenticatedPort = NormalizePort(settings.UnauthenticatedPort) ?? 5222,
            AutoUpdateEnabled = settings.AutoUpdateEnabled,
            ExtraScanNetworks = NormalizeExtraScanNetworks(settings.ExtraScanNetworks),
            DetectedScanNetworks = [],
            SkyStreamKnownHosts = NormalizeKnownHosts(settings.SkyStreamKnownHosts)
        });

    private static LocalSetupSettings Empty() =>
        WithDetectedFfmpeg(new LocalSetupSettings());

    private static LocalSetupSettings WithDetectedFfmpeg(LocalSetupSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.FfmpegPath) || !AppPaths.IsContainer)
        {
            return settings;
        }

        var detected = FfmpegPathResolver.TryDetect();
        return detected is null ? settings : settings with { FfmpegPath = detected };
    }

    private static IReadOnlyList<string> NormalizeExtraScanNetworks(IEnumerable<string>? values)
    {
        PrivateIpv4.TryNormalizeScanNetworks(values, out var networks, out _);
        return networks;
    }

    private static IReadOnlyList<SkyStreamKnownHost> NormalizeKnownHosts(IEnumerable<SkyStreamKnownHost>? values)
    {
        var hosts = new Dictionary<string, SkyStreamKnownHost>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? [])
        {
            if (!IPAddress.TryParse(value.Host, out var address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                !PrivateIpv4.IsPrivateLike(address) ||
                !WakeOnLan.TryNormalizeMac(value.MacAddress, out var mac))
            {
                continue;
            }

            hosts[address.ToString()] = new SkyStreamKnownHost(address.ToString(), mac);
        }

        return hosts.Values.OrderBy(item => item.Host, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int? NormalizePort(int? port) =>
        port is > 0 and <= 65535 ? port : null;
}
