using System.Text.Json;
using H265Player.Models;

namespace H265Player.Services;

public sealed class DirectSkyQPresetStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private List<DirectSkyQPreset> _presets;

    public DirectSkyQPresetStore(IHostEnvironment environment)
    {
        _path = AppPaths.File("direct-skyq-presets.json");
        _presets = Load();
    }

    public event Action? Changed;

    public IReadOnlyList<DirectSkyQPreset> GetAll()
    {
        lock (_gate)
        {
            return _presets
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.UpdatedAt)
                .ToArray();
        }
    }

    public DirectSkyQPreset? Get(Guid id)
    {
        lock (_gate)
        {
            return _presets.FirstOrDefault(item => item.Id == id);
        }
    }

    public async Task<DirectSkyQPreset> SaveAsync(DirectSkyQPreset preset, CancellationToken cancellationToken = default)
    {
        DirectSkyQPreset saved;
        lock (_gate)
        {
            var normalized = NormalizePreset(preset with
            {
                Id = preset.Id == Guid.Empty ? Guid.NewGuid() : preset.Id,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            _presets.RemoveAll(item => item.Id == normalized.Id);
            _presets.Add(normalized);
            saved = normalized;
        }

        await PersistAsync(cancellationToken);
        Changed?.Invoke();
        return saved;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var removed = false;
        lock (_gate)
        {
            removed = _presets.RemoveAll(item => item.Id == id) > 0;
        }

        if (!removed)
        {
            return;
        }

        await PersistAsync(cancellationToken);
        Changed?.Invoke();
    }

    private List<DirectSkyQPreset> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_path);
            var presets = JsonSerializer.Deserialize<List<DirectSkyQPreset>>(json);
            return presets?.Select(NormalizePreset).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        List<DirectSkyQPreset> snapshot;
        lock (_gate)
        {
            snapshot = _presets
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.UpdatedAt)
                .ToList();
        }

        var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
        await File.WriteAllTextAsync(_path, json, cancellationToken);
    }

    private static DirectSkyQPreset NormalizePreset(DirectSkyQPreset preset)
    {
        var sourceType = preset.SourceType.Trim().ToLowerInvariant();
        var watchdogCheckIntervalSeconds = NormalizeNullableInt(preset.WatchdogCheckIntervalSeconds, 1, 30);
        var watchdogStallSeconds = NormalizeNullableInt(preset.WatchdogStallSeconds, 2, 120);

        return preset with
        {
            Name = preset.Name.Trim(),
            SourceType = sourceType,
            StreamUrl = preset.StreamUrl.Trim(),
            VideoCodec = preset.VideoCodec.Trim(),
            Preset = preset.Preset.Trim(),
            AudioMode = preset.AudioMode.Trim(),
            WatchdogCheckIntervalSeconds = watchdogCheckIntervalSeconds,
            WatchdogStallSeconds = watchdogStallSeconds is null || watchdogCheckIntervalSeconds is null
                ? watchdogStallSeconds
                : Math.Max(watchdogStallSeconds.Value, watchdogCheckIntervalSeconds.Value + 1),
            SkyQHost = preset.SkyQHost.Trim(),
            SkyQHostName = preset.SkyQHostName.Trim(),
            SkyQModel = preset.SkyQModel.Trim(),
            RemoteKind = SkyRemoteKinds.Normalize(preset.RemoteKind)
        };
    }

    private static int? NormalizeNullableInt(int? value, int min, int max)
    {
        if (value is null)
        {
            return null;
        }

        return Math.Clamp(value.Value, min, max);
    }
}
