namespace TruckingAdvisor.AI.Streaming;

/// <summary>
/// Twitch chat, convoy, stream-overlay and SimHub-panel settings (saved inside the AI settings file).
/// Twitch chat is read anonymously, so the Twitch settings aren't secret. The convoy code is: it is the convoy's
/// encryption secret (anyone who has it can read and join the convoy's radio), so keep it off stream.
/// </summary>
public sealed class StreamSettings
{
    // ---- Twitch chat -> CB radio ----

    /// <summary>Listen to a Twitch channel's chat and let viewers key up on the CB.</summary>
    public bool TwitchEnabled { get; set; }
    /// <summary>The Twitch channel to read (the streamer's login name, no #).</summary>
    public string TwitchChannel { get; set; } = string.Empty;
    /// <summary>Seconds between any two viewer calls (keeps the cab from being flooded).</summary>
    public int GlobalCooldownSeconds { get; set; } = 30;
    /// <summary>Seconds before the same viewer can call again. Mods and the broadcaster skip this.</summary>
    public int UserCooldownSeconds { get; set; } = 120;
    /// <summary>Only subscribers, VIPs, mods and the broadcaster can use the commands.</summary>
    public bool SubscribersOnly { get; set; }
    /// <summary>The chosen Channel Points reward (<see cref="ChannelPointsRewardId"/>) keys up on the CB with its message
    /// (no cooldown or subscriber rule: the viewer paid for it).</summary>
    public bool ChannelPointsEnabled { get; set; } = true;
    /// <summary>The reward that keys up. Empty = no reward is special: redemptions count as ordinary chat (command, cooldowns).</summary>
    public string ChannelPointsRewardId { get; set; } = string.Empty;
    /// <summary>Shown on the overlay so viewers know which reward to redeem.</summary>
    public string ChannelPointsRewardName { get; set; } = "Key Up on Channel 19";
    /// <summary>Read the viewer's message aloud in a caller voice before the reply (two-voice relay).</summary>
    public bool ReadViewerMessageAloud { get; set; } = true;
    /// <summary>Messages containing any of these words (comma separated) are ignored.</summary>
    public string BlockedWords { get; set; } = string.Empty;
    /// <summary>Which crew commands viewers may use (the CB command is always on).</summary>
    public bool AllowDispatch { get; set; } = true;
    public bool AllowMechanic { get; set; } = true;
    public bool AllowSafety { get; set; } = true;

    // ---- Stream overlay (OBS browser source) ----

    public bool OverlayEnabled { get; set; }
    public int OverlayPort { get; set; } = 8789;
    /// <summary>Also answer other PCs on the network (for a two-PC streaming setup). Off = this PC only.</summary>
    public bool OverlayAllowLan { get; set; }

    // ---- SimHub dashboard ----

    /// <summary>Serve TruckingAdvisor's live panel (score, delivery, CB radio, coaching) for the SimHub dashboard's web view.
    /// Uses the same local web server and port as the overlay.</summary>
    public bool SimHubPanelEnabled { get; set; }
    /// <summary>With TruckingAdvisor's SimHub plugin installed: show the triple-screen dashboards on the round and 4" screens
    /// while TruckingAdvisor runs, and put SimHub's own back when it closes.</summary>
    public bool SimHubAutoTriple { get; set; } = true;
    /// <summary>The plugin found the two round screens the wrong way round: swap left and right.</summary>
    public bool SimHubSwapRounds { get; set; }

    // ---- Convoy CB (several TruckingAdvisor users share one AI radio) ----

    public bool ConvoyEnabled { get; set; }
    /// <summary>Everyone in the convoy types the same code (at least 12 letters or digits; "Generate code" makes one).
    /// It is the convoy's encryption secret: keep it off stream.</summary>
    public string ConvoyCode { get; set; } = string.Empty;
    /// <summary>This PC answers for the convoy's AI. If nobody ticks it, one PC is picked automatically.</summary>
    public bool ConvoyHost { get; set; }
    /// <summary>The relay server (ntfy). The public one is free; a self-hosted ntfy server works too.</summary>
    public string ConvoyServer { get; set; } = "https://ntfy.sh";
    /// <summary>Read the other drivers' radio lines aloud (off: they're usually heard over voice chat already).</summary>
    public bool ReadConvoyLinesAloud { get; set; }

    public static readonly int[] PortRange = { 1024, 65535 };

    /// <summary>Normalizes a typed Twitch channel ("https://twitch.tv/Name", "#name", "@name") to a login name.</summary>
    public static string NormalizeChannel(string? raw)
    {
        string s = (raw ?? string.Empty).Trim();
        int slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        s = s.TrimStart('#', '@').Trim().ToLowerInvariant();
        return new string(s.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
    }

    public IReadOnlyList<string> BlockedWordList() =>
        BlockedWords.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 0).ToArray();
}
