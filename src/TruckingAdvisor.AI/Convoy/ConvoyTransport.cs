using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TruckingAdvisor.AI.Convoy;

/// <summary>A pub/sub room that carries convoy packets between PCs.</summary>
public interface IConvoyTransport
{
    Task PublishAsync(string payload, CancellationToken cancellationToken);
    /// <summary>Yields each message published to the room (including this PC's own) until cancelled or dropped.</summary>
    IAsyncEnumerable<string> SubscribeAsync(CancellationToken cancellationToken);
}

/// <summary>Thrown when the relay server refuses a message (rate limit, size, outage).</summary>
public sealed class ConvoyRelayException : Exception
{
    public HttpStatusCode? Status { get; }
    public ConvoyRelayException(string message, HttpStatusCode? status = null) : base(message) => Status = status;
}

/// <summary>
/// ntfy (https://ntfy.sh, or a self-hosted server) as the convoy room: HTTP POST to publish, a streamed
/// JSON line feed to subscribe. No account, key or port forwarding needed.
/// </summary>
public sealed class NtfyConvoyTransport : IConvoyTransport
{
    private readonly HttpClient _http;
    private readonly string _topicUrl;

    public NtfyConvoyTransport(HttpClient http, string server, string topic)
    {
        _http = http;
        string s = string.IsNullOrWhiteSpace(server) ? "https://ntfy.sh" : server.Trim().TrimEnd('/');
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "https://" + s;
        _topicUrl = $"{s}/{topic}";
    }

    public async Task PublishAsync(string payload, CancellationToken ct)
    {
        using var content = new StringContent(payload, Encoding.UTF8, "text/plain");
        using var req = new HttpRequestMessage(HttpMethod.Post, _topicUrl) { Content = content };
        // Convoy packets are machine data: no phone notifications, kept only briefly.
        req.Headers.TryAddWithoutValidation("X-Cache", "no");
        req.Headers.TryAddWithoutValidation("X-Firebase", "no");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ConvoyRelayException("The convoy relay's free message limit was reached. Try again later, or use your own ntfy server.", resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
            throw new ConvoyRelayException($"The convoy relay refused a message ({(int)resp.StatusCode}).", resp.StatusCode);
    }

    public async IAsyncEnumerable<string> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _topicUrl + "/json");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new ConvoyRelayException($"Couldn't join the convoy relay ({(int)resp.StatusCode}).", resp.StatusCode);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            // ntfy sends a keepalive about every 45 s; silence for 2 minutes means the stream is dead.
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(TimeSpan.FromMinutes(2));
            string? line;
            try { line = await reader.ReadLineAsync(idle.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { yield break; }
            if (line is null) yield break;
            if (line.Length == 0) continue;
            string? message = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("event", out var ev) && ev.GetString() == "message" &&
                    doc.RootElement.TryGetProperty("message", out var m))
                    message = m.GetString();
            }
            catch (JsonException) { }
            if (message is not null) yield return message;
        }
    }
}

/// <summary>Why <see cref="ConvoyCrypto.TryOpen"/> turned a message down.</summary>
public enum ConvoyOpenResult
{
    Ok,
    /// <summary>Not sealed with this convoy's key (another convoy, an older TruckingAdvisor, or tampered).</summary>
    NotOurs,
    /// <summary>Sealed with our key, but stamped more than <see cref="ConvoyCrypto.MaxClockDifference"/> away from this PC's clock
    /// (a replayed old message, or a PC whose clock is wrong).</summary>
    ClockOff
}

/// <summary>
/// Turns a convoy code into a relay topic and an encryption key (convoy protocol v2).
/// The code is the convoy's only secret: it is stretched with PBKDF2-SHA256 (600,000 rounds), and HKDF then derives two
/// independent values from that, the AES-GCM key and the public topic id, so the topic reveals nothing usable about the
/// key. Codes need at least 12 letters or digits (<see cref="GenerateCode"/> makes 16 random ones, 80 bits), so the
/// topic, which anyone on the relay can see, can't be used to guess the code offline.
/// Every packet carries the time it was sealed (inside the encryption) and is bound to the topic (associated data);
/// packets more than 10 minutes from this PC's clock are refused, so old traffic can't be replayed.
/// Packets from the old v1 protocol simply fail to open and are ignored.
/// </summary>
public sealed class ConvoyCrypto
{
    public const byte ProtocolVersion = 2;
    public const int MinCodeLength = 12;
    public const int Pbkdf2Iterations = 600_000;
    public static readonly TimeSpan MaxClockDifference = TimeSpan.FromMinutes(10);
    public const string TooShortMessage =
        "The convoy code is too short: it needs at least 12 letters or digits. Click \"Generate code\", share the new code with your convoy (off stream), and Save & apply.";

    /// <summary>Base32 without the look-alikes I, O, 0 and 1.</summary>
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int MaxSealedLength = 64 * 1024;
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("TruckingAdvisor.Convoy.v2.salt");
    private static readonly byte[] KeyInfo = Encoding.UTF8.GetBytes("TruckingAdvisor.Convoy.v2 encryption key");
    private static readonly byte[] TopicInfo = Encoding.UTF8.GetBytes("TruckingAdvisor.Convoy.v2 topic id");
    // PBKDF2 at 600k rounds takes a noticeable moment: each code is derived once per run (Save & apply re-uses it).
    private static readonly Dictionary<string, (byte[] Key, string Topic)> Derived = new(StringComparer.Ordinal);

