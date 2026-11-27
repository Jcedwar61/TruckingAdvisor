namespace TruckingAdvisor.AI;

/// <summary>Mirror of TruckingAdvisor.Shared.Interfaces.LlmJob for the catalog (kept separate so the catalog needs no interfaces).</summary>
public enum LlmJobKind { Reply, Conversation }

/// <summary>How much to spend on AI conversations. The player picks this; the app picks the models.</summary>
public enum AiQuality
{
    /// <summary>Cheapest, still good for short CB replies.</summary>
    Economy = 0,
    /// <summary>Default: natural conversation at low cost.</summary>
    Balanced = 1,
    /// <summary>The most capable models; costs more.</summary>
    Best = 2
}

/// <summary>
/// Built-in, ordered lists of OpenAI models for each job. The player never types a model name.
/// The client uses the first model on the list that the account can use, and if OpenAI rejects one
/// (retired, renamed or not enabled for the account) it moves to the next and remembers.
/// </summary>
public static class AiModelCatalog
{
    public static IReadOnlyList<string> Chat(AiQuality quality) => quality switch
    {
        AiQuality.Economy => new[] { "gpt-4.1-mini", "gpt-4o-mini", "gpt-5-nano", "gpt-4.1-nano" },
        AiQuality.Best => new[] { "gpt-5", "gpt-4.1", "gpt-4o", "gpt-5-mini", "gpt-4.1-mini", "gpt-4o-mini" },
        _ => new[] { "gpt-5-mini", "gpt-4.1-mini", "gpt-4o-mini", "gpt-4.1" }
    };

    /// <summary>
    /// OpenAI models for the truckers' conversations among themselves: written ahead of time, so the strongest
    /// writer leads (full-size before mini/nano, reasoning-heavy last). Newer models the key can use are added
    /// ahead of these automatically (see <see cref="Discover"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> Conversation = new[] { "gpt-4.1", "gpt-5", "gpt-4o", "gpt-5-mini", "gpt-4.1-mini", "gpt-4o-mini" };

    /// <summary>DeepSeek, quickest first (replies to the player).</summary>
    public static readonly IReadOnlyList<string> DeepSeekReply = new[] { "deepseek-chat", "deepseek-reasoner" };
    /// <summary>DeepSeek, best writer first (conversations); the reasoner thinks too long for radio chatter.</summary>
    public static readonly IReadOnlyList<string> DeepSeekConversation = new[] { "deepseek-chat", "deepseek-reasoner" };

    private static readonly string[] NotChat =
        { "tts", "audio", "realtime", "transcribe", "whisper", "image", "dall-e", "embedding", "moderation", "search",
          "computer-use", "codex", "instruct", "davinci", "babbage", "sora", "vision", "deep-research", "oss" };

