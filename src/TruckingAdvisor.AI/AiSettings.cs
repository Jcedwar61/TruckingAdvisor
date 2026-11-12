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
    /// <summary>Brief / Natural / Chatty / Quiet (only answer when called) / Immersive.</summary>
    public TruckingAdvisor.Shared.Enums.ConversationTimingMode ConversationMode { get; set; } = TruckingAdvisor.Shared.Enums.ConversationTimingMode.Natural;
    /// <summary>Cap on radio calls nobody asked for (chatter, banter, dispatch check-ins) per hour.</summary>
    public int MaxUnsolicitedPerHour { get; set; } = 12;
    public DispatchContactLevel DispatchContact { get; set; } = DispatchContactLevel.Normal;
    public bool MicrophoneEnabled { get; set; }
    /// <summary>Use Windows' built-in offline recognizer when OpenAI transcription isn't available.</summary>
    public bool OfflineSpeechRecognition { get; set; } = true;
    /// <summary>Modular = transcribe -> chat -> voice. Realtime = one OpenAI realtime speech-to-speech session.</summary>
    public VoicePipeline VoicePipeline { get; set; } = VoicePipeline.Modular;
    public string RealtimeModel { get; set; } = "gpt-realtime-mini";
    /// <summary>Keep short memories of conversations so truckers remember you. Off = nothing is stored.</summary>
    public bool RememberConversations { get; set; } = true;
    public TruckingAdvisor.Shared.Enums.CoDriverPersonality CoDriver { get; set; } = TruckingAdvisor.Shared.Enums.CoDriverPersonality.Friendly;

    // Audio devices (-1 = Windows default) and per-channel volumes (0..1).
    public int MicrophoneDevice { get; set; } = -1;
    public int RadioOutputDevice { get; set; } = -1;
    public int AdvisorOutputDevice { get; set; } = -1;
    public int DispatcherOutputDevice { get; set; } = -1;
    public bool CleanAudio { get; set; }
    public float RadioVolume { get; set; } = 0.85f;
    public float DispatcherVolume { get; set; } = 0.9f;
    public float AdvisorVolume { get; set; } = 0.95f;

    // App state (not AI-specific, kept in the same settings file).
    /// <summary>Game folders the player picked by hand when automatic search missed them.</summary>
    public List<string> GameFolders { get; set; } = new();
    /// <summary>The "plugin missing, install now?" question is asked once.</summary>
    public bool PluginPromptShown { get; set; }
    public bool WelcomeShown { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public double? HudLeft { get; set; }
    public double? HudTop { get; set; }
    public double? HudWidth { get; set; }
    public double? HudHeight { get; set; }

    // Look & layout.
    public string Theme { get; set; } = "Midnight";
    public string Accent { get; set; } = "Amber";
    /// <summary>Dashboard widgets in display order.</summary>
    public List<string> WidgetOrder { get; set; } = new(DefaultWidgetOrder);
    public List<string> HiddenWidgets { get; set; } = new();
    /// <summary>HUD rows to show, in order.</summary>
    public List<string> HudRows { get; set; } = new(DefaultHudRows);
    public double HudOpacity { get; set; } = 0.85;

    public static readonly string[] DefaultWidgetOrder = { "Gauges", "Delivery", "Advisories", "Trend", "Report" };
    /// <summary>Individual gauge cards inside the Gauges widget (can be hidden too).</summary>
    public static readonly string[] GaugeCards = { "Speed", "Engine", "Fuel", "Score" };
    public static readonly string[] AllHudRows = { "Speed", "Limit", "Fuel", "Range", "Remaining", "Score", "Location", "Grade", "Radio" };
    public static readonly string[] DefaultHudRows = { "Speed", "Limit", "Fuel", "Remaining", "Radio" };

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

/// <summary>How often dispatch calls you on its own. Important notices (damage, deadline risk) still come through unless Off.</summary>
public enum DispatchContactLevel { Off, Rare, Normal, Frequent }

public enum VoicePipeline { Modular, Realtime }
