using System.Text.Json;
using H265Player.Models;

namespace H265Player.Services;

public sealed class TranscoderSettingsStore
{
    private readonly object _gate = new();
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly LocalSetupStore _localSetupStore;
    private readonly TranscoderDefaults _defaults;
    private TranscoderSettings _settings;

    public TranscoderSettingsStore(
        IHostEnvironment environment,
        IConfiguration configuration,
        LocalSetupStore localSetupStore)
    {
        _settingsPath = Path.Combine(environment.ContentRootPath, "transcoder-settings.json");
        _localSetupStore = localSetupStore;
        _defaults = configuration.GetSection("Transcoder").Get<TranscoderDefaults>() ?? new TranscoderDefaults();
        _settings = LoadSettings();
    }

    public TranscoderSettings Get()
    {
        lock (_gate)
        {
            return ApplyLocalSetupDefaults(_settings);
        }
    }

    public async Task SaveAsync(TranscoderSettings settings, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _settings = ApplyLocalSetupDefaults(settings);
        }

        var json = JsonSerializer.Serialize(_settings, _jsonOptions);
        await File.WriteAllTextAsync(_settingsPath, json, cancellationToken);
    }

    private TranscoderSettings LoadSettings()
    {
        if (File.Exists(_settingsPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<TranscoderSettings>(File.ReadAllText(_settingsPath));
                if (loaded is not null)
                {
                    return ApplyLocalSetupDefaults(loaded);
                }
            }
            catch
            {
            }
        }

        return new TranscoderSettings(
            _defaults.FfmpegPath,
            _defaults.InputUrl,
            _defaults.VideoCodec,
            _defaults.Preset,
            _defaults.AudioMode,
            _defaults.AutoRestart,
            _defaults.AutoPlay,
            _defaults.AutoStart);
    }

    private TranscoderSettings ApplyLocalSetupDefaults(TranscoderSettings settings)
    {
        var local = _localSetupStore.Get();
        var inputUrl = string.IsNullOrWhiteSpace(settings.InputUrl)
            ? local.DefaultHttpStreamUrl
            : settings.InputUrl;

        return settings with
        {
            FfmpegPath = local.FfmpegPath,
            InputUrl = inputUrl
        };
    }
}