    /// <summary>
    /// Models the account lists that aren't on the built-in list (newer releases), best first for the job:
    /// newest first; for conversations full-size models before mini/nano/flash/lite. Same rule SimCast uses.
    /// </summary>
    public static IReadOnlyList<string> Discover(IEnumerable<(string Id, long Created)> listed, string family, IReadOnlyCollection<string> known, LlmJobKind job)
    {
        var found = listed
            .Where(m => m.Id.StartsWith(family, StringComparison.OrdinalIgnoreCase)
                        && !known.Contains(m.Id, StringComparer.OrdinalIgnoreCase)
                        && !NotChat.Any(x => m.Id.Contains(x, StringComparison.OrdinalIgnoreCase))
                        && !System.Text.RegularExpressions.Regex.IsMatch(m.Id, @"-\d{4}-\d{2}-\d{2}$")   // dated snapshots
                        && !m.Id.StartsWith("gpt-3", StringComparison.OrdinalIgnoreCase)
                        // OpenAI's "-pro" models cost many times more and answer far too slowly for the radio.
                        && !(family == "gpt-" && m.Id.Contains("-pro", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        int Penalty(string id)
        {
            string m = id.ToLowerInvariant();
            int p = 0;
            if (job == LlmJobKind.Conversation)
            {
                if (m.Contains("lite") || m.Contains("nano") || m.Contains("mini")) p += 2;
                if (m.Contains("flash")) p += 1;
                if (m.Contains("pro")) p -= 1;
            }
            if (m.Contains("reasoner")) p += 4;
            return p;
        }
        return found.OrderBy(m => Penalty(m.Id)).ThenByDescending(m => m.Created).Select(m => m.Id).ToList();
    }

    /// <summary>Speech to text, best first.</summary>
    public static readonly IReadOnlyList<string> Transcription = new[] { "gpt-4o-mini-transcribe", "gpt-4o-transcribe", "whisper-1" };

    /// <summary>Text to speech (only used when the player picks OpenAI voices).</summary>
    public static readonly IReadOnlyList<string> Speech = new[] { "gpt-4o-mini-tts", "tts-1" };

    /// <summary>Realtime speech-to-speech (optional voice mode).</summary>
    public static readonly IReadOnlyList<string> Realtime = new[] { "gpt-realtime-mini", "gpt-realtime", "gpt-4o-mini-realtime-preview" };

    /// <summary>Voices every OpenAI speech model has (older models lack the newer ones).</summary>
    public static readonly IReadOnlySet<string> ClassicVoices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "alloy", "echo", "fable", "onyx", "nova", "shimmer" };

    /// <summary>Reasoning models (gpt-5 family) think before answering: keep it minimal for snappy radio replies.</summary>
    public static bool IsReasoningModel(string model) => model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ||
                                                        model.StartsWith("o", StringComparison.OrdinalIgnoreCase) && model.Length > 1 && char.IsDigit(model[1]);

    // ---- Built-in price estimates (USD), used for the daily spend limit when the player hasn't entered prices ----
    //
    // These are NOT live prices: the app has no reliable way to fetch them. They are deliberately CONSERVATIVE:
    // each is set at or above the published list price we know of (rounded up, roughly 20% headroom), because a
    // spend cap that over-estimates stops a little early, while one that under-estimates lets real money through.
    // A model we don't recognise is priced at the most expensive known tier of its family/size (or a high default).
    // Prices the player enters in AI settings (non-zero) replace these. The provider's dashboard is the truth.

    /// <summary>Chat price per 1M tokens (input, output) for a model, by family and size.</summary>
    public static (decimal Input, decimal Output) ChatPrice(string? model)
    {
        string m = (model ?? "").Trim().ToLowerInvariant();
        if (m.StartsWith("deepseek")) return DeepSeekPrice(m);
        bool nano = m.Contains("nano"), mini = !nano && m.Contains("mini");
        if (m.StartsWith("gpt-4o")) return nano || mini ? (0.20m, 0.80m) : (3.00m, 12.00m);                   // list 0.15/0.60, 2.50/10
        if (m.StartsWith("gpt-4.1")) return nano ? (0.15m, 0.50m) : mini ? (0.50m, 2.00m) : (2.50m, 10.00m);  // list 0.10/0.40, 0.40/1.60, 2/8
        if (m.StartsWith("gpt-5")) return nano ? (0.10m, 0.50m) : mini ? (0.30m, 2.50m) : (2.00m, 16.00m);    // list 0.05/0.40, 0.25/2, 1.25-1.75/10-14
        if (m.StartsWith("gpt-4")) return (30.00m, 60.00m);                                                  // gpt-4 / gpt-4-turbo (old and dear)
        if (m.Length > 1 && m[0] == 'o' && char.IsDigit(m[1])) return mini ? (1.50m, 6.00m) : (15.00m, 60.00m); // o-series (o1 is 15/60)
        // Unknown (e.g. a newer model the key lists): the dearest known tier of its size.
        return nano ? (0.15m, 0.50m) : mini ? (0.50m, 2.50m) : (3.00m, 16.00m);
    }

    /// <summary>DeepSeek price per 1M tokens (input at the cache-miss rate, output): above every published DeepSeek chat/reasoner price.</summary>
    public static (decimal Input, decimal Output) DeepSeekPrice(string? model) => (0.60m, 2.20m); // highest list: reasoner 0.55/2.19

    /// <summary>Speech-to-text price per minute of audio.</summary>
    public static decimal TranscriptionPricePerMinute(string? model)
    {
        string m = (model ?? "").ToLowerInvariant();
        if (m.Contains("mini")) return 0.004m;                                  // gpt-4o-mini-transcribe, list 0.003
        if (m.Contains("transcribe") || m.StartsWith("whisper")) return 0.008m; // gpt-4o-transcribe / whisper-1, list 0.006
        return 0.010m;
    }

    /// <summary>Text-to-speech price per 1M characters of input text.</summary>
    public static decimal SpeechPricePerMillionChars(string? model)
    {
        string m = (model ?? "").ToLowerInvariant();
        if (m == "tts-1") return 18m;              // list 15
        if (m.Contains("mini-tts")) return 20m;    // gpt-4o-mini-tts: about $0.015 per minute of speech, ~16-17 per 1M chars
        return 36m;                                // tts-1-hd (list 30) and anything unknown
    }

    /// <summary>Realtime (speech-to-speech) prices per 1M tokens.</summary>
    public readonly record struct RealtimePrices(decimal TextIn, decimal TextOut, decimal AudioIn, decimal AudioOut, decimal CachedIn);

    public static RealtimePrices RealtimePrice(string? model) =>
        (model ?? "").Contains("mini", StringComparison.OrdinalIgnoreCase)
            ? new RealtimePrices(0.80m, 3.00m, 12m, 24m, 0.50m)    // gpt-realtime-mini / 4o-mini-realtime: list 0.60/2.40 text, 10/20 audio
            : new RealtimePrices(6.00m, 24.00m, 48m, 96m, 3.00m);  // gpt-realtime list 4/16 + 32/64; 4o-realtime-preview 5/20 + 40/80

    /// <summary>Price for an OpenAI chat request: the player's prices when entered (non-zero), else the built-in estimate for that model.</summary>
    public static (decimal Input, decimal Output) EffectiveChatPrice(AiSettings s, string? model)
    {
        var b = ChatPrice(model);
        return (s.ChatInputPricePerMillionTokens > 0 ? s.ChatInputPricePerMillionTokens : b.Input,
                s.ChatOutputPricePerMillionTokens > 0 ? s.ChatOutputPricePerMillionTokens : b.Output);
    }

    public static (decimal Input, decimal Output) EffectiveDeepSeekPrice(AiSettings s, string? model)
    {
        var b = DeepSeekPrice(model);
        return (s.DeepSeekInputPricePerMillionTokens > 0 ? s.DeepSeekInputPricePerMillionTokens : b.Input,
                s.DeepSeekOutputPricePerMillionTokens > 0 ? s.DeepSeekOutputPricePerMillionTokens : b.Output);
    }

    public static decimal EffectiveTranscriptionPrice(AiSettings s, string? model) =>
        s.SpeechToTextPricePerMinute > 0 ? s.SpeechToTextPricePerMinute : TranscriptionPricePerMinute(model);

    public static decimal EffectiveSpeechPrice(AiSettings s, string? model) =>
        s.TextToSpeechPricePerMillionChars > 0 ? s.TextToSpeechPricePerMillionChars : SpeechPricePerMillionChars(model);

    /// <summary>Rough token count for a prompt (errs high: ~3 characters per token).</summary>
    public static int EstimateTokens(string? text) => string.IsNullOrEmpty(text) ? 0 : text.Length / 3 + 8;

    public static string Describe(AiQuality q) => q switch
    {
        AiQuality.Economy => "Economy: cheapest, short and to the point",
        AiQuality.Best => "Best: most natural conversations, costs more",
        _ => "Balanced: natural conversations at low cost (recommended)"
    };
}
