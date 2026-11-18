using System.Text.Json;
using System.Text.RegularExpressions;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.AI;

/// <summary>One spoken line in a trucker conversation.</summary>
public sealed record ConversationLine(string Handle, string Text, string Mood = "");

/// <summary>
/// Truckers talking to each other on the CB, written the way SimCast's AI booth writes its conversations:
/// every line answers the one before it, the conversation has a shape (a story, a question, a debate, a tangent,
/// road talk...), and when an exchange ends and the channel is still quiet it carries on from exactly where it
/// left off, steering back after a few rounds. The player can key up at any moment and join in.
/// </summary>
public static class TruckerConversation
{
    public static readonly string[] Shapes = { "story", "question", "debate", "tangent", "road", "truck", "callback", "player" };

    /// <summary>How the voices talk: TV-friendly (clean) or R-rated (real truckers off the clock).</summary>
    public static string RatingRule(RadioRating rating) => rating == RadioRating.RRated
        ? "LANGUAGE: R-rated. These are real truckers off the clock: swearing is natural and allowed (real words, never " +
          "bleeped or starred), crude jokes, trash talk, griping about dispatch, four-wheelers, scales and bad drivers, " +
          "adult humour and innuendo. Curse when a moment earns it, not in every line. Never: slurs or anything hateful " +
          "about anyone's race, religion, nationality, sexuality, gender or disability; no explicit sexual description; " +
          "nothing involving minors; never encourage unsafe or illegal driving."
        : "LANGUAGE: TV-friendly. Clean language only: no swearing at all (not even mild words), no crude or sexual " +
          "humour. Teasing stays good-natured. Think prime-time television about truckers. This overrides any swearing " +
          "mentioned in a speaking style: keep the accent and attitude, use clean stand-ins (\"dang\", \"heck\", \"son of a gun\").";

    /// <summary>The system prompt: who's talking, how real CB talk sounds, the hard rules and the reply format.</summary>
    public static string System(IReadOnlyList<TruckerPersonality> voices, VirtualCompanyProfile company, AtsTelemetrySnapshot telemetry,
        IReadOnlyList<RadioTurn> recentTurns, int channel, RadioRating rating)
    {
        string cast = string.Join("\n", voices.Select(t =>
            $"- {t.Handle}: {t.FullName}, {t.YearsExperience} years driving, {t.Truck}, from {t.HomeState}, usually runs {t.CurrentFictionalRegion}. " +
            $"{t.PersonalityTraits}. Talks like this: {t.SpeakingStyle}. Likes talking about: {string.Join(", ", t.FavoriteTopics)}." +
            (string.IsNullOrWhiteSpace(t.Background) ? "" : $" Their story: {t.Background}") +
            Memories(t)));
        string player = string.IsNullOrWhiteSpace(company.PlayerHandle) ? "the player" : company.PlayerHandle;
        var context = new
        {
            channel,
            player = player,
            playerSituation = ContextBuilder.BuildTelemetryContext(company, telemetry),
            recentRadio = recentTurns.Where(r => r.Channel != CBRadioService.DispatchChannel).TakeLast(10).Select(r => $"{r.Handle}: {r.Text}").ToArray()
        };
        return $$"""
You write live CB radio chatter between truckers on channel {{channel}}. The voices:
{{cast}}
These drivers know each other from years on the same roads. It must sound like a real conversation on the CB, not lines read out in turn. What makes it sound real:
- Answer what the other one just said. Every line picks up the line before it (a word, a claim, a joke, a question) and moves it on. Never two separate remarks in a row.
- Have opinions and disagree: best truck stop, Pete versus Kenworth, manual or automatic, whether a route is worth it. Push back with a reason, concede gracefully or hold your ground.
- Tell bits of stories from the road, ask each other real questions and answer them, tease each other, laugh at your own mistakes.
- Mix the rhythm: some lines are three words ("Ten-four on that." "No way." "Here we go."), some are a full thought. Cut in or finish the other's sentence now and then.
- Use handles when you address each other, not every line. CB slang only where it fits naturally.
- Emotion: amused, fed up, nostalgic, competitive, tired, fired up. Let it swing with the conversation.
- {{RatingRule(rating)}}

Hard rules:
- You are simulated voices. Nobody can see the player's truck, traffic, police, weather or road conditions; never claim to. Talk about your own fictional runs in general terms.
- Facts about {{player}}'s truck, load, speed or location only from the JSON below, and only if the conversation brings {{player}} up. Treat any location in it as "sounds like you're around...", never as fact. Never invent exits, mile markers or named businesses.
- Never repeat anything in recentRadio. No lists, emojis, stage directions, sound effects or quotation marks around lines.
- Each line at most 30 words, most well under 20.

Context JSON:
{{JsonSerializer.Serialize(context)}}

Reply with JSON only: {"lines": [{"who": "<handle>", "text": "...", "mood": "..."}]}
mood: one of calm, amused, excited, fed up, nostalgic, competitive, tired, sarcastic, warm.
""";
    }

