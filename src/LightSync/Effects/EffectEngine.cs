using System.IO;
using LightSync.OpenRgb;
using LightSync.Services;

namespace LightSync.Effects;

public sealed class EffectEngine : IDisposable
{
    private readonly OpenRgbClient _client = new();
    private readonly SystemAudioMeter _audio = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _music;
    private CancellationTokenSource? _run;
    private Task? _loop;
    private bool _disposed;
    private EffectKind _effect = EffectKind.SolidCycle;
    private float _cycleSeconds = 8f;
    private float _brightness = 1f;
    private RgbColor _staticColor = new(0, 220, 140);
    private float _phase;
    private bool _reassert;
    private bool _wasDark;

    public IReadOnlyList<LightingDevice> Devices { get; private set; } = [];

    public RgbColor LastColor { get; private set; } = new(0, 255, 80);

    public float Phase => _phase;

    public float DisplayLevel => _music ? _audio.Sample() : _brightness;

    public string? OutputName => _audio.OutputName;

    public string? ListenError => _audio.ListenError;

    public bool HasFrame { get; private set; }

    public async Task<IReadOnlyList<LightingDevice>> ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _client.ConnectAsync("127.0.0.1", 6742, cancellationToken).ConfigureAwait(false);
            var detected = await _client.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            await _client.PrepareAsync(detected, cancellationToken).ConfigureAwait(false);
            Devices = await _client.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            return Devices;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SetStaticColor(RgbColor color)
    {
        _staticColor = color;
    }

    public void SetBrightness(float brightness)
    {
        _brightness = Math.Clamp(brightness, 0f, 1f);
    }

    public string? SetMusic(bool enabled)
    {
        _music = enabled;
        _reassert = true;
        if (!enabled)
        {
            _audio.Stop();
            _wasDark = false;
            return null;
        }

        try
        {
            _audio.Start();
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException or NAudio.MmException)
        {
            _music = false;
            return exception.Message;
        }
    }

    public void Start(EffectKind effect, float cycleSeconds)
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _effect = effect;
            _cycleSeconds = Math.Clamp(cycleSeconds, 2f, 20f);
            if (_run is not null && _loop is { IsCompleted: false })
            {
                _reassert = true;
                return;
            }

            Stop();

            _client.EnterDirect(Devices);
            Paint(0f, DisplayLevel);
            var run = new CancellationTokenSource();
            _run = run;
            _loop = Task.Run(() => Loop(run.Token), run.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Hold()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            if (!HasFrame)
            {
                return;
            }

            _client.HoldColor(Devices, LastColor);
        }
        catch (IOException)
        {
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        if (_run is null)
        {
            return;
        }

        _run.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _run.Dispose();
        _run = null;
        _loop = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            _audio.Dispose();
            _client.Dispose();
            GC.SuppressFinalize(this);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Loop(CancellationToken cancellationToken)
    {
        var nextFrame = System.Diagnostics.Stopwatch.StartNew();
        var frame = TimeSpan.FromMilliseconds(33);
        var last = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                var elapsed = (float)(now - last) / System.Diagnostics.Stopwatch.Frequency;
                last = now;
                var level = DisplayLevel;
                var dark = _music && level <= 0.02f;
                if (!_music || !dark)
                {
                    _phase += elapsed / Math.Max(_cycleSeconds, 2f);
                }

                if (_reassert || (_wasDark && !dark))
                {
                    _client.EnterDirect(Devices);
                    _reassert = false;
                }

                _wasDark = dark;
                Paint(_phase, level);
                _client.DrainAcks();

                var remaining = frame - nextFrame.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    cancellationToken.WaitHandle.WaitOne(remaining);
                }

                nextFrame.Restart();
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private void Paint(float phase, float level)
    {
        _phase = phase;
        foreach (var device in Devices)
        {
            if (device.LedCount == 0)
            {
                continue;
            }

            var colors = LightingEffects.Render(_effect, device, phase, _staticColor);
            for (var index = 0; index < colors.Length; index++)
            {
                colors[index] = colors[index].Scale(level);
            }

            if (colors.Length > 0)
            {
                LastColor = colors[0];
                HasFrame = true;
            }

            _client.UpdateLeds(device.Id, colors);
        }
    }
}
