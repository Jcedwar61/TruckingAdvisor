using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TruckingAdvisor.Shared.Interfaces;

namespace TruckingAdvisor.AI;

/// <summary>
/// DeepSeek chat (OpenAI-compatible API) with its own key, stored encrypted like the OpenAI key.
/// Models come from the built-in lists plus whatever newer ones the key lists; a model DeepSeek rejects is
/// skipped and remembered. Returns null on any failure so the radio falls back (to OpenAI, then offline lines).
/// </summary>
public sealed class DeepSeekClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly AiSettings _settings;
    private readonly AiUsageTracker _usage;
    private readonly Func<string?> _key;
    private readonly HashSet<string> _unusable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string, string)> _noOption = new();
    private HashSet<string>? _available;
    private List<(string Id, long Created)> _listed = new();
    private readonly object _lock = new();

    public string? LastError { get; private set; }
    public string LastModel { get; private set; } = "";

    public DeepSeekClient(AiSettings settings, AiUsageTracker usage, Func<string?>? key = null, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _usage = usage;
        _key = key ?? (() => ApiKeyStore.Load(AiLlmProvider.DeepSeek));
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public bool HasKey => !string.IsNullOrWhiteSpace(_key());
    public bool IsAvailable => _settings.GlobalAiEnabled && KeyMayGoTo($"{BaseUrl}/chat/completions") && !_usage.IsOverLimit(_settings) && HasKey;

    /// <summary>The DeepSeek key only goes to the DeepSeek address it was set up for, over https (http only to this PC).</summary>
    private bool KeyMayGoTo(string url) => _settings.KeyMayBeSentTo(AiLlmProvider.DeepSeek, AiSettings.TryUri(url));

    private string BaseUrl => (string.IsNullOrWhiteSpace(_settings.DeepSeekBaseUrl) ? "https://api.deepseek.com" : _settings.DeepSeekBaseUrl).TrimEnd('/');

    public IReadOnlyList<string> Candidates(LlmJobKind job)
    {
        var list = job == LlmJobKind.Conversation ? AiModelCatalog.DeepSeekConversation : AiModelCatalog.DeepSeekReply;
        lock (_lock)
        {
            var known = list.Where(m => !_unusable.Contains(m) && (_available is null || _available.Contains(m))).ToList();
            var newer = AiModelCatalog.Discover(_listed, "deepseek-", AiModelCatalog.DeepSeekReply.Concat(AiModelCatalog.DeepSeekConversation).ToList(), job)
                .Where(m => !_unusable.Contains(m)).ToList();
            var all = (job == LlmJobKind.Conversation ? newer.Concat(known) : known.Concat(newer)).Distinct().ToList();
            return all.Count > 0 ? all : list.Where(m => !_unusable.Contains(m)).DefaultIfEmpty(list[0]).ToList();
        }
    }

    /// <summary>Asks DeepSeek which models this key can use (background; failures ignored).</summary>
    public async Task RefreshModelsAsync(CancellationToken ct = default)
    {
        if (!IsAvailable || !KeyMayGoTo($"{BaseUrl}/models")) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await _http.SendAsync(req, timeout.Token);
            if (!resp.IsSuccessStatusCode) return;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var listed = doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(m => (Id: m.GetProperty("id").GetString() ?? "",
                              Created: m.TryGetProperty("created", out var c) && c.TryGetInt64(out long t) ? t : 0L))
                .Where(m => m.Id.Length > 0).ToList();
            lock (_lock)
            {
                _listed = listed;
                _available = new HashSet<string>(listed.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            TruckingAdvisor.Shared.Diagnostics.AppLog.Warn($"Couldn't list DeepSeek models: {ex.Message}");
        }
    }

    public Task<LlmResult?> CompleteAsync(string systemPrompt, string userPrompt, int maxOutputTokens, CancellationToken cancellationToken) =>
        CompleteAsync(systemPrompt, userPrompt, maxOutputTokens, LlmJobKind.Reply, cancellationToken);

    public async Task<LlmResult?> CompleteAsync(string systemPrompt, string userPrompt, int maxOutputTokens, LlmJobKind job, CancellationToken ct)
    {
        if (!IsAvailable) return null;
        string url = $"{BaseUrl}/chat/completions"; // IsAvailable checked the key may go there (https, DeepSeek address)
        string? key = _key();
        foreach (var model in Candidates(job))
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                bool reasoner = model.Contains("reasoner", StringComparison.OrdinalIgnoreCase);
                bool NoOption(string o) { lock (_lock) return _noOption.Contains((model, o)); }
                int budget = reasoner ? Math.Max(800, maxOutputTokens * 6) : Math.Max(16, maxOutputTokens);

                // Every attempt is billable: re-check the limit and reserve it before sending.
                var (priceIn, priceOut) = AiModelCatalog.EffectiveDeepSeekPrice(_settings, model);
                decimal estimate = ((AiModelCatalog.EstimateTokens(systemPrompt) + AiModelCatalog.EstimateTokens(userPrompt)) * priceIn
                                    + budget * priceOut) / 1_000_000m;
                using var reservation = _usage.TryReserve(_settings, estimate);
                if (reservation is null) { LastError = "Daily AI limit reached (requests or estimated spend)."; return null; }

                var body = new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["max_tokens"] = budget,
                    ["messages"] = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    }
                };
                // DeepSeek recommends a livelier temperature for conversation and creative writing.
                if (!reasoner && !NoOption("temperature")) body["temperature"] = job == LlmJobKind.Conversation ? 1.3 : 1.0;

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _settings.RequestTimeoutSeconds) * (job == LlmJobKind.Conversation ? 2 : 1)));
                HttpResponseMessage resp;
                try { resp = await _http.SendAsync(req, timeout.Token); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    if (ex is TaskCanceledException) reservation.CommitEstimate(); // sent; may still be billed
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    LastError = ex is TaskCanceledException ? "DeepSeek timed out." : "Can't reach DeepSeek (network).";
                    return null;
                }
                using (resp)
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        string err = "";
                        try { err = await resp.Content.ReadAsStringAsync(ct); } catch { }
                        int status = (int)resp.StatusCode;
                        if (OpenAiClientService.IsModelProblem(status, err))
                        {
                            string? option = new[] { "temperature", "max_tokens" }
                                .FirstOrDefault(o => err.Contains(o, StringComparison.OrdinalIgnoreCase) && !NoOption(o));
                            if (option is not null) { lock (_lock) _noOption.Add((model, option)); continue; }
                            lock (_lock) _unusable.Add(model);
                            TruckingAdvisor.Shared.Diagnostics.AppLog.Warn($"DeepSeek model {model} skipped: {status}");
                            goto nextModel;
                        }
                        LastError = status switch
                        {
                            401 => "DeepSeek rejected the API key (401).",
                            402 => "The DeepSeek account is out of credit (402).",
                            429 => "DeepSeek is rate limiting (429).",
                            _ => $"DeepSeek request failed ({status})."
                        };
                        return null;
                    }
                    try
                    {
                        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                        var root = doc.RootElement;
                        string text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                        int input = 0, output = 0;
                        if (root.TryGetProperty("usage", out var u))
                        {
                            if (u.TryGetProperty("prompt_tokens", out var pt)) input = pt.GetInt32();
                            if (u.TryGetProperty("completion_tokens", out var cpt)) output = cpt.GetInt32();
                        }
                        _usage.RecordDeepSeek(_settings, model, input, output, reservation);
                        text = text.Trim();
                        if (text.Length == 0) { lock (_lock) _unusable.Add(model); goto nextModel; }
                        LastModel = model;
                        if (job == LlmJobKind.Conversation) _settings.ConversationModel = $"DeepSeek {model}";
                        LastError = null;
                        return new LlmResult(text, new LlmUsage(input, output));
                    }
                    catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or JsonException)
                    {
                        reservation.CommitEstimate();
                        LastError = "Unexpected DeepSeek response.";
                        return null;
                    }
                }
            }
        nextModel:;
        }
        LastError ??= "None of the DeepSeek models worked for this key.";
        return null;
    }
}