    private readonly byte[] _key;
    private readonly byte[] _associatedData;

    /// <summary>The relay topic (public: it is part of the relay URL).</summary>
    public string Topic { get; }
    /// <summary>The clock used to stamp and check packets (injectable for tests).</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Derives the key (slow: use <see cref="CreateAsync"/> from a UI thread). Throws <see cref="ArgumentException"/> for a short code.</summary>
    public ConvoyCrypto(string convoyCode)
    {
        string code = NormalizeCode(convoyCode);
        if (code.Length < MinCodeLength) throw new ArgumentException(TooShortMessage, nameof(convoyCode));
        (_key, Topic) = Derive(code);
        _associatedData = Encoding.UTF8.GetBytes($"TruckingAdvisor.Convoy.v{ProtocolVersion}|{Topic}");
    }

    /// <summary>Derives the key on a worker thread.</summary>
    public static Task<ConvoyCrypto> CreateAsync(string convoyCode) => Task.Run(() => new ConvoyCrypto(convoyCode));

    /// <summary>Codes ignore case, spaces and dashes ("abcd-efgh-jkmn-pqrs" = "ABCD EFGH JKMN PQRS").</summary>
    public static string NormalizeCode(string? code) =>
        new string((code ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>True when the code has enough letters or digits to be used.</summary>
    public static bool IsCodeLongEnough(string? code) => NormalizeCode(code).Length >= MinCodeLength;

    /// <summary>A new random convoy code: 16 base32 characters (80 bits) in groups of four, e.g. ABCD-EFGH-JKLM-NPQR.</summary>
    public static string GenerateCode()
    {
        string raw = RandomNumberGenerator.GetString(CodeAlphabet, 16);
        return $"{raw[..4]}-{raw[4..8]}-{raw[8..12]}-{raw[12..]}";
    }

    private static (byte[] Key, string Topic) Derive(string code)
    {
        lock (Derived)
            if (Derived.TryGetValue(code, out var known)) return known;
        byte[] master = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code), Salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        byte[] key = HKDF.Expand(HashAlgorithmName.SHA256, master, 32, KeyInfo);
        byte[] topicId = HKDF.Expand(HashAlgorithmName.SHA256, master, 16, TopicInfo);
        CryptographicOperations.ZeroMemory(master);
        var result = (key, "ta_convoy2_" + Convert.ToHexString(topicId).ToLowerInvariant());
        lock (Derived)
        {
            if (Derived.Count >= 8) Derived.Clear();
            Derived[code] = result;
        }
        return result;
    }

    /// <summary>Encrypts a packet: version byte, nonce, then AES-GCM over (send time + text), bound to the topic.</summary>
    public string Seal(string plaintext)
    {
        byte[] body = Encoding.UTF8.GetBytes(plaintext);
        byte[] plain = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt64BigEndian(plain, Clock().ToUnixTimeMilliseconds());
        body.CopyTo(plain, 8);
        byte[] output = new byte[1 + 12 + plain.Length + 16];
        output[0] = ProtocolVersion;
        var nonce = output.AsSpan(1, 12);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(_key, 16))
            aes.Encrypt(nonce, plain, output.AsSpan(13, plain.Length), output.AsSpan(13 + plain.Length, 16), _associatedData);
        return Convert.ToBase64String(output);
    }

    /// <summary>The plaintext, or null if the message wasn't sealed with this convoy's key (or is too old or new).</summary>
    public string? Open(string sealedText) => TryOpen(sealedText, out string? plain) == ConvoyOpenResult.Ok ? plain : null;

    /// <summary>Decrypts and checks a packet's version and send time.</summary>
    public ConvoyOpenResult TryOpen(string sealedText, out string? plaintext)
    {
        plaintext = null;
        if (string.IsNullOrWhiteSpace(sealedText) || sealedText.Length > MaxSealedLength) return ConvoyOpenResult.NotOurs;
        try
        {
            byte[] all = Convert.FromBase64String(sealedText.Trim());
            if (all.Length < 1 + 12 + 8 + 16 || all[0] != ProtocolVersion) return ConvoyOpenResult.NotOurs;
            int length = all.Length - 1 - 12 - 16;
            byte[] plain = new byte[length];
            using (var aes = new AesGcm(_key, 16))
                aes.Decrypt(all.AsSpan(1, 12), all.AsSpan(13, length), all.AsSpan(13 + length, 16), plain, _associatedData);
            var sent = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(plain));
            if ((Clock() - sent).Duration() > MaxClockDifference) return ConvoyOpenResult.ClockOff;
            plaintext = Encoding.UTF8.GetString(plain, 8, length - 8);
            return ConvoyOpenResult.Ok;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentOutOfRangeException)
        {
            return ConvoyOpenResult.NotOurs;
        }
    }
}
