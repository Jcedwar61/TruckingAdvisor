using System.Text;
using System.Text.RegularExpressions;

namespace TruckingAdvisor.AI.Streaming;

/// <summary>Who answers a caller on the radio.</summary>
public enum CallerRole
{
    /// <summary>A passing trucker on the CB channel (one of the regular cast).</summary>
    Trucker,
    /// <summary>The company dispatcher.</summary>
    Dispatch,
    /// <summary>The company shop mechanic.</summary>
    Mechanic,
    /// <summary>The company safety officer.</summary>
    Safety
}

/// <summary>A chat command viewers can type, as shown on the stream overlay.</summary>
public sealed record ViewerCommand(string Command, string[] Aliases, CallerRole Role, string Description, string Example);

/// <summary>A viewer message that passed the filters and should be answered on the radio.</summary>
public sealed record ViewerCall(string User, string DisplayName, string Text, CallerRole Role, bool FromChannelPoints);

/// <summary>
/// Turns Twitch chat lines into radio calls: command parsing, clean-up, permission and cooldown checks.
/// Thread-safe; the clock is injectable for tests.
/// </summary>
public sealed class ViewerCallGate
{
    public const int MaxMessageLength = 200;

    public static readonly IReadOnlyList<ViewerCommand> AllCommands = new ViewerCommand[]
    {
        new("!cb", new[] { "!ch19", "!breaker" }, CallerRole.Trucker,
            "Key up on the CB - a trucker on the channel answers you", "!cb anybody got eyes on a bear?"),
        new("!dispatch", new[] { "!dispatcher" }, CallerRole.Dispatch,
            "Radio the company dispatcher about the load, ETA or pay", "!dispatch are we going to make it on time?"),
        new("!mechanic", new[] { "!shop", "!wrench" }, CallerRole.Mechanic,
            "Ask the shop mechanic how the truck is holding up", "!mechanic how are the tires?"),
        new("!safety", new[] { "!hos" }, CallerRole.Safety,
            "Ask the safety officer about speed, rest and fuel", "!safety is the driver speeding?"),
    };

    private readonly StreamSettings _settings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastByUser = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;

