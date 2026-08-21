using System.Diagnostics;
using System.Text;
using H265Player.Models;

namespace H265Player.Services;

public sealed class FfmpegTranscoderManager : IDisposable
{
    private readonly object _gate = new();
    private readonly string _pidFilePath;
    private readonly ILogger<FfmpegTranscoderManager> _logger;
    private readonly TranscoderWatchdogOptions _watchdogOptions;
    private Process? _process;
    private List<string> _logs = [];
    private string? _outputDirectory;
    private string? _manifestPath;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastOutputAt;
    private string? _lastRestartReason;
    private CancellationTokenSource? _restartCts;
    private CancellationTokenSource? _watchdogCts;
    private bool _manualStop;
    private TranscoderSettings? _currentSettings;

    public FfmpegTranscoderManager(
        IHostEnvironment environment,
        IConfiguration configuration,
        ILogger<FfmpegTranscoderManager> logger)
    {
        _pidFilePath = AppPaths.Runtime("ffmpeg.pid");
        _logger = logger;
        _watchdogOptions = configuration.GetSection("Transcoder").GetSection("Watchdog").Get<TranscoderWatchdogOptions>() ?? new TranscoderWatchdogOptions();
        TryKillPersistedProcess();
    }

    public async Task StartAsync(TranscoderSettings settings, string outputDirectory, CancellationToken cancellationToken)
    {
        await StartCoreAsync(settings, outputDirectory, cancellationToken, cancelScheduledRestart: true, resetLogs: true);
    }

    private async Task StartCoreAsync(
        TranscoderSettings settings,
        string outputDirectory,
        CancellationToken cancellationToken,
        bool cancelScheduledRestart,
        bool resetLogs)
    {
        var resolvedPath = FfmpegPathResolver.ResolveOrThrow(settings.FfmpegPath);
        var manifestPath = Path.Combine(outputDirectory, "stream.m3u8");

        Stop(cancelScheduledRestart);
        TryKillPersistedProcess();
        PrepareOutputDirectory(outputDirectory);

        var process = CreateProcess(resolvedPath, settings, outputDirectory, manifestPath);

        lock (_gate)
        {
            if (resetLogs)
            {
                _logs = [];
                _lastRestartReason = null;
            }

            _manualStop = false;
            _outputDirectory = outputDirectory;
            _manifestPath = manifestPath;
            _currentSettings = settings with { FfmpegPath = resolvedPath };
            _startedAt = DateTimeOffset.UtcNow;
            _lastOutputAt = null;
            _process = process;
        }

        AppendLog($"ffmpeg started: {Path.GetFileName(resolvedPath)} {process.StartInfo.Arguments}");

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg.");
        }

