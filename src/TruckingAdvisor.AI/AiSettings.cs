using TruckingAdvisor.Shared.Enums;
using System.Text.Json;

namespace TruckingAdvisor.AI;

public enum AiLlmProvider
{
    OpenAI,
    /// <summary>Any OpenAI-compatible chat endpoint (set <see cref="AiSettings.CustomChatEndpoint"/>).</summary>
    OpenAICompatible,
    Offline,
    /// <summary>DeepSeek (its own key; OpenAI-compatible API). Used for trucker conversations when it has a key.</summary>
    DeepSeek
}

/// <summary>How the AI voices on the radio talk.</summary>
public enum RadioRating
{
    /// <summary>Clean: no swearing, no crude humour. Good with family around or on stream.</summary>
    TvFriendly = 0,
    /// <summary>Real truckers off the clock: uncensored swearing, raunchy and dark humour (only no slurs/hate and nothing involving minors).</summary>
    RRated = 1
}

/// <summary>Which AI writes the truckers' conversations among themselves.</summary>
public enum ConversationWriter
{
    /// <summary>DeepSeek when it has a key (a second AI working alongside OpenAI), otherwise OpenAI.</summary>
    Automatic = 0,
    OpenAI = 1,
    DeepSeek = 2
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

    /// <summary>TV-friendly (default) or R-rated radio talk, for every AI voice on the radio.</summary>
    public RadioRating Rating { get; set; } = RadioRating.RRated;
    /// <summary>Who writes the truckers' conversations among themselves.</summary>
    public ConversationWriter ConversationWriter { get; set; } = ConversationWriter.Automatic;
    public string DeepSeekBaseUrl { get; set; } = "https://api.deepseek.com";
    /// <summary>DeepSeek prices for the cost estimate (USD per 1M tokens; 0 = built-in estimate).</summary>
    public decimal DeepSeekInputPricePerMillionTokens { get; set; }
    public decimal DeepSeekOutputPricePerMillionTokens { get; set; }
    /// <summary>Filled in by the app: the model writing conversations right now (for display).</summary>
    public string ConversationModel { get; set; } = "";

    /// <summary>Picks the models (see <see cref="AiModelCatalog"/>). The player never types a model name.</summary>
    public AiQuality Quality { get; set; } = AiQuality.Balanced;
    // The model names below are filled in by the app (the working model from the built-in list), for display and
    // for the realtime client. Values from older settings files are ignored.
    public string ChatModel { get; set; } = "gpt-5-mini";
    public string SpeechToTextModel { get; set; } = "gpt-4o-mini-transcribe";
    public string TextToSpeechModel { get; set; } = "gpt-4o-mini-tts";
    /// <summary>When false, AI voices use free Windows voices instead of OpenAI TTS (cheaper).</summary>
    /// <summary>Where voices come from. Default: free Microsoft neural voices (Windows voices as fallback).</summary>
    public VoiceEngine VoiceEngine { get; set; } = VoiceEngine.FreeNeural;
    /// <summary>Old setting (before voice engines); kept so old settings files load. Not used.</summary>
    public bool UseOpenAiVoices { get; set; }
    /// <summary>Speech recognition: 0 Auto, 1 Whisper on this PC, 2 OpenAI, 3 Windows basic (see SpeechRecognition).</summary>
    public int SpeechRecognitionMode { get; set; }
    /// <summary>Whisper model: accurate (small.en) or fast (base.en).</summary>
    public bool WhisperAccurate { get; set; }
    /// <summary>Keep the microphone open (last half second only, in memory) so the first word isn't clipped.</summary>
    public bool KeepMicReady { get; set; } = true;

    /// <summary>Co-driver (advisor) neural voice.</summary>
    public string CoDriverNeuralVoice { get; set; } = "en-US-AndrewNeural|-44%|+0Hz";
    /// <summary>Set once the on-device voice model is downloaded, so nothing is fetched twice.</summary>
    public bool LocalVoiceModelReady { get; set; }

    /// <summary>Max tokens per radio reply. CB transmissions are short.</summary>
    public int MaxResponseTokens { get; set; } = 160;
    /// <summary>Creativity (temperature). Null = model default; some models reject custom values.</summary>
    public double? Creativity { get; set; }
    /// <summary>Optional reasoning effort for reasoning models ("none", "low", ...). Null = omit.</summary>
    public string? ReasoningEffort { get; set; }
    public int MaxConversationTurns { get; set; } = 6;
    public int MaxMemoriesPerTrucker { get; set; } = 12;
    public int RequestTimeoutSeconds { get; set; } = 20;

    // Spending controls. A price of 0 (the default) means "use the built-in estimate for the model actually used"
    // (conservative, see AiModelCatalog); a price the user enters from their pricing page replaces it.
    // The chat prices, when entered, apply to every OpenAI chat model the app falls back to.
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
    /// <summary>The player closed the getting-started checklist.</summary>
    public bool SetupChecklistDismissed { get; set; }

