using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TruckingAdvisor.AI;

/// <summary>Raw text-message transport (a WebSocket in the app, a fake in tests).</summary>
public interface IRealtimeTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct);
    Task SendAsync(string json, CancellationToken ct);
    /// <summary>Next server message, or null when the connection closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);
}

public sealed class WebSocketRealtimeTransport : IRealtimeTransport
{
    private readonly ClientWebSocket _ws = new();

    public async Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct)
    {
        _ws.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        await _ws.ConnectAsync(uri, ct);
    }

    public Task SendAsync(string json, CancellationToken ct) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await _ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch { /* closing is best effort */ }
        _ws.Dispose();
    }
}

public sealed record RealtimeReply(string Text, byte[] WavAudio);

/// <summary>
/// One push-to-talk exchange over the OpenAI Realtime API (speech in, speech out):
/// 1. <see cref="SendPlayerAudioAsync"/> uploads the recording and waits for its transcript
///    (so app commands can be caught before anyone answers);
/// 2. <see cref="ReplyAsync"/> asks the model to answer in voice and returns the audio and its text.
/// Handles both the GA and beta event names. Turn detection is off: push-to-talk decides turns.
/// </summary>
public sealed class RealtimeVoiceSession : IAsyncDisposable
{
    public const int SampleRate = 24000;
    private readonly IRealtimeTransport _transport;
    private readonly AiSettings _settings;
    private readonly AiUsageTracker _usage;

    public string? LastError { get; private set; }
    /// <summary>Longest wait for the server during one step before giving up (then the radio falls back).</summary>
    public TimeSpan StepTimeout { get; set; } = TimeSpan.FromSeconds(20);
    private string _voice = "ash";

    private RealtimeVoiceSession(IRealtimeTransport transport, AiSettings settings, AiUsageTracker usage)
    {
        _transport = transport;
        _settings = settings;
        _usage = usage;
    }