/// <summary>
/// Picks the AI for each job, the way SimCast's booth does: replies to the player go to the main AI (OpenAI,
/// or DeepSeek when it's the only key) on the quickest good model; the truckers' conversations among themselves go
/// to a second AI when there is one (Automatic = DeepSeek) on the strongest writer, so both run side by side.
/// If one provider fails, the other one answers.
/// </summary>
public sealed class LlmRouter : IJobLlmClient
{
    private readonly OpenAiClientService _openAi;
    private readonly DeepSeekClient _deepSeek;
    private readonly AiSettings _settings;
    private readonly Dictionary<LlmJob, string> _last = new();

    public LlmRouter(OpenAiClientService openAi, DeepSeekClient deepSeek, AiSettings settings)
    {
        _openAi = openAi;
        _deepSeek = deepSeek;
        _settings = settings;
    }

    public bool IsAvailable => _openAi.IsAvailable || _deepSeek.IsAvailable;
    public DeepSeekClient DeepSeek => _deepSeek;

    public Task<LlmResult?> CompleteAsync(string systemPrompt, string userPrompt, int maxOutputTokens, CancellationToken cancellationToken) =>
        CompleteAsync(systemPrompt, userPrompt, maxOutputTokens, LlmJob.Reply, cancellationToken);

    /// <summary>Providers in the order they're tried for a job.</summary>
    public IReadOnlyList<string> Order(LlmJob job)
    {
        if (job == LlmJob.Reply) return new[] { "OpenAI", "DeepSeek" };
        return _settings.ConversationWriter switch
        {
            ConversationWriter.OpenAI => new[] { "OpenAI", "DeepSeek" },
            ConversationWriter.DeepSeek => new[] { "DeepSeek", "OpenAI" },
            _ => _deepSeek.IsAvailable && _openAi.IsAvailable ? new[] { "DeepSeek", "OpenAI" }   // a second AI alongside
                 : new[] { "OpenAI", "DeepSeek" }
        };
    }

