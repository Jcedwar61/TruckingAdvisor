using System.Globalization;
using System.Text.RegularExpressions;

namespace TruckingAdvisor.AI;

public enum VoiceCommandKind
{
    SwitchChannel,
    CallDispatch,
    FuelReport,
    DistanceReport,
    VolumeDown,
    VolumeUp,
    Mute,
    Unmute,
    RepeatLast,
    Unknown
}

public sealed record VoiceCommand(VoiceCommandKind Kind, int? Channel = null, string Raw = "");

/// <summary>
/// App commands spoken over push-to-talk. Only phrases that START with the wake word
/// ("TruckingAdvisor", "Trucking Advisor", or "Advisor,") count, so normal CB talk is never
/// mistaken for a command.
/// </summary>
public static class VoiceCommandParser
{
    private static readonly Regex WakeWord = new(
        // Full name ("TruckingAdvisor", "truck adviser") with optional punctuation, or bare "Advisor" only
        // when followed by a pause mark ("Advisor, ..."), so "Advisor says ..." isn't a command.
        @"^\s*(?:hey\s+|ok(?:ay)?\s+)?(?:truck(?:ing)?[\s-]*advi[sz][eo]rs?\s*[,.!?:;-]?|advi[sz][eo]r\s*[,.!?:;-])\s*(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ChannelPattern = new(
        @"\b(?:switch|go|change|turn|flip)\b.*?\bchannel\s+(?<n>[a-z0-9\- ]+?)\s*[.!?]?$|^channel\s+(?<n>[a-z0-9\- ]+?)\s*[.!?]?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, int> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
        ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
        ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["one-nine"] = 19, ["one nine"] = 19,
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40
    };

    /// <summary>Returns null when the text isn't addressed to the app.</summary>
    public static VoiceCommand? TryParse(string text)
    {
        var m = WakeWord.Match(text ?? string.Empty);
        if (!m.Success) return null;
        string rest = m.Groups["rest"].Value.Trim().TrimEnd('.', '!', '?');
        string lower = rest.ToLowerInvariant();
        // Reported speech ("TruckingAdvisor said to ...") is talk about the app, not a command.
        if (Regex.IsMatch(lower, @"^(says|said|told|thinks|wants|is|was)\b")) return null;

        var ch = ChannelPattern.Match(rest);
        if (ch.Success && ParseNumber(ch.Groups["n"].Value) is { } n and >= 1 and <= 40)
            return new VoiceCommand(VoiceCommandKind.SwitchChannel, n, rest);

        if (Regex.IsMatch(lower, @"\b(call|contact|get|raise)\b.*\bdispatch\b|^dispatch\b")) return new VoiceCommand(VoiceCommandKind.CallDispatch, Raw: rest);
        if (Regex.IsMatch(lower, @"\bunmute\b")) return new VoiceCommand(VoiceCommandKind.Unmute, Raw: rest);
        if (Regex.IsMatch(lower, @"\b(mute|silence)\b")) return new VoiceCommand(VoiceCommandKind.Mute, Raw: rest);
        if (Regex.IsMatch(lower, @"\b(turn\b.*\bdown|quieter|lower\b.*\b(volume|radio)|volume down)")) return new VoiceCommand(VoiceCommandKind.VolumeDown, Raw: rest);
        if (Regex.IsMatch(lower, @"\b(turn\b.*\bup|louder|raise\b.*\b(volume|radio)|volume up)")) return new VoiceCommand(VoiceCommandKind.VolumeUp, Raw: rest);
        if (Regex.IsMatch(lower, @"\b(fuel|diesel|range|gas)\b")) return new VoiceCommand(VoiceCommandKind.FuelReport, Raw: rest);
        if (Regex.IsMatch(lower, @"\b(how (much )?(far|farther|further)|distance|miles (left|to go)|eta|arrive|destination)\b")) return new VoiceCommand(VoiceCommandKind.DistanceReport, Raw: rest);
        if (Regex.IsMatch(lower, @"\b(repeat|say (that|it) again)\b")) return new VoiceCommand(VoiceCommandKind.RepeatLast, Raw: rest);
        return new VoiceCommand(VoiceCommandKind.Unknown, Raw: rest);
    }

    private static int? ParseNumber(string s)
    {
        s = s.Trim().TrimEnd('.', '!', '?');
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return n;
        // Digits spoken one at a time: "1 9" -> 19.
        if (Regex.IsMatch(s, @"^\d(\s+\d)+$") && int.TryParse(s.Replace(" ", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
        if (Words.TryGetValue(s, out n)) return n;
        // "twenty one", "twenty-five", "thirty"
        var parts = s.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && Words.TryGetValue(parts[0], out int tens) && tens % 10 == 0 && Words.TryGetValue(parts[1], out int ones) && ones < 10)
            return tens + ones;
        return null;
    }
}

/// <summary>Spots the player introducing a CB handle ("call me Highway Ghost", "my handle is ...").</summary>
public static class HandleDetector
{
    private static readonly Regex Pattern = new(
        @"\b(?:call me|my handle(?:'s| is)|they call me|handle's)\s+(?<h>[A-Za-z][A-Za-z'\-]*(?:\s+[A-Za-z][A-Za-z'\-]*){0,2})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> TrailingNoise = new(StringComparer.OrdinalIgnoreCase)
        { "over", "out", "and", "on", "here", "now", "please", "driver", "ok", "okay", "copy" };

    // "call me when you get there", "call me back" are requests, not names.
    private static readonly HashSet<string> NotAHandle = new(StringComparer.OrdinalIgnoreCase)
    {
        "when", "if", "back", "later", "after", "before", "at", "in", "to", "the", "a", "an", "you", "your", "i", "we",
        "he", "she", "they", "it", "tomorrow", "tonight", "sometime", "maybe", "up", "anytime", "whenever", "once", "soon",
        "crazy", "anything", "whatever", "what", "something", "sir", "buddy", "pal", "man"
    };

    public static string? TryDetect(string text)
    {
        var m = Pattern.Match(text ?? string.Empty);
        if (!m.Success) return null;
        var words = m.Groups["h"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && TrailingNoise.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        if (words.Count == 0 || words.Any(NotAHandle.Contains)) return null;
        var ti = CultureInfo.InvariantCulture.TextInfo;
        return string.Join(' ', words.Select(w => ti.ToTitleCase(w.ToLowerInvariant())));
    }
}
