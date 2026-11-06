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
using LightSync.OpenRgb;
using LightSync.Services;

namespace LightSync;

public partial class MainWindow : Window
{
    private readonly EffectEngine _engine = new();
    private readonly AppSettings _settings = AppSettings.Load();
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
        MusicToggle.Content = _settings.Music ? "Escuchando" : "Apagada";
        BrightnessSlider.Value = Math.Clamp(_settings.Brightness * 100f, BrightnessSlider.Minimum, BrightnessSlider.Maximum);
        BrightnessSlider.IsEnabled = !_settings.Music;
        BrightnessHint.Visibility = _settings.Music ? Visibility.Visible : Visibility.Collapsed;
        _engine.SetBrightness(_settings.Brightness);
        _preview.Tick += (_, _) => UpdatePreview();
        _preview.Start();
        Closed += (_, _) =>
        {
            _preview.Stop();
            _tray.Dispose();
            _engine.Dispose();
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
                return;
            }

            OpenRgbHost.StopTemporaryClock();
            if (_closing)
            {
                return;
            }

            StartSavedEffect();
            SetStatus("En control", "#3DDC97", null);
            ApplyMusic(_settings.Music);
        }
        catch (Exception exception)
        {
            SetStatus("Error", "#F07178", exception.Message);
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private async void RetryClick(object sender, RoutedEventArgs e)
    {
        await ApplySavedConfigurationAsync();
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
        MusicToggle.Content = enabled ? "Escuchando" : "Apagada";
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
        MusicToggle.Content = "Apagada";
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
        _settings.Save();
    }

    private void ShowDevices(IReadOnlyList<LightingDevice> devices)
    {
        DeviceList.ItemsSource = devices.Select(device => new DeviceRow(
            Role(device.Name),
            device.Name,
            device.LedCount == 1 ? "1 LED" : $"{device.LedCount} LEDs")).ToList();
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
            PreviewBar.Background = Brush(CurrentStaticColor().Scale(level));
            return;
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
        const int width = 360;
        const int height = 100;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var saturation = 1f - (y / (float)(height - 1));
            for (var x = 0; x < width; x++)
            {
                var color = RgbColor.FromHsv(x / (float)width, saturation, 1f);
                var offset = ((y * width) + x) * 4;
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

        _hue = (float)Math.Clamp(point.X / width, 0, 0.9999);
        _saturation = (float)Math.Clamp(1d - (point.Y / height), 0, 1);
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

        var x = (hue * SpectrumHost.ActualWidth) - (SpectrumMarker.Width / 2);
        var y = ((1f - saturation) * SpectrumHost.ActualHeight) - (SpectrumMarker.Height / 2);
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

    private sealed record DeviceRow(string Role, string Name, string Detail);
}
