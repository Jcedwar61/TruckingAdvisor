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

    /// <summary>
    /// Most a discovered (not built-in) model may cost per 1M output tokens, by the estimates below: a bit above the
    /// dearest built-in model, so newer models of the same class qualify and premium or unpriced ones don't.
    /// </summary>
    public const decimal MaxDiscoveredOutputPrice = 20m;

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
                        && !(family == "gpt-" && m.Id.Contains("-pro", StringComparison.OrdinalIgnoreCase))
                        // Never jump to a model that costs far more than the built-in ones (premium tiers, or a new
                        // model the price table doesn't know yet): with the daily spend limit it would just make the
                        // radio go quiet early. Such models are used once the table prices them.
                        && ChatPrice(m.Id).Output <= MaxDiscoveredOutputPrice)
            .ToList();
        int Penalty(string id)
        {
            string m = id.ToLowerInvariant();
            int p = 0;
            if (job == LlmJobKind.Conversation)
            {
                if (m.Contains("lite") || m.Contains("nano") || m.Contains("mini") || m.Contains("luna") || m.Contains("terra")) p += 2;
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
    // These are NOT live prices: the app has no reliable way to fetch them. They were checked against OpenAI's and
    // DeepSeek's official pricing pages on 2026-11-27 and are deliberately CONSERVATIVE: list price + ~20%, rounded
    // up (see Headroom), never below list, because a spend cap that over-estimates stops a little early, while one
    // that under-estimates lets real money through. Where a provider has peak/off-peak or promotional rates, the
    // higher (peak / launch) price is used. A model we don't recognise is priced at the most expensive current model
    // of its size tier. Prices the player enters in AI settings (non-zero) replace these. The provider's dashboard
    // is the truth.

    /// <summary>List price + ~20%, rounded UP to a sensible step (so never below list).</summary>
    public static decimal Headroom(decimal listPrice)
    {
        decimal x = listPrice * 1.2m;
        decimal step = x < 0.1m ? 0.01m : x < 1m ? 0.05m : x < 10m ? 0.10m : 1m;
        return Math.Ceiling(x / step) * step;
    }

    private static (decimal Input, decimal Output) Headroom((decimal In, decimal Out) list) => (Headroom(list.In), Headroom(list.Out));

    /// <summary>
    /// OpenAI chat LIST prices (Standard tier, USD per 1M tokens: input, output) as published on 2026-11-27.
    /// A rule matches the exact id or the id followed by "-..." (so "gpt-5" covers "gpt-5-chat-latest" but not
    /// "gpt-5.6-sol"); the longest matching rule wins.
    /// </summary>
    private static readonly (string Prefix, decimal In, decimal Out)[] OpenAiChatList =
    {
        ("gpt-6-cyber", 20.00m, 100.00m),    // specialised, access by approval
        ("gpt-6.2-astra", 8.00m, 40.00m),
        ("gpt-6-astra", 10.00m, 50.00m),
        ("gpt-6.2-sol", 2.00m, 10.00m),
        ("gpt-6.1-sol", 2.00m, 10.00m),
        ("gpt-6-sol", 2.00m, 10.00m),
        ("gpt-6.1-terra", 1.00m, 5.00m),
        ("gpt-6-luna", 0.10m, 0.50m),
        ("gpt-5.6-sol", 5.00m, 30.00m),      // launch price: the 4/20 promotion ends 2026-11-30
        ("gpt-5.6-terra", 2.00m, 12.00m),
        ("gpt-5.6-luna", 0.20m, 1.20m),
        ("gpt-5.5", 5.00m, 30.00m),
        ("gpt-5.4-mini", 0.75m, 4.50m),
        ("gpt-5.4-nano", 0.20m, 1.25m),
        ("gpt-5.4", 2.50m, 15.00m),
        ("gpt-5.3", 1.75m, 14.00m),          // gpt-5.3-chat-latest
        ("gpt-5.2", 1.75m, 14.00m),
        ("gpt-5.1", 1.25m, 10.00m),
        ("gpt-5-mini", 0.25m, 2.00m),
        ("gpt-5-nano", 0.05m, 0.40m),
        ("gpt-5", 1.25m, 10.00m),
        ("gpt-4.1-mini", 0.40m, 1.60m),
        ("gpt-4.1-nano", 0.10m, 0.40m),
        ("gpt-4.1", 2.00m, 8.00m),
        ("gpt-4o-mini", 0.15m, 0.60m),
        ("gpt-4o", 2.50m, 10.00m),
        ("gpt-4-turbo", 10.00m, 30.00m),
        ("gpt-4", 30.00m, 60.00m),           // old and dear
    };

    // Unknown-model fallbacks: the dearest CURRENT list price of each size tier on 2026-11-27 (headroom added on use).
    private static readonly (decimal, decimal) SmallTierList = (0.20m, 1.25m);   // nano / luna: gpt-5.4-nano (gpt-5.6-luna 0.20/1.20)
    private static readonly (decimal, decimal) MidTierList = (2.00m, 12.00m);    // mini / terra: gpt-5.6-terra
    private static readonly (decimal, decimal) SolTierList = (5.00m, 30.00m);    // sol: gpt-5.6-sol at launch price
    private static readonly (decimal, decimal) AstraTierList = (10.00m, 50.00m); // astra: gpt-6-astra
    private static readonly (decimal, decimal) FullTierList = (20.00m, 100.00m); // any other full-size model: gpt-6-cyber

    /// <summary>Chat price per 1M tokens (input, output) for a model: by model id, else by family and size.</summary>
    public static (decimal Input, decimal Output) ChatPrice(string? model)
    {
        string m = (model ?? "").Trim().ToLowerInvariant();
        if (m.StartsWith("deepseek")) return DeepSeekPrice(m);
        var rule = OpenAiChatList.Where(r => m == r.Prefix || m.StartsWith(r.Prefix + "-"))
                                 .OrderByDescending(r => r.Prefix.Length).FirstOrDefault();
        if (rule.Prefix is not null) return Headroom((rule.In, rule.Out));
        bool small = m.Contains("nano") || m.Contains("luna");
        bool mid = !small && (m.Contains("mini") || m.Contains("terra"));
        if (m.Length > 1 && m[0] == 'o' && char.IsDigit(m[1]))           // o-series: o1 15/60, o1/o3/o4-mini 1.10/4.40
            return mid ? Headroom((1.10m, 4.40m)) : Headroom((15.00m, 60.00m));
        // Unknown (e.g. a newer model the key lists): the dearest current model of its size tier.
        if (small) return Headroom(SmallTierList);
        if (mid) return Headroom(MidTierList);
        if (m.Contains("sol")) return Headroom(SolTierList);
        if (m.Contains("astra")) return Headroom(AstraTierList);
        return Headroom(FullTierList);
    }

    /// <summary>
    /// DeepSeek price per 1M tokens (input at the cache-miss rate, output), at the PEAK-hour rate (off-peak is half).
    /// List on 2026-11-27: deepseek-flash (V4.1-Flash) 0.30/1.20, deepseek-v4-pro 1.32/3.96.
    /// </summary>
    public static (decimal Input, decimal Output) DeepSeekPrice(string? model)
    {
        string m = (model ?? "").Trim().ToLowerInvariant();
        if (m.Contains("flash")) return Headroom((0.30m, 1.20m));
        // Legacy deepseek-chat / deepseek-reasoner (retired 2026-07-24; until then billed as V4-Flash):
        // kept above every price these names have had (R1 reasoner 0.55/2.19).
        if (m == "deepseek-chat" || m == "deepseek-reasoner") return (0.60m, 2.20m);
        // deepseek-v4-pro and anything unknown: the dearest current DeepSeek model.
        return Headroom((1.32m, 3.96m));
    }

    /// <summary>Speech-to-text price per minute of audio.</summary>
    public static decimal TranscriptionPricePerMinute(string? model)
    {
        string m = (model ?? "").ToLowerInvariant();
        if (m.Contains("mini")) return 0.004m;                                  // gpt-4o-mini-transcribe, list 0.003
        if (m.Contains("realtime")) return 0.021m;                              // gpt-realtime-whisper, list 0.017
        if (m.Contains("transcribe") || m.StartsWith("whisper")) return 0.008m; // gpt-4o-transcribe / whisper-1 0.006, gpt-transcribe 0.0045
        return 0.021m;                                                          // unknown: dearest current (gpt-realtime-whisper 0.017)
    }

    /// <summary>Text-to-speech price per 1M characters of input text.</summary>
    public static decimal SpeechPricePerMillionChars(string? model)
    {
        string m = (model ?? "").ToLowerInvariant();
        if (m == "tts-1") return 18m;              // list 15
        if (m.Contains("mini-tts")) return 20m;    // gpt-4o-mini-tts: 0.60 text in / 12 audio out per 1M tokens, OpenAI estimates $0.015/minute ≈ 16-17 per 1M chars
        return 36m;                                // tts-1-hd (list 30) and anything unknown
    }

    /// <summary>Realtime (speech-to-speech) prices per 1M tokens. CachedIn covers cached text and cached audio.</summary>
    public readonly record struct RealtimePrices(decimal TextIn, decimal TextOut, decimal AudioIn, decimal AudioOut, decimal CachedIn);

    public static RealtimePrices RealtimePrice(string? model)
    {
        string m = (model ?? "").Trim().ToLowerInvariant();
        // gpt-realtime-mini / gpt-realtime-2.1-mini / gpt-4o-mini-realtime-preview: list 0.60/2.40 text, 10/20 audio, cached <= 0.30
        if (m.Contains("mini")) return new RealtimePrices(0.80m, 3.00m, 12m, 24m, 0.50m);
        // gpt-realtime / -1.5 / -2 / -2.1: list 4/16-24 text, 32/64 audio, cached 0.40
        if (m.StartsWith("gpt-realtime")) return new RealtimePrices(5.00m, 29.00m, 39m, 77m, 0.50m);
        // gpt-4o-realtime-preview (list 5/20 text, 40/80 audio, cached 2.50) and anything unknown: the dearer of both.
        return new RealtimePrices(6.00m, 29.00m, 48m, 96m, 3.00m);
    }

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
