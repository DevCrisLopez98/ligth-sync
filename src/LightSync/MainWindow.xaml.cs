using System.Runtime.InteropServices;
using System.Windows;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightSync.Effects;
using LightSync.Network;
using LightSync.OpenRgb;
using LightSync.Services;

namespace LightSync;

public partial class MainWindow : Window
{
    private readonly EffectEngine _engine = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _scan;
    private int _scanGeneration;
    private IReadOnlyList<LightingDevice> _pcDevices = [];
    private IReadOnlyList<NetworkDevice> _lanDevices = [];
    private readonly NetworkDirector _lan = new();
    private int _lanBusy;
    private string _filter = "all";
    private readonly DispatcherTimer _preview = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly TrayIcon _tray = new();
    private readonly bool _startInTray;
    private bool _running;
    private bool _closing;
    private bool _allowExit;
    private bool _hintShown;

    public MainWindow(bool startInTray = false)
    {
        _startInTray = startInTray;
        InitializeComponent();
        _tray.OpenRequested += (_, _) => Dispatcher.Invoke(ShowFromTray);
        _tray.ExitRequested += (_, _) => Dispatcher.Invoke(ExitFromTray);
        SolidCard.IsChecked = _settings.Effect == EffectKind.SolidCycle;
        RainbowCard.IsChecked = _settings.Effect == EffectKind.Rainbow;
        StaticCard.IsChecked = _settings.Effect == EffectKind.Static;
        BuildSpectrum(new RgbColor(_settings.StaticRed, _settings.StaticGreen, _settings.StaticBlue));
        SpeedSlider.Value = Math.Clamp(_settings.CycleSeconds, SpeedSlider.Minimum, SpeedSlider.Maximum);
        MusicToggle.IsChecked = _settings.Music;
        MusicToggle.Content = _settings.Music ? "Música activa" : "Activar música";
        BrightnessSlider.Value = Math.Clamp(_settings.Brightness * 100f, BrightnessSlider.Minimum, BrightnessSlider.Maximum);
        BrightnessSlider.IsEnabled = !_settings.Music;
        BrightnessHint.Visibility = _settings.Music ? Visibility.Visible : Visibility.Collapsed;
        _engine.SetBrightness(_settings.Brightness);
        _lan.Restore(_settings.SyncedLights);
        _engine.LoadPcModes(_settings.PcWhite, _settings.PcOff);
        _engine.FramePainted = _lan.Note;
        _preview.Tick += (_, _) => UpdatePreview();
        _preview.Start();
        Closed += (_, _) =>
        {
            _preview.Stop();
            _engine.FramePainted = null;
            _lan.Stop();
            _lifetime.Cancel();
            _tray.Dispose();
            _engine.Dispose();
            _lifetime.Dispose();
        };
        Loaded += async (_, _) =>
        {
            if (_startInTray)
            {
                HideToTray(false);
                Opacity = 1;
            }

            await ApplySavedConfigurationAsync();
        };
        UpdateColorPanel();
        UpdatePreview();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var useDark = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref useDark, sizeof(int));
    }

    private async Task ApplySavedConfigurationAsync()
    {
        SetStatus("Aplicando", "#E2B657", "Cargando la iluminación guardada.");
        RetryButton.Visibility = Visibility.Collapsed;
        PauseButton.IsEnabled = false;
        var (generation, token) = BeginLanScan();
        ShowLanStatus("Buscando focos y enchufes en la red.");
        try
        {
            LightingGuard.TakeControl();
            await OpenRgbHost.EnsureServerAsync(CancellationToken.None);
            var devices = await _engine.ConnectAsync(CancellationToken.None);
            if (_closing)
            {
                return;
            }

            ShowDevices(devices);
            if (devices.Count == 0)
            {
                SetStatus("Sin dispositivos", "#F07178", "OpenRGB está activo, pero no encontró iluminación.");
                RetryButton.Visibility = Visibility.Visible;
            }
            else
            {
                OpenRgbHost.StopTemporaryClock();
                if (_closing)
                {
                    return;
                }

                StartSavedEffect();
                SetStatus("En control", "#3DDC97", null);
                ApplyMusic(_settings.Music);
            }
        }
        catch (Exception exception)
        {
            SetStatus("Error", "#F07178", exception.Message);
            RetryButton.Visibility = Visibility.Visible;
        }

        await PublishLanAsync(generation, token);
    }

    private async void RetryClick(object sender, RoutedEventArgs e)
    {
        await ApplySavedConfigurationAsync();
    }

    private async void RescanClick(object sender, RoutedEventArgs e)
    {
        var (generation, token) = BeginLanScan();
        ShowLanStatus("Buscando focos y enchufes en la red.");
        await PublishLanAsync(generation, token);
    }

    private void PauseClick(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _engine.Hold();
            _running = false;
            PauseButton.Content = "Reanudar";
            SetStatus("Pausado", "#E2B657", "El último color queda fijo.");
            return;
        }

        if (_engine.Devices.Count == 0)
        {
            SetStatus("Sin dispositivos", "#F07178", "No hay iluminación para reanudar.");
            return;
        }

        StartSavedEffect();
        SetStatus("En control", "#3DDC97", null);
    }

    private void MusicChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _closing)
        {
            return;
        }

        var enabled = MusicToggle.IsChecked == true;
        MusicToggle.Content = enabled ? "Música activa" : "Activar música";
        BrightnessSlider.IsEnabled = !enabled;
        BrightnessHint.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ApplyMusic(enabled);
        SaveSettings();
    }

    private void ApplyMusic(bool enabled)
    {
        var error = _engine.SetMusic(enabled);
        if (error is null)
        {
            return;
        }

        MusicToggle.IsChecked = false;
        MusicToggle.Content = "Activar música";
        _settings.Music = false;
        SetStatus("Sin audio", "#F07178", error);
    }

    private void BrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText is null)
        {
            return;
        }

        BrightnessText.Text = $"{e.NewValue:0} %";
        _engine.SetBrightness((float)(e.NewValue / 100d));
        if (IsLoaded && !_closing)
        {
            SaveSettings();
        }
    }

    private void SpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedText is null)
        {
            return;
        }

        SpeedText.Text = $"{e.NewValue:0} s";
        if (_running && !_closing)
        {
            _engine.Start(SelectedEffect(), (float)e.NewValue);
            SaveSettings();
        }
    }

    private void EffectPicked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _closing)
        {
            return;
        }

        UpdateColorPanel();
        SaveSettings();
        if (_running)
        {
            _engine.Start(SelectedEffect(), (float)SpeedSlider.Value);
        }

        UpdatePreview();
    }

    private void SpectrumDown(object sender, MouseButtonEventArgs e)
    {
        SpectrumHost.CaptureMouse();
        PickSpectrum(e.GetPosition(SpectrumHost));
    }

    private void SpectrumMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !SpectrumHost.IsMouseCaptured)
        {
            return;
        }

        PickSpectrum(e.GetPosition(SpectrumHost));
    }

    private void SpectrumUp(object sender, MouseButtonEventArgs e)
    {
        if (SpectrumHost.IsMouseCaptured)
        {
            SpectrumHost.ReleaseMouseCapture();
        }
    }

    public void BeginInTray()
    {
        ShowInTaskbar = false;
        WindowState = WindowState.Minimized;
        Show();
    }

    public void AttachShowSignal(EventWaitHandle show)
    {
        ThreadPool.RegisterWaitForSingleObject(show, (_, _) => Dispatcher.Invoke(ShowFromTray), null, Timeout.Infinite, false);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            HideToTray(!_hintShown);
            _hintShown = true;
            return;
        }

        _closing = true;
        _lifetime.Cancel();
        _preview.Stop();
        SaveSettings();
        try
        {
            _engine.Hold();
        }
        finally
        {
            _engine.Dispose();
            _running = false;
        }

        base.OnClosing(e);
    }

    private void HideToTray(bool notify)
    {
        ShowInTaskbar = false;
        Hide();
        if (notify)
        {
            _tray.ShowHint();
        }
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _allowExit = true;
        Close();
    }

    private void StartSavedEffect()
    {
        if (_closing)
        {
            return;
        }

        SaveSettings();
        _engine.Start(SelectedEffect(), (float)SpeedSlider.Value);
        _running = true;
        PauseButton.IsEnabled = true;
        PauseButton.Content = "Pausar";
    }

    private void SaveSettings()
    {
        _settings.Effect = SelectedEffect();
        _settings.CycleSeconds = (float)SpeedSlider.Value;
        _settings.Music = MusicToggle.IsChecked == true;
        _settings.Brightness = (float)(BrightnessSlider.Value / 100d);
        var color = CurrentStaticColor();
        _settings.StaticRed = color.Red;
        _settings.StaticGreen = color.Green;
        _settings.StaticBlue = color.Blue;
        _settings.SyncedLights = _lan.SyncedIds.ToList();
        _settings.PcWhite = _engine.PcWhite.ToList();
        _settings.PcOff = _engine.PcOff.ToList();
        _settings.Save();
    }

    private (int Generation, CancellationToken Token) BeginLanScan()
    {
        _scan?.Cancel();
        _scan = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        return (++_scanGeneration, _scan.Token);
    }

    private async Task PublishLanAsync(int generation, CancellationToken token)
    {
        NetworkScanResult result;
        try
        {
            result = await NetworkScanner.DiscoverAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            result = new NetworkScanResult { Devices = [], Error = exception.Message };
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (_closing || generation != _scanGeneration)
            {
                return;
            }

            _lanDevices = result.Devices;
            _lan.Replace(result.Devices);
            RenderDevices();
            if (DeviceList.Items.Count > 0)
            {
                DeviceList.ScrollIntoView(DeviceList.Items[0]);
            }

            ShowLanStatus(result.Error ?? (result.Devices.Count == 0
                ? "No hay focos ni enchufes en esta red."
                : DescribeLan(result.Devices)));
        });
    }

    private void ShowLanStatus(string text)
    {
        LanStatus.Text = text;
        LanStatus.Visibility = Visibility.Visible;
    }

    private static string DescribeLan(IReadOnlyList<NetworkDevice> devices)
    {
        var lights = devices.Count(device => device.Kind == NetworkKind.Light);
        var plugs = devices.Count(device => device.Kind == NetworkKind.Plug);
        var other = devices.Count - lights - plugs;
        var parts = new List<string>();
        if (lights > 0)
        {
            parts.Add(lights == 1 ? "1 foco" : $"{lights} focos");
        }

        if (plugs > 0)
        {
            parts.Add(plugs == 1 ? "1 enchufe" : $"{plugs} enchufes");
        }

        if (other > 0)
        {
            parts.Add(other == 1 ? "1 dispositivo de red" : $"{other} dispositivos de red");
        }

        return $"Red: {string.Join(", ", parts)}.";
    }

    private void ShowDevices(IReadOnlyList<LightingDevice> devices)
    {
        _pcDevices = devices;
        RenderDevices();
    }

    private void RenderDevices()
    {
        var rows = new List<DeviceRow>();
        rows.AddRange(_lanDevices.Select(device =>
        {
            var on = _lan.IsOn(device);
            var synced = device.Kind == NetworkKind.Light && _lan.IsSynced(device.Id);
            var group = device.Kind == NetworkKind.Plug ? "plug" : "light";
            return new DeviceRow(
                device.Role,
                device.Name,
                on ? device.State : device.Kind == NetworkKind.Light ? "Apagada" : "Apagado",
                device.Meta,
                device.Id,
                device.Kind == NetworkKind.Light,
                true,
                synced ? "Sincronizado" : "Sincronizar",
                on ? "Apagar" : "Prender",
                group,
                synced,
                on);
        }));
        rows.AddRange(_pcDevices.Select(device =>
        {
            var on = _engine.IsPcOn(device.Name);
            var synced = _engine.IsPcSynced(device.Name);
            return new DeviceRow(
                Role(device.Name),
                device.Name,
                device.LedCount == 1 ? "1 LED" : $"{device.LedCount} LEDs",
                synced ? "Sigue el reloj" : on ? "Blanco frío" : "Apagado",
                device.Name,
                true,
                true,
                synced ? "Sincronizado" : "Sincronizar",
                on ? "Apagar" : "Prender",
                "pc",
                synced,
                on);
        }));
        if (_filter != "all")
        {
            rows = rows.Where(row => row.Group == _filter).ToList();
        }

        DeviceList.ItemsSource = rows;
        if (SyncSummary is not null)
        {
            var synced = _pcDevices.Count(device => _engine.IsPcOn(device.Name) && _engine.IsPcSynced(device.Name))
                + _lanDevices.Count(device => device.Kind == NetworkKind.Light && _lan.IsOn(device) && _lan.IsSynced(device.Id));
            var total = _pcDevices.Count + _lanDevices.Count(device => device.Kind != NetworkKind.Plug);
            SyncSummary.Text = $"{synced} de {total} sincronizados";
        }

        if (FilterAll is not null)
        {
            var lights = _lanDevices.Count(device => device.Kind == NetworkKind.Light);
            var plugs = _lanDevices.Count(device => device.Kind == NetworkKind.Plug);
            FilterAll.Content = $"Todos · {_pcDevices.Count + _lanDevices.Count}";
            FilterLights.Content = $"Focos · {lights}";
            FilterPlugs.Content = $"Enchufes · {plugs}";
            FilterPc.Content = $"PC · {_pcDevices.Count}";
            HighlightFilter();
        }
    }

    private void UpdatePreview()
    {
        if (PreviewBar is null)
        {
            return;
        }

        var effect = SelectedEffect();
        var phase = _engine.Phase;
        var level = _engine.DisplayLevel;
        if (effect == EffectKind.Static)
        {
            var solid = CurrentStaticColor().Scale(level);
            PreviewBar.Background = Brush(solid);
            if (AccentHex is not null)
            {
                var hex = $"#{_staticColor.Red:X2}{_staticColor.Green:X2}{_staticColor.Blue:X2}";
                AccentHex.Text = hex;
                StaticSwatch.Fill = Brush(_staticColor);
                HexPreview.Background = Brush(_staticColor);
            }

            return;
        }

        if (AccentHex is not null)
        {
            AccentHex.Text = string.Empty;
        }
        if (MusicDeviceText is not null)
        {
            MusicDeviceText.Text = MusicToggle.IsChecked == true
                ? _engine.ListenError ?? (_engine.OutputName is null ? string.Empty : $"Escuchando {_engine.OutputName}")
                : string.Empty;
        }
        if (effect == EffectKind.SolidCycle)
        {
            var color = RgbColor.FromHue(phase).Scale(level);
            PreviewBar.Background = Brush(color);
            return;
        }

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
        };
        const int stops = 28;
        for (var index = 0; index <= stops; index++)
        {
            var led = index / (float)stops * 32f;
            var color = RgbColor.FromHue(LightingEffects.Hue(EffectKind.Rainbow, led, phase)).Scale(level);
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(color.Red, color.Green, color.Blue), index / (double)stops));
        }

        PreviewBar.Background = gradient;
    }

    private void SetStatus(string pill, string color, string? detail)
    {
        StatusPill.Text = pill;
        StatusDot.Fill = (Brush)new BrushConverter().ConvertFrom(color)!;
        StatusText.Text = detail ?? string.Empty;
        StatusText.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    private EffectKind SelectedEffect()
    {
        if (RainbowCard.IsChecked == true)
        {
            return EffectKind.Rainbow;
        }

        return StaticCard.IsChecked == true ? EffectKind.Static : EffectKind.SolidCycle;
    }

    private RgbColor _staticColor = new(0, 220, 96);
    private float _hue = 0.4f;
    private float _saturation = 1f;

    private void BuildSpectrum(RgbColor saved)
    {
        const int width = 200;
        const int height = 200;
        var pixels = new byte[width * height * 4];
        var center = (width - 1) / 2f;
        var radius = center - 2f;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var distance = MathF.Sqrt((dx * dx) + (dy * dy));
                var offset = ((y * width) + x) * 4;
                if (distance > radius)
                {
                    continue;
                }

                var hue = (MathF.Atan2(dy, dx) / (MathF.PI * 2f)) + 0.5f;
                var color = RgbColor.FromHsv(hue, distance / radius, 1f);
                pixels[offset] = color.Blue;
                pixels[offset + 1] = color.Green;
                pixels[offset + 2] = color.Red;
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        SpectrumImage.Source = bitmap;

        var hsv = saved.ToHsv();
        _hue = hsv.Hue;
        _saturation = hsv.Saturation;
        _staticColor = RgbColor.FromHsv(_hue, _saturation, 1f);
        _engine.SetStaticColor(_staticColor);
        SpectrumHost.SizeChanged += (_, _) => PlaceMarker(_hue, _saturation);
        PlaceMarker(_hue, _saturation);
    }

    private void PickSpectrum(System.Windows.Point point)
    {
        var width = SpectrumHost.ActualWidth;
        var height = SpectrumHost.ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        var centerX = width / 2d;
        var centerY = height / 2d;
        var dx = point.X - centerX;
        var dy = point.Y - centerY;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        var radius = Math.Min(centerX, centerY) - 2d;
        if (radius <= 1)
        {
            return;
        }

        _hue = (float)(Math.Atan2(dy, dx) / (Math.PI * 2d) + 0.5d);
        if (_hue < 0f)
        {
            _hue += 1f;
        }
        _saturation = (float)Math.Clamp(distance / radius, 0, 1);
        var hue = _hue;
        var saturation = _saturation;
        _staticColor = RgbColor.FromHsv(hue, saturation, 1f);
        _engine.SetStaticColor(_staticColor);
        PlaceMarker(hue, saturation);
        if (!IsLoaded || _closing)
        {
            return;
        }

        SaveSettings();
        UpdatePreview();
    }

    private void PlaceMarker(float hue, float saturation)
    {
        if (SpectrumHost.ActualWidth <= 1 || SpectrumHost.ActualHeight <= 1)
        {
            return;
        }

        var radius = (Math.Min(SpectrumHost.ActualWidth, SpectrumHost.ActualHeight) / 2d) - 2d;
        var angle = ((hue - 0.5f) * Math.PI * 2d);
        var x = (SpectrumHost.ActualWidth / 2d) + (Math.Cos(angle) * saturation * radius) - (SpectrumMarker.Width / 2);
        var y = (SpectrumHost.ActualHeight / 2d) + (Math.Sin(angle) * saturation * radius) - (SpectrumMarker.Height / 2);
        SpectrumMarker.Margin = new Thickness(Math.Max(0, x), Math.Max(0, y), 0, 0);
    }

    private RgbColor CurrentStaticColor()
    {
        return _staticColor;
    }

    private void UpdateColorPanel()
    {
        var isStatic = SelectedEffect() == EffectKind.Static;
        ColorPanel.Visibility = isStatic ? Visibility.Visible : Visibility.Collapsed;
        SpeedSlider.IsEnabled = !isStatic;
        if (EffectTitle is not null)
        {
            EffectTitle.Text = SelectedEffect() switch
            {
                EffectKind.Rainbow => "Arcoíris",
                EffectKind.Static => "Color fijo",
                _ => "Ciclo sólido",
            };
        }

        if (HexBox is not null && !HexBox.IsKeyboardFocusWithin)
        {
            HexBox.Text = $"#{_staticColor.Red:X2}{_staticColor.Green:X2}{_staticColor.Blue:X2}";
        }
    }

    private static string Role(string name)
    {
        if (name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || name.Contains("RTX", StringComparison.OrdinalIgnoreCase))
        {
            return "Placa de video";
        }

        if (name.Contains("HyperX", StringComparison.OrdinalIgnoreCase) || name.Contains("Predator", StringComparison.OrdinalIgnoreCase) || name.Contains("Fury", StringComparison.OrdinalIgnoreCase))
        {
            return "Memoria";
        }

        if (name.Contains("Impact", StringComparison.OrdinalIgnoreCase))
        {
            return "Mouse";
        }

        if (name.Contains("PRIME", StringComparison.OrdinalIgnoreCase) || name.Contains("B660", StringComparison.OrdinalIgnoreCase))
        {
            return "Placa madre";
        }

        return "Iluminación";
    }

    private static SolidColorBrush Brush(RgbColor color)
    {
        var brush = new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));
        brush.Freeze();
        return brush;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private async void SyncClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not DeviceRow row || row.Id is null)
        {
            return;
        }

        if (row.Group == "pc")
        {
            var enablePc = !_engine.IsPcSynced(row.Name);
            if (enablePc && _running)
            {
                _ = _engine.RestartCycleAsync();
            }

            _engine.SetPcSync(row.Name, enablePc);
            SaveSettings();
            RenderDevices();
            ShowLanStatus(enablePc
                ? "Ciclo reiniciado. Lo sincronizado arranca junto."
                : $"{row.Name} quedó en blanco frío.");
            return;
        }

        var device = _lanDevices.FirstOrDefault(item => item.Id == row.Id);
        if (device is null || Interlocked.Exchange(ref _lanBusy, 1) == 1)
        {
            return;
        }

        try
        {
            var enable = !_lan.IsSynced(device.Id);
            var color = ActiveLanColor();
            if (enable && _running)
            {
                try
                {
                    color = await _engine.RestartCycleAsync();
                }
                catch (TimeoutException)
                {
                }
            }

            await _lan.SetSyncAsync(device, enable, color, CancellationToken.None);
            SaveSettings();
            RenderDevices();
            ShowLanStatus(enable
                ? "Ciclo reiniciado. Los focos sincronizados arrancan juntos."
                : $"{device.Name} quedó en blanco frío.");
        }
        catch (Exception exception)
        {
            ShowLanStatus(exception.Message);
            RenderDevices();
        }
        finally
        {
            _lanBusy = 0;
        }
    }

    private async void PowerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not DeviceRow row || row.Id is null)
        {
            return;
        }

        if (row.Group == "pc")
        {
            var turnPcOn = !_engine.IsPcOn(row.Name);
            _engine.SetPcPower(row.Name, turnPcOn);
            SaveSettings();
            RenderDevices();
            ShowLanStatus(turnPcOn ? $"{row.Name} encendido." : $"{row.Name} apagado.");
            return;
        }

        var device = _lanDevices.FirstOrDefault(item => item.Id == row.Id);
        if (device is null || Interlocked.Exchange(ref _lanBusy, 1) == 1)
        {
            return;
        }

        try
        {
            var turnOn = !_lan.IsOn(device);
            await _lan.SetPowerAsync(device, turnOn, ActiveLanColor(), CancellationToken.None);
            RenderDevices();
            ShowLanStatus(turnOn ? $"{device.Name} encendido." : $"{device.Name} apagado.");
        }
        catch (Exception exception)
        {
            ShowLanStatus(exception.Message);
            RenderDevices();
        }
        finally
        {
            _lanBusy = 0;
        }
    }

    private RgbColor ActiveLanColor()
    {
        return _engine.HasFrame ? _engine.LastColor : CurrentStaticColor().Scale(_engine.DisplayLevel);
    }

    private void QuickColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string hex)
        {
            return;
        }

        ApplyHex(hex);
    }

    private void HexCommit(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || HexBox is null)
        {
            return;
        }

        ApplyHex(HexBox.Text);
    }

    private void ApplyHex(string text)
    {
        var hex = text.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var packed))
        {
            return;
        }

        var color = new RgbColor((byte)((packed >> 16) & 255), (byte)((packed >> 8) & 255), (byte)(packed & 255));
        var hsv = color.ToHsv();
        _hue = hsv.Hue;
        _saturation = hsv.Saturation;
        _staticColor = RgbColor.FromHsv(_hue, _saturation, 1f);
        _engine.SetStaticColor(_staticColor);
        PlaceMarker(_hue, _saturation);
        if (!IsLoaded || _closing)
        {
            return;
        }

        SaveSettings();
        UpdatePreview();
        UpdateColorPanel();
    }

    private void HighlightFilter()
    {
        foreach (var button in new[] { FilterAll, FilterLights, FilterPlugs, FilterPc })
        {
            var selected = button.Tag as string == _filter;
            button.Background = selected
                ? new SolidColorBrush(Color.FromRgb(0x1E, 0x25, 0x33))
                : System.Windows.Media.Brushes.Transparent;
            button.Foreground = selected
                ? new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xFA))
                : new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB6));
        }
    }

    private void FilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string filter)
        {
            return;
        }

        _filter = filter;
        RenderDevices();
    }

    private sealed record DeviceRow(
        string Role,
        string Name,
        string Detail,
        string? Meta,
        string? Id,
        bool ShowSync,
        bool ShowPower,
        string SyncLabel,
        string PowerLabel,
        string Group,
        bool IsSynced,
        bool IsOn)
    {
        public bool HasMeta => !string.IsNullOrWhiteSpace(Meta);
    }
}
