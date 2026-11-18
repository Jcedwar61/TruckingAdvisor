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

    public static string Describe(AiQuality q) => q switch
    {
        AiQuality.Economy => "Economy: cheapest, short and to the point",
        AiQuality.Best => "Best: most natural conversations, costs more",
        _ => "Balanced: natural conversations at low cost (recommended)"
    };
}
