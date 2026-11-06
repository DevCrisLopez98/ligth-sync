using LightSync.OpenRgb;

namespace LightSync.Effects;

public static class LightingEffects
{
    // One full rainbow every 20 LEDs. Spanning all 120 configured LEDs looks solid,
    // because the visible strip only shows the first slice of that gradient.
    private const int AuraWaveLeds = 20;
    private const float RainbowMotion = 1.8f;

    public static RgbColor[] Render(EffectKind effect, LightingDevice device, float phase, RgbColor staticColor)
    {
        var colors = new RgbColor[device.LedCount];
        if (device.LedCount == 0)
        {
            return colors;
        }

        if (effect == EffectKind.Static)
        {
            Array.Fill(colors, staticColor);
            return colors;
        }

        if (effect != EffectKind.Rainbow)
        {
            Array.Fill(colors, RgbColor.FromHue(phase));
            return colors;
        }

        phase *= RainbowMotion;

        var cursor = 0;
        if (IsMemory(device))
        {
            foreach (var zone in device.Zones)
            {
                if (zone.LedCount <= 0)
                {
                    continue;
                }

                FillIncoming(colors, cursor, zone.LedCount, phase);
                cursor += zone.LedCount;
            }

            return colors;
        }

        foreach (var zone in device.Zones)
        {
            if (zone.LedCount <= 0)
            {
                continue;
            }

            if (zone.Name.Contains("Addressable", StringComparison.OrdinalIgnoreCase))
            {
                FillAuraWave(colors, cursor, zone.LedCount, phase);
            }
            else
            {
                var cycle = zone.LedCount < AuraWaveLeds ? zone.LedCount : AuraWaveLeds;
                FillWave(colors, cursor, zone.LedCount, cycle, phase);
            }
            cursor += zone.LedCount;
        }

        return colors;
    }

    public static float Hue(EffectKind effect, float ledIndex, float phase)
    {
        return effect == EffectKind.Rainbow
            ? (phase * RainbowMotion) - (ledIndex / AuraWaveLeds)
            : phase;
    }

    private static void FillIncoming(RgbColor[] colors, int start, int count, float phase)
    {
        const int steps = 12;
        var wrapped = phase - MathF.Floor(phase);
        var scaled = wrapped * steps;
        var step = (int)MathF.Floor(scaled);
        var travel = scaled - step;
        var hue = step / (float)steps;
        var previous = ((step - 1 + steps) % steps) / (float)steps;
        var head = travel * count;
        var entering = RgbColor.FromHue(hue);
        var held = RgbColor.FromHue(previous);

        for (var index = 0; index < count; index++)
        {
            var fromBottom = count - 1 - index;
            var covered = head - fromBottom;
            colors[start + index] = covered >= 1f
                ? entering
                : covered >= 0f
                    ? Blend(held, entering, covered)
                    : held;
        }
    }

    private static RgbColor Blend(RgbColor from, RgbColor to, float amount)
    {
        return new RgbColor(
            (byte)(from.Red + ((to.Red - from.Red) * amount)),
            (byte)(from.Green + ((to.Green - from.Green) * amount)),
            (byte)(from.Blue + ((to.Blue - from.Blue) * amount)));
    }

    private static void FillAuraWave(RgbColor[] colors, int start, int count, float phase)
    {
        // Header LED matches the GPU. The full spectrum repeats along the strip, not once per 120.
        for (var index = 0; index < count; index++)
        {
            colors[start + index] = RgbColor.FromHue(phase - (index / (float)AuraWaveLeds));
        }
    }

    private static void FillWave(RgbColor[] colors, int start, int count, int cycle, float phase)
    {
        var span = Math.Max(cycle, 1);
        for (var index = 0; index < count; index++)
        {
            colors[start + index] = RgbColor.FromHue(phase + (index / (float)span));
        }
    }

    private static bool IsMemory(LightingDevice device)
    {
        return device.Name.Contains("HyperX", StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains("Predator", StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains("Fury", StringComparison.OrdinalIgnoreCase)
            || device.Zones.Any(zone => zone.Name.Contains("Slot", StringComparison.OrdinalIgnoreCase));
    }
}
