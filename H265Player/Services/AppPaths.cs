namespace H265Player.Services;

public static class AppPaths
{
    public static string DataDirectory { get; private set; } = AppContext.BaseDirectory;

    public static bool IsContainer { get; private set; }

    public static bool IsHomeAssistantAddOn { get; private set; }

    public static string KeysDirectory => Path.Combine(DataDirectory, "keys");

    public static void Initialize(string? contentRoot)
    {
        IsContainer = DetectContainer();
        IsHomeAssistantAddOn = DetectHomeAssistantAddOn();

        var configured = Environment.GetEnvironmentVariable("SKYQ_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            DataDirectory = Path.GetFullPath(configured);
        }
        else if (IsHomeAssistantAddOn || (IsContainer && Directory.Exists("/data")))
        {
            DataDirectory = "/data";
        }
        else
        {
            DataDirectory = string.IsNullOrWhiteSpace(contentRoot)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(contentRoot);
        }

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(Path.Combine(DataDirectory, "runtime", "live"));
        Directory.CreateDirectory(KeysDirectory);
    }

    public static string File(string fileName) => Path.Combine(DataDirectory, fileName);

    public static string Runtime(params string[] parts) =>
        Path.Combine(new[] { DataDirectory, "runtime" }.Concat(parts).ToArray());

    private static bool DetectContainer() =>
        System.IO.File.Exists("/.dockerenv") ||
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    private static bool DetectHomeAssistantAddOn() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN")) ||
        string.Equals(Environment.GetEnvironmentVariable("SKYQ_HOMEASSISTANT"), "true", StringComparison.OrdinalIgnoreCase);
}