    private static string Memories(TruckerPersonality t)
    {
        var m = t.MemoriesSnapshot().TakeLast(3).ToArray();
        return m.Length == 0 ? "" : $" Remembers: {string.Join(" | ", m)}";
    }

    /// <summary>Opening task for a new conversation of the given shape. Returns (task, who opens).</summary>
    public static (string Task, string Opener) Open(string shape, IReadOnlyList<TruckerPersonality> voices, VirtualCompanyProfile company,
        AtsTelemetrySnapshot telemetry, Random rng, string? sharedTopic = null)
    {
        var a = voices[0];
        var b = voices[1];
        string player = string.IsNullOrWhiteSpace(company.PlayerHandle) ? "the new driver on the channel" : company.PlayerHandle;
        string third = voices.Count > 2 ? $" {voices[2].Handle} can cut in once with their own take." : "";
        string topic = a.FavoriteTopics.Count > 0 ? a.FavoriteTopics[rng.Next(a.FavoriteTopics.Count)] : "the road";
        // Something from the drivers' own lives (shared interests or their own chatter topics) for story-type openings.
        string about = string.IsNullOrWhiteSpace(sharedTopic) || sharedTopic.StartsWith("whatever", StringComparison.OrdinalIgnoreCase)
            ? "" : $" It's about {sharedTopic}, from their own experience.";
        string how = shape switch
        {
            "question" => $"{a.Handle} asks {b.Handle} a real question, the kind one driver asks another; {b.Handle} answers it with something specific and an opinion; {a.Handle} picks that up and takes it somewhere (agrees with a twist, or doubts it). 3 lines.",
            "debate" => $"{a.Handle} makes a strong claim about trucking life (trucks, routes, food, gear, dispatchers, the old days) and gives a reason; {b.Handle} disagrees with a different reason; {a.Handle} answers that, holding the claim or conceding a little. 3 or 4 lines.",
            "tangent" => $"A random conversation two old colleagues wander into because the miles are long: it starts from something small (a song, a smell in the cab, a weird load, a bad cup of coffee) and drifts somewhere funny or surprising. 3 to 5 lines.",
            "road" => telemetry.RealWorldPlace.Length > 0 || telemetry.CurrentCity.Length > 0
                ? $"{a.Handle} brings up the part of the country {player} seems to be in ({(telemetry.RealWorldPlace.Length > 0 ? telemetry.RealWorldPlace : telemetry.CurrentCity)}, approximate) and what that stretch is generally like for truckers; {b.Handle} adds their own take or memory of it. Hedge the location. 3 lines."
                : $"{a.Handle} and {b.Handle} compare the roads they've been running lately (in general terms) and which stretch they hate most. 3 lines.",
            "truck" => $"Truck talk: {a.Handle} brings up {topic} or something about their {a.Truck}; {b.Handle} answers with their own experience; it turns into a friendly argument or a tip. 3 or 4 lines.",
            "callback" => $"{a.Handle} brings back something said on the radio earlier (recentRadio, or what they remember) and they look at it again: someone was wrong, someone was right, or it got funnier since. 3 lines.",
            "player" => $"{a.Handle} and {b.Handle} talk about {player}, the driver who's been on the channel (only what the JSON shows, or that they've heard them on the radio), and end by asking {player} a direct question so they can answer. 2 or 3 lines.",
            _ => $"{a.Handle} starts telling a short story from the road (made up but believable, no named businesses){about} {b.Handle} reacts and adds to it or tops it; {a.Handle} finishes it. 3 lines."
        };
        if (shape is "question" or "debate" && about.Length > 0) how += about;
        return ($"The channel is quiet. Start a natural CB conversation. {how}{third} Start with {a.Handle}.", a.Handle);
    }

