using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace H265Player.Services;

internal sealed class SkyStreamClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _host;
    private readonly int _port;
    private readonly Action<string>? _log;
    private readonly Channel<WorkItem> _work = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _runCts = new();
    private TcpClient? _tcp;
    private SslStream? _ssl;
    private WebSocket? _socket;
    private string _tid = Guid.NewGuid().ToString();
    private string _controllerNonce = Guid.NewGuid().ToString();
    private string? _authToken;
    private int? _bindId;
    private int _sequenceGeneration;
    private Task? _run;

    public SkyStreamClient(string host, int port = SkyStreamCredentials.Port, Action<string>? log = null)
    {
        _host = host;
        _port = port;
        _log = log;
    }

    public string? DeviceName { get; private set; }

    public bool IsBound => _socket is { State: WebSocketState.Open } && _bindId is not null && !string.IsNullOrWhiteSpace(_authToken);

    public void Start()
    {
        _run ??= Task.Run(() => RunAsync(_runCts.Token));
    }

    public void Warm()
    {
        Start();
        _work.Writer.TryWrite(WorkItem.Connect);
    }

    public void QueueUserKey(string key, int settleMs)
    {
        Start();
        Interlocked.Increment(ref _sequenceGeneration);
        _work.Writer.TryWrite(WorkItem.UserKey(key, Math.Max(0, settleMs)));
    }

    public int BeginSequence()
    {
        Start();
        return Interlocked.Increment(ref _sequenceGeneration);
    }

    public void QueueSequenceStroke(int generation, string? key, int settleMs)
    {
        Start();
        _work.Writer.TryWrite(WorkItem.SequenceStroke(generation, key, Math.Max(0, settleMs)));
    }

    public async ValueTask DisposeAsync()
    {
        _work.Writer.TryComplete();
        try
        {
            _runCts.Cancel();
        }
        catch
        {
        }

        CloseSocket();
        if (_run is not null)
        {
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }

        _runCts.Dispose();
        _tcp?.Dispose();
        _tcp = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _work.Reader.ReadAllAsync(cancellationToken))
            {
                if (item.IsSequence && item.Generation != Volatile.Read(ref _sequenceGeneration))
                {
                    continue;
                }

                try
                {
                    await EnsureBoundAsync(cancellationToken);
                    if (!string.IsNullOrEmpty(item.KeyName))
                    {
                        await SendKeyNowAsync(item.KeyName, cancellationToken);
                    }

                    if (item.SettleMs > 0)
                    {
                        await Task.Delay(item.SettleMs, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log($"Send failed: {ex.Message}. Reconnecting.");
                    CloseSocket();
                    if (string.IsNullOrEmpty(item.KeyName) || IsTcpUnreachable(ex))
                    {
                        continue;
                    }

                    try
                    {
                        await EnsureBoundAsync(cancellationToken);
                        await SendKeyNowAsync(item.KeyName, cancellationToken);
                    }
                    catch (Exception retry)
                    {
                        Log($"Retry failed: {retry.Message}");
                        CloseSocket();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"Key worker stopped: {ex.Message}");
        }
    }

    private async Task EnsureBoundAsync(CancellationToken cancellationToken)
    {
        if (IsBound)
        {
            return;
        }

        CloseSocket();
        await ConnectAndBindAsync(cancellationToken);
    }

    private async Task ConnectAndBindAsync(CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken);
        Log("Sending Pair Request.");
        var pair = await PairAsync(cancellationToken);
        var pairingCode = pair.GetString("pairingcode");
        var stbNonce = pair.GetString("stbnonce");
        if (string.IsNullOrEmpty(pairingCode) || string.IsNullOrEmpty(stbNonce))
        {
            throw new InvalidOperationException("Sky Stream pair response did not include pairingcode/stbnonce.");
        }

        DeviceName = pair.GetString("name");
        Log(string.IsNullOrWhiteSpace(DeviceName) ? "Paired." : $"Paired with {DeviceName}.");
        Log("Sending Bind Request.");
        await BindAsync(pairingCode, stbNonce, cancellationToken);
        Log($"Bound bind_id={_bindId}. Keys are queued and sent without waiting for a box reply.");
        await Task.Delay(400, cancellationToken);
    }

    private async Task SendKeyNowAsync(string key, CancellationToken cancellationToken)
    {
        if (!IsBound)
        {
            throw new InvalidOperationException("Sky Stream session is not bound.");
        }

        await SendJsonAsync(new Dictionary<string, object?>
        {
            ["command_name"] = "Key Command Request",
            ["tid"] = Guid.NewGuid().ToString(),
            ["authtoken"] = _authToken,
            ["bind_id"] = _bindId,
            ["cmd"] = "keyatomic",
            ["key"] = key
        }, cancellationToken);
        Log($"Sent key {key}.");
    }

    private void CloseSocket()
    {
        try
        {
            _socket?.Abort();
        }
        catch
        {
        }

        _socket?.Dispose();
        try
        {
            _ssl?.Dispose();
        }
        catch
        {
        }

        _tcp?.Dispose();
        _socket = null;
        _ssl = null;
        _tcp = null;
        _bindId = null;
        _authToken = null;
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(_host, out var address) || !PrivateIpv4.IsPrivateLike(address))
        {
            throw new InvalidOperationException("Sky Stream control is limited to private IPv4 addresses.");
        }

        _tcp = new TcpClient();
        Log($"TCP connect {_host}:{_port} ...");
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(8));
            await _tcp.ConnectAsync(address, _port, connectCts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException)
        {
            _tcp.Dispose();
            _tcp = null;
            throw new InvalidOperationException(
                $"TCP {_host}:{_port} did not connect in 8s ({ex.GetType().Name}: {ex.Message}). " +
                "nmap 8091/tcp filtered means the box can ping while Sky Remote is still down. If you are already on the same LAN, wake the puck with the Sky remote until 8091 is open. A Tailscale route that only forwards ping looks the same.",
                ex);
        }

        Log("TCP connected. Starting mTLS (SNI sky.xcal.tv, ALPN http/1.1).");
        _ssl = new SslStream(_tcp.GetStream(), leaveInnerStreamOpen: true);
        var sslOptions = new SslClientAuthenticationOptions
        {
            TargetHost = SkyStreamCredentials.ServerName,
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
            CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            ClientCertificateContext = SkyStreamCredentials.CertificateContext,
            ApplicationProtocols = [SslApplicationProtocol.Http11]
        };
        try
        {
            await _ssl.AuthenticateAsClientAsync(sslOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"TLS/mTLS to {_host}:{_port} failed: {ex.Message}", ex);
        }

        Log($"TLS negotiated {_ssl.NegotiatedCipherSuite}. GET /iptarget WebSocket upgrade.");
        var wsKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var upgrade =
            $"GET /iptarget HTTP/1.1\r\n" +
            $"Host: {SkyStreamCredentials.ServerName}:{_port}\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {wsKey}\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            $"Origin: https://{_host}:{_port}/\r\n" +
            "User-Agent: Dart/3.9 (dart:io)\r\n" +
            "Cache-Control: no-cache\r\n" +
            "\r\n";
        await _ssl.WriteAsync(Encoding.ASCII.GetBytes(upgrade), cancellationToken);
        await _ssl.FlushAsync(cancellationToken);

        var response = await ReadHttpHeadersAsync(_ssl, cancellationToken);
        var statusLine = response.Split('\n')[0].Trim();
        Log($"Upgrade response: {statusLine}");
        if (!statusLine.Contains("101", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Sky Stream WebSocket upgrade failed: {statusLine}");
        }

        _socket = WebSocket.CreateFromStream(_ssl, new WebSocketCreationOptions
        {
            IsServer = false,
            KeepAliveInterval = Timeout.InfiniteTimeSpan
        });
        Log("WebSocket open.");
    }

    private void Log(string message) => _log?.Invoke(message);

    private async Task<JsonElement> PairAsync(CancellationToken cancellationToken)
    {
        _tid = Guid.NewGuid().ToString();
        _controllerNonce = Guid.NewGuid().ToString();
        await SendJsonAsync(new Dictionary<string, object?>
        {
            ["command_name"] = "Pair Request",
            ["tid"] = _tid,
            ["name"] = "Soft Remote",
            ["manufacturer"] = "Comcast",
            ["model"] = "IPRemote",
            ["controllernonce"] = _controllerNonce
        }, cancellationToken);

        var response = await ReceiveHandshakeAsync(cancellationToken);
        if (!response.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException($"Sky Stream pair failed: {response}");
        }

        return response;
    }

    private async Task BindAsync(string pairingCode, string stbNonce, CancellationToken cancellationToken)
    {
        _authToken = SkyStreamCredentials.ComputeAuthToken(pairingCode, _controllerNonce, stbNonce);
        await SendJsonAsync(new Dictionary<string, object?>
        {
            ["command_name"] = "Bind Request",
            ["tid"] = _tid,
            ["authtoken"] = _authToken
        }, cancellationToken);

        var response = await ReceiveHandshakeAsync(cancellationToken);
        if (!response.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException("Sky Stream bind failed. Reboot the box if it has locked out failed pairing attempts.");
        }

        if (!response.TryGetProperty("bind_id", out var bindId) || bindId.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException("Sky Stream bind response did not include bind_id.");
        }

        _bindId = bindId.GetInt32();
    }

    private async Task SendJsonAsync(object payload, CancellationToken cancellationToken)
    {
        if (_socket is not { State: WebSocketState.Open })
        {
            throw new InvalidOperationException("Sky Stream WebSocket is not open.");
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        var send = _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        var timeout = Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        var completed = await Task.WhenAny(send, timeout);
        if (completed != send)
        {
            CloseSocket();
            try
            {
                await send;
            }
            catch
            {
            }

            throw new TimeoutException("Sky Stream send timed out after 2s.");
        }

        await send;
    }

    private async Task<JsonElement> ReceiveHandshakeAsync(CancellationToken cancellationToken)
    {
        var receive = ReceiveJsonAsync(CancellationToken.None);
        var timeout = Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
        var completed = await Task.WhenAny(receive, timeout);
        if (completed != receive)
        {
            CloseSocket();
            try
            {
                await receive;
            }
            catch
            {
            }

            throw new TimeoutException("Sky Stream pair/bind did not reply in 8s.");
        }

        return await receive;
    }

    private async Task<JsonElement> ReceiveJsonAsync(CancellationToken cancellationToken)
    {
        if (_socket is not { State: WebSocketState.Open })
        {
            throw new InvalidOperationException("Sky Stream WebSocket is not open.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var result = await _socket.ReceiveAsync(chunk, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Sky Stream closed the WebSocket.");
            }

            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static async Task<string> ReadHttpHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 16_384)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0)
            {
                throw new InvalidOperationException("Sky Stream closed the socket during the WebSocket upgrade.");
            }

            buffer.WriteByte(one[0]);
            if (buffer.Length >= 4)
            {
                var data = buffer.GetBuffer();
                var end = (int)buffer.Length;
                if (data[end - 4] == '\r' && data[end - 3] == '\n' && data[end - 2] == '\r' && data[end - 1] == '\n')
                {
                    return Encoding.ASCII.GetString(data, 0, end);
                }
            }
        }

        throw new InvalidOperationException("Sky Stream HTTP upgrade response was too large.");
    }

    private static bool IsTcpUnreachable(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("did not connect", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("8091/tcp filtered", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct WorkItem(bool IsSequence, int Generation, string? KeyName, int SettleMs)
    {
        public static WorkItem Connect => new(false, 0, null, 0);

        public static WorkItem UserKey(string key, int settleMs) => new(false, 0, key, settleMs);

        public static WorkItem SequenceStroke(int generation, string? key, int settleMs) =>
            new(true, generation, key, settleMs);
    }
}

internal static class JsonElementExtensions
{
    public static string? GetString(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
