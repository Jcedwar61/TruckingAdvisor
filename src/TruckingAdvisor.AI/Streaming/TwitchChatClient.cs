using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace TruckingAdvisor.AI.Streaming;

/// <summary>One chat message from Twitch, with the bits of the IRC tags the radio needs.</summary>
public sealed record TwitchChatMessage(
    string User,
    string DisplayName,
    string Text,
    bool IsBroadcaster = false,
    bool IsModerator = false,
    bool IsVip = false,
    bool IsSubscriber = false,
    string? CustomRewardId = null)
{
    /// <summary>Broadcaster and mods skip the per-viewer cooldown.</summary>
    public bool IsPrivileged => IsBroadcaster || IsModerator;
}

/// <summary>
/// Reads a Twitch channel's chat anonymously over TLS IRC (irc.chat.twitch.tv:6697). No account, token or
/// app registration is needed, and nothing is ever posted to chat. Reconnects on its own with backoff.
/// </summary>
public sealed class TwitchChatClient : IAsyncDisposable
{
    public const string Host = "irc.chat.twitch.tv";
    public const int TlsPort = 6697;

    private readonly string _channel;
    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Raised on a background thread for every chat message in the channel.</summary>
    public event Action<TwitchChatMessage>? MessageReceived;
    /// <summary>Connection status for the UI ("Connected to #name", "Reconnecting in 5 s...").</summary>
    public event Action<string>? StatusChanged;

