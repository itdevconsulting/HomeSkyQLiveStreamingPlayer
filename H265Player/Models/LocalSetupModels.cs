namespace H265Player.Models;

public sealed record LocalSetupSettings(
    string FfmpegPath,
    string DefaultHttpStreamUrl,
    string DefaultRtspStreamUrl)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(FfmpegPath);
}

