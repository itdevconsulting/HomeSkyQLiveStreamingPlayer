namespace H265Player.Services;

public static class FfmpegPathResolver
{
    public static string ResolveOrThrow(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            throw new FileNotFoundException("FFmpeg executable was not found. Run setup and configure ffmpeg.exe.");
        }

        if (Path.IsPathRooted(ffmpegPath))
        {
            if (!File.Exists(ffmpegPath))
            {
                throw new FileNotFoundException("FFmpeg executable was not found.", ffmpegPath);
            }

            return ffmpegPath;
        }

        var searchPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), ffmpegPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (!candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var windowsCandidate = candidate + ".exe";
                if (File.Exists(windowsCandidate))
                {
                    return windowsCandidate;
                }
            }
        }

        throw new FileNotFoundException("FFmpeg executable was not found. Enter a full path to ffmpeg.exe.", ffmpegPath);
    }
}

