using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace LightSync.OpenRgb;

public sealed class OpenRgbClient : IDisposable
{
    public const int ProtocolVersion = 6;
    public const int DefaultStripLeds = 120;

    private const int PacketAck = 10;
    private const int PacketControllerCount = 0;
    private const int PacketControllerData = 1;
    private const int PacketProtocol = 40;
    private const int PacketClientName = 50;
    private const int PacketResizeZone = 1000;
    private const int PacketUpdateLeds = 1050;
    private const int PacketCustomMode = 1100;
    private const int PacketUpdateMode = 1101;
    private const int PacketSaveMode = 1102;

    private readonly TcpClient _client = new();
    private NetworkStream? _stream;
    private bool _disposed;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client.Connected && _stream is not null)
        {
            return;
        }

        await _client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        _stream = _client.GetStream();
        var version = BinaryPrimitives.ReadUInt32LittleEndian(
            await RequestAsync(0, PacketProtocol, BitConverter.GetBytes(ProtocolVersion), PacketProtocol, cancellationToken));
        if (version < ProtocolVersion)
        {
            throw new InvalidOperationException($"OpenRGB protocol {version} is older than {ProtocolVersion}.");
        }

        Send(0, PacketClientName, Encoding.ASCII.GetBytes("LightSync\0"));
        Drain();
    }

    public async Task<IReadOnlyList<LightingDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var devices = await ReadDevicesAsync(cancellationToken);
            if (devices.Count > 0)
            {
                return devices;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return [];
    }

    public async Task PrepareAsync(IReadOnlyList<LightingDevice> devices, CancellationToken cancellationToken)
    {
        foreach (var device in devices)
        {
            for (var zoneIndex = 0; zoneIndex < device.Zones.Count; zoneIndex++)
            {
                var zone = device.Zones[zoneIndex];
                var target = Math.Min(DefaultStripLeds, zone.LedMax);
                if (zone.Name.Contains("Addressable", StringComparison.Ordinal) && zone.LedCount != target && target > 1)
                {
                    var payload = new byte[8];
                    BinaryPrimitives.WriteInt32LittleEndian(payload, zoneIndex);
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), target);
                    Send(device.Id, PacketResizeZone, payload);
                }
            }
        }

        Drain();
        if (devices.Any(device => device.Zones.Any(zone => zone.Name.Contains("Addressable", StringComparison.Ordinal) && zone.LedCount != Math.Min(DefaultStripLeds, zone.LedMax) && Math.Min(DefaultStripLeds, zone.LedMax) > 1)))
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        foreach (var device in await ReadDevicesAsync(cancellationToken).ConfigureAwait(false))
        {
            Send(device.Id, PacketCustomMode, []);
        }

        Drain();
    }

    public void UpdateLeds(uint deviceId, ReadOnlySpan<RgbColor> colors)
    {
        var payloadSize = 6 + (4 * colors.Length);
        var payload = new byte[payloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)payloadSize);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), (ushort)colors.Length);
        for (var index = 0; index < colors.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6 + (index * 4)), colors[index].Packed);
        }

        Send(deviceId, PacketUpdateLeds, payload);
    }

    public void DrainAcks()
    {
        if (_stream is null || _disposed)
        {
            return;
        }

        Drain();
    }

    public void EnterDirect(IReadOnlyList<LightingDevice> devices)
    {
        if (_stream is null || _disposed)
        {
            return;
        }

        DrainPending();
        foreach (var device in devices)
        {
            SendAndWaitAck(device.Id, PacketCustomMode, []);
        }
    }

    public void HoldColor(IReadOnlyList<LightingDevice> devices, RgbColor color)
    {
        if (_stream is null || _disposed)
        {
            return;
        }

        DrainPending();

        foreach (var device in devices)
        {
            var mode = LightingMode.SelectHold(device.Modes);
            if (mode is null)
            {
                continue;
            }

            if ((mode.Flags & LightingMode.PerLedColor) != 0 && device.LedCount > 0)
            {
                var colors = new RgbColor[device.LedCount];
                Array.Fill(colors, color);
                UpdateLeds(device.Id, colors);
                WaitAck();
            }

            var payload = EncodeMode(mode, color);
            SendAndWaitAck(device.Id, PacketUpdateMode, payload);
            if ((mode.Flags & LightingMode.ManualSave) != 0)
            {
                SendAndWaitAck(device.Id, PacketSaveMode, payload);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream?.Dispose();
        _client.Dispose();
    }

    private async Task<IReadOnlyList<LightingDevice>> ReadDevicesAsync(CancellationToken cancellationToken)
    {
        var countPayload = await RequestAsync(0, PacketControllerCount, [], PacketControllerCount, cancellationToken);
        var count = BinaryPrimitives.ReadInt32LittleEndian(countPayload);
        var devices = new List<LightingDevice>(count);
        for (var index = 0; index < count; index++)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(countPayload.AsSpan(4 + (index * 4)));
            var data = await RequestAsync(id, PacketControllerData, BitConverter.GetBytes(ProtocolVersion), PacketControllerData, cancellationToken);
            devices.Add(DeviceReader.Read(id, data));
        }

        return devices;
    }

    private async Task<byte[]> RequestAsync(uint deviceId, int packetId, byte[] payload, int expectedPacketId, CancellationToken cancellationToken)
    {
        Send(deviceId, packetId, payload);
        while (true)
        {
            var packet = await ReadPacketAsync(cancellationToken);
            if (packet.PacketId == PacketAck)
            {
                continue;
            }

            if (packet.PacketId == expectedPacketId)
            {
                return packet.Payload;
            }
        }
    }

    private void Send(uint deviceId, int packetId, byte[] payload)
    {
        var header = new byte[16 + payload.Length];
        Encoding.ASCII.GetBytes("ORGB").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), deviceId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(header, 16);
        Stream.Write(header);
    }

    private async Task<Packet> ReadPacketAsync(CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(16, cancellationToken);
        if (Encoding.ASCII.GetString(header, 0, 4) != "ORGB")
        {
            throw new InvalidOperationException("OpenRGB sent a packet without the ORGB header.");
        }

        var size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        return new Packet(BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)), await ReadExactAsync(size, cancellationToken));
    }

    private async Task<byte[]> ReadExactAsync(int size, CancellationToken cancellationToken)
    {
        var buffer = new byte[size];
        var read = 0;
        while (read < size)
        {
            var count = await Stream.ReadAsync(buffer.AsMemory(read, size - read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new IOException("OpenRGB closed the connection.");
            }

            read += count;
        }

        return buffer;
    }

    private byte[] EncodeMode(LightingMode mode, RgbColor color)
    {
        var nameBytes = Encoding.UTF8.GetBytes(mode.Name + "\0");
        var useSpecific = mode.ColorsMax > 0;
        var colorCount = useSpecific ? 1 : 0;
        var brightness = mode.Brightness;
        if ((mode.Flags & LightingMode.HasBrightness) != 0 && brightness == 0 && mode.BrightnessMax > 0)
        {
            brightness = mode.BrightnessMax;
        }

        var descriptionLength = 2 + nameBytes.Length + (11 * 4) + 2 + (colorCount * 4);
        var payload = new byte[8 + descriptionLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), mode.Index);

        var offset = 8;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), (ushort)nameBytes.Length);
        offset += 2;
        nameBytes.CopyTo(payload.AsSpan(offset));
        offset += nameBytes.Length;
        foreach (var value in new[]
        {
            mode.Flags,
            mode.SpeedMin,
            mode.SpeedMax,
            mode.BrightnessMin,
            mode.BrightnessMax,
            mode.ColorsMin,
            mode.ColorsMax,
            mode.Speed,
            brightness,
            mode.Direction,
            useSpecific ? LightingMode.ColorModeSpecific : mode.ColorMode,
        })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), value);
            offset += 4;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), (ushort)colorCount);
        offset += 2;
        if (useSpecific)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), color.Packed);
        }

        return payload;
    }

    private void SendAndWaitAck(uint deviceId, int packetId, byte[] payload)
    {
        Send(deviceId, packetId, payload);
        WaitAck();
    }

    private void DrainPending()
    {
        Stream.ReadTimeout = 80;
        try
        {
            while (true)
            {
                try
                {
                    _ = ReadPacketBlocking();
                }
                catch (IOException)
                {
                    return;
                }
            }
        }
        finally
        {
            Stream.ReadTimeout = Timeout.Infinite;
        }
    }

    private void WaitAck()
    {
        Stream.ReadTimeout = 8000;
        try
        {
            while (true)
            {
                var packet = ReadPacketBlocking();
                if (packet.PacketId == PacketAck)
                {
                    return;
                }
            }
        }
        finally
        {
            Stream.ReadTimeout = Timeout.Infinite;
        }
    }

    private Packet ReadPacketBlocking()
    {
        var header = new byte[16];
        Stream.ReadExactly(header);
        if (Encoding.ASCII.GetString(header, 0, 4) != "ORGB")
        {
            throw new InvalidOperationException("OpenRGB sent a packet without the ORGB header.");
        }

        var size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        var payload = size > 0 ? new byte[size] : [];
        if (size > 0)
        {
            Stream.ReadExactly(payload);
        }

        return new Packet(BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)), payload);
    }

    private void Drain()
    {
        while (Stream.DataAvailable)
        {
            var header = new byte[16];
            Stream.ReadExactly(header);
            var size = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            if (size > 0)
            {
                var payload = new byte[size];
                Stream.ReadExactly(payload);
            }
        }
    }

    private NetworkStream Stream => _stream ?? throw new InvalidOperationException("OpenRGB is not connected.");

    private readonly record struct Packet(int PacketId, byte[] Payload);
}
