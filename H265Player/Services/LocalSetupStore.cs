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
        var normalized = new LocalSetupSettings(
            settings.FfmpegPath.Trim(),
            settings.DefaultHttpStreamUrl.Trim(),
            settings.DefaultRtspStreamUrl.Trim());

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
            return new LocalSetupSettings(string.Empty, string.Empty, string.Empty);
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<LocalSetupSettings>(File.ReadAllText(_settingsPath));
            return loaded ?? new LocalSetupSettings(string.Empty, string.Empty, string.Empty);
        }
        catch
        {
            return new LocalSetupSettings(string.Empty, string.Empty, string.Empty);
        }
    }
}