    public ViewerCallGate(StreamSettings settings, Func<DateTimeOffset>? clock = null)
    {
        _settings = settings;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The commands that are switched on, in display order.</summary>
    public IReadOnlyList<ViewerCommand> EnabledCommands() => AllCommands.Where(c => c.Role switch
    {
        CallerRole.Dispatch => _settings.AllowDispatch,
        CallerRole.Mechanic => _settings.AllowMechanic,
        CallerRole.Safety => _settings.AllowSafety,
        _ => true
    }).ToArray();

    /// <summary>Seconds until the next chat command is accepted (0 = open).</summary>
    public int SecondsUntilOpen()
    {
        lock (_lock)
        {
            double left = (_lastCall + TimeSpan.FromSeconds(Math.Max(0, _settings.GlobalCooldownSeconds)) - _clock()).TotalSeconds;
            return left > 0 ? (int)Math.Ceiling(left) : 0;
        }
    }

    /// <summary>
    /// Decides whether a chat message becomes a radio call. Returns null (and a reason, for the log) when it doesn't.
    /// Accepting a call starts the cooldowns.
    /// </summary>
    public ViewerCall? TryAccept(TwitchChatMessage message, out string? rejectedBecause)
    {
        rejectedBecause = null;
        // Only the one reward the streamer chose skips the cooldowns and subscriber rule. Any other redemption (or every
        // redemption, while no reward is chosen) is treated like an ordinary chat line: it needs a command and waits its turn.
        bool redemption = message.CustomRewardId is { Length: > 0 };
        string chosenReward = (_settings.ChannelPointsRewardId ?? string.Empty).Trim();
        bool reward = redemption && _settings.ChannelPointsEnabled && chosenReward.Length > 0 &&
                      string.Equals(chosenReward, message.CustomRewardId, StringComparison.OrdinalIgnoreCase);
        CallerRole role;
        string body;

        if (reward)
        {
            // A reward message may still start with a crew command to pick who answers.
            (role, body) = ParseCommand(message.Text) ?? (CallerRole.Trucker, message.Text);
        }
        else
        {
            if (ParseCommand(message.Text) is not { } parsed)
            {
                rejectedBecause = !redemption ? "not a command"
                    : !_settings.ChannelPointsEnabled ? "channel points are off"
                    : chosenReward.Length == 0 ? "no Channel Points reward is chosen yet (Stream settings), so it counts as chat"
                    : "a different reward";
                return null;
            }
            (role, body) = parsed;
        }

        if (!EnabledCommands().Any(c => c.Role == role)) { rejectedBecause = "that command is off"; return null; }
        if (_settings.SubscribersOnly && !message.IsPrivileged && !message.IsSubscriber && !reward)
        { rejectedBecause = "subscribers only"; return null; }

        string text = Clean(body);
        if (text.Length < 2) { rejectedBecause = "empty message"; return null; }
        if (ContainsBlockedWord(text, _settings.BlockedWordList())) { rejectedBecause = "blocked word"; return null; }

        lock (_lock)
        {
            var now = _clock();
            if (!reward)
            {
                var global = TimeSpan.FromSeconds(Math.Max(0, _settings.GlobalCooldownSeconds));
                if (now - _lastCall < global)
                { rejectedBecause = $"radio cooldown, {SecondsLeft(_lastCall + global - now)} s left (Stream settings > cooldown)"; return null; }
                var perUser = TimeSpan.FromSeconds(Math.Max(0, _settings.UserCooldownSeconds));
                if (!message.IsPrivileged && _lastByUser.TryGetValue(message.User, out var last) && now - last < perUser)
                { rejectedBecause = $"per-viewer cooldown, {SecondsLeft(last + perUser - now)} s left (mods and the broadcaster skip it)"; return null; }
            }
            _lastCall = now;
            _lastByUser[message.User] = now;
            if (_lastByUser.Count > 2000) _lastByUser.Clear();
        }
        return new ViewerCall(message.User, message.DisplayName, text, role, reward);
    }

    private static int SecondsLeft(TimeSpan left) => Math.Max(1, (int)Math.Ceiling(left.TotalSeconds));

    /// <summary>"!mechanic how are the tires" -> (Mechanic, "how are the tires"). Null if it isn't a radio command.</summary>
    public static (CallerRole Role, string Body)? ParseCommand(string text)
    {
        string t = text.Trim();
        if (!t.StartsWith('!')) return null;
        int space = t.IndexOf(' ');
        string word = (space < 0 ? t : t[..space]).ToLowerInvariant();
        string body = space < 0 ? string.Empty : t[(space + 1)..].Trim();
        foreach (var c in AllCommands)
            if (word == c.Command || c.Aliases.Contains(word))
                return (c.Role, body);
        return null;
    }

    private static readonly Regex Urls = new(@"(https?://|www\.)\S+|\S+\.(com|net|org|tv|gg|ly|io|co)(/\S*)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Mentions = new(@"(?<!\w)@(\w+)", RegexOptions.Compiled);
    private static readonly Regex Repeats = new(@"(.)\1{3,}", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>Strips links, control/emoji characters and letter spam, and limits the length, so it can be spoken.</summary>
    public static string Clean(string raw)
    {
        string s = Urls.Replace(raw, " ");
        s = Mentions.Replace(s, "$1");
        var sb = new StringBuilder(s.Length);
        foreach (var rune in s.EnumerateRunes())
        {
            if (Rune.IsControl(rune)) continue;
            // Keep letters, digits, punctuation and spaces from any language; drop emoji and symbols.
            if (Rune.IsLetterOrDigit(rune) || Rune.IsWhiteSpace(rune) || Rune.IsPunctuation(rune) || rune.Value is '$' or '%' or '+' or '=' or '&')
                sb.Append(rune.ToString());
        }
        s = Repeats.Replace(sb.ToString(), "$1$1$1");
        s = Spaces.Replace(s, " ").Trim();
        if (s.Length > MaxMessageLength)
        {
            s = s[..MaxMessageLength];
            int cut = s.LastIndexOf(' ');
            if (cut > MaxMessageLength / 2) s = s[..cut];
            s = s.TrimEnd() + "...";
        }
        return s;
    }

    public static bool ContainsBlockedWord(string text, IReadOnlyList<string> words)
    {
        foreach (var w in words)
            if (Regex.IsMatch(text, $@"(?<!\w){Regex.Escape(w)}(?!\w)", RegexOptions.IgnoreCase))
                return true;
        return false;
    }

    private static readonly Regex NameJunk = new(@"[_\-\.]+|\d+", RegexOptions.Compiled);

    /// <summary>
    /// A name a voice can say: "xX_RoadDog_99_Xx" -> "Road Dog". Falls back to "chat" when nothing pronounceable is left.
    /// </summary>
    public static string SpeakableName(string displayName)
    {
        string s = displayName.Trim();
        // xX...Xx decorations
        s = Regex.Replace(s, @"^[xX]{2,}_+|_+[xX]{2,}$|^xX(?=[A-Z0-9])|(?<=[a-z0-9])Xx$", "").Trim();
        if (s.Length == 0) s = displayName;
        // Split camel case: RoadDog -> Road Dog
        s = Regex.Replace(s, @"(?<=[a-z])(?=[A-Z])", " ");
        s = NameJunk.Replace(s, " ");
        s = Spaces.Replace(s, " ").Trim();
        s = new string(s.Where(c => char.IsLetter(c) || c == ' ' || c == '\'').ToArray()).Trim();
        return s.Length >= 2 ? s : "chat";
    }
}
