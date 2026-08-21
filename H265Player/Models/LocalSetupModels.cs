using System.Text.Json.Serialization;

namespace H265Player.Models;

public sealed record LocalSetupSettings
{
    public string FfmpegPath { get; init; } = string.Empty;
    public string DefaultHttpStreamUrl { get; init; } = string.Empty;
    public string DefaultRtspStreamUrl { get; init; } = string.Empty;
    public bool EnableUnauthenticatedPort { get; init; }
    public int? UnauthenticatedPort { get; init; } = 5222;
    public bool AutoUpdateEnabled { get; init; }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(FfmpegPath);

    public int? GetEffectiveUnauthenticatedPort() =>
        EnableUnauthenticatedPort && UnauthenticatedPort is > 0
            ? UnauthenticatedPort
            : null;
}
