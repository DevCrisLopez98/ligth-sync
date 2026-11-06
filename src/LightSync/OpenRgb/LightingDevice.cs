namespace LightSync.OpenRgb;

public sealed class LightingMode
{
    public const uint PerLedColor = 0x20;
    public const uint ModeSpecificColor = 0x40;
    public const uint HasBrightness = 0x10;
    public const uint ManualSave = 0x100;
    public const uint ColorModeSpecific = 2;

    public required int Index { get; init; }

    public required string Name { get; init; }

    public required uint Flags { get; init; }

    public required uint SpeedMin { get; init; }

    public required uint SpeedMax { get; init; }

    public required uint BrightnessMin { get; init; }

    public required uint BrightnessMax { get; init; }

    public required uint ColorsMin { get; init; }

    public required uint ColorsMax { get; init; }

    public required uint Speed { get; init; }

    public required uint Brightness { get; init; }

    public required uint Direction { get; init; }

    public required uint ColorMode { get; init; }

    public static LightingMode? SelectHold(IReadOnlyList<LightingMode> modes)
    {
        var namedStatic = modes.FirstOrDefault(mode => mode.Name.Equals("Static", StringComparison.OrdinalIgnoreCase));
        if (namedStatic is not null)
        {
            return namedStatic;
        }

        // Some Aura mice expose the savable static color under the name Direct.
        return modes.FirstOrDefault(mode =>
            mode.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase)
            && (mode.Flags & ManualSave) != 0
            && (mode.Flags & PerLedColor) != 0);
    }
}

public sealed class LightingZone
{
    public required string Name { get; init; }

    public required int LedCount { get; init; }

    public required int LedMax { get; init; }
}

public sealed class LightingDevice
{
    public required uint Id { get; init; }

    public required string Name { get; init; }

    public required IReadOnlyList<LightingZone> Zones { get; init; }

    public required IReadOnlyList<LightingMode> Modes { get; init; }

    public int LedCount => Zones.Sum(zone => zone.LedCount);
}
