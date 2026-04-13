namespace H265Player.Models;

public sealed record StartTranscoderRequest(TranscoderSettings Settings);

public sealed record TranscoderStatusResponse(
    bool IsRunning,
    DateTimeOffset? StartedAt,
    string ManifestUrl,
    TranscoderSettings? Settings,
    IReadOnlyList<string> Logs);

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
    public string InputUrl { get; init; } = "http://192.168.15.169/hd.ts";
    public string VideoCodec { get; init; } = "libx264";
    public string Preset { get; init; } = "browser-safe-h264";
    public string AudioMode { get; init; } = "none";
    public bool AutoRestart { get; init; } = true;
    public bool AutoPlay { get; init; } = true;
    public bool AutoStart { get; init; }
}
