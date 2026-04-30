namespace H265Player.Models;

public sealed record ServiceRestartStatusResponse(
    bool CanSelfRestart,
    bool RestartScheduled,
    string ServiceMode,
    string Message);
