using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace LightSync.Network;

internal sealed class CozyLink : IDisposable
{
    private static readonly ConcurrentDictionary<string, CozyLink> Links = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _host;
    private readonly object _gate = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private long _serial;

    private CozyLink(string host)
    {
        _host = host;
    }

    public static CozyLink Shared(string host)
    {
        return Links.GetOrAdd(host, static value => new CozyLink(value));
    }

    public void Send(string messageJson)
    {
        lock (_gate)
        {
            var serial = NextSerial();
            var frame = Encoding.UTF8.GetBytes($"{{\"pv\":0,\"cmd\":3,\"sn\":\"{serial}\",\"msg\":{messageJson}}}\r\n");
            try
            {
                Ensure();
                _stream!.Write(frame);
                Drain();
            }
            catch (Exception exception) when (exception is System.IO.IOException or SocketException or ObjectDisposedException)
            {
                Close();
                Ensure();
                _stream!.Write(frame);
                Drain();
            }
        }
    }

    private long NextSerial()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now <= _serial)
        {
            now = _serial + 1;
        }

        _serial = now;
        return now;
    }

    private void Ensure()
    {
        if (_stream is not null)
        {
            return;
        }

        var client = new TcpClient { NoDelay = true };
        client.Connect(_host, 5555);
        _client = client;
        _stream = client.GetStream();
    }

    private void Drain()
    {
        if (_stream is null || !_stream.DataAvailable)
        {
            return;
        }

        var junk = new byte[1024];
        while (_stream.DataAvailable)
        {
            _ = _stream.Read(junk, 0, junk.Length);
        }
    }

    private void Close()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Close();
        }
    }
}
