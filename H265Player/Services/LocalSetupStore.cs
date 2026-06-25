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
        _settingsPath = Path.Combine(environment.ContentRootPath, "local-settings.json");
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
        new()
        {
            FfmpegPath = settings.FfmpegPath.Trim(),
            DefaultHttpStreamUrl = settings.DefaultHttpStreamUrl.Trim(),
            DefaultRtspStreamUrl = settings.DefaultRtspStreamUrl.Trim(),
            EnableUnauthenticatedPort = settings.EnableUnauthenticatedPort,
            UnauthenticatedPort = NormalizePort(settings.UnauthenticatedPort) ?? 5222
        };

    private static LocalSetupSettings Empty() =>
        new();

    private static int? NormalizePort(int? port) =>
        port is > 0 and <= 65535 ? port : null;
}
