using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace H265Player.Services;

internal static class PrivateIpv4
{
    public static bool IsPrivate(IPAddress address)
    {
        var ipv4 = address.MapToIPv4();
        var bytes = ipv4.GetAddressBytes();
        return bytes.Length == 4 && (
            bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168));
    }

    public static InterfaceScan GetInterfaces(ILogger? logger = null)
    {
        var interfaces = new List<PrivateInterface>();
        var messages = new List<string>();
        NetworkInterface[] nics;

        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException ex)
        {
            logger?.LogWarning(ex, "Could not enumerate local network interfaces.");
            string? fallbackError = null;
            if (OperatingSystem.IsLinux() && TryGetFromLinuxIp(out var fallback, out fallbackError))
            {
                return new InterfaceScan(fallback, messages);
            }

            if (!string.IsNullOrWhiteSpace(fallbackError))
            {
                messages.Add(fallbackError);
            }

            messages.Add($"Unable to enumerate local network interfaces: {ex.Message}.");
            return new InterfaceScan(interfaces, messages);
        }
        catch (Exception ex)
        {
            messages.Add($"Unable to inspect local network interfaces: {ex.Message}.");
            return new InterfaceScan(interfaces, messages);
        }

        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask is null ||
                    !IsPrivate(unicast.Address))
                {
                    continue;
                }

                var prefix = PrefixLength(unicast.IPv4Mask);
                var network = NetworkAddress(unicast.Address, prefix);
                if (interfaces.All(existing => !existing.Address.Equals(unicast.Address)))
                {
                    interfaces.Add(new PrivateInterface(unicast.Address, network, prefix, $"{network}/{prefix}"));
                }
            }
        }

        return new InterfaceScan(interfaces, messages);
    }

    public static IEnumerable<IPAddress> EnumerateHosts(PrivateInterface iface)
    {
        if (iface.PrefixLength is < 22 or > 30)
        {
            yield break;
        }

        var hostCount = (1 << (32 - iface.PrefixLength)) - 2;
        var networkValue = ToUInt32(iface.Network);
        for (var i = 1; i <= hostCount; i++)
        {
            yield return FromUInt32(networkValue + (uint)i);
        }
    }

    private static bool TryGetFromLinuxIp(out List<PrivateInterface> interfaces, out string? errorMessage)
    {
        interfaces = [];
        errorMessage = null;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ip",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-o");
            process.StartInfo.ArgumentList.Add("-4");
            process.StartInfo.ArgumentList.Add("addr");
            process.StartInfo.ArgumentList.Add("show");
            process.StartInfo.ArgumentList.Add("up");
            if (!process.Start())
            {
                errorMessage = "Failed to start the Linux 'ip' command.";
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                errorMessage = "Timed out while reading interface data from the Linux 'ip' command.";
                return false;
            }

            if (process.ExitCode != 0)
            {
                errorMessage = string.IsNullOrWhiteSpace(error)
                    ? $"The Linux 'ip' command exited with code {process.ExitCode}."
                    : error.Trim();
                return false;
            }

            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length < 4 || !string.Equals(tokens[2], "inet", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var cidrParts = tokens[3].Split('/', StringSplitOptions.TrimEntries);
                if (cidrParts.Length != 2 ||
                    !IPAddress.TryParse(cidrParts[0], out var address) ||
                    address.AddressFamily != AddressFamily.InterNetwork ||
                    !int.TryParse(cidrParts[1], out var prefix) ||
                    prefix is < 0 or > 32 ||
                    !IsPrivate(address))
                {
                    continue;
                }

                var network = NetworkAddress(address, prefix);
                if (interfaces.All(existing => !existing.Address.Equals(address)))
                {
                    interfaces.Add(new PrivateInterface(address, network, prefix, $"{network}/{prefix}"));
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static int PrefixLength(IPAddress mask)
    {
        var prefix = 0;
        foreach (var maskByte in mask.MapToIPv4().GetAddressBytes())
        {
            var value = maskByte;
            while (value != 0)
            {
                prefix += value & 1;
                value >>= 1;
            }
        }

        return prefix;
    }

    private static IPAddress NetworkAddress(IPAddress address, int prefixLength)
    {
        var value = ToUInt32(address);
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return FromUInt32(value & mask);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.MapToIPv4().GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value) =>
        new(
        [
            (byte)((value >> 24) & 0xff),
            (byte)((value >> 16) & 0xff),
            (byte)((value >> 8) & 0xff),
            (byte)(value & 0xff)
        ]);

    internal sealed record PrivateInterface(IPAddress Address, IPAddress Network, int PrefixLength, string Cidr);

    internal sealed record InterfaceScan(IReadOnlyList<PrivateInterface> Interfaces, IReadOnlyList<string> Messages);
}
