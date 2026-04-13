using System.Diagnostics;
using System.Text;
using H265Player.Models;

namespace H265Player.Services;

public sealed class FfmpegTranscoderManager : IDisposable
{
    private readonly object _gate = new();
    private readonly string _pidFilePath;
    private Process? _process;
    private List<string> _logs = [];
    private string? _outputDirectory;
    private DateTimeOffset? _startedAt;
    private CancellationTokenSource? _restartCts;
    private bool _manualStop;
    private TranscoderSettings? _currentSettings;

    public FfmpegTranscoderManager(IHostEnvironment environment)
    {
        _pidFilePath = Path.Combine(environment.ContentRootPath, "runtime", "ffmpeg.pid");
        TryKillPersistedProcess();
    }

    public async Task StartAsync(TranscoderSettings settings, string outputDirectory, CancellationToken cancellationToken)
    {
        var resolvedPath = FfmpegPathResolver.ResolveOrThrow(settings.FfmpegPath);
        var manifestPath = Path.Combine(outputDirectory, "stream.m3u8");

        Stop();
        TryKillPersistedProcess();
        PrepareOutputDirectory(outputDirectory);

        var process = CreateProcess(resolvedPath, settings, outputDirectory, manifestPath);

        lock (_gate)
        {
            _logs = [];
            _manualStop = false;
            _outputDirectory = outputDirectory;
            _currentSettings = settings with { FfmpegPath = resolvedPath };
            _startedAt = DateTimeOffset.UtcNow;
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
    }

    public void Stop()
    {
        Process? process;

        lock (_gate)
        {
            _manualStop = true;
            _restartCts?.Cancel();
            _restartCts?.Dispose();
            _restartCts = null;
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
                Logs: [.. _logs]);
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

        TranscoderSettings? settings;
        string? outputDirectory;
        CancellationTokenSource restartCts;

        lock (_gate)
        {
            if (_manualStop || _currentSettings is null || !_currentSettings.AutoRestart)
            {
                return;
            }

            settings = _currentSettings;
            outputDirectory = _outputDirectory;
            _restartCts?.Cancel();
            _restartCts?.Dispose();
            _restartCts = new CancellationTokenSource();
            restartCts = _restartCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                AppendLog("auto-restart scheduled in 2 seconds");
                await Task.Delay(TimeSpan.FromSeconds(2), restartCts.Token);
                await StartAsync(settings!, outputDirectory!, restartCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppendLog($"auto-restart failed: {ex.Message}");
            }
        });
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
}
