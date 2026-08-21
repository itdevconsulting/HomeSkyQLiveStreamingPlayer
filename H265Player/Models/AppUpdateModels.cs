namespace H265Player.Models;

public sealed record AppVersionStamp
{
    public string CommitSha { get; init; } = string.Empty;
    public string Branch { get; init; } = "main";
    public DateTimeOffset? BuiltAt { get; init; }
    public string RepoOwner { get; init; } = AppUpdateDefaults.RepoOwner;
    public string RepoName { get; init; } = AppUpdateDefaults.RepoName;
}

public sealed record AppVersionSummary(
    string Sha,
    string ShortSha,
    DateTimeOffset? Timestamp,
    string? Message);

public sealed record AppUpdateStatusResponse(
    bool CanApply,
    bool UpdateAvailable,
    bool UpdateQueued,
    bool AutoUpdateEnabled,
    string Channel,
    string Message,
    AppVersionSummary? Current,
    AppVersionSummary? Latest);

public static class AppUpdateDefaults
{
    public const string RepoOwner = "itdevconsulting";
    public const string RepoName = "HomeSkyQLiveStreamingPlayer";
    public const string Branch = "main";
}
