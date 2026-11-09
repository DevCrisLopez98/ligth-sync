using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LightSync.Network;

internal static class CozyLifeDiscovery
{
    private const int Port = 5555;

    public static async Task<IReadOnlyList<NetworkDevice>> DiscoverAsync(
        IReadOnlyList<IPAddress> hosts,
        CancellationToken cancellationToken)
    {
        var found = new ConcurrentBag<NetworkDevice>();
        await Parallel.ForEachAsync(
            hosts,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 32,
                CancellationToken = cancellationToken,
            },
            async (host, token) =>
            {
                var device = await ProbeAsync(host, token).ConfigureAwait(false);
                if (device is not null)
                {
                    found.Add(device);
                }
            }).ConfigureAwait(false);

        return found.ToArray();
    }

    private static async Task<NetworkDevice?> ProbeAsync(IPAddress host, CancellationToken cancellationToken)
    {
        var info = await ExchangeAsync(host, 0, "{}", cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            return null;
        }

        var state = await ExchangeAsync(host, 2, "{\"attr\":[0]}", cancellationToken).ConfigureAwait(false);
        return ToDevice(host, info.Value, state);
    }

    private static NetworkDevice? ToDevice(IPAddress host, JsonElement info, JsonElement? state)
    {
        var message = Message(info);
        if (message is null)
        {
            return null;
        }

        var id = ReadString(message.Value, "did");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var kind = ReadString(message.Value, "dtp") == "02" ? NetworkKind.Light : NetworkKind.Other;
        var data = state is null ? null : Data(state.Value);
        int? power = data is null ? null : ReadInt(data.Value, "1");
        var on = power is null ? (bool?)null : power != 0;
        return new NetworkDevice
        {
            Id = id,
            Name = host.ToString(),
            Host = ReadString(message.Value, "ip") ?? host.ToString(),
            Transport = "CozyLife",
            Kind = kind,
            Model = id,
            IsOn = on,
            Mode = kind == NetworkKind.Light ? LightMode(data, on) : null,
        };
    }

    private static string? LightMode(JsonElement? data, bool? on)
    {
        if (on != true || data is null)
        {
            return null;
        }

        var hue = ReadInt(data.Value, "5");
        var saturation = ReadInt(data.Value, "6");
        var brightness = ReadInt(data.Value, "4");
        var mode = hue is 65535 or null || saturation is 65535 or null ? "blanco" : "color";
        if (brightness is null)
        {
            return mode;
        }

        var percent = brightness > 100 ? brightness / 10 : brightness;
        return $"{mode} · {percent} %";
    }

    internal static void SendNow(string host, string messageJson)
    {
        CozyLink.Shared(host).Send(messageJson);
    }

    internal static async Task SetAsync(string host, string dataJson, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(host, out var address))
        {
            throw new InvalidOperationException($"IP inválida: {host}");
        }

        var response = await ExchangeAsync(address, 3, dataJson, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            throw new System.IO.IOException($"El foco {host} no aceptó el comando.");
        }
    }

    private static async Task<JsonElement?> ExchangeAsync(
        IPAddress host,
        int command,
        string message,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(TimeSpan.FromMilliseconds(1000));
        try
        {
            await client.ConnectAsync(host, Port, connect.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }

        var serial = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var frame = Encoding.UTF8.GetBytes($"{{\"pv\":0,\"cmd\":{command},\"sn\":\"{serial}\",\"msg\":{message}}}\r\n");
        var stream = client.GetStream();
        stream.ReadTimeout = 1500;
        stream.WriteTimeout = 1500;
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[2048];
        var size = 0;
        using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        read.CancelAfter(TimeSpan.FromMilliseconds(1500));
        while (size < buffer.Length && !buffer.AsSpan(0, size).Contains((byte)'\n'))
        {
            int count;
            try
            {
                count = await stream.ReadAsync(buffer.AsMemory(size), read.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (System.IO.IOException)
            {
                return null;
            }

            if (count == 0)
            {
                break;
            }

            size += count;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, size).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("res", out var result)
                && result.ValueKind == JsonValueKind.Number
                && result.TryGetInt32(out var code)
                && code != 0)
            {
                return null;
            }

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Message(JsonElement root)
    {
        return root.TryGetProperty("msg", out var message) && message.ValueKind == JsonValueKind.Object
            ? message
            : null;
    }

    private static JsonElement? Data(JsonElement root)
    {
        var message = Message(root);
        return message is not null && message.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data
            : null;
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
}
