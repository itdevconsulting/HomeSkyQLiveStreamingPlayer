namespace H265Player.Models;

public sealed record DirectSkyQPreset(
    Guid Id,
    string Name,
    string SourceType,
    string StreamUrl,
    string VideoCodec,
    string Preset,
    string AudioMode,
    bool AutoRestart,
    bool AutoPlay,
    bool UseHlsProxy,
    string SkyQHost,
    string SkyQHostName,
    string SkyQModel,
    DateTimeOffset UpdatedAt);
