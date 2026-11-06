namespace LightSync.OpenRgb;

public readonly record struct RgbColor(byte Red, byte Green, byte Blue)
{
    public uint Packed => (uint)((Blue << 16) | (Green << 8) | Red);

    public RgbColor Scale(float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new RgbColor((byte)(Red * amount), (byte)(Green * amount), (byte)(Blue * amount));
    }

    public static RgbColor FromHsv(float hue, float saturation, float value)
    {
        hue = ((hue % 1f) + 1f) % 1f;
        saturation = Math.Clamp(saturation, 0f, 1f);
        value = Math.Clamp(value, 0f, 1f);
        var sector = hue * 6f;
        var index = (int)sector;
        var fraction = sector - index;
        var p = value * (1f - saturation);
        var q = value * (1f - (fraction * saturation));
        var t = value * (1f - ((1f - fraction) * saturation));
        (float red, float green, float blue) = index switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q),
        };
        return new RgbColor((byte)(red * 255f), (byte)(green * 255f), (byte)(blue * 255f));
    }

    public (float Hue, float Saturation, float Value) ToHsv()
    {
        var red = Red / 255f;
        var green = Green / 255f;
        var blue = Blue / 255f;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var delta = max - min;
        var hue = 0f;
        if (delta > 0f)
        {
            hue = max == red
                ? ((green - blue) / delta) + (green < blue ? 6f : 0f)
                : max == green
                    ? ((blue - red) / delta) + 2f
                    : ((red - green) / delta) + 4f;
            hue /= 6f;
        }

        var saturation = max == 0f ? 0f : delta / max;
        return (hue, saturation, max);
    }

    public static RgbColor FromHue(float hue)
    {
        hue = ((hue % 1f) + 1f) % 1f;
        var sector = hue * 6f;
        var index = (int)sector;
        var fraction = sector - index;
        var rising = (byte)(fraction * 255f);
        var falling = (byte)((1f - fraction) * 255f);
        return index switch
        {
            0 => new RgbColor(255, rising, 0),
            1 => new RgbColor(falling, 255, 0),
            2 => new RgbColor(0, 255, rising),
            3 => new RgbColor(0, falling, 255),
            4 => new RgbColor(rising, 0, 255),
            _ => new RgbColor(255, 0, falling),
        };
    }
}