    public bool IsConnected { get; private set; }
    public string Channel => _channel;
    /// <summary>Backoff after a dropped connection (tests shorten it).</summary>
    public TimeSpan[] RetryDelays { get; set; } = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) };

    /// <param name="channel">Login name of the channel.</param>
    /// <param name="connect">Opens the byte stream (TLS socket by default; tests pass a plain one).</param>
    public TwitchChatClient(string channel, Func<CancellationToken, Task<Stream>>? connect = null)
    {
        _channel = StreamSettings.NormalizeChannel(channel);
        if (_channel.Length == 0) throw new ArgumentException("A Twitch channel name is required.", nameof(channel));
        _connect = connect ?? ConnectTlsAsync;
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    private static async Task<Stream> ConnectTlsAsync(CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(Host, TlsPort, ct);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = Host }, ct);
            return new OwningStream(ssl, tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            bool joined = false;
            try
            {
                StatusChanged?.Invoke($"Connecting to Twitch chat #{_channel}...");
                await using var stream = await _connect(ct);
                using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

                // Anonymous read-only login ("justinfan" + digits needs no password or token).
                await writer.WriteLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands");
                await writer.WriteLineAsync("PASS SCHMOOPIIE");
                await writer.WriteLineAsync($"NICK justinfan{Random.Shared.Next(10000, 99999)}");
                await writer.WriteLineAsync($"JOIN #{_channel}");

                while (!ct.IsCancellationRequested)
                {
                    // Twitch PINGs about every 5 minutes; no traffic for 7 minutes means the link is dead.
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(TimeSpan.FromMinutes(7));
                    string? line = await reader.ReadLineAsync(idle.Token);
                    if (line is null) break;
                    if (line.StartsWith("PING", StringComparison.Ordinal))
                    {
                        await writer.WriteLineAsync("PONG" + line[4..]);
                        continue;
                    }
                    var parsed = ParseLine(line);
                    if (parsed is null) continue;
                    switch (parsed.Command)
                    {
                        case "JOIN" when !joined:
                        case "ROOMSTATE" when !joined:
                            joined = true;
                            failures = 0;
                            IsConnected = true;
                            StatusChanged?.Invoke($"Connected to Twitch chat #{_channel}");
                            break;
                        case "RECONNECT":
                            goto reconnect;
                        case "NOTICE" when parsed.Trailing.Contains("Login", StringComparison.OrdinalIgnoreCase):
                            StatusChanged?.Invoke($"Twitch refused the connection: {parsed.Trailing}");
                            break;
                        case "PRIVMSG":
                            if (ToChatMessage(parsed) is { } msg) MessageReceived?.Invoke(msg);
                            break;
                    }
                }
                reconnect:;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Twitch chat connection problem: {ex.Message}");
            }
            IsConnected = false;
            if (ct.IsCancellationRequested) break;
            var delay = RetryDelays[Math.Min(failures, RetryDelays.Length - 1)];
            failures++;
            StatusChanged?.Invoke($"Twitch chat disconnected. Reconnecting in {delay.TotalSeconds:F0} s...");
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
        IsConnected = false;
    }

    /// <summary>One parsed IRC line: tags, prefix nick, command, channel/params and the trailing text.</summary>
    public sealed record IrcLine(IReadOnlyDictionary<string, string> Tags, string Nick, string Command, string Params, string Trailing);

    /// <summary>Parses a raw IRCv3 line from Twitch. Null for blank or malformed lines.</summary>
    public static IrcLine? ParseLine(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string rest = raw.TrimEnd('\r', '\n');
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rest.StartsWith('@'))
        {
            int sp = rest.IndexOf(' ');
            if (sp < 0) return null;
            foreach (var pair in rest[1..sp].Split(';'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) tags[pair] = string.Empty;
                else tags[pair[..eq]] = UnescapeTag(pair[(eq + 1)..]);
            }
            rest = rest[(sp + 1)..];
        }
        string nick = string.Empty;
        if (rest.StartsWith(':'))
        {
            int sp = rest.IndexOf(' ');
            if (sp < 0) return null;
            string prefix = rest[1..sp];
            int bang = prefix.IndexOf('!');
            nick = bang >= 0 ? prefix[..bang] : prefix;
            rest = rest[(sp + 1)..];
        }
        string trailing = string.Empty;
        int colon = rest.IndexOf(" :", StringComparison.Ordinal);
        if (colon >= 0)
        {
            trailing = rest[(colon + 2)..];
            rest = rest[..colon];
        }
        int cmdEnd = rest.IndexOf(' ');
        string command = cmdEnd < 0 ? rest : rest[..cmdEnd];
        string parameters = cmdEnd < 0 ? string.Empty : rest[(cmdEnd + 1)..];
        if (command.Length == 0) return null;
        return new IrcLine(tags, nick, command, parameters, trailing);
    }

    /// <summary>A PRIVMSG line as a chat message; /me actions are unwrapped. Null if it has no text.</summary>
    public static TwitchChatMessage? ToChatMessage(IrcLine line)
    {
        if (line.Command != "PRIVMSG") return null;
        string text = line.Trailing;
        // "/me waves" arrives as \u0001ACTION waves\u0001
        if (text.StartsWith("\u0001ACTION ", StringComparison.Ordinal)) text = text[8..].TrimEnd('\u0001');
        if (string.IsNullOrWhiteSpace(text)) return null;
        string user = line.Tags.TryGetValue("login", out var l) && l.Length > 0 ? l : line.Nick;
        if (user.Length == 0) return null;
        string display = line.Tags.TryGetValue("display-name", out var d) && d.Length > 0 ? d : user;
        string badges = line.Tags.TryGetValue("badges", out var b) ? b : string.Empty;
        bool Has(string badge) => badges.Split(',').Any(x => x.StartsWith(badge + "/", StringComparison.Ordinal));
        bool mod = line.Tags.TryGetValue("mod", out var m) && m == "1";
        bool sub = (line.Tags.TryGetValue("subscriber", out var s) && s == "1") || Has("subscriber") || Has("founder");
        string? reward = line.Tags.TryGetValue("custom-reward-id", out var r) && r.Length > 0 ? r : null;
        return new TwitchChatMessage(user, display, text.Trim(), Has("broadcaster"), mod || Has("moderator"), Has("vip"), sub, reward);
    }

    private static string UnescapeTag(string v)
    {
        if (v.IndexOf('\\') < 0) return v;
        var sb = new StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] != '\\' || i + 1 >= v.Length) { if (v[i] != '\\') sb.Append(v[i]); continue; }
            char n = v[++i];
            sb.Append(n switch { ':' => ';', 's' => ' ', 'r' => '\r', 'n' => '\n', _ => n });
        }
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* shutting down */ }
        }
        _cts.Dispose();
    }

    /// <summary>A stream that also disposes the socket it reads from.</summary>
    private sealed class OwningStream : Stream
    {
        private readonly Stream _inner;
        private readonly IDisposable _owner;
        public OwningStream(Stream inner, IDisposable owner) { _inner = inner; _owner = owner; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _inner.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.WriteAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _inner.Dispose(); _owner.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
