using H265Player.Services;

var host = args.Length > 0 ? args[0] : "192.168.80.147";
var key = args.Length > 1 ? args[1] : "Home";

void Log(string message) => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {message}");

Log($"Sky Stream Home probe → {host}:8091 key={key}");
await using var client = new SkyStreamClient(host, log: Log);
try
{
    client.QueueUserKey(key, 500);
    await Task.Delay(TimeSpan.FromSeconds(4));
    Log("Key queued without waiting for a box reply.");
    return 0;
}
catch (Exception ex)
{
    Log($"FAILED {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException is not null)
    {
        Log($"Inner {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    }

    return 1;
}
