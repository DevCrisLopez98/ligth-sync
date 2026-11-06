using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace LightSync.Services;

public static class OpenRgbHost
{
    public static async Task EnsureServerAsync(CancellationToken cancellationToken)
    {
        if (await CanConnectAsync(cancellationToken))
        {
            return;
        }

        var executable = FindExecutable();
        if (executable is null)
        {
            throw new FileNotFoundException("Patched OpenRGB.exe was not found.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "--server --server-host 127.0.0.1 --noautoconnect",
            WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = false,
        });

        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (await CanConnectAsync(cancellationToken))
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("OpenRGB server did not open port 6742.");
    }

    public static void StopTemporaryClock()
    {
        var killed = false;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.ProcessName is not ("python" or "python3" or "py" or "powershell" or "pwsh"))
                {
                    continue;
                }

                var command = ProcessCommand.Read(process.Id);
                if (command is null || !command.Contains("sync-color-cycle.py", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                killed = true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        if (killed)
        {
            return;
        }

        using var fallback = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'sync-color-cycle.py' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        fallback?.WaitForExit(8000);
    }

    private static async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", 6742, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return false;
        }
    }

    private static string? FindExecutable()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "OpenRGB", "OpenRGB.exe");
        if (File.Exists(beside))
        {
            return beside;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".vendor", "OpenRGB", "OpenRGB Windows 64-bit", "OpenRGB.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static class ProcessCommand
    {
        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessVmRead = 0x0010;

        public static string? Read(int processId)
        {
            if (!Environment.Is64BitProcess)
            {
                return null;
            }

            var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var info = new ProcessBasicInformation();
                if (NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                {
                    return null;
                }

                var parametersAddress = ReadPointer(handle, info.PebBaseAddress + 0x20);
                if (parametersAddress == IntPtr.Zero)
                {
                    return null;
                }

                var commandLine = new byte[16];
                if (!ReadProcessMemory(handle, parametersAddress + 0x70, commandLine, commandLine.Length, out _))
                {
                    return null;
                }

                var length = BitConverter.ToUInt16(commandLine, 0);
                var buffer = (IntPtr)BitConverter.ToInt64(commandLine, 8);
                if (length == 0 || buffer == IntPtr.Zero)
                {
                    return null;
                }

                var bytes = new byte[length];
                if (!ReadProcessMemory(handle, buffer, bytes, bytes.Length, out _))
                {
                    return null;
                }

                return Encoding.Unicode.GetString(bytes);
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static IntPtr ReadPointer(IntPtr handle, IntPtr address)
        {
            var buffer = new byte[8];
            return ReadProcessMemory(handle, address, buffer, buffer.Length, out _)
                ? (IntPtr)BitConverter.ToInt64(buffer, 0)
                : IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr Reserved3;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, int size, out IntPtr read);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr handle, int processInformationClass, ref ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);
    }
}
