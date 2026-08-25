using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace H265Player.Services;

internal static class PrivateIpv4
{
    public static bool IsPrivate(IPAddress address) => IsPrivateLike(address);

    public static bool IsPrivateLike(IPAddress address)
    {
        var ipv4 = address.MapToIPv4();
        var bytes = ipv4.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
    }

    public static IReadOnlyList<string> DetectedCidrs() =>
        GetInterfaces().Interfaces
            .Select(item => item.Cidr)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyList<string> NormalizeScanNetworks(IEnumerable<string>? values)
    {
        if (!TryNormalizeScanNetworks(values, out var networks, out var errors))
        {
            throw new InvalidOperationException(string.Join(' ', errors));
        }

        return networks;
    }

    public static bool TryNormalizeScanNetworks(
        IEnumerable<string>? values,
        out IReadOnlyList<string> networks,
        out IReadOnlyList<string> errors)
    {
        var normalized = new List<string>();
        var problems = new List<string>();
        foreach (var raw in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!TryParseCidr(raw, out var parsed, out var error))
            {
                problems.Add(error ?? $"'{raw.Trim()}' is not a valid IPv4 subnet.");
                continue;
            }

            if (!normalized.Contains(parsed.Cidr, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(parsed.Cidr);
            }
        }

        networks = normalized.Order(StringComparer.OrdinalIgnoreCase).ToList();
        errors = problems;
        return problems.Count == 0;
    }

    public static bool TryParseCidr(string value, out PrivateInterface parsed, out string? error)
    {
        parsed = null!;
        error = null;
        var text = value.Trim();
        var parts = text.Split('/', 2, StringSplitOptions.TrimEntries);
        if (!IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            error = $"'{text}' must start with an IPv4 address.";
            return false;
        }

        var prefix = 32;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32))
        {
            error = $"'{text}' must use a prefix between 0 and 32.";
            return false;
        }

        if (prefix < 22)
        {
            error = $"'{text}' is too large to scan. Use a prefix of /22 to /32, or a single host such as 10.8.0.10.";
            return false;
        }

        if (!IsPrivateLike(address))
        {
            error = $"'{text}' must be a private, VPN, or Tailscale IPv4 subnet.";
            return false;
        }

        var network = NetworkAddress(address, prefix);
        parsed = new PrivateInterface(address, network, prefix, $"{network}/{prefix}");
        return true;
    }

    public static IReadOnlyList<PrivateInterface> ExtraScanTargets(IEnumerable<string>? values)
    {
        var result = new List<PrivateInterface>();
        foreach (var value in values ?? [])
        {
            if (TryParseCidr(value, out var parsed, out _))
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    public static IReadOnlyList<PrivateInterface> ExtraProbeTargets(
        IReadOnlyList<PrivateInterface> local,
        IReadOnlyList<PrivateInterface> extras) =>
        extras
            .Where(extra => extra.PrefixLength == 32 ||
                            local.All(item => !string.Equals(item.Cidr, extra.Cidr, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    public static bool Contains(PrivateInterface network, IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var mask = network.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - network.PrefixLength);
        return (ToUInt32(address) & mask) == ToUInt32(network.Network);
    }

    public static bool IsAllowedTarget(IPAddress address, IEnumerable<string>? extraScanNetworks)
    {
        if (IsPrivateLike(address))
        {
            return true;
        }

        return ExtraScanTargets(extraScanNetworks).Any(network => Contains(network, address));
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
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
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
        if (iface.PrefixLength is < 22 or > 32)
        {
            yield break;
        }

        var networkValue = ToUInt32(iface.Network);
        if (iface.PrefixLength == 32)
        {
            yield return iface.Network;
            yield break;
        }

        var hostCount = (1 << (32 - iface.PrefixLength)) - 2;
        if (hostCount <= 0)
        {
            yield return FromUInt32(networkValue);
            yield return FromUInt32(networkValue + 1);
            yield break;
        }

        for (var i = 1; i <= hostCount; i++)
        {
            yield return FromUInt32(networkValue + (uint)i);
        }
    }

    public static async Task<IReadOnlyList<IPAddress>> ProbeOpenTcpAsync(
        IEnumerable<IPAddress> hosts,
        int port,
        TimeSpan timeout,
        int concurrency,
        CancellationToken cancellationToken)
    {
        var unique = hosts.Distinct().ToList();
        if (unique.Count == 0)
        {
            return [];
        }

        var found = new ConcurrentBag<IPAddress>();
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = unique.Select(async address =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);
                await client.ConnectAsync(address, port, cts.Token);
                found.Add(address);
            }
            catch
            {
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return found.ToList();
    }

    public static async Task<bool> WaitForOpenTcpAsync(
        IPAddress address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var attempt = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
            var open = await ProbeOpenTcpAsync([address], port, attempt, 1, cancellationToken);
            if (open.Count > 0)
            {
                return true;
            }

            var delay = TimeSpan.FromMilliseconds(400);
            if (DateTime.UtcNow + delay >= deadline)
            {
                break;
            }

            await Task.Delay(delay, cancellationToken);
        }

        return false;
    }

    public static IPAddress DirectedBroadcast(IPAddress address, int prefixLength)
    {
        var value = ToUInt32(address);
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return FromUInt32(value | ~mask);
    }

    public static async Task<bool> IsReachableAsync(IPAddress address, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, timeoutMilliseconds);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
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
