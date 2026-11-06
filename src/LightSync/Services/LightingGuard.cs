using System.Diagnostics;

namespace LightSync.Services;

public static class LightingGuard
{
    public static string TakeControl()
    {
        var lighting = Run("sc.exe", "config LightingService start= disabled");
        var stopLighting = Run("sc.exe", "stop LightingService");
        var memory = Run("sc.exe", "config SONiXDDRService start= demand");
        var stopMemory = Run("sc.exe", "stop SONiXDDRService");
        return string.Join(Environment.NewLine, new[] { lighting, stopLighting, memory, stopMemory }.Where(line => line.Length > 0));
    }

    private static string Run(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
        {
            return $"{fileName} {arguments} failed to start.";
        }

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }
}
