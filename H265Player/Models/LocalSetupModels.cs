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
    public IReadOnlyList<string> ExtraScanNetworks { get; init; } = [];
    public IReadOnlyList<string> DetectedScanNetworks { get; init; } = [];
    public IReadOnlyList<SkyStreamKnownHost> SkyStreamKnownHosts { get; init; } = [];

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(FfmpegPath);

    public int? GetEffectiveUnauthenticatedPort() =>
        EnableUnauthenticatedPort && UnauthenticatedPort is > 0
            ? UnauthenticatedPort
            : null;
}

public sealed record ScanNetworksRequest(IReadOnlyList<string>? ExtraScanNetworks);

public sealed record SkyStreamKnownHost(string Host, string MacAddress);

public sealed record SkyStreamWakeRequest(string? Host, string? MacAddress);
