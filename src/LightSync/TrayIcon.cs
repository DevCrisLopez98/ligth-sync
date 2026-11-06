using System.Drawing;
using System.Windows.Forms;

namespace LightSync;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon()
    {
        var open = new ToolStripMenuItem("Abrir");
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        var exit = new ToolStripMenuItem("Salir");
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        var menu = new ContextMenuStrip();
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Visible = true,
            Text = "LightSync",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    public void ShowHint()
    {
        _icon.BalloonTipTitle = "LightSync";
        _icon.BalloonTipText = "Sigue activo. Para cerrarlo, usá Salir en el icono de la barra.";
        _icon.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
