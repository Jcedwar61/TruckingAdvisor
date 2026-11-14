// SPDX-License-Identifier: LGPL-3.0-or-later
// C# port of the synthesis part of edge_tts/communicate.py (edge-tts 7.3.1, https://github.com/rany2/edge-tts, LGPL-3.0).
using System.Net;
using System.Net.WebSockets;
using System.Security;
using System.Text;

namespace TruckingAdvisor.EdgeTts;

/// <summary>
/// Free Microsoft neural voices (the ones Edge's "Read aloud" uses), the same source SimCoach uses.
/// Needs an internet connection; returns MP3 audio (24 kHz mono).
/// </summary>
public sealed class EdgeTtsClient
{
    private const string BaseUrl = "speech.platform.bing.com/consumer/speech/synthesize/readaloud";
    private const string ChromiumFullVersion = "143.0.3650.75";
    private static readonly string ChromiumMajor = ChromiumFullVersion.Split('.')[0];
    private static readonly string SecMsGecVersion = $"2-{ChromiumFullVersion}";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Speaks <paramref name="text"/> with <paramref name="voice"/> (e.g. "en-US-ChristopherNeural").
    /// Rate like "+0%", pitch like "-6Hz". Throws <see cref="EdgeTtsException"/> on failure.
    /// </summary>
    public async Task<byte[]> SynthesizeMp3Async(string text, string voice, string rate = "+0%", string pitch = "+0Hz", CancellationToken ct = default)
    {
        try
        {
            return await AttemptAsync(text, voice, rate, pitch, ct).ConfigureAwait(false);
        }
        catch (EdgeTtsException ex) when (ex.ServerDate is { } serverDate)
        {
            // 403 because the PC clock is off: correct for the server's clock and try once more.
            EdgeTtsToken.ClockSkewSeconds += (serverDate - DateTimeOffset.UtcNow).TotalSeconds;
            return await AttemptAsync(text, voice, rate, pitch, ct).ConfigureAwait(false);
        }
    }

    private async Task<byte[]> AttemptAsync(string text, string voice, string rate, string pitch, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var token = timeout.Token;

        using var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        ws.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("User-Agent",
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromiumMajor}.0.0.0 Safari/537.36 Edg/{ChromiumMajor}.0.0.0");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        ws.Options.SetRequestHeader("Cookie", "muid=" + Guid.NewGuid().ToString("N").ToUpperInvariant() + ";");

        var uri = new Uri($"wss://{BaseUrl}/edge/v1?TrustedClientToken={EdgeTtsToken.TrustedClientToken}" +
                          $"&ConnectionId={Guid.NewGuid():N}&Sec-MS-GEC={EdgeTtsToken.Generate()}&Sec-MS-GEC-Version={SecMsGecVersion}");
        try
        {
            await ws.ConnectAsync(uri, token).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            DateTimeOffset? date = null;
            if (ws.HttpStatusCode == HttpStatusCode.Forbidden && ws.HttpResponseHeaders is { } h &&
                h.TryGetValue("Date", out var values) && DateTimeOffset.TryParse(values.FirstOrDefault(), out var d))
                date = d;
            throw new EdgeTtsException($"Couldn't reach the voice service ({(int)ws.HttpStatusCode} {ex.Message})", ex) { ServerDate = date };
        }

        string Timestamp() => DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) +
                              " GMT+0000 (Coordinated Universal Time)";

        await SendTextAsync(ws,
            $"X-Timestamp:{Timestamp()}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"true\",\"wordBoundaryEnabled\":\"false\"}," +
            "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n", token).ConfigureAwait(false);

        string ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
                      $"<voice name='{voice}'><prosody pitch='{pitch}' rate='{rate}' volume='+0%'>{Escape(Clean(text))}</prosody></voice></speak>";
        await SendTextAsync(ws,
            $"X-RequestId:{Guid.NewGuid():N}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{Timestamp()}Z\r\nPath:ssml\r\n\r\n{ssml}", token).ConfigureAwait(false);

        using var audio = new MemoryStream();
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new EdgeTtsException($"The voice service closed the connection ({result.CloseStatusDescription}).");
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var data = message.ToArray();
            if (result.MessageType == WebSocketMessageType.Text)
            {
                string s = Encoding.UTF8.GetString(data);
                int end = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                string head = end >= 0 ? s[..end] : s;
                if (head.Contains("Path:turn.end", StringComparison.Ordinal)) break;
            }
            else if (result.MessageType == WebSocketMessageType.Binary && data.Length >= 2)
            {
                int headerLength = (data[0] << 8) | data[1];
                if (headerLength + 2 > data.Length) continue;
                string head = Encoding.UTF8.GetString(data, 2, headerLength);
                if (!head.Contains("Path:audio", StringComparison.Ordinal)) continue;
                if (!head.Contains("Content-Type:audio/mpeg", StringComparison.Ordinal)) continue; // end-of-stream marker, no data
                audio.Write(data, headerLength + 2, data.Length - headerLength - 2);
            }
        }
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); } catch { }
        if (audio.Length == 0) throw new EdgeTtsException("The voice service sent no audio (check the voice name).");
        return audio.ToArray();
    }

    private static Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    /// <summary>The service rejects some control characters; it also has a ~4 KB limit per request.</summary>
    private static string Clean(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is (>= '\u0000' and <= '\u0008') or '\u000B' or '\u000C' or (>= '\u000E' and <= '\u001F')) chars[i] = ' ';
        var s = new string(chars).Trim();
        return s.Length > 1500 ? s[..1500] : s;
    }

    private static string Escape(string s) => SecurityElement.Escape(s) ?? "";
}

public sealed class EdgeTtsException : Exception
{
    public EdgeTtsException(string message, Exception? inner = null) : base(message, inner) { }
    /// <summary>Set on a 403 so the caller can correct the clock and retry.</summary>
    public DateTimeOffset? ServerDate { get; init; }
}