    public static async Task<RealtimeVoiceSession> StartAsync(IRealtimeTransport transport, AiSettings settings, AiUsageTracker usage,
        string apiKey, string instructions, string voice, CancellationToken ct)
    {
        var session = new RealtimeVoiceSession(transport, settings, usage);
        string baseUrl = settings.ApiBaseUrl.TrimEnd('/').Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase);
        await transport.ConnectAsync(new Uri($"{baseUrl}/realtime?model={Uri.EscapeDataString(settings.RealtimeModel)}"), apiKey, ct);
        session._voice = voice;
        await transport.SendAsync(SessionUpdate(instructions, voice, settings), ct);
        session.StepTimeout = TimeSpan.FromSeconds(Math.Max(10, settings.RequestTimeoutSeconds));
        return session;
    }

    public static string SessionUpdate(string instructions, string voice, AiSettings settings) => new JsonObject
    {
        ["type"] = "session.update",
        ["session"] = new JsonObject
        {
            ["type"] = "realtime",
            ["model"] = settings.RealtimeModel,
            ["instructions"] = instructions,
            ["output_modalities"] = new JsonArray("audio"),
            ["audio"] = new JsonObject
            {
                ["input"] = new JsonObject
                {
                    ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = SampleRate },
                    ["transcription"] = new JsonObject { ["model"] = settings.SpeechToTextModel },
                    ["turn_detection"] = null
                },
                ["output"] = new JsonObject
                {
                    ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = SampleRate },
                    ["voice"] = string.IsNullOrWhiteSpace(voice) ? "ash" : voice
                }
            }
        }
    }.ToJsonString();

    /// <summary>Switches the character (system prompt) and voice before the reply, once we know who's answering.</summary>
    public Task UpdateInstructionsAsync(string instructions, string voice, CancellationToken ct)
    {
        _voice = string.IsNullOrWhiteSpace(voice) ? _voice : voice;
        return _transport.SendAsync(SessionUpdate(instructions, _voice, _settings), ct);
    }

    /// <summary>
    /// Uploads the player's PTT recording (any PCM16 WAV) and returns what they said
    /// ("" if nothing was said), or null if the realtime service failed.
    /// </summary>
    public async Task<string?> SendPlayerAudioAsync(byte[] wav, CancellationToken ct)
    {
        using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
        step.CancelAfter(StepTimeout);
        try { return await SendPlayerAudioCoreAsync(wav, step.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { LastError = "Realtime transcription timed out."; return null; }
    }

    private async Task<string?> SendPlayerAudioCoreAsync(byte[] wav, CancellationToken ct)
    {
        byte[] pcm = WavAudio.ToPcm16Mono(wav, SampleRate);
        if (pcm.Length < SampleRate / 5) return string.Empty; // under ~0.1 s: nothing said
        for (int i = 0; i < pcm.Length; i += 12_000) // 250 ms per message
        {
            string chunk = Convert.ToBase64String(pcm, i, Math.Min(12_000, pcm.Length - i));
            await _transport.SendAsync(new JsonObject { ["type"] = "input_audio_buffer.append", ["audio"] = chunk }.ToJsonString(), ct);
        }
        await _transport.SendAsync("""{"type":"input_audio_buffer.commit"}""", ct);
        _usage.RecordSpeechToText(_settings, pcm.Length / 2.0 / SampleRate);

        while (await NextAsync(ct) is { } msg)
        {
            switch (Type(msg))
            {
                case "conversation.item.input_audio_transcription.completed":
                    return msg["transcript"]?.GetValue<string>()?.Trim() ?? string.Empty;
                case "conversation.item.input_audio_transcription.failed":
                    LastError = "Realtime transcription failed.";
                    return null;
                case "error":
                    LastError = ErrorText(msg);
                    return null;
            }
        }
        return null;
    }

    /// <summary>Asks the model to answer (in the session's character) and returns its voice and text, or null on failure.</summary>
    public async Task<RealtimeReply?> ReplyAsync(int maxOutputTokens, CancellationToken ct)
    {
        using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
        step.CancelAfter(StepTimeout);
        try { return await ReplyCoreAsync(maxOutputTokens, step.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { LastError = "Realtime reply timed out."; return null; }
    }

    private async Task<RealtimeReply?> ReplyCoreAsync(int maxOutputTokens, CancellationToken ct)
    {
        await _transport.SendAsync(new JsonObject
        {
            ["type"] = "response.create",
            ["response"] = new JsonObject { ["max_output_tokens"] = Math.Max(64, maxOutputTokens * 4) } // audio tokens included
        }.ToJsonString(), ct);

        using var pcm = new MemoryStream();
        var text = new StringBuilder();
        while (await NextAsync(ct) is { } msg)
        {
            switch (Type(msg))
            {
                case "response.output_audio.delta":
                case "response.audio.delta":
                    var bytes = Convert.FromBase64String(msg["delta"]?.GetValue<string>() ?? "");
                    pcm.Write(bytes, 0, bytes.Length);
                    break;
                case "response.output_audio_transcript.delta":
                case "response.audio_transcript.delta":
                    text.Append(msg["delta"]?.GetValue<string>());
                    break;
                case "response.output_audio_transcript.done":
                case "response.audio_transcript.done":
                    if (msg["transcript"]?.GetValue<string>() is { Length: > 0 } t) { text.Clear(); text.Append(t); }
                    break;
                case "response.done":
                    var usage = msg["response"]?["usage"];
                    _usage.RecordChat(_settings, usage?["input_tokens"]?.GetValue<int>() ?? 0, usage?["output_tokens"]?.GetValue<int>() ?? 0);
                    if (msg["response"]?["status"]?.GetValue<string>() is "failed" or "cancelled") { LastError = "Realtime reply failed."; return null; }
                    return pcm.Length == 0 ? null : new RealtimeReply(text.ToString().Trim(), WavAudio.FromPcm16Mono(pcm.ToArray(), SampleRate));
                case "error":
                    LastError = ErrorText(msg);
                    return null;
            }
        }
        return null;
    }

    private async Task<JsonNode?> NextAsync(CancellationToken ct)
    {
        string? raw = await _transport.ReceiveAsync(ct);
        if (raw is null) return null;
        try { return JsonNode.Parse(raw); } catch (JsonException) { return new JsonObject(); }
    }

    private static string? Type(JsonNode msg) => msg["type"]?.GetValue<string>();
    private static string ErrorText(JsonNode msg) => "Realtime error: " + (msg["error"]?["message"]?.GetValue<string>() ?? "unknown");

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}

/// <summary>Minimal PCM16 WAV helpers for the realtime pipeline.</summary>
public static class WavAudio
{
    /// <summary>Extracts mono PCM16 samples from a WAV and resamples them to <paramref name="targetRate"/>.</summary>
    public static byte[] ToPcm16Mono(byte[] wav, int targetRate)
    {
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return Array.Empty<byte>();
        int channels = BitConverter.ToInt16(wav, 22);
        int rate = BitConverter.ToInt32(wav, 24);
        int bits = BitConverter.ToInt16(wav, 34);
        if (bits != 16 || channels < 1 || rate <= 0) return Array.Empty<byte>();

        // Find the "data" chunk.
        int pos = 12, dataStart = -1, dataLen = 0;
        while (pos + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, pos, 4);
            int len = BitConverter.ToInt32(wav, pos + 4);
            if (id == "data") { dataStart = pos + 8; dataLen = Math.Min(len < 0 ? int.MaxValue : len, wav.Length - dataStart); break; }
            pos += 8 + len + (len & 1);
        }
        if (dataStart < 0) return Array.Empty<byte>();

        int frames = dataLen / (2 * channels);
        var mono = new short[frames];
        for (int i = 0; i < frames; i++)
        {
            int sum = 0;
            for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(wav, dataStart + (i * channels + c) * 2);
            mono[i] = (short)(sum / channels);
        }

        int outFrames = (int)((long)frames * targetRate / rate);
        var output = new byte[outFrames * 2];
        for (int i = 0; i < outFrames; i++)
        {
            double src = (double)i * rate / targetRate;
            int i0 = (int)src;
            int i1 = Math.Min(i0 + 1, frames - 1);
            double frac = src - i0;
            short v = (short)Math.Round(mono[i0] + (mono[i1] - mono[i0]) * frac);
            output[i * 2] = (byte)(v & 0xFF);
            output[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return output;
    }

    /// <summary>Wraps raw mono PCM16 in a WAV header.</summary>
    public static byte[] FromPcm16Mono(byte[] pcm, int rate)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }
}
