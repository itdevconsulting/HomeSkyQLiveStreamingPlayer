using System.Net;
using System.Net.Sockets;
using System.Text;

namespace H265Player.Services;

internal static class SkyStreamMdns
{
    private static readonly IPAddress MdnsAddress = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;
    private static readonly byte[] QueryPacket = BuildPtrQuery("_rdk-rics._tcp.local");

    public static async Task<IReadOnlyCollection<Discovered>> QueryAsync(
        IReadOnlyList<PrivateIpv4.PrivateInterface> interfaces,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, Discovered>(StringComparer.OrdinalIgnoreCase);
        var tasks = interfaces.Select(item => QueryInterfaceAsync(item.Address, cancellationToken));
        foreach (var group in await Task.WhenAll(tasks))
        {
            foreach (var device in group)
            {
                found[device.Host] = device;
            }
        }

        return found.Values;
    }

    public static async Task<IReadOnlyCollection<Discovered>> QueryHostsAsync(
        IEnumerable<IPAddress> hosts,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, Discovered>(StringComparer.OrdinalIgnoreCase);
        var tasks = hosts.Distinct().Select(host => QueryUnicastAsync(host, cancellationToken));
        foreach (var device in await Task.WhenAll(tasks))
        {
            if (device is not null)
            {
                found[device.Host] = device;
            }
        }

        return found.Values;
    }

    private static async Task<List<Discovered>> QueryInterfaceAsync(IPAddress localAddress, CancellationToken cancellationToken)
    {
        var results = new List<Discovered>();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(localAddress, 0));
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.MulticastLoopback = false;
            await udp.SendAsync(QueryPacket, QueryPacket.Length, new IPEndPoint(MdnsAddress, MdnsPort));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult response;
                try
                {
                    response = await udp.ReceiveAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }

                if (!PrivateIpv4.IsPrivateLike(response.RemoteEndPoint.Address))
                {
                    continue;
                }

                var parsed = Parse(response.Buffer, response.RemoteEndPoint.Address);
                if (parsed is not null)
                {
                    results.Add(parsed);
                }
            }
        }
        catch
        {
        }

        return results;
    }

    private static async Task<Discovered?> QueryUnicastAsync(IPAddress host, CancellationToken cancellationToken)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            await udp.SendAsync(QueryPacket, QueryPacket.Length, new IPEndPoint(host, MdnsPort));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var response = await udp.ReceiveAsync(timeout.Token);
            return Parse(response.Buffer, host);
        }
        catch
        {
            return null;
        }
    }

    private static Discovered? Parse(byte[] buffer, IPAddress source)
    {
        var text = Encoding.ASCII.GetString(buffer);
        if (text.IndexOf("_rdk-rics", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }

        var host = source.ToString();
        var name = TryReadName(buffer) ?? "Sky Stream";
        var mac = TryReadMac(text);
        return new Discovered(host, name, mac, SkyStreamCredentials.Port);
    }

    private static string? TryReadName(byte[] buffer)
    {
        try
        {
            var labels = new List<string>();
            var offset = 12;
            while (offset < buffer.Length)
            {
                var length = buffer[offset];
                if (length == 0)
                {
                    break;
                }

                if ((length & 0xC0) == 0xC0)
                {
                    break;
                }

                offset++;
                if (offset + length > buffer.Length)
                {
                    break;
                }

                labels.Add(Encoding.ASCII.GetString(buffer, offset, length));
                offset += length;
            }

            var joined = string.Join('.', labels);
            if (string.IsNullOrWhiteSpace(joined))
            {
                return null;
            }

            var first = joined.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(first) || first.StartsWith('_')
                ? null
                : first;
        }
        catch
        {
            return null;
        }
    }

    private static string TryReadMac(string text)
    {
        foreach (var key in new[] { "wol_mac=", "wowl_mac=" })
        {
            var index = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var start = index + key.Length;
            var end = start;
            while (end < text.Length && !char.IsControl(text[end]) && text[end] != '\0')
            {
                end++;
            }

            return text[start..end].Trim();
        }

        return string.Empty;
    }

    private static byte[] BuildPtrQuery(string name)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(1);
        stream.Write(new byte[6]);
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(12);
        stream.WriteByte(0);
        stream.WriteByte(1);
        return stream.ToArray();
    }

    internal sealed record Discovered(string Host, string Name, string MacAddress, int Port);
}
