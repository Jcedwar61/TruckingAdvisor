using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace TruckingAdvisor.AI.Streaming;

/// <summary>
/// A tiny local web server for the stream overlay: add http://localhost:PORT/overlay as an OBS (or Streamlabs)
/// Browser Source. It serves one page and pushes live updates over Server-Sent Events (/events): the chat
/// commands, the radio cooldown, and a CB caller banner while a viewer is on the air.
/// Listens on this PC only unless LAN access is switched on. Read-only: it accepts no commands.
/// </summary>
public sealed class StreamOverlayServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TcpListener _listener;
    private readonly Func<object> _state;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<SseClient> _clients = new();
    private readonly Queue<string> _recent = new();
    private Task? _acceptLoop, _keepAlive;

    public int Port { get; }
    public bool AllowLan { get; }
    public string Url => $"http://localhost:{Port}/overlay";
    public int ClientCount { get { lock (_clients) return _clients.Count; } }

    /// <param name="state">Builds the full overlay state (sent on connect and with every <see cref="PushState"/>).</param>
    public StreamOverlayServer(int port, Func<object> state, bool allowLan = false)
    {
        Port = port;
        AllowLan = allowLan;
        _state = state;
        _listener = new TcpListener(allowLan ? IPAddress.Any : IPAddress.Loopback, port);
    }

    /// <summary>Starts listening. Throws <see cref="SocketException"/> if the port is taken.</summary>
    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _keepAlive = Task.Run(() => KeepAliveLoopAsync(_cts.Token));
    }

    /// <summary>Sends the current state to every overlay (call after settings, cooldown or channel changes).</summary>
    public void PushState() => Broadcast("state", _state());

    /// <summary>Sends a radio line (kind: "caller", "reply" or "line") to every overlay.</summary>
    public void PushLine(object line)
    {
        string json = JsonSerializer.Serialize(line, Json);
        lock (_recent)
        {
            _recent.Enqueue(json);
            while (_recent.Count > 8) _recent.Dequeue();
        }
        BroadcastRaw("line", json);
    }

    private void Broadcast(string evt, object data) => BroadcastRaw(evt, JsonSerializer.Serialize(data, Json));

    private void BroadcastRaw(string evt, string json)
    {
        SseClient[] clients;
        lock (_clients) clients = _clients.ToArray();
        foreach (var c in clients) c.Send($"event: {evt}\ndata: {json}\n\n");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => HandleAsync(client, ct));
        }
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { break; }
            BroadcastRaw("ping", "{}");
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        bool keepOpen = false;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            string? path = await ReadRequestPathAsync(stream, ct);
            if (path is null) return;
            string route = path.Split('?')[0].TrimEnd('/').ToLowerInvariant();
            switch (route)
            {
                case "":
                case "/overlay":
                case "/overlay/commands":
                case "/overlay/banner":
                case "/overlay/feed":
                    await WriteAsync(stream, "200 OK", "text/html; charset=utf-8", OverlayHtml(), ct);
                    break;
                case "/state":
                case "/state.json":
                    await WriteAsync(stream, "200 OK", "application/json; charset=utf-8", JsonSerializer.Serialize(_state(), Json), ct);
                    break;
                case "/events":
                    keepOpen = true;
                    await StartEventsAsync(client, stream, ct);
                    break;
                case "/favicon.ico":
                    await WriteAsync(stream, "204 No Content", "text/plain", string.Empty, ct);
                    break;
                default:
                    await WriteAsync(stream, "404 Not Found", "text/plain; charset=utf-8", "Not found. The overlay is at /overlay", ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            if (!keepOpen) client.Dispose();
        }
    }

    private static async Task<string?> ReadRequestPathAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        int total = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token);
            if (n == 0) return null;
            total += n;
            string head = Encoding.ASCII.GetString(buffer, 0, total);
            if (head.Contains("\r\n\r\n") || head.Contains("\n\n"))
            {
                string first = head.Split('\n')[0].Trim();
                var parts = first.Split(' ');
                if (parts.Length < 2 || (parts[0] != "GET" && parts[0] != "HEAD")) return null;
                return parts[1];
            }
        }
        return null;
    }

    private static async Task WriteAsync(Stream stream, string status, string contentType, string body, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\n" +
                        "Cache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private async Task StartEventsAsync(TcpClient tcp, NetworkStream stream, CancellationToken ct)
    {
        const string header = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-store\r\n" +
                              "Access-Control-Allow-Origin: *\r\nConnection: keep-alive\r\n\r\nretry: 2000\n\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        var client = new SseClient(tcp, stream, c => { lock (_clients) _clients.Remove(c); });
        client.Send($"event: state\ndata: {JsonSerializer.Serialize(_state(), Json)}\n\n");
        string[] recent;
        lock (_recent) recent = _recent.ToArray();
        foreach (var line in recent) client.Send($"event: history\ndata: {line}\n\n");
        lock (_clients) _clients.Add(client);
    }

    private static string? _html;

    /// <summary>The overlay page (embedded in the app).</summary>
    public static string OverlayHtml()
    {
        if (_html is not null) return _html;
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("TruckingAdvisor.AI.Streaming.overlay.html")
            ?? throw new InvalidOperationException("Overlay page is missing from the build.");
        using var r = new StreamReader(s, Encoding.UTF8);
        return _html = r.ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        SseClient[] clients;
        lock (_clients) { clients = _clients.ToArray(); _clients.Clear(); }
        foreach (var c in clients) c.Dispose();
        foreach (var t in new[] { _acceptLoop, _keepAlive })
            if (t is not null) { try { await t.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
        _cts.Dispose();
    }

    /// <summary>One connected overlay; writes are queued so a slow browser never blocks the radio.</summary>
    private sealed class SseClient : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly Stream _stream;
        private readonly Action<SseClient> _onClosed;
        private readonly SemaphoreSlim _write = new(1, 1);
        private int _queued;
        private bool _closed;

        public SseClient(TcpClient tcp, Stream stream, Action<SseClient> onClosed)
        {
            _tcp = tcp;
            _stream = stream;
            _onClosed = onClosed;
        }

        public void Send(string frame)
        {
            if (_closed) return;
            // A browser that stopped reading gets dropped instead of piling up memory.
            if (Interlocked.Increment(ref _queued) > 200) { Dispose(); return; }
            byte[] bytes = Encoding.UTF8.GetBytes(frame);
            _ = Task.Run(async () =>
            {
                await _write.WaitAsync();
                try
                {
                    if (_closed) return;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await _stream.WriteAsync(bytes, timeout.Token);
                    await _stream.FlushAsync(timeout.Token);
                }
                catch { Dispose(); }
                finally
                {
                    Interlocked.Decrement(ref _queued);
                    _write.Release();
                }
            });
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            try { _tcp.Dispose(); } catch { }
            _onClosed(this);
        }
    }
}
