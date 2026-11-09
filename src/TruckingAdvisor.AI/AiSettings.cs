using System.Text.Json;

namespace TruckingAdvisor.AI;

public enum AiLlmProvider
{
    OpenAI,
    /// <summary>Any OpenAI-compatible chat endpoint (set <see cref="AiSettings.CustomChatEndpoint"/>).</summary>
    OpenAICompatible,
    Offline
}

/// <summary>
/// User-editable AI settings. Model names are plain strings so they can be swapped without code
/// changes when OpenAI retires or adds models. The API key is NOT stored here (see ApiKeyStore).
/// </summary>
public sealed class AiSettings
{
    public bool GlobalAiEnabled { get; set; }
    public AiLlmProvider Provider { get; set; } = AiLlmProvider.OpenAI;
    public string ApiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string CustomChatEndpoint { get; set; } = string.Empty;

    public string ChatModel { get; set; } = "gpt-5.4-mini";
    public string SpeechToTextModel { get; set; } = "gpt-4o-mini-transcribe";
    public string TextToSpeechModel { get; set; } = "gpt-4o-mini-tts";
    /// <summary>When false, AI voices use free Windows voices instead of OpenAI TTS (cheaper).</summary>
    public bool UseOpenAiVoices { get; set; } = true;

    /// <summary>Max tokens per radio reply. CB transmissions are short.</summary>
    public int MaxResponseTokens { get; set; } = 160;
    /// <summary>Creativity (temperature). Null = model default; some models reject custom values.</summary>
    public double? Creativity { get; set; }
    /// <summary>Optional reasoning effort for reasoning models ("none", "low", ...). Null = omit.</summary>
    public string? ReasoningEffort { get; set; }
    public int MaxConversationTurns { get; set; } = 6;
    public int MaxMemoriesPerTrucker { get; set; } = 12;
    public int RequestTimeoutSeconds { get; set; } = 20;

    // Spending controls. Prices are entered by the user from their OpenAI pricing page;
    // with no prices set we still count requests and tokens and enforce the request limit.
    public decimal ChatInputPricePerMillionTokens { get; set; }
    public decimal ChatOutputPricePerMillionTokens { get; set; }
    public decimal SpeechToTextPricePerMinute { get; set; }
    public decimal TextToSpeechPricePerMillionChars { get; set; }
    public decimal DailySpendLimitUsd { get; set; } = 1.00m;
    public int DailyRequestLimit { get; set; } = 300;

    public RadioActivitySetting RadioActivity { get; set; } = RadioActivitySetting.Normal;
    public bool MicrophoneEnabled { get; set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TruckingAdvisor", "ai-settings.json");

    public static AiSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(path)) ?? new AiSettings();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new AiSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public enum RadioActivitySetting { Off, Quiet, Normal, Busy, VeryBusy }
