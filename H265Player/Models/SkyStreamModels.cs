namespace H265Player.Models;

public sealed record SkyStreamScanResponse(
    IReadOnlyList<string> Networks,
    IReadOnlyList<string> SkippedNetworks,
    IReadOnlyList<SkyStreamDevice> Devices,
    DateTimeOffset? LastScanAt);

public sealed record SkyStreamDevice(
    string Host,
    string HostName,
    string DisplayName,
    string MacAddress,
    int Port,
    bool Asleep = false);

public static class SkyRemoteKinds
{
    public const string SkyQ = "skyq";
    public const string SkyStream = "skystream";

    public static string Normalize(string? value) =>
        string.Equals(value, SkyStream, StringComparison.OrdinalIgnoreCase)
            ? SkyStream
            : SkyQ;
}