    /// <summary>Auto = miles in ATS, kilometres in ETS2.</summary>
    public TruckingAdvisor.Shared.UnitPreference Units { get; set; } = TruckingAdvisor.Shared.UnitPreference.Auto;
    /// <summary>The co-driver says fines out loud (tolls and fares are never spoken).</summary>
    public bool AnnounceFines { get; set; } = true;

    // Updates.
    /// <summary>Check GitHub for new versions in the background and download them (never installs mid-drive).</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Install a downloaded update when the app closes.</summary>
    public bool InstallUpdatesOnExit { get; set; } = true;
    /// <summary>A version the player chose to skip (no banner for it).</summary>
    public string SkippedUpdateVersion { get; set; } = "";

    /// <summary>Clickable CB channel picker attached under the HUD.</summary>
    public bool HudChannelPicker { get; set; } = true;

    /// <summary>Twitch chat, convoy CB and stream overlay.</summary>
    public TruckingAdvisor.AI.Streaming.StreamSettings Stream { get; set; } = new();

    public static readonly string[] DefaultWidgetOrder = { "Gauges", "Delivery", "Advisories", "Trend", "Report" };
    /// <summary>Individual gauge cards inside the Gauges widget (can be hidden too).</summary>
    public static readonly string[] GaugeCards = { "Speed", "Engine", "Fuel", "Score" };
    public static readonly string[] AllHudRows = { "Speed", "Limit", "Fuel", "Range", "Remaining", "Score", "Location", "Grade", "Radio", "Map" };
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
        AtomicFile.Write(path, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })));
    }

    // ---- Which host each API key belongs to. A key is only ever sent to the host it was set up for. ----

    /// <summary>
    /// True for https URLs, and for plain http only to this PC (localhost, 127.0.0.1, ::1: a local OpenAI-compatible
    /// server). Same rule for wss/ws. API keys are never sent over anything else.
    /// </summary>
    public static bool IsSecureEndpoint(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (uri.Scheme is "https" or "wss") return true;
        return uri.Scheme is "http" or "ws" && uri.IsLoopback;
    }

    public static Uri? TryUri(string? url) =>
        Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u) ? u : null;

    /// <summary>
    /// The endpoint the key stored for <paramref name="provider"/> was configured for: OpenAI = <see cref="ApiBaseUrl"/>,
    /// OpenAI-compatible = its chat endpoint (<see cref="CustomChatEndpoint"/>, else ApiBaseUrl), DeepSeek =
    /// <see cref="DeepSeekBaseUrl"/>. Null when there is none (offline) or it isn't a secure URL.
    /// </summary>
    public Uri? KeyEndpoint(AiLlmProvider provider)
    {
        Uri? u = provider switch
        {
            AiLlmProvider.OpenAI => TryUri(ApiBaseUrl),
            AiLlmProvider.OpenAICompatible => TryUri(string.IsNullOrWhiteSpace(CustomChatEndpoint) ? ApiBaseUrl : CustomChatEndpoint),
            AiLlmProvider.DeepSeek => TryUri(string.IsNullOrWhiteSpace(DeepSeekBaseUrl) ? "https://api.deepseek.com" : DeepSeekBaseUrl),
            _ => null
        };
        return IsSecureEndpoint(u) ? u : null;
    }

    /// <summary>True when <paramref name="target"/> is secure and on the same host and port as the provider's key endpoint.</summary>
    public bool KeyMayBeSentTo(AiLlmProvider provider, Uri? target)
    {
        var home = KeyEndpoint(provider);
        if (home is null || !IsSecureEndpoint(target)) return false;
        return string.Equals(home.Host, target!.Host, StringComparison.OrdinalIgnoreCase) && DefaultPort(home) == DefaultPort(target);
    }

    private static int DefaultPort(Uri u) => u.IsDefaultPort ? (u.Scheme is "https" or "wss" ? 443 : 80) : u.Port;
}

/// <summary>Writes a file so a crash never leaves it half-written: write a temp file, then swap it in.</summary>
internal static class AtomicFile
{
    public static void Write(string path, byte[] bytes, bool keepBackup = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        if (keepBackup && File.Exists(path))
        {
            try
            {
                File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Fall through to a plain move (some file systems / AV tools refuse Replace).
                try { File.Copy(path, path + ".bak", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        File.Move(tmp, path, overwrite: true);
    }
}

public enum RadioActivitySetting { Off, Quiet, Normal, Busy, VeryBusy }

/// <summary>How often dispatch calls you on its own. Important notices (damage, deadline risk) still come through unless Off.</summary>
public enum DispatchContactLevel { Off, Rare, Normal, Frequent }

public enum VoicePipeline { Modular, Realtime }
