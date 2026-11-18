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

/// <summary>
/// Turns a convoy code into a room name and an encryption key. The room name is a hash, and every packet is
/// AES-GCM encrypted with a key derived from the code, so the relay server (and anyone guessing room names)
/// can't read the convoy's radio traffic. Everyone with the same code gets the same room and key.
/// </summary>
public sealed class ConvoyCrypto
{
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("TruckingAdvisor.Convoy.v1");
    private readonly byte[] _key;

    public string Topic { get; }

    public ConvoyCrypto(string convoyCode)
    {
        string code = NormalizeCode(convoyCode);
        if (code.Length < 4) throw new ArgumentException("A convoy code needs at least 4 letters or digits.", nameof(convoyCode));
        _key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code), Salt, 60_000, HashAlgorithmName.SHA256, 32);
        byte[] topicHash = SHA256.HashData(_key.Concat(Encoding.UTF8.GetBytes("topic")).ToArray());
        Topic = "ta_convoy_" + Convert.ToHexString(topicHash, 0, 12).ToLowerInvariant();
    }

    /// <summary>Codes ignore case, spaces and dashes ("conv-7740" = "CONV 7740").</summary>
    public static string NormalizeCode(string? code) =>
        new string((code ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public string Seal(string plaintext)
    {
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(_key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(cipher).Concat(tag).ToArray());
    }

    /// <summary>The plaintext, or null if the message wasn't sealed with this convoy's key.</summary>
    public string? Open(string sealedText)
    {
        try
        {
            byte[] all = Convert.FromBase64String(sealedText.Trim());
            if (all.Length < 12 + 16) return null;
            var nonce = all.AsSpan(0, 12);
            var tag = all.AsSpan(all.Length - 16);
            var cipher = all.AsSpan(12, all.Length - 28);
            byte[] plain = new byte[cipher.Length];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
