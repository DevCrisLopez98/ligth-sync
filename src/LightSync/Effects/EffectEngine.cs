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
    private int _restart;
    private TaskCompletionSource<RgbColor>? _restarted;
    private readonly HashSet<string> _pcUnsynced = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pcOff = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _held = new(StringComparer.OrdinalIgnoreCase);
    private static readonly RgbColor ColdWhite = new(210, 228, 255);

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

    public void LoadPcModes(IEnumerable<string>? white, IEnumerable<string>? off)
    {
        _pcUnsynced.Clear();
        _pcOff.Clear();
        _held.Clear();
        if (white is not null)
        {
            foreach (var name in white)
            {
                _pcUnsynced.Add(name);
            }
        }

        if (off is not null)
        {
            foreach (var name in off)
            {
                _pcOff.Add(name);
            }
        }
    }

    public bool IsPcSynced(string name) => !_pcUnsynced.Contains(name);

    public bool IsPcOn(string name) => !_pcOff.Contains(name);

    public IReadOnlyCollection<string> PcWhite => _pcUnsynced;

    public IReadOnlyCollection<string> PcOff => _pcOff;

    public void SetPcSync(string name, bool sync)
    {
        if (sync)
        {
            _pcUnsynced.Remove(name);
            _pcOff.Remove(name);
        }
        else
        {
            _pcUnsynced.Add(name);
            _pcOff.Remove(name);
        }

        _held.Remove(name);
    }

    public void SetPcPower(string name, bool on)
    {
        if (on)
        {
            _pcOff.Remove(name);
        }
        else
        {
            _pcOff.Add(name);
        }

        _held.Remove(name);
    }

    public Task<RgbColor> RestartCycleAsync()
    {
        var source = new TaskCompletionSource<RgbColor>(TaskCreationOptions.RunContinuationsAsynchronously);
        _restarted = source;
        Interlocked.Exchange(ref _restart, 1);
        return source.Task.WaitAsync(TimeSpan.FromMilliseconds(250));
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
                var restarted = Interlocked.Exchange(ref _restart, 0) == 1;
                if (restarted)
                {
                    _phase = 0f;
                }
                else if (!_music || !dark)
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
                if (restarted)
                {
                    _restarted?.TrySetResult(LastColor);
                    _restarted = null;
                }

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

    private void Hold(LightingDevice device, int count, RgbColor color, string key)
    {
        if (_held.TryGetValue(device.Name, out var held) && held == key)
        {
            return;
        }

        var colors = new RgbColor[count];
        Array.Fill(colors, color);
        _client.UpdateLeds(device.Id, colors);
        _held[device.Name] = key;
    }

    private void Paint(float phase, float level)
    {
        _phase = phase;
        RgbColor? lead = null;
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

            if (colors.Length > 0 && lead is null)
            {
                lead = colors[0];
            }

            if (_pcOff.Contains(device.Name))
            {
                Hold(device, colors.Length, default, "off");
                continue;
            }

            if (_pcUnsynced.Contains(device.Name))
            {
                Hold(device, colors.Length, ColdWhite, "white");
                continue;
            }

            _held.Remove(device.Name);
            if (colors.Length > 0)
            {
                _client.UpdateLeds(device.Id, colors);
            }
        }

        if (lead is not null)
        {
            LastColor = lead.Value;
            HasFrame = true;
        }

        if (HasFrame)
        {
            FramePainted?.Invoke(LastColor);
        }
    }

    public Action<RgbColor>? FramePainted { get; set; }
}
