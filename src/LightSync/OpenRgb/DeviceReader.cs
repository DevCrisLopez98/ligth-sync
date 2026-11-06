using System.Buffers.Binary;
using System.Text;

namespace LightSync.OpenRgb;

internal static class DeviceReader
{
    public static LightingDevice Read(uint id, byte[] data)
    {
        var reader = new Reader(data);
        reader.U32();
        reader.I32();
        var name = reader.Text();
        for (var index = 0; index < 5; index++)
        {
            reader.Text();
        }

        var modeCount = reader.U16();
        reader.I32();
        var modes = new LightingMode[modeCount];
        for (var index = 0; index < modeCount; index++)
        {
            modes[index] = ReadMode(reader, index);
        }

        var zoneCount = reader.U16();
        var zones = new LightingZone[zoneCount];
        for (var index = 0; index < zoneCount; index++)
        {
            zones[index] = ReadZone(reader);
        }

        return new LightingDevice { Id = id, Name = name, Zones = zones, Modes = modes };
    }

    private static LightingZone ReadZone(Reader reader)
    {
        var name = reader.Text();
        reader.I32();
        reader.U32();
        var ledMax = reader.U32();
        var ledCount = reader.U32();
        reader.Skip(reader.U16());
        var segmentCount = reader.U16();
        for (var index = 0; index < segmentCount; index++)
        {
            reader.Text();
            reader.I32();
            reader.U32();
            reader.U32();
            reader.Skip(reader.U16());
            reader.U32();
        }

        reader.U32();
        reader.I32();
        var modeCount = reader.U16();
        for (var index = 0; index < modeCount; index++)
        {
            _ = ReadMode(reader, index);
        }

        reader.Text();
        return new LightingZone { Name = name, LedCount = (int)ledCount, LedMax = (int)ledMax };
    }

    private static LightingMode ReadMode(Reader reader, int index)
    {
        var name = reader.Text();
        var flags = reader.U32();
        var speedMin = reader.U32();
        var speedMax = reader.U32();
        var brightnessMin = reader.U32();
        var brightnessMax = reader.U32();
        var colorsMin = reader.U32();
        var colorsMax = reader.U32();
        var speed = reader.U32();
        var brightness = reader.U32();
        var direction = reader.U32();
        var colorMode = reader.U32();
        reader.Skip(reader.U16() * 4);
        return new LightingMode
        {
            Index = index,
            Name = name,
            Flags = flags,
            SpeedMin = speedMin,
            SpeedMax = speedMax,
            BrightnessMin = brightnessMin,
            BrightnessMax = brightnessMax,
            ColorsMin = colorsMin,
            ColorsMax = colorsMax,
            Speed = speed,
            Brightness = brightness,
            Direction = direction,
            ColorMode = colorMode,
        };
    }

    private sealed class Reader(byte[] data)
    {
        private int _position;

        public ushort U16()
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_position));
            _position += 2;
            return value;
        }

        public uint U32()
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_position));
            _position += 4;
            return value;
        }

        public int I32()
        {
            var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(_position));
            _position += 4;
            return value;
        }

        public string Text()
        {
            var size = U16();
            var text = Encoding.UTF8.GetString(data, _position, size).TrimEnd('\0');
            _position += size;
            return text;
        }

        public void Skip(int size) => _position += size;
    }
}