        PersistProcessId(process.Id);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WaitForOutputAsync(manifestPath, cancellationToken);
        UpdateLastOutputAt(GetLatestOutputTimestamp(manifestPath));
        StartWatchdog(manifestPath);
    }

    public void Stop()
    {
        Stop(cancelScheduledRestart: true);
    }

    private void Stop(bool cancelScheduledRestart)
    {
        Process? process;

        lock (_gate)
        {
            _manualStop = true;
            if (cancelScheduledRestart)
            {
                _restartCts?.Cancel();
                _restartCts?.Dispose();
                _restartCts = null;
            }

            _watchdogCts?.Cancel();
            _watchdogCts?.Dispose();
            _watchdogCts = null;
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(3000))
                {
                    ForceKillByPid(process.Id);
                    process.WaitForExit(3000);
                }
            }
        }
        catch
        {
            ForceKillByPid(process.Id);
        }
        finally
        {
            DeletePersistedProcessId();
            process.Dispose();
            AppendLog("ffmpeg stopped");
        }
    }

    public TranscoderStatusResponse GetStatus()
    {
        lock (_gate)
        {
            return new TranscoderStatusResponse(
                IsRunning: _process is { HasExited: false },
                StartedAt: _startedAt,
                ManifestUrl: "/live/stream.m3u8",
                Settings: _currentSettings,
                Logs: [.. _logs],
                WatchdogEnabled: _watchdogOptions.Enabled && _currentSettings?.AutoRestart == true,
                LastOutputAt: _lastOutputAt,
                LastRestartReason: _lastRestartReason);
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private Process CreateProcess(string resolvedPath, TranscoderSettings settings, string outputDirectory, string manifestPath)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = resolvedPath,
                Arguments = BuildArguments(settings, manifestPath),
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = outputDirectory
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, args) => AppendLog(args.Data);
        process.ErrorDataReceived += (_, args) => AppendLog(args.Data);
        process.Exited += (_, _) => HandleUnexpectedExit(process.ExitCode);
        return process;
    }

    private void HandleUnexpectedExit(int exitCode)
    {
        DeletePersistedProcessId();
        AppendLog($"ffmpeg exited with code {exitCode}");
        QueueRestart($"process exit (code {exitCode})", TimeSpan.FromSeconds(2), stopCurrentProcess: false);
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var trimmed = line.Trim();
        if (trimmed.StartsWith("frame=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("size=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("speed=", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_gate)
        {
            _logs.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {trimmed}");
            if (_logs.Count > 120)
            {
                _logs.RemoveRange(0, _logs.Count - 120);
            }
        }
    }

    private static string BuildArguments(TranscoderSettings settings, string manifestPath)
    {
        var builder = new StringBuilder();
        builder.Append("-hide_banner -loglevel info ");
        if (settings.InputUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append("-rtsp_transport tcp -rtsp_flags prefer_tcp ");
        }

        builder.Append("-fflags nobuffer -flags low_delay -analyzeduration 1000000 -probesize 1000000 ");
        builder.Append("-i ").Append('"').Append(settings.InputUrl).Append("\" ");
        builder.Append(BuildVideoArguments(settings)).Append(' ');
        builder.Append(BuildAudioArguments(settings)).Append(' ');
        builder.Append("-f hls -hls_time 1 -hls_list_size 4 ");
        builder.Append("-hls_flags delete_segments+append_list+omit_endlist+independent_segments ");
        builder.Append("-hls_segment_filename ").Append('"').Append(Path.Combine(Path.GetDirectoryName(manifestPath)!, "segment_%03d.ts")).Append("\" ");
        builder.Append('"').Append(manifestPath).Append('"');
        return builder.ToString();
    }

    private static string BuildVideoArguments(TranscoderSettings settings)
    {
        return settings.Preset switch
        {
            "copy-video" => "-c:v copy",
            "browser-safe-h265" => "-c:v libx265 -preset veryfast -pix_fmt yuv420p -x265-params keyint=60:min-keyint=60:scenecut=0:bframes=0:repeat-headers=1",
            "low-latency-h264" => "-c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -g 30 -keyint_min 30 -sc_threshold 0 -bf 0",
            _ => settings.VideoCodec == "libx265"
                ? "-c:v libx265 -preset veryfast -pix_fmt yuv420p -x265-params keyint=60:min-keyint=60:scenecut=0:bframes=0:repeat-headers=1"
                : "-c:v libx264 -preset veryfast -tune zerolatency -pix_fmt yuv420p -g 60 -keyint_min 60 -sc_threshold 0 -bf 0"
        };
    }

    private static string BuildAudioArguments(TranscoderSettings settings)
    {
        return settings.AudioMode switch
        {
            "copy" => "-c:a copy",
            "aac" => "-c:a aac -b:a 128k -ac 2 -ar 48000",
            _ => "-an"
        };
    }

    private static void PrepareOutputDirectory(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        foreach (var file in Directory.EnumerateFiles(outputDirectory))
        {
            File.Delete(file);
        }
    }

    private void PersistProcessId(int processId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_pidFilePath)!);
        File.WriteAllText(_pidFilePath, processId.ToString());
    }

    private void DeletePersistedProcessId()
    {
        try
        {
            if (File.Exists(_pidFilePath))
            {
                File.Delete(_pidFilePath);
            }
        }
        catch
        {
        }
    }

    private void TryKillPersistedProcess()
    {
        try
        {
            if (!File.Exists(_pidFilePath))
            {
                return;
            }

            var raw = File.ReadAllText(_pidFilePath).Trim();
            if (!int.TryParse(raw, out var processId))
            {
                DeletePersistedProcessId();
                return;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(3000))
                    {
                        ForceKillByPid(processId);
                        process.WaitForExit(3000);
                    }
                }
            }
            catch
            {
            }
        }
        finally
        {
            DeletePersistedProcessId();
        }
    }

    private static void ForceKillByPid(int processId)
    {
        try
        {
            using var taskKill = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {processId} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };

            taskKill.Start();
            taskKill.WaitForExit(3000);
        }
        catch
        {
        }
    }

    private void WaitForOutput(string manifestPath, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 50; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_process is { HasExited: true })
                {
                    throw new InvalidOperationException(
                        $"FFmpeg exited before producing a manifest. Recent log output:{Environment.NewLine}{GetRecentLogSummary()}");
                }
            }

            if (File.Exists(manifestPath))
            {
                UpdateLastOutputAt(GetLatestOutputTimestamp(manifestPath));
                return;
            }

            Thread.Sleep(200);
        }

        throw new InvalidOperationException(
            $"FFmpeg did not produce an HLS manifest in time. Recent log output:{Environment.NewLine}{GetRecentLogSummary()}");
    }

    private string GetRecentLogSummary()
    {
        lock (_gate)
        {
            if (_logs.Count == 0)
            {
                return "(no ffmpeg log lines captured)";
            }

            return string.Join(Environment.NewLine, _logs.TakeLast(12));
        }
    }

    private Task WaitForOutputAsync(string manifestPath, CancellationToken cancellationToken) =>
        Task.Run(() => WaitForOutput(manifestPath, cancellationToken), cancellationToken);

    private void StartWatchdog(string manifestPath)
    {
        CancellationTokenSource watchdogCts;

        lock (_gate)
        {
            _watchdogCts?.Cancel();
            _watchdogCts?.Dispose();

            if (!_watchdogOptions.Enabled || _currentSettings?.AutoRestart != true)
            {
                _watchdogCts = null;
                return;
            }

            _watchdogCts = new CancellationTokenSource();
            watchdogCts = _watchdogCts;
        }

        _ = Task.Run(() => WatchdogLoopAsync(manifestPath, watchdogCts.Token));
    }

    private async Task WatchdogLoopAsync(string manifestPath, CancellationToken cancellationToken)
    {
        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, _watchdogOptions.PollIntervalSeconds));
        var startupGrace = TimeSpan.FromSeconds(Math.Max(1, _watchdogOptions.StartupGraceSeconds));
        var staleThreshold = TimeSpan.FromSeconds(Math.Max(2, _watchdogOptions.StaleOutputSeconds));

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(pollInterval, cancellationToken);

            Process? process;
            DateTimeOffset? startedAt;
            bool shouldWatch;

            lock (_gate)
            {
                process = _process;
                startedAt = _startedAt;
                shouldWatch = _currentSettings?.AutoRestart == true && _manifestPath == manifestPath;
            }

            if (!shouldWatch || process is null || process.HasExited)
            {
                return;
            }

            var latestOutputAt = GetLatestOutputTimestamp(manifestPath);
            if (latestOutputAt is not null)
            {
                UpdateLastOutputAt(latestOutputAt);

                if (DateTimeOffset.UtcNow - latestOutputAt.Value < staleThreshold)
                {
                    continue;
                }

                QueueRestart(
                    $"watchdog detected stale HLS output ({Math.Round((DateTimeOffset.UtcNow - latestOutputAt.Value).TotalSeconds)}s without a manifest or segment update)",
                    TimeSpan.Zero,
                    stopCurrentProcess: true);
                return;
            }

            if (startedAt is null || DateTimeOffset.UtcNow - startedAt.Value < startupGrace)
            {
                continue;
            }

            QueueRestart(
                $"watchdog detected missing HLS output after {Math.Round((DateTimeOffset.UtcNow - startedAt.Value).TotalSeconds)}s",
                TimeSpan.Zero,
                stopCurrentProcess: true);
            return;
        }
    }

    private void QueueRestart(string reason, TimeSpan delay, bool stopCurrentProcess)
    {
        TranscoderSettings? settings;
        string? outputDirectory;
        CancellationTokenSource restartCts;

        lock (_gate)
        {
            if (_manualStop || _currentSettings is null || !_currentSettings.AutoRestart || string.IsNullOrWhiteSpace(_outputDirectory))
            {
                return;
            }

            settings = _currentSettings;
            outputDirectory = _outputDirectory;
            _lastRestartReason = reason;
            _restartCts?.Cancel();
            _restartCts?.Dispose();
            _restartCts = new CancellationTokenSource();
            restartCts = _restartCts;
        }

        _logger.LogWarning("FFmpeg restart queued: {Reason}", reason);

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    AppendLog($"{reason}; restarting ffmpeg in {Math.Round(delay.TotalSeconds)}s");
                    await Task.Delay(delay, restartCts.Token);
                }
                else
                {
                    AppendLog($"{reason}; restarting ffmpeg");
                }

                if (stopCurrentProcess)
                {
                    Stop(cancelScheduledRestart: false);
                }

                await StartCoreAsync(settings!, outputDirectory!, restartCts.Token, cancelScheduledRestart: false, resetLogs: false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppendLog($"auto-restart failed: {ex.Message}");
                _logger.LogError(ex, "FFmpeg auto-restart failed.");
            }
            finally
            {
                ClearRestartRequest(restartCts);
            }
        });
    }

    private void ClearRestartRequest(CancellationTokenSource restartCts)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_restartCts, restartCts))
            {
                return;
            }

            _restartCts?.Dispose();
            _restartCts = null;
        }
    }

    private void UpdateLastOutputAt(DateTimeOffset? lastOutputAt)
    {
        if (lastOutputAt is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_lastOutputAt is null || lastOutputAt > _lastOutputAt)
            {
                _lastOutputAt = lastOutputAt;
            }
        }
    }

    private static DateTimeOffset? GetLatestOutputTimestamp(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            var latestUtc = File.GetLastWriteTimeUtc(manifestPath);
            var directory = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return new DateTimeOffset(latestUtc);
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.ts"))
            {
                var timestamp = File.GetLastWriteTimeUtc(file);
                if (timestamp > latestUtc)
                {
                    latestUtc = timestamp;
                }
            }

            return new DateTimeOffset(latestUtc);
        }
        catch
        {
            return null;
        }
    }
}
