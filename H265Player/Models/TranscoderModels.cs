namespace H265Player.Models;

public sealed record StartTranscoderRequest(TranscoderSettings Settings);

public sealed record TranscoderStatusResponse(
    bool IsRunning,
    DateTimeOffset? StartedAt,
    string ManifestUrl,
    TranscoderSettings? Settings,
    IReadOnlyList<string> Logs,
    bool WatchdogEnabled,
    DateTimeOffset? LastOutputAt,
    string? LastRestartReason);

public sealed record TranscoderSettings(
    string FfmpegPath,
    string InputUrl,
    string VideoCodec,
    string Preset,
    string AudioMode,
    bool AutoRestart,
    bool AutoPlay,
    bool AutoStart);

public sealed class TranscoderDefaults
{
    public string FfmpegPath { get; init; } = "ffmpeg.exe";
    public string InputUrl { get; init; } = "http://encoder.local/hd.ts";
    public string VideoCodec { get; init; } = "libx264";
    public string Preset { get; init; } = "browser-safe-h264";
    public string AudioMode { get; init; } = "none";
    public bool AutoRestart { get; init; } = true;
    public bool AutoPlay { get; init; } = true;
    public bool AutoStart { get; init; }
}

public sealed class TranscoderWatchdogOptions
{
    public bool Enabled { get; init; } = true;
    public int PollIntervalSeconds { get; init; } = 3;
    public int StartupGraceSeconds { get; init; } = 10;
    public int StaleOutputSeconds { get; init; } = 12;
}