    public async Task<LlmResult?> CompleteAsync(string systemPrompt, string userPrompt, int maxOutputTokens, LlmJob job, CancellationToken cancellationToken)
    {
        var kind = job == LlmJob.Conversation ? LlmJobKind.Conversation : LlmJobKind.Reply;
        foreach (var provider in Order(job))
        {
            LlmResult? result = provider == "DeepSeek"
                ? (_deepSeek.IsAvailable ? await _deepSeek.CompleteAsync(systemPrompt, userPrompt, maxOutputTokens, kind, cancellationToken) : null)
                : (_openAi.IsAvailable ? await _openAi.CompleteAsync(systemPrompt, userPrompt, maxOutputTokens, kind, cancellationToken) : null);
            if (result is not null)
            {
                string model = provider == "DeepSeek" ? _deepSeek.LastModel : _openAi.LastModel(kind);
                lock (_last) _last[job] = $"{provider} {model}".Trim();
                if (job == LlmJob.Conversation) _settings.ConversationModel = _last[job];
                return result;
            }
        }
        return null;
    }

    public string LastUsed(LlmJob job)
    {
        lock (_last) return _last.TryGetValue(job, out var s) ? s : "";
    }

    /// <summary>Who will write conversations right now (before the first one), for Settings.</summary>
    public string PlannedConversationWriter() => Order(LlmJob.Conversation).FirstOrDefault(p =>
        p == "DeepSeek" ? _deepSeek.IsAvailable : _openAi.IsAvailable) ?? "offline lines";
}
