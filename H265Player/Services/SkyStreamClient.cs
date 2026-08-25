using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace H265Player.Services;

internal sealed class SkyStreamClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _host;
    private readonly int _port;
    private TcpClient? _tcp;
    private SslStream? _ssl;
    private WebSocket? _socket;
    private string _tid = Guid.NewGuid().ToString();
    private string _controllerNonce = Guid.NewGuid().ToString();
    private string? _authToken;
    private int? _bindId;

    public SkyStreamClient(string host, int port = SkyStreamCredentials.Port)
    {
        _host = host;
        _port = port;
    }

    public string? DeviceName { get; private set; }

    public bool IsBound => _socket is { State: WebSocketState.Open } && _bindId is not null && !string.IsNullOrWhiteSpace(_authToken);

    public async Task ConnectAndBindAsync(CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken);
        var pair = await PairAsync(cancellationToken);
        var pairingCode = pair.GetString("pairingcode");
        var stbNonce = pair.GetString("stbnonce");
        if (string.IsNullOrEmpty(pairingCode) || string.IsNullOrEmpty(stbNonce))
        {
            throw new InvalidOperationException("Sky Stream pair response did not include pairingcode/stbnonce.");
        }

        DeviceName = pair.GetString("name");
        await BindAsync(pairingCode, stbNonce, cancellationToken);
    }

    public async Task<JsonElement> SendKeyAsync(string key, CancellationToken cancellationToken)
    {
        if (!IsBound)
        {
            throw new InvalidOperationException("Sky Stream session is not bound.");
        }

        await SendJsonAsync(new Dictionary<string, object?>
        {
            ["command_name"] = "Key Command Request",
            ["tid"] = _tid,
            ["authtoken"] = _authToken,
            ["bind_id"] = _bindId,
            ["cmd"] = "keyatomic",
            ["key"] = key
        }, cancellationToken);

        return await ReceiveJsonAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket is { State: WebSocketState.Open })
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            }
        }
        catch
        {
        }

        _socket?.Dispose();
        if (_ssl is not null)
        {
            await _ssl.DisposeAsync();
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
        if (!IPAddress.TryParse(_host, out var address) || !PrivateIpv4.IsPrivate(address))
        {
            throw new InvalidOperationException("Sky Stream control is limited to private IPv4 addresses.");
        }

        _tcp = new TcpClient();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(8));
        await _tcp.ConnectAsync(address, _port, connectCts.Token);

        _ssl = new SslStream(_tcp.GetStream(), leaveInnerStreamOpen: true, static (_, _, _, _) => true);
        var sslOptions = new SslClientAuthenticationOptions
        {
            TargetHost = SkyStreamCredentials.ServerName,
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
            CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            ClientCertificateContext = SkyStreamCredentials.CertificateContext,
            ApplicationProtocols = [SslApplicationProtocol.Http11]
        };
        await _ssl.AuthenticateAsClientAsync(sslOptions, cancellationToken);

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
        if (!statusLine.Contains("101", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Sky Stream WebSocket upgrade failed: {statusLine}");
        }

        _socket = WebSocket.CreateFromStream(_ssl, new WebSocketCreationOptions
        {
            IsServer = false,
            KeepAliveInterval = TimeSpan.FromSeconds(20)
        });
    }

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

        var response = await ReceiveJsonAsync(cancellationToken);
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

        var response = await ReceiveJsonAsync(cancellationToken);
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
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
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
}

internal static class JsonElementExtensions
{
    public static string? GetString(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
