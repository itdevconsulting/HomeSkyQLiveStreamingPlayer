using System.Net;
using Microsoft.AspNetCore.Http;

namespace H265Player.Services;

public sealed class TrustedNetworkService
{
    public bool IsTrustedRequest(HttpContext context) => IsTrustedAddress(GetClientAddress(context));

    public IPAddress? GetClientAddress(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is null)
        {
            return null;
        }

        if (TryGetForwardedAddress(context, remoteIp, out var forwarded))
        {
            return forwarded;
        }

        return remoteIp;
    }

    private static bool TryGetForwardedAddress(HttpContext context, IPAddress remoteIp, out IPAddress forwarded)
    {
        forwarded = IPAddress.None;
        if (!IsTrustedAddress(remoteIp))
        {
            return false;
        }

        var rawHeader = context.Request.Headers["X-Forwarded-For"].FirstOrDefault()
            ?? context.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
            ?? context.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawHeader))
        {
            return false;
        }

        var first = rawHeader.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return false;
        }

        if (!IPAddress.TryParse(first, out var parsed))
        {
            return false;
        }

        forwarded = parsed;
        return true;
    }

    public static bool IsTrustedAddress(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || IsUniqueLocalV6(address);
        }

        var ipv4 = address.MapToIPv4();
        var bytes = ipv4.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        return bytes[0] == 10
               || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
               || (bytes[0] == 192 && bytes[1] == 168)
               || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
    }

    private static bool IsUniqueLocalV6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC;
    }
}
