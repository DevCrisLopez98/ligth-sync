using System.Diagnostics;
using System.Net;

namespace LightSync.Network;

internal static class ArpTable
{
    public static IReadOnlyList<IPAddress> Neighbors(LanSubnet subnet)
    {
        var known = subnet.Hosts.Select(host => host.ToString()).ToHashSet(StringComparer.Ordinal);
        var found = new List<IPAddress>();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "arp",
            Arguments = "-a",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
        {
            return found;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(2000);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !known.Contains(parts[0]) || !IPAddress.TryParse(parts[0], out var address))
            {
                continue;
            }

            if (parts[1].StartsWith("00-00-00-00-00-00", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            found.Add(address);
        }

        return found;
    }
}