using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TruckingAdvisor.Shared.Interfaces;

namespace TruckingAdvisor.AI;

/// <summary>
/// OpenAI client for the CB radio's modular pipeline: speech-to-text -> chat -> text-to-speech.
///
/// - Uses plain HTTPS so models can be swapped in settings without code changes.
/// - Every call has a timeout and returns null on failure (the radio then falls back to offline
///   lines), so AI problems never block telemetry or safety warnings.
/// - Usage is recorded for every call; calls stop once the daily limit is reached.
/// - The API key is read from DPAPI storage on demand and never logged.
/// </summary>
public sealed class OpenAiClientService : ILlmClient, ISpeechToText, ITextToSpeech
{
    private readonly HttpClient _http;
    private readonly AiSettings _settings;
    private readonly AiUsageTracker _usage;
    private readonly Func<string?> _apiKeyProvider;

    /// <summary>Last error, safe to show in the UI (never contains the key).</summary>
    public string? LastError { get; private set; }

    public OpenAiClientService(AiSettings settings, AiUsageTracker usage, Func<string?>? apiKeyProvider = null, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _usage = usage;
        _apiKeyProvider = apiKeyProvider ?? (() => ApiKeyStore.Load(settings.Provider));
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-call timeouts below
    }

    public AiSettings Settings => _settings;
    public AiUsageTracker Usage => _usage;

    public bool IsAvailable =>
        _settings.GlobalAiEnabled &&
        _settings.Provider != AiLlmProvider.Offline &&
        !_usage.IsOverLimit(_settings) &&
        !string.IsNullOrWhiteSpace(_apiKeyProvider());

    bool ISpeechToText.IsAvailable => IsAvailable && _settings.MicrophoneEnabled;
    bool ITextToSpeech.IsAvailable => IsAvailable && _settings.UseOpenAiVoices;

    private string BaseUrl => _settings.ApiBaseUrl.TrimEnd('/');

    private string ChatEndpoint =>
        _settings.Provider == AiLlmProvider.OpenAICompatible && !string.IsNullOrWhiteSpace(_settings.CustomChatEndpoint)
            ? _settings.CustomChatEndpoint
            : $"{BaseUrl}/chat/completions";

    public async Task<LlmResult?> CompleteAsync(string systemPrompt, string userPrompt, int maxOutputTokens, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return null;

        var body = new Dictionary<string, object>
        {
            ["model"] = _settings.ChatModel,
            ["max_completion_tokens"] = Math.Max(16, maxOutputTokens),
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };
        if (_settings.Creativity is { } temperature) body["temperature"] = temperature;
        if (!string.IsNullOrWhiteSpace(_settings.ReasoningEffort)) body["reasoning_effort"] = _settings.ReasoningEffort!;

        using var req = new HttpRequestMessage(HttpMethod.Post, ChatEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var doc = await SendForJsonAsync(req, cancellationToken);
        if (doc is null) return null;

        try
        {
            var root = doc.RootElement;
            string text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
            int input = 0, output = 0;
            if (root.TryGetProperty("usage", out var u))
            {
                if (u.TryGetProperty("prompt_tokens", out var pt)) input = pt.GetInt32();
                if (u.TryGetProperty("completion_tokens", out var ctk)) output = ctk.GetInt32();
            }
            _usage.RecordChat(_settings, input, output);
            text = text.Trim();
            return text.Length == 0 ? null : new LlmResult(text, new LlmUsage(input, output));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            LastError = "Unexpected chat response format.";
            return null;
        }
    }

    public async Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken cancellationToken)
    {
        if (!IsAvailable || wavAudio.Length < 64) return null;

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wavAudio);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "ptt.wav");
        form.Add(new StringContent(_settings.SpeechToTextModel), "model");
        form.Add(new StringContent("json"), "response_format");
        // Bias recognition toward CB/trucking vocabulary.
        form.Add(new StringContent("CB radio trucker talk: breaker one-nine, ten-four, copy, handle, rig, hammer down, bear, Jake brake."), "prompt");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/audio/transcriptions") { Content = form };
        using var doc = await SendForJsonAsync(req, cancellationToken);
        if (doc is null) return null;

        _usage.RecordSpeechToText(_settings, WavDurationSeconds(wavAudio));
        return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
    }

    public async Task<byte[]?> SynthesizeAsync(string text, string voiceId, CancellationToken cancellationToken)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(text)) return null;

        var body = new
        {
            model = _settings.TextToSpeechModel,
            input = text,
            voice = string.IsNullOrWhiteSpace(voiceId) ? "onyx" : voiceId,
            response_format = "wav"
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/audio/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        try
        {
            using var resp = await SendAsync(req, cancellationToken);
            if (resp is null) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
            _usage.RecordTextToSpeech(_settings, text.Length);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LastError = "Voice request failed or timed out.";
            return null;
        }
    }

    private async Task<JsonDocument?> SendForJsonAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(req, ct);
            if (resp is null) return null;
            return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LastError = ex is TaskCanceledException ? "AI request timed out." : "AI request failed.";
            return null;
        }
    }

    private async Task<HttpResponseMessage?> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        string? key = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(key)) { LastError = "No API key saved."; return null; }
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _settings.RequestTimeoutSeconds)));
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token);
        if (resp.IsSuccessStatusCode) { LastError = null; return resp; }

        LastError = (int)resp.StatusCode switch
        {
            401 => "OpenAI rejected the API key (401).",
            404 => "Model not found (404). Check the model names in AI settings.",
            429 => "Rate limited or out of credit (429).",
            _ => $"AI request failed ({(int)resp.StatusCode})."
        };
        resp.Dispose();
        return null;
    }

    /// <summary>Duration of a PCM WAV from its header; 0 if it can't be read.</summary>
    public static double WavDurationSeconds(byte[] wav)
    {
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return 0;
        int byteRate = BitConverter.ToInt32(wav, 28);
        return byteRate > 0 ? Math.Max(0, wav.Length - 44) / (double)byteRate : 0;
    }
}
