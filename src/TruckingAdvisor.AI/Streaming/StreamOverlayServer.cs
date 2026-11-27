using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TruckingAdvisor.AI.Streaming;

/// <summary>
/// A tiny local web server for the stream overlay: add http://localhost:PORT/overlay as an OBS (or Streamlabs)
/// Browser Source. It serves one page and pushes live updates over Server-Sent Events (/events): the chat
/// commands, the radio cooldown, and a CB caller banner while a viewer is on the air.
/// It also serves TruckingAdvisor's SimHub panel (/simhub, polled from /simhub/state.json), which the SimHub
/// dashboard shows in its web view; a compact copy of that state (/simhub/dash.json) that the triple-screen
/// dashboards read with SimHub's downloadstringasync; and the dashboard packages (/simhub/NAME.simhubdash).
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
    /// <summary>Builds the SimHub panel's state (null: the panel is switched off, so the page says so).</summary>
    public Func<object?>? PanelState { get; set; }
    /// <summary>Adds settings to /simhub/dash.json (e.g. whether the SimHub plugin should switch screens).</summary>
    public Action<System.Text.Json.Nodes.JsonObject>? DecorateCompact { get; set; }

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
            var request = await ReadRequestAsync(stream, ct);
            if (request is null) return;
            // Only pages served from here (OBS, SimHub, a browser tab on the overlay) and non-browser clients may read
            // the live data: no other website open in the streamer's browser, and no DNS-rebinding name.
            if (!IsAllowedRequest(request.Host, request.Origin, AllowLan, Port))
            {
                await WriteAsync(stream, "403 Forbidden", "text/plain; charset=utf-8", "Forbidden.", ct);
                return;
            }
            string path = request.Path;
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
                case "/simhub":
                case "/simhub/panel":
                    await WriteAsync(stream, "200 OK", "text/html; charset=utf-8", PageHtml("simhub.html"), ct);
                    break;
                case "/simhub/state":
                case "/simhub/state.json":
                    object panel = PanelState?.Invoke() ?? new { available = false };
                    await WriteAsync(stream, "200 OK", "application/json; charset=utf-8", JsonSerializer.Serialize(panel, Json), ct);
                    break;
                case "/simhub/dash.json":
                {
                    var compact = SimHubDashboard.CompactState(PanelState?.Invoke() is { } p ? JsonSerializer.SerializeToNode(p, Json) : null);
                    DecorateCompact?.Invoke(compact);
                    await WriteAsync(stream, "200 OK", "application/json; charset=utf-8", compact.ToJsonString(), ct);
                    break;
                }
                case var r when r.StartsWith("/simhub/", StringComparison.Ordinal) && r.EndsWith(".simhubdash", StringComparison.Ordinal)
                                && SimHubDashboard.FindByFileName(Uri.UnescapeDataString(r["/simhub/".Length..])) is { } layout:
                {
                    using var package = new MemoryStream();
                    SimHubDashboard.WritePackage(package, layout, Port);
                    await WriteAsync(stream, "200 OK", "application/octet-stream", package.ToArray(), ct,
                        $"Content-Disposition: attachment; filename=\"{layout.FileName}\"\r\n");
                    break;
                }
                case "/events":
                    // Kept open only once the client is registered (a failed start must not leak the connection).
                    keepOpen = await StartEventsAsync(client, stream, ct);
                    break;
                case "/favicon.ico":
                    await WriteAsync(stream, "204 No Content", "text/plain", string.Empty, ct);
                    break;
                default:
                    await WriteAsync(stream, "404 Not Found", "text/plain; charset=utf-8", "Not found. The overlay is at /overlay", ct);
                    break;
            }
        }
        catch (Exception) { /* a broken request or a failing state builder only ends this connection */ }
        finally
        {
            if (!keepOpen) client.Dispose();
        }
    }

    /// <summary>The parts of a request this server looks at.</summary>
    private sealed record RequestHead(string Path, string? Host, string? Origin);

    /// <summary>Reads the request line and headers (8 KB at most, within 10 s). Null for anything but a GET or HEAD.</summary>
    private static async Task<RequestHead?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
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
            int end = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) end = head.IndexOf("\n\n", StringComparison.Ordinal);
            if (end >= 0) return ParseRequestHead(head[..end]);
        }
        return null;
    }

    private static RequestHead? ParseRequestHead(string head)
    {
        var lines = head.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var parts = lines[0].Trim().Split(' ');
        if (parts.Length < 2 || (parts[0] != "GET" && parts[0] != "HEAD")) return null;
        string? host = null, origin = null;
        int hosts = 0, origins = 0;
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) { host = value; hosts++; }
            else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) { origin = value; origins++; }
        }
        // Two Host (or Origin) headers: ambiguous, so treat it as not allowed.
        if (hosts > 1) host = "";
        if (origins > 1) origin = "null";
        return new RequestHead(parts[1], host, origin);
    }

    /// <summary>
    /// Whether a request may be answered: its Host must name this PC (never an arbitrary DNS name, which is how DNS
    /// rebinding reaches local servers), and a browser's Origin, if sent, must be this server itself.
    /// Pages served from here (OBS's browser source, SimHub's web view, a browser tab) fetch their own origin, so
    /// browsers send no Origin or a matching one; SimHub's downloadstringasync and the SimHub plugin send none.
    /// </summary>
    public static bool IsAllowedRequest(string? host, string? origin, bool allowLan, int port)
    {
        if (!IsAllowedHost(host, allowLan, port)) return false;
        if (origin is null) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || o.Scheme != Uri.UriSchemeHttp || o.Port != port) return false;
        if (o.AbsolutePath is not ("/" or "") || o.Query.Length > 0 || o.UserInfo.Length > 0) return false;
        string originHost = o.HostNameType == UriHostNameType.IPv6 ? $"[{o.DnsSafeHost}]" : o.Host;
        return IsAllowedHost($"{originHost}:{o.Port}", allowLan, port);
    }

    /// <summary>
    /// localhost, 127.0.0.1 or [::1] (no port or this server's port). With LAN access on, also an IP address or this
    /// PC's own name, for another PC on the network.
    /// </summary>
    public static bool IsAllowedHost(string? host, bool allowLan, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string h = host.Trim();
        string name;
        string? portText = null;
        if (h.StartsWith('['))
        {
            int close = h.IndexOf(']');
            if (close < 0) return false;
            name = h[..(close + 1)];
            string rest = h[(close + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':')) return false;
                portText = rest[1..];
            }
        }
        else
        {
            int colon = h.IndexOf(':');
            if (colon >= 0)
            {
                if (h.IndexOf(':', colon + 1) >= 0) return false; // a bare IPv6 address isn't a valid Host
                portText = h[(colon + 1)..];
                name = h[..colon];
            }
            else name = h;
        }
        if (portText is not null && (!int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int p) || p != port))
            return false;
        if (name.Length == 0) return false;

        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (IsIpLiteral(name, out var ip))
            return IPAddress.IsLoopback(ip) || allowLan;
        if (!allowLan) return false;
        string machine;
        try { machine = Dns.GetHostName(); } catch (SocketException) { return false; }
        return machine.Length > 0 && name.Equals(machine, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly System.Text.RegularExpressions.Regex DottedQuad =
        new(@"^[0-9]{1,3}(\.[0-9]{1,3}){3}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A dotted IPv4 address or a bracketed IPv6 address (not the odd numeric forms a DNS name could mimic).</summary>
    private static bool IsIpLiteral(string name, out IPAddress ip)
    {
        ip = IPAddress.None;
        if (name.StartsWith('[') && name.EndsWith(']'))
        {
            string inner = name[1..^1];
            return inner.Contains(':') && IPAddress.TryParse(inner, out ip!) && ip.AddressFamily == AddressFamily.InterNetworkV6;
        }
        return DottedQuad.IsMatch(name) && IPAddress.TryParse(name, out ip!) && ip.AddressFamily == AddressFamily.InterNetwork;
    }

    private static Task WriteAsync(Stream stream, string status, string contentType, string body, CancellationToken ct) =>
        WriteAsync(stream, status, contentType, Encoding.UTF8.GetBytes(body), ct);

    private static async Task WriteAsync(Stream stream, string status, string contentType, byte[] bytes, CancellationToken ct, string extraHeaders = "")
    {
        // No Access-Control-Allow-Origin: other websites must not be able to read the overlay's or the panel's data.
        string header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\n" + extraHeaders +
                        "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Starts a live event stream. True once the client is registered (it then owns the connection).</summary>
    private async Task<bool> StartEventsAsync(TcpClient tcp, NetworkStream stream, CancellationToken ct)
    {
        const string header = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-store\r\n" +
                              "X-Content-Type-Options: nosniff\r\nConnection: keep-alive\r\n\r\nretry: 2000\n\n";
        string state = JsonSerializer.Serialize(_state(), Json); // may throw: the caller then closes the connection
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        var client = new SseClient(tcp, stream, c => { lock (_clients) _clients.Remove(c); });
        client.Send($"event: state\ndata: {state}\n\n");
        string[] recent;
        lock (_recent) recent = _recent.ToArray();
        foreach (var line in recent) client.Send($"event: history\ndata: {line}\n\n");
        lock (_clients) _clients.Add(client);
        client.Start();
        // Shutting down meanwhile: DisposeAsync may already have closed the others.
        if (ct.IsCancellationRequested) client.Dispose();
        return true;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Pages = new();

    /// <summary>The overlay page (embedded in the app).</summary>
    public static string OverlayHtml() => PageHtml("overlay.html");

    /// <summary>The SimHub panel page (embedded in the app).</summary>
    public static string SimHubPanelHtml() => PageHtml("simhub.html");

    private static string PageHtml(string file) => Pages.GetOrAdd(file, f =>
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"TruckingAdvisor.AI.Streaming.{f}")
            ?? throw new InvalidOperationException($"{f} is missing from the build.");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    });

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

    /// <summary>
    /// One connected overlay. Frames go through a queue with a single writer, so they arrive in order and a slow
    /// browser never blocks the radio; a browser that stops reading (200 frames behind) is dropped.
    /// </summary>
    private sealed class SseClient : IDisposable
    {
        public const int MaxQueuedFrames = 200;
        private readonly TcpClient _tcp;
        private readonly Stream _stream;
        private readonly Action<SseClient> _onClosed;
        private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(MaxQueuedFrames) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private int _closed;

        public SseClient(TcpClient tcp, Stream stream, Action<SseClient> onClosed)
        {
            _tcp = tcp;
            _stream = stream;
            _onClosed = onClosed;
        }

        /// <summary>Starts the writer loop (frames sent before this wait in the queue).</summary>
        public void Start() => _ = Task.Run(WriteLoopAsync);

        public void Send(string frame)
        {
            if (Volatile.Read(ref _closed) != 0) return;
            if (!_queue.Writer.TryWrite(Encoding.UTF8.GetBytes(frame))) Dispose(); // full (or closed): drop the client
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                await foreach (var bytes in _queue.Reader.ReadAllAsync())
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await _stream.WriteAsync(bytes, timeout.Token);
                    await _stream.FlushAsync(timeout.Token);
                }
            }
            catch { /* the browser went away */ }
            finally { Dispose(); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _queue.Writer.TryComplete();
            try { _tcp.Dispose(); } catch { }
            _onClosed(this);
        }
    }
}
