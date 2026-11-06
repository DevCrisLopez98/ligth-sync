using System.Threading;
using System.Windows;

namespace LightSync;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _show;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "Local\\LightSync.Single", out var created);
        if (!created)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting("Local\\LightSync.Show");
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            Shutdown();
            return;
        }

        _show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\LightSync.Show");
        var startInTray = e.Args.Any(arg => arg.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(startInTray);
        window.AttachShowSignal(_show);
        MainWindow = window;
        if (startInTray)
        {
            window.BeginInTray();
        }
        else
        {
            window.Show();
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _show?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
