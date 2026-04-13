namespace H265Player.Models;

public sealed record SkyQScanResponse(
    IReadOnlyList<string> Networks,
    IReadOnlyList<string> SkippedNetworks,
    IReadOnlyList<SkyQDevice> Devices,
    DateTimeOffset? LastScanAt);

public sealed record SkyQDevice(
    string Host,
    string HostName,
    string Manufacturer,
    string Model,
    string HardwareName,
    string SerialNumber,
    string DeviceType,
    bool Gateway,
    string WakeReason);

public sealed record SkyQCommandRequest(string Host, string Command);

public sealed record SkyQCommandResult(
    bool Success,
    string Host,
    string Command,
    string Message,
    IReadOnlyList<string> Logs);
