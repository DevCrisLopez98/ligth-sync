namespace LightSync.Network;

public sealed class NetworkScanResult
{
    public required IReadOnlyList<NetworkDevice> Devices { get; init; }

    public string? Error { get; init; }
}

public static class NetworkScanner
{
    public static async Task<NetworkScanResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        var subnets = LanSubnet.Current();
        if (subnets.Count == 0)
        {
            return new NetworkScanResult { Devices = [], Error = "Esta PC no tiene una red local activa." };
        }

        var found = new List<NetworkDevice>();
        string? error = null;
        foreach (var subnet in subnets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                found.AddRange(await KasaDiscovery.DiscoverAsync(subnet, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                error ??= exception.Message;
            }

            try
            {
                found.AddRange(await CozyLifeDiscovery.DiscoverAsync(subnet.Hosts, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                error ??= exception.Message;
            }
        }

        var devices = found
            .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(device => device.Kind)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new NetworkScanResult
        {
            Devices = devices,
            Error = devices.Length == 0 ? error : null,
        };
    }
}
