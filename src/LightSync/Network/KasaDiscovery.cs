using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LightSync.Network;

internal static class KasaDiscovery
{
    private const int Port = 9999;

    public static async Task<IReadOnlyList<NetworkDevice>> DiscoverAsync(LanSubnet subnet, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, (IPAddress Host, JsonElement Info)>(StringComparer.OrdinalIgnoreCase);
        using var client = new UdpClient(new IPEndPoint(subnet.Local, 0));
        client.EnableBroadcast = true;
        var query = Encrypt("{\"system\":{\"get_sysinfo\":{}}}");
        await client.SendAsync(query, new IPEndPoint(subnet.Broadcast, Port), cancellationToken).ConfigureAwait(false);
        foreach (var host in subnet.Hosts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await client.SendAsync(query, new IPEndPoint(host, Port), cancellationToken).ConfigureAwait(false);
        }

        var deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (client.Available == 0)
            {
                await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                continue;
            }

            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SocketException)
            {
                break;
            }

            if (!TrySysInfo(result.Buffer, out var info))
            {
                continue;
            }

            var id = ReadString(info, "deviceId");
            if (string.IsNullOrWhiteSpace(id))
            {
                id = result.RemoteEndPoint.Address.ToString();
            }

            found[id] = (result.RemoteEndPoint.Address, info.Clone());
        }

        var devices = new List<NetworkDevice>(found.Count);
        foreach (var (id, seen) in found)
        {
            cancellationToken.ThrowIfCancellationRequested();
            devices.Add(await ToDeviceAsync(seen.Host, id, seen.Info, cancellationToken).ConfigureAwait(false));
        }

        return devices;
    }

    private static async Task<NetworkDevice> ToDeviceAsync(
        IPAddress host,
        string id,
        JsonElement info,
        CancellationToken cancellationToken)
    {
        var kind = KindOf(ReadString(info, "mic_type"));
        var alias = ReadString(info, "alias");
        var model = ReadString(info, "model");
        var on = kind == NetworkKind.Light ? ReadInt(info, "light_state", "on_off") : ReadInt(info, "relay_state");
        var meter = kind == NetworkKind.Plug
            ? await ReadMeterAsync(host, cancellationToken).ConfigureAwait(false)
            : null;

        return new NetworkDevice
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(alias) ? model ?? host.ToString() : alias,
            Host = host.ToString(),
            Transport = "Kasa",
            Kind = kind,
            Model = model,
            IsOn = on is null ? null : on != 0,
            Mode = kind == NetworkKind.Light ? LightMode(info) : null,
            Meter = meter,
        };
    }

    private static async Task<string?> ReadMeterAsync(IPAddress host, CancellationToken cancellationToken)
    {
        using var client = new UdpClient(0);
        var query = Encrypt("{\"emeter\":{\"get_realtime\":{}}}");
        await client.SendAsync(query, new IPEndPoint(host, Port), cancellationToken).ConfigureAwait(false);
        using var listen = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listen.CancelAfter(TimeSpan.FromMilliseconds(800));
        try
        {
            var result = await client.ReceiveAsync(listen.Token).ConfigureAwait(false);
            return FormatMeter(Decrypt(result.Buffer));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "sin medición";
        }
        catch (SocketException)
        {
            return "sin medición";
        }
    }

    internal static string? FormatMeter(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("emeter", out var emeter)
                || !emeter.TryGetProperty("get_realtime", out var realtime)
                || realtime.ValueKind != JsonValueKind.Object)
            {
                return "sin medición";
            }

            var watts = Scale(realtime, "power_mw", "power", 1000d);
            var volts = Scale(realtime, "voltage_mv", "voltage", 1000d);
            var amps = Scale(realtime, "current_ma", "current", 1000d);
            var wattHours = Scale(realtime, "total_wh", "total", 1d);
            if (watts is null && volts is null && amps is null && wattHours is null)
            {
                return "sin medición";
            }

            var parts = new List<string>();
            if (watts is not null)
            {
                parts.Add(watts >= 100 ? $"{watts:0} W" : $"{watts:0.0} W");
            }

            if (volts is not null)
            {
                parts.Add($"{volts:0.0} V");
            }

            if (amps is not null)
            {
                parts.Add(amps >= 1 ? $"{amps:0.00} A" : $"{amps * 1000:0} mA");
            }

            if (wattHours is not null)
            {
                parts.Add(wattHours >= 1000 ? $"{wattHours / 1000:0.00} kWh" : $"{wattHours:0} Wh");
            }

            return string.Join(" · ", parts);
        }
        catch (JsonException)
        {
            return "sin medición";
        }
    }

    private static double? Scale(JsonElement element, string milliName, string unitName, double milliDivisor)
    {
        if (TryNumber(element, milliName, out var milli))
        {
            return milli / milliDivisor;
        }

        return TryNumber(element, unitName, out var unit) ? unit : null;
    }

    private static bool TryNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString(), out value);
    }

    private static string? LightMode(JsonElement info)
    {
        if (!info.TryGetProperty("light_state", out var state))
        {
            return null;
        }

        var source = state.TryGetProperty("dft_on_state", out var fallback) ? fallback : state;
        var saturation = ReadInt(source, "saturation") ?? ReadInt(state, "saturation");
        var colorTemp = ReadInt(source, "color_temp") ?? ReadInt(state, "color_temp");
        if (saturation is > 0)
        {
            return "color";
        }

        return colorTemp is > 0 ? "blanco" : null;
    }

    private static NetworkKind KindOf(string? micType)
    {
        if (micType?.Contains("BULB", StringComparison.OrdinalIgnoreCase) == true)
        {
            return NetworkKind.Light;
        }

        if (micType?.Contains("PLUG", StringComparison.OrdinalIgnoreCase) == true)
        {
            return NetworkKind.Plug;
        }

        return NetworkKind.Other;
    }

    private static bool TrySysInfo(byte[] payload, out JsonElement info)
    {
        info = default;
        try
        {
            using var document = JsonDocument.Parse(Decrypt(payload));
            if (!document.RootElement.TryGetProperty("system", out var system)
                || !system.TryGetProperty("get_sysinfo", out var sysinfo)
                || sysinfo.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            info = sysinfo.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return property.TryGetInt32(out var value) ? value : null;
    }

    private static int? ReadInt(JsonElement element, string parent, string name)
    {
        return element.TryGetProperty(parent, out var child) ? ReadInt(child, name) : null;
    }

    private static readonly UdpClient HotSocket = new();
    private static readonly object HotGate = new();

    internal static void SendNow(string host, string json)
    {
        if (!IPAddress.TryParse(host, out var address))
        {
            throw new InvalidOperationException($"IP inválida: {host}");
        }

        var packet = Encrypt(json);
        lock (HotGate)
        {
            HotSocket.Send(packet, packet.Length, new IPEndPoint(address, Port));
        }
    }

    internal static async Task SendAsync(string host, string json, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(host, out var address))
        {
            throw new InvalidOperationException($"IP inválida: {host}");
        }

        var encrypted = Encrypt(json);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(address, Port, timeout.Token).ConfigureAwait(false);
            var packet = new byte[4 + encrypted.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(packet, encrypted.Length);
            encrypted.CopyTo(packet.AsSpan(4));
            await tcp.GetStream().WriteAsync(packet, timeout.Token).ConfigureAwait(false);
            return;
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
        }

        using var udp = new UdpClient(0);
        await udp.SendAsync(encrypted, new IPEndPoint(address, Port), cancellationToken).ConfigureAwait(false);
    }

    private static byte[] Encrypt(string json)
    {
        var plain = Encoding.UTF8.GetBytes(json);
        var key = 171;
        var result = new byte[plain.Length];
        for (var index = 0; index < plain.Length; index++)
        {
            key ^= plain[index];
            result[index] = (byte)key;
        }

        return result;
    }

    private static string Decrypt(byte[] data)
    {
        var key = 171;
        var result = new byte[data.Length];
        for (var index = 0; index < data.Length; index++)
        {
            result[index] = (byte)(key ^ data[index]);
            key = data[index];
        }

        return Encoding.UTF8.GetString(result);
    }
}
