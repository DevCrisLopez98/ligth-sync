using System.Collections.Concurrent;
using System.Net.Sockets;
using LightSync.OpenRgb;

namespace LightSync.Network;

public sealed class NetworkDirector
{
    private readonly HashSet<string> _synced = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _on = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _wake = new();
    private readonly Thread _clock;
    private IReadOnlyList<NetworkDevice> _devices = [];
    private int _packed;
    private bool _stop;
    private int _lastHue = -1;
    private int _lastBrightness = -1;
    private readonly ConcurrentDictionary<string, long> _nextSend = new(StringComparer.OrdinalIgnoreCase);

    public NetworkDirector()
    {
        _clock = new Thread(Follow)
        {
            IsBackground = true,
            Name = "LightSync.Bulbs",
        };
        _clock.Start();
    }

    public IReadOnlyList<NetworkDevice> Devices => _devices;

    public IReadOnlyCollection<string> SyncedIds => _synced;

    public void Restore(IEnumerable<string>? synced)
    {
        _synced.Clear();
        if (synced is null)
        {
            return;
        }

        foreach (var id in synced)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                _synced.Add(id);
            }
        }
    }

    public void Replace(IReadOnlyList<NetworkDevice> devices)
    {
        _devices = devices;
        _on.Clear();
        _lastHue = -1;
    }

    public void Stop()
    {
        _stop = true;
        Pulse();
    }

    public void Note(RgbColor color)
    {
        Interlocked.Exchange(ref _packed, unchecked((int)color.Packed));
        Pulse();
    }

    public bool IsSynced(string id) => _synced.Contains(id);

    public bool IsOn(NetworkDevice device)
    {
        return _on.TryGetValue(device.Id, out var on) ? on : device.IsOn == true;
    }

    public async Task SetSyncAsync(NetworkDevice device, bool sync, RgbColor active, CancellationToken cancellationToken)
    {
        if (device.Kind != NetworkKind.Light)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sync)
            {
                _synced.Add(device.Id);
                _on[device.Id] = true;
                PaintSynced(active);
                _lastHue = -1;
                return;
            }

            _synced.Remove(device.Id);
            _on[device.Id] = true;
            await SendColdWhiteAsync(device, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (sync)
            {
                _synced.Remove(device.Id);
            }
            else
            {
                _synced.Add(device.Id);
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetPowerAsync(NetworkDevice device, bool on, RgbColor active, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _on[device.Id] = on;
            if (!on)
            {
                await SendPowerAsync(device, false, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (device.Kind == NetworkKind.Light && IsSynced(device.Id))
            {
                await PaintOneAsync(device, active, cancellationToken).ConfigureAwait(false);
                _lastHue = -1;
                return;
            }

            if (device.Kind == NetworkKind.Light)
            {
                await SendColdWhiteAsync(device, cancellationToken).ConfigureAwait(false);
                return;
            }

            await SendPowerAsync(device, true, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _on.Remove(device.Id);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Follow()
    {
        while (!_stop)
        {
            if (_synced.Count == 0)
            {
                Wait();
                continue;
            }

            var color = Unpack(Volatile.Read(ref _packed));
            var hsv = color.ToHsv();
            var hue = (int)Math.Round(hsv.Hue * 360f);
            var brightness = (int)Math.Round(hsv.Value * 100f);
            if (hue == _lastHue && brightness == _lastBrightness)
            {
                Wait();
                continue;
            }

            var targets = _devices.Where(device => device.Kind == NetworkKind.Light && IsSynced(device.Id) && IsOn(device)).ToArray();
            if (targets.Length == 0)
            {
                Wait();
                continue;
            }

            var saturation = (int)Math.Round(hsv.Saturation * 100f);
            var now = Environment.TickCount64;
            Parallel.ForEach(targets, device =>
            {
                if (_nextSend.TryGetValue(device.Id, out var next) && now < next)
                {
                    return;
                }

                try
                {
                    SendColorNow(device, hue, saturation, brightness);
                    _nextSend[device.Id] = 0;
                }
                catch (Exception exception) when (exception is System.IO.IOException or SocketException or InvalidOperationException or ObjectDisposedException)
                {
                    _nextSend[device.Id] = Environment.TickCount64 + 8000;
                }
            });

            _lastHue = hue;
            _lastBrightness = brightness;
        }
    }

    private void Wait()
    {
        lock (_wake)
        {
            Monitor.Wait(_wake, 20);
        }
    }

    private void Pulse()
    {
        lock (_wake)
        {
            Monitor.Pulse(_wake);
        }
    }

    private static RgbColor Unpack(int packed)
    {
        return new RgbColor((byte)packed, (byte)(packed >> 8), (byte)(packed >> 16));
    }

    private void PaintSynced(RgbColor color)
    {
        var hsv = color.ToHsv();
        var hue = (int)Math.Round(hsv.Hue * 360f);
        var saturation = (int)Math.Round(hsv.Saturation * 100f);
        var brightness = (int)Math.Round(hsv.Value * 100f);
        var targets = _devices.Where(item => item.Kind == NetworkKind.Light && IsSynced(item.Id) && IsOn(item)).ToArray();
        Parallel.ForEach(targets, item => SendColorNow(item, hue, saturation, brightness));
    }

    private static async Task PaintOneAsync(NetworkDevice device, RgbColor color, CancellationToken cancellationToken)
    {
        var hsv = color.ToHsv();
        var hue = (int)Math.Round(hsv.Hue * 360f);
        var saturation = (int)Math.Round(hsv.Saturation * 100f);
        var brightness = (int)Math.Round(hsv.Value * 100f);
        SendColorNow(device, hue, saturation, brightness);
        await Task.CompletedTask;
    }

    private static void SendColorNow(NetworkDevice device, int hue, int saturation, int brightness)
    {
        if (device.Transport == "Kasa")
        {
            KasaDiscovery.SendNow(
                device.Host,
                "{\"smartlife.iot.smartbulb.lightingservice\":{\"transition_light_state\":{"
                + $"\"on_off\":1,\"ignore_default\":1,\"transition\":0,\"mode\":\"normal\",\"hue\":{hue},\"saturation\":{saturation},\"color_temp\":0,\"brightness\":{brightness}"
                + "}}}");
            return;
        }

        CozyLifeDiscovery.SendNow(
            device.Host,
            "{\"attr\":[1,2,3,4,5,6],\"data\":{"
            + $"\"1\":1,\"2\":0,\"3\":65535,\"4\":{brightness * 10},\"5\":{hue},\"6\":{saturation * 10}"
            + "}}");
    }

    private static async Task SendColdWhiteAsync(NetworkDevice device, CancellationToken cancellationToken)
    {
        if (device.Transport == "Kasa")
        {
            const string json = "{\"smartlife.iot.smartbulb.lightingservice\":{\"transition_light_state\":{"
                + "\"on_off\":1,\"ignore_default\":1,\"transition\":0,\"mode\":\"normal\",\"hue\":0,\"saturation\":0,\"color_temp\":7000,\"brightness\":100"
                + "}}}";
            await KasaDiscovery.SendAsync(device.Host, json, cancellationToken).ConfigureAwait(false);
            return;
        }

        const string message = "{\"attr\":[1,2,3,4,5,6],\"data\":{\"1\":1,\"2\":0,\"3\":1000,\"4\":1000,\"5\":65535,\"6\":65535}}";
        await CozyLifeDiscovery.SetAsync(device.Host, message, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendPowerAsync(NetworkDevice device, bool on, CancellationToken cancellationToken)
    {
        if (device.Kind == NetworkKind.Plug)
        {
            var state = on ? 1 : 0;
            await KasaDiscovery.SendAsync(
                device.Host,
                "{\"system\":{\"set_relay_state\":{\"state\":" + state + "}}}",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!on && device.Transport == "Kasa")
        {
            await KasaDiscovery.SendAsync(
                device.Host,
                "{\"smartlife.iot.smartbulb.lightingservice\":{\"transition_light_state\":{\"on_off\":0,\"transition\":0}}}",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!on)
        {
            await CozyLifeDiscovery.SetAsync(
                device.Host,
                "{\"attr\":[1],\"data\":{\"1\":0}}",
                cancellationToken).ConfigureAwait(false);
        }
    }

}
