using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LightSync.Services;

public sealed class SystemAudioMeter : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _gate = new();
    private WasapiLoopbackCapture? _capture;
    private MMDevice? _device;
    private string? _deviceId;
    private long _nextCheck;
    private float _envelope;
    private long _last = Stopwatch.GetTimestamp();
    private bool _disposed;

    public string? OutputName { get; private set; }

    public string? ListenError { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FollowOutput(force: true);
        if (_capture is null)
        {
            throw new InvalidOperationException(ListenError ?? "No se pudo leer la salida de audio.");
        }
    }

    public void Stop()
    {
        StopCapture();
        DisposeDevice();
        lock (_gate)
        {
            _envelope = 0f;
        }
    }

    public float Sample()
    {
        if (_disposed)
        {
            return 0f;
        }

        FollowOutput(force: false);
        var gain = EndpointGain();
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - _last) / (float)Stopwatch.Frequency;
            if (elapsed > 0.02f)
            {
                _envelope *= MathF.Pow(0.05f, Math.Min(elapsed, 0.4f));
                _last = now;
            }

            return _envelope * gain;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _enumerator.Dispose();
    }

    private void FollowOutput(bool force)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && now < _nextCheck)
        {
            return;
        }

        _nextCheck = now + (long)(Stopwatch.Frequency * 0.5);
        MMDevice device;
        try
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or NAudio.MmException)
        {
            ListenError = "Windows no tiene una salida de audio activa.";
            return;
        }

        if (!force && _capture is not null && device.ID == _deviceId)
        {
            device.Dispose();
            ListenError = null;
            return;
        }

        var name = device.FriendlyName;
        try
        {
            StopCapture();
            DisposeDevice();
            var capture = new WasapiLoopbackCapture(device);
            capture.DataAvailable += OnData;
            capture.StartRecording();
            _capture = capture;
            _device = device;
            _deviceId = device.ID;
            OutputName = name;
            ListenError = null;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or NAudio.MmException)
        {
            device.Dispose();
            ListenError = $"{name} no deja leer el audio de salida.";
        }
    }

    private float EndpointGain()
    {
        var device = _device;
        if (device is null)
        {
            return 0f;
        }

        try
        {
            var volume = device.AudioEndpointVolume;
            return volume.Mute ? 0f : volume.MasterVolumeLevelScalar;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or NAudio.MmException)
        {
            return 0f;
        }
    }

    private void StopCapture()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null)
        {
            return;
        }

        capture.DataAvailable -= OnData;
        try
        {
            capture.StopRecording();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NAudio.MmException)
        {
        }

        capture.Dispose();
    }

    private void DisposeDevice()
    {
        _device?.Dispose();
        _device = null;
        _deviceId = null;
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        if (_capture is null || args.BytesRecorded <= 0)
        {
            return;
        }

        var format = _capture.WaveFormat;
        var bytesPerSample = Math.Max(format.BitsPerSample / 8, 1);
        var count = args.BytesRecorded / bytesPerSample;
        if (count == 0)
        {
            return;
        }

        double sum = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
        {
            for (var offset = 0; offset + 4 <= args.BytesRecorded; offset += 4)
            {
                var sample = BitConverter.ToSingle(args.Buffer, offset);
                sum += sample * sample;
            }
        }
        else
        {
            for (var offset = 0; offset + 2 <= args.BytesRecorded; offset += 2)
            {
                var sample = BitConverter.ToInt16(args.Buffer, offset) / 32768f;
                sum += sample * sample;
            }
        }

        var rms = MathF.Sqrt((float)(sum / count));
        var target = rms < 0.015f ? 0f : Math.Clamp((rms - 0.015f) * 6.5f, 0f, 1f);
        lock (_gate)
        {
            if (target <= 0f)
            {
                _envelope *= 0.3f;
                if (_envelope < 0.02f)
                {
                    _envelope = 0f;
                }
            }
            else
            {
                _envelope = target > _envelope
                    ? target
                    : (_envelope * 0.35f) + (target * 0.65f);
            }

            _last = Stopwatch.GetTimestamp();
        }
    }
}
