using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LightSync.Network;

internal sealed class LanSubnet
{
    public required IPAddress Local { get; init; }

    public required IPAddress Broadcast { get; init; }

    public required IReadOnlyList<IPAddress> Hosts { get; init; }

    public static IReadOnlyList<LanSubnet> Current()
    {
        var found = new List<LanSubnet>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address))
                {
                    continue;
                }

                var prefix = unicast.PrefixLength;
                if (prefix is 0 or > 30)
                {
                    continue;
                }

                // A wider mask can be a corporate LAN. Stay inside the local /24.
                var scanPrefix = Math.Max(prefix, 24);
                var ip = ToUInt32(unicast.Address);
                var mask = scanPrefix == 0 ? 0u : uint.MaxValue << (32 - scanPrefix);
                var network = ip & mask;
                var broadcast = network | ~mask;
                var hosts = new List<IPAddress>();
                for (var host = network + 1; host < broadcast; host++)
                {
                    if (host == ip)
                    {
                        continue;
                    }

                    hosts.Add(FromUInt32(host));
                }

                found.Add(new LanSubnet
                {
                    Local = unicast.Address,
                    Broadcast = FromUInt32(broadcast),
                    Hosts = hosts,
                });
            }
        }

        return found;
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value)
    {
        return new IPAddress(
        [
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value,
        ]);
    }
}