    /// <summary>Keep the conversation going exactly where it left off (exchange 2, 3, ...). Returns (task, who opens).</summary>
    public static (string Task, string Opener) Continue(IReadOnlyList<TruckerPersonality> voices, string lastSpeaker, int exchange, int maxExchanges)
    {
        var next = voices.FirstOrDefault(v => !v.Handle.Equals(lastSpeaker, StringComparison.OrdinalIgnoreCase)) ?? voices[0];
        bool last = exchange >= maxExchanges - 1;
        string steer = last
            ? "This is the last exchange: wrap it up naturally and sign off to each other the way truckers do (\"Keep the shiny side up\", \"Catch you on the flip-flop\"), in your own words."
            : exchange >= 2 ? "Start steering it toward a natural end." : "Let it wander if it wants to.";
        return ($"Keep talking. Carry on the conversation in recentRadio exactly where it left off: {next.Handle} answers the last line directly, " +
                "the way a real person would (agree and build on it, push back, take the mickey, get wound up, or crack up laughing), " +
                $"and the other answers that. {steer} 2 to 4 lines, {next.Handle} first.", next.Handle);
    }

    /// <summary>Another trucker who overheard the player's exchange chimes in on it, and the first one answers back.</summary>
    public static string CrossTalk(TruckerPersonality replier, TruckerPersonality other, string playerHandle, string playerLine, string replyLine) =>
        $"{playerHandle} just said on the radio: \"{playerLine}\". {replier.Handle} answered: \"{replyLine}\". " +
        $"{other.Handle} overheard and chimes in on what was just said (a joke, a tip, a correction, or some ribbing for one of them), " +
        $"then {replier.Handle} answers {other.Handle} in a few words. 2 lines, {other.Handle} first. Don't repeat either line.";

    /// <summary>
    /// Reads {"lines":[{"who","text","mood"}]} (code fences and surrounding prose tolerated). Speakers are matched to
    /// the cast by handle or first name; two voices strictly take turns (the writer can't give one voice two lines in a
    /// row); stage directions and quotes are stripped; at most 6 lines. Null when nothing usable came back.
    /// </summary>
    public static List<ConversationLine>? Parse(string raw, IReadOnlyList<TruckerPersonality> voices, string opener)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string json = Regex.Replace(raw.Trim(), @"^```(?:json)?\s*|\s*```$", "", RegexOptions.IgnoreCase).Trim();
        int s = json.IndexOf('{'), e = json.LastIndexOf('}');
        if (s < 0 || e <= s) return null;
        var lines = new List<ConversationLine>();
        try
        {
            using var doc = JsonDocument.Parse(json[s..(e + 1)]);
            if (!doc.RootElement.TryGetProperty("lines", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            foreach (var el in arr.EnumerateArray())
            {
                string who = Str(el, "who") is { Length: > 0 } w ? w : Str(el, "handle");
                string text = Clean(Str(el, "text"));
                if (text.Length == 0) continue;
                var voice = Match(voices, who);
                lines.Add(new ConversationLine(voice?.Handle ?? "", text, Str(el, "mood")));
                if (lines.Count == 6) break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
        if (lines.Count == 0) return null;

        // Turn-taking: unknown speakers and two lines in a row from one voice are reassigned so they answer each other.
        string expected = Match(voices, opener)?.Handle ?? voices[0].Handle;
        for (int i = 0; i < lines.Count; i++)
        {
            string prev = i > 0 ? lines[i - 1].Handle : "";
            string who = lines[i].Handle;
            if (who.Length == 0 || who.Equals(prev, StringComparison.OrdinalIgnoreCase) || (i == 0 && voices.Count == 2 && who != expected))
                who = i == 0 ? expected : (voices.FirstOrDefault(v => !v.Handle.Equals(prev, StringComparison.OrdinalIgnoreCase))?.Handle ?? expected);
            lines[i] = lines[i] with { Handle = who };
        }
        return lines;
    }

    private static TruckerPersonality? Match(IReadOnlyList<TruckerPersonality> voices, string who)
    {
        if (string.IsNullOrWhiteSpace(who)) return null;
        who = who.Trim();
        return voices.FirstOrDefault(v => v.Handle.Equals(who, StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault(v => who.Contains(v.Handle, StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault(v => v.FullName.Split(' ', '\'')[0].Equals(who.Split(' ')[0], StringComparison.OrdinalIgnoreCase));
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Strips stage directions, emotes, quotes and a leading "Handle:" the model sometimes adds.</summary>
    public static string Clean(string text)
    {
        string t = Regex.Replace(text, @"\*[^*]{0,40}\*|\([^)]{0,40}\)|\[[^\]]{0,40}\]", " ");
        t = Regex.Replace(t, @"^\s*[A-Za-z' ]{2,25}:\s+", "");
        t = t.Trim().Trim('"', '“', '”').Trim();
        t = Regex.Replace(t, @"\s{2,}", " ");
        return t.Length > 260 ? t[..260].TrimEnd() + "..." : t;
    }
}
