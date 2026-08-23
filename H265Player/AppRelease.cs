namespace H265Player;

public static class AppRelease
{
    public static string Current { get; private set; } = "0.0.0.0.0";

    public static void Initialize(string contentRootPath)
    {
        Current = ReadVersionFile(contentRootPath)
            ?? ReadStampVersion(contentRootPath)
            ?? Current;
    }

    public static string FromTimestamp(DateTimeOffset timestamp)
    {
        var local = timestamp.ToLocalTime();
        return $"{local.Year}.{local.Month}.{local.Day}.{local.Hour}.{local.Minute}";
    }

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var parts = trimmed.Split('.');
        if (parts.Length != 5)
        {
            return null;
        }

        var numbers = new int[5];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]))
            {
                return null;
            }
        }

        return $"{numbers[0]}.{numbers[1]}.{numbers[2]}.{numbers[3]}.{numbers[4]}";
    }

    public static string? ReadVersionFile(string contentRootPath)
    {
        var path = Path.Combine(contentRootPath, "VERSION");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Normalize(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadStampVersion(string contentRootPath)
    {
        var path = Path.Combine(contentRootPath, "version.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("version", out var version))
            {
                return Normalize(version.GetString());
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
