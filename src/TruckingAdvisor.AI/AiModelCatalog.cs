namespace TruckingAdvisor.AI;

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
