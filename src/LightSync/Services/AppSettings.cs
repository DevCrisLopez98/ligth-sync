using System.IO;
using System.Text.Json;
using LightSync.Effects;

namespace LightSync.Services;

public sealed class AppSettings
{
    public EffectKind Effect { get; set; } = EffectKind.SolidCycle;

    public float CycleSeconds { get; set; } = 8;

    public bool Music { get; set; }

    public float Brightness { get; set; } = 1f;

    public byte StaticRed { get; set; }

    public byte StaticGreen { get; set; } = 220;

    public byte StaticBlue { get; set; } = 140;

    public List<string> SyncedLights { get; set; } = [];

    public List<string> PcWhite { get; set; } = [];

    public List<string> PcOff { get; set; } = [];

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LightSync",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
            settings.SyncedLights ??= [];
            settings.PcWhite ??= [];
            settings.PcOff ??= [];
            return settings;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
