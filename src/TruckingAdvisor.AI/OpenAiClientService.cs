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
    bool ITextToSpeech.IsAvailable => IsAvailable && _settings.VoiceEngine == TruckingAdvisor.Shared.Enums.VoiceEngine.OpenAI;

    private string BaseUrl => _settings.ApiBaseUrl.TrimEnd('/');

    private string ChatEndpoint =>
        _settings.Provider == AiLlmProvider.OpenAICompatible && !string.IsNullOrWhiteSpace(_settings.CustomChatEndpoint)
            ? _settings.CustomChatEndpoint
            : $"{BaseUrl}/chat/completions";

    // ---- Model choice: built-in lists, first usable model wins, rejected models are skipped ----

    private readonly HashSet<string> _unusable = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _available;   // from GET /models; null = unknown (don't filter)
    private readonly object _modelLock = new();

    /// <summary>Models on the built-in list that haven't been rejected, best first.</summary>
    public IReadOnlyList<string> Candidates(IReadOnlyList<string> list)
    {
        lock (_modelLock)
        {
            var usable = list.Where(m => !_unusable.Contains(m) && (_available is null || _available.Contains(m))).ToList();
            // If everything was filtered out, try the whole list again rather than give up.
            return usable.Count > 0 ? usable : list.Where(m => !_unusable.Contains(m)).DefaultIfEmpty(list[^1]).ToList();
        }
    }

    private void MarkUnusable(string model, string why)
    {
        lock (_modelLock) _unusable.Add(model);
        TruckingAdvisor.Shared.Diagnostics.AppLog.Warn($"OpenAI model {model} skipped: {why}");
    }

    /// <summary>
    /// Asks OpenAI which models this key can use, so the built-in lists skip the rest straight away.
    /// Fills in the working model names for display. Safe to call any time; failures are ignored.
    /// </summary>
    public async Task RefreshModelsAsync(CancellationToken ct = default)
    {
        if (!IsAvailable) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
            var (resp, _) = await SendRawAsync(req, ct);
            if (resp is null) return;
            using (resp)
            using (var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct))
            {
                var ids = doc.RootElement.GetProperty("data").EnumerateArray()
                    .Select(m => m.GetProperty("id").GetString() ?? "").Where(id => id.Length > 0);
                lock (_modelLock) _available = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            }
            ApplyResolvedModels();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            TruckingAdvisor.Shared.Diagnostics.AppLog.Warn($"Couldn't list OpenAI models: {ex.Message}");
        }
    }

    /// <summary>Puts the model each job will use into the settings (shown in the UI, read by the realtime client).</summary>
    public void ApplyResolvedModels()
    {
        _settings.ChatModel = Candidates(AiModelCatalog.Chat(_settings.Quality))[0];
        _settings.SpeechToTextModel = Candidates(AiModelCatalog.Transcription)[0];
        _settings.TextToSpeechModel = Candidates(AiModelCatalog.Speech)[0];
        _settings.RealtimeModel = Candidates(AiModelCatalog.Realtime)[0];
    }

    public async Task<LlmResult?> CompleteAsync(string systemPrompt, string userPrompt, int maxOutputTokens, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return null;

        foreach (var model in Candidates(AiModelCatalog.Chat(_settings.Quality)))
        {
            bool reasoning = AiModelCatalog.IsReasoningModel(model);
            var body = new Dictionary<string, object>
            {
                ["model"] = model,
                // Reasoning models count their thinking in this budget; give them room so the reply isn't cut off.
                ["max_completion_tokens"] = reasoning ? Math.Max(600, maxOutputTokens * 6) : Math.Max(16, maxOutputTokens),
                ["messages"] = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            };
            if (reasoning) body["reasoning_effort"] = "minimal";

            using var req = new HttpRequestMessage(HttpMethod.Post, ChatEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            var (resp, modelProblem) = await SendRawAsync(req, cancellationToken);
            if (resp is null)
            {
                if (modelProblem is not null) { MarkUnusable(model, modelProblem); continue; }
                return null; // key, credit, network: another model won't help
            }
            using (resp)
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
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
                    if (text.Length == 0)
                    {
                        // Spent its whole budget thinking: try the next model rather than go silent.
                        MarkUnusable(model, "empty reply");
                        continue;
                    }
                    _settings.ChatModel = model;
                    return new LlmResult(text, new LlmUsage(input, output));
                }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or JsonException)
                {
                    LastError = "Unexpected chat response format.";
                    return null;
                }
            }
        }
        LastError = "None of the built-in chat models worked for this API key.";
        return null;
    }

    public async Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken cancellationToken)
    {
        if (!IsAvailable || wavAudio.Length < 64) return null;
        foreach (var model in Candidates(AiModelCatalog.Transcription))
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wavAudio);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "ptt.wav");
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent("json"), "response_format");
            // Bias recognition toward CB/trucking vocabulary.
            form.Add(new StringContent("CB radio trucker talk: breaker one-nine, ten-four, copy, handle, rig, hammer down, bear, Jake brake."), "prompt");

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/audio/transcriptions") { Content = form };
            var (resp, modelProblem) = await SendRawAsync(req, cancellationToken);
            if (resp is null)
            {
                if (modelProblem is not null) { MarkUnusable(model, modelProblem); continue; }
                return null;
            }
            using (resp)
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                    _usage.RecordSpeechToText(_settings, WavDurationSeconds(wavAudio));
                    _settings.SpeechToTextModel = model;
                    return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
                }
                catch (JsonException) { LastError = "Unexpected transcription response."; return null; }
            }
        }
        return null;
    }

    public async Task<byte[]?> SynthesizeAsync(string text, string voiceId, CancellationToken cancellationToken)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(text)) return null;
        foreach (var model in Candidates(AiModelCatalog.Speech))
        {
            string voice = string.IsNullOrWhiteSpace(voiceId) ? "onyx" : voiceId;
            if (model == "tts-1" && !AiModelCatalog.ClassicVoices.Contains(voice)) voice = "onyx";
            // MP3: compact and always complete (streamed WAV can arrive with an unfinished header).
            var body = new { model, input = text, voice, response_format = "mp3" };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/audio/speech")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            try
            {
                var (resp, modelProblem) = await SendRawAsync(req, cancellationToken);
                if (resp is null)
                {
                    if (modelProblem is not null) { MarkUnusable(model, modelProblem); continue; }
                    return null;
                }
                using (resp)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
                    _usage.RecordTextToSpeech(_settings, text.Length);
                    _settings.TextToSpeechModel = model;
                    return bytes;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                LastError = "Voice request failed or timed out.";
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Sends a request. On failure returns null and, if the failure is about the model itself
    /// (not found, not enabled for this account, retired, unsupported option), a short reason.
    /// </summary>
    private async Task<(HttpResponseMessage? Response, string? ModelProblem)> SendRawAsync(HttpRequestMessage req, CancellationToken ct)
    {
        string? key = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(key)) { LastError = "No API key saved."; return (null, null); }
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _settings.RequestTimeoutSeconds)));
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LastError = ex is TaskCanceledException ? "AI request timed out." : "AI request failed (network).";
            return (null, null);
        }
        if (resp.IsSuccessStatusCode) { LastError = null; return (resp, null); }

        string errorText = "";
        try { errorText = await resp.Content.ReadAsStringAsync(ct); } catch { }
        int status = (int)resp.StatusCode;
        resp.Dispose();
        string? modelProblem = IsModelProblem(status, errorText) ? $"{status} {Short(errorText)}" : null;
        LastError = status switch
        {
            401 => "OpenAI rejected the API key (401).",
            429 => "Rate limited or out of credit (429).",
            _ when modelProblem is not null => null, // handled by moving to the next model
            _ => $"AI request failed ({status})."
        };
        return (null, modelProblem);
    }

    /// <summary>True when OpenAI is saying "this model can't be used" (so another model might work).</summary>
    public static bool IsModelProblem(int status, string body)
    {
        if (status is 401 or 429 or >= 500) return false;
        string b = body.ToLowerInvariant();
        if (status == 404) return true;
        return b.Contains("model_not_found") || b.Contains("does not exist") || b.Contains("do not have access to") ||
               b.Contains("does not have access to model") || b.Contains("unsupported_model") || b.Contains("not supported with this model") ||
               b.Contains("unsupported_parameter") || b.Contains("unsupported_value") || b.Contains("\"param\": \"model\"") ||
               b.Contains("\"param\":\"model\"") || b.Contains("deprecated") || b.Contains("has been retired");
    }

    private static string Short(string s)
    {
        s = s.Replace('\n', ' ').Trim();
        return s.Length > 160 ? s[..160] : s;
    }

    /// <summary>Duration of a PCM WAV from its header; 0 if it can't be read.</summary>
    public static double WavDurationSeconds(byte[] wav)
    {
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return 0;
        int byteRate = BitConverter.ToInt32(wav, 28);
        return byteRate > 0 ? Math.Max(0, wav.Length - 44) / (double)byteRate : 0;
    }
}
