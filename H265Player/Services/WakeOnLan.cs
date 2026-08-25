using System.Net;
using System.Net.Sockets;

namespace H265Player.Services;

internal static class WakeOnLan
{
    public const int Port = 9;

    public static string NormalizeMac(string? value)
    {
        var hex = new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12)
        {
            throw new ArgumentException("MAC address must contain 12 hex digits.", nameof(value));
        }

        hex = hex.ToUpperInvariant();
        return string.Join(':', Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2)));
    }

    public static bool TryNormalizeMac(string? value, out string mac)
    {
        try
        {
            mac = NormalizeMac(value);
            return true;
        }
        catch
        {
            mac = string.Empty;
            return false;
        }
    }

    public static IReadOnlyList<string> Send(string macAddress, IPAddress? host = null, IPAddress? directedBroadcast = null)
    {
        var packet = BuildPacket(NormalizeMac(macAddress));
        var targets = new List<IPEndPoint>
        {
            new(IPAddress.Broadcast, Port)
        };

        if (host is not null && host.AddressFamily == AddressFamily.InterNetwork)
        {
            targets.Add(new IPEndPoint(host, Port));
            targets.Add(new IPEndPoint(PrivateIpv4.DirectedBroadcast(host, 24), Port));
        }

        if (directedBroadcast is not null && directedBroadcast.AddressFamily == AddressFamily.InterNetwork)
        {
            targets.Add(new IPEndPoint(directedBroadcast, Port));
        }

        var sent = new List<string>();
        foreach (var target in targets.DistinctBy(item => item.ToString()))
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            udp.Send(packet, packet.Length, target);
            sent.Add(target.ToString());
        }

        return sent;
    }

    private static byte[] BuildPacket(string macAddress)
    {
        var mac = Convert.FromHexString(new string(macAddress.Where(Uri.IsHexDigit).ToArray()));
        var packet = new byte[102];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++)
        {
            Buffer.BlockCopy(mac, 0, packet, 6 + (i * 6), 6);
        }

        return packet;
    }
}
