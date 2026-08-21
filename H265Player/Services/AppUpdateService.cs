using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using H265Player.Models;

namespace H265Player.Services;

public sealed class AppUpdateService
{
    public const string HttpClientName = "github";

    private static readonly TimeSpan CheckCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions StampJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IHostEnvironment _environment;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LocalSetupStore _setupStore;
    private readonly ILogger<AppUpdateService> _logger;
    private readonly object _gate = new();
    private AppUpdateStatusResponse? _cachedStatus;
    private DateTimeOffset _cacheExpiresAt;

    public AppUpdateService(
        IHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        LocalSetupStore setupStore,
        ILogger<AppUpdateService> logger)
    {
        _environment = environment;
        _httpClientFactory = httpClientFactory;
        _setupStore = setupStore;
        _logger = logger;
    }

    public async Task<AppUpdateStatusResponse> GetStatusAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh)
        {
            lock (_gate)
            {
                if (_cachedStatus is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
                {
                    return WithLiveFlags(_cachedStatus);
                }
            }
        }

        var current = ReadCurrentVersion();
        var latest = await FetchLatestAsync(cancellationToken);
        var status = BuildStatus(current, latest);

        lock (_gate)
        {
            _cachedStatus = status;
            _cacheExpiresAt = DateTimeOffset.UtcNow.Add(CheckCacheLifetime);
        }

        return WithLiveFlags(status);
    }

    public async Task<AppUpdateStatusResponse> RequestUpdateAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(forceRefresh: true, cancellationToken);
        if (!status.CanApply)
        {
            return status;
        }

        if (!status.UpdateAvailable && !status.UpdateQueued)
        {
            return status with
            {
                Message = "The installed app already matches the latest GitHub commit."
            };
        }

        var requestPath = GetUpdateRequestPath();
        Directory.CreateDirectory(Path.GetDirectoryName(requestPath)!);
        var payload = JsonSerializer.Serialize(new
        {
            requestedAt = DateTimeOffset.UtcNow,
            currentSha = status.Current?.Sha,
            latestSha = status.Latest?.Sha
        }, StampJsonOptions);
        await File.WriteAllTextAsync(requestPath, payload, cancellationToken);
        TryTriggerHelper();

        var queued = status with
        {
            UpdateQueued = true,
            Message = GetQueuedMessage()
        };

        lock (_gate)
        {
            _cachedStatus = queued;
            _cacheExpiresAt = DateTimeOffset.UtcNow.Add(CheckCacheLifetime);
        }

        _logger.LogWarning("Application update requested. Current={Current} Latest={Latest}", status.Current?.Sha, status.Latest?.Sha);
        return queued;
    }

    private AppUpdateStatusResponse WithLiveFlags(AppUpdateStatusResponse status) =>
        status with
        {
            AutoUpdateEnabled = _setupStore.Get().AutoUpdateEnabled,
            UpdateQueued = File.Exists(GetUpdateRequestPath()),
            CanApply = CanApplyUpdates()
        };

    private AppUpdateStatusResponse BuildStatus(AppVersionSummary? current, AppVersionSummary? latest)
    {
        var canApply = CanApplyUpdates();
        var queued = File.Exists(GetUpdateRequestPath());
        var autoUpdate = _setupStore.Get().AutoUpdateEnabled;
        var updateAvailable = current is not null
            && latest is not null
            && !string.Equals(current.Sha, latest.Sha, StringComparison.OrdinalIgnoreCase);

        if (queued)
        {
            return new AppUpdateStatusResponse(
                canApply,
                updateAvailable,
                true,
                autoUpdate,
                GetChannel(),
                GetQueuedMessage(),
                current,
                latest);
        }

        if (!canApply)
        {
            return new AppUpdateStatusResponse(
                false,
                updateAvailable,
                false,
                autoUpdate,
                GetChannel(),
                AppPaths.IsContainer
                    ? "Container installs are updated by pulling a newer image or rebuilding the Home Assistant add-on."
                    : "In-app updates are available after the Linux or Windows installer has registered the update helper.",
                current,
                latest);
        }

        if (latest is null)
        {
            return new AppUpdateStatusResponse(
                canApply,
                false,
                false,
                autoUpdate,
                GetChannel(),
                "Unable to reach GitHub to check for a newer commit.",
                current,
                latest);
        }

        if (current is null)
        {
            return new AppUpdateStatusResponse(
                canApply,
                true,
                false,
                autoUpdate,
                GetChannel(),
                "This build has no version stamp. Installing the latest GitHub snapshot will create one.",
                current,
                latest);
        }

        if (updateAvailable)
        {
            return new AppUpdateStatusResponse(
                canApply,
                true,
                false,
                autoUpdate,
                GetChannel(),
                $"A newer GitHub commit is available ({latest.ShortSha}).",
                current,
                latest);
        }

        return new AppUpdateStatusResponse(
            canApply,
            false,
            false,
            autoUpdate,
            GetChannel(),
            "This install is already on the latest GitHub commit.",
            current,
            latest);
    }

    private AppVersionSummary? ReadCurrentVersion()
    {
        var path = Path.Combine(_environment.ContentRootPath, "version.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stamp = JsonSerializer.Deserialize<AppVersionStamp>(File.ReadAllText(path), StampJsonOptions);
            if (stamp is null || string.IsNullOrWhiteSpace(stamp.CommitSha))
            {
                return null;
            }

            return ToSummary(stamp.CommitSha, stamp.BuiltAt, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read version.json");
            return null;
        }
    }

    private async Task<AppVersionSummary?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(
                $"repos/{AppUpdateDefaults.RepoOwner}/{AppUpdateDefaults.RepoName}/commits/{AppUpdateDefaults.Branch}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub version check failed with {Status}", (int)response.StatusCode);
                return null;
            }

            var commit = await response.Content.ReadFromJsonAsync<GitHubCommitResponse>(cancellationToken);
            if (commit is null || string.IsNullOrWhiteSpace(commit.Sha))
            {
                return null;
            }

            return ToSummary(commit.Sha, commit.Commit?.Committer?.Date, commit.Commit?.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub version check failed");
            return null;
        }
    }

    private bool CanApplyUpdates()
    {
        if (AppPaths.IsContainer)
        {
            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            return File.Exists("/etc/systemd/system/SkyStreamingService-update.path")
                && Directory.Exists("/var/lib/skystreamingservice");
        }

        if (OperatingSystem.IsWindows())
        {
            var helper = Path.Combine(GetWindowsInstallRoot(), "update-if-requested.ps1");
            return File.Exists(helper);
        }

        return false;
    }

    private string GetUpdateRequestPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(GetWindowsInstallRoot(), "update.request");
        }

        return "/var/lib/skystreamingservice/update.request";
    }

    private string GetWindowsInstallRoot()
    {
        var contentRoot = Path.GetFullPath(_environment.ContentRootPath);
        var parent = Directory.GetParent(contentRoot)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            return parent;
        }

        return Path.GetFullPath(Path.Combine(contentRoot, ".."));
    }

    private void TryTriggerHelper()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Run /TN \"SkyQStreamingServiceUpdate\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            process?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Immediate Windows update task trigger was not permitted; the scheduled helper will pick the request up.");
        }
    }

    private static AppVersionSummary ToSummary(string sha, DateTimeOffset? timestamp, string? message)
    {
        var trimmed = sha.Trim();
        var shortSha = trimmed.Length <= 7 ? trimmed : trimmed[..7];
        var firstLine = string.IsNullOrWhiteSpace(message)
            ? null
            : message.Split('\n', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return new AppVersionSummary(trimmed, shortSha, timestamp, firstLine);
    }

    private static string GetChannel() =>
        $"github:{AppUpdateDefaults.RepoOwner}/{AppUpdateDefaults.RepoName}@{AppUpdateDefaults.Branch}";

    private static string GetQueuedMessage() =>
        OperatingSystem.IsWindows()
            ? "Update queued. The Windows helper applies it in the background and restarts the service."
            : "Update queued. The Linux helper applies it shortly and restarts the service.";

    private sealed class GitHubCommitResponse
    {
        [JsonPropertyName("sha")]
        public string Sha { get; set; } = string.Empty;

        [JsonPropertyName("commit")]
        public GitHubCommitBody? Commit { get; set; }
    }

    private sealed class GitHubCommitBody
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("committer")]
        public GitHubCommitUser? Committer { get; set; }
    }

    private sealed class GitHubCommitUser
    {
        [JsonPropertyName("date")]
        public DateTimeOffset Date { get; set; }
    }
}
