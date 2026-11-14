using TruckingAdvisor.Shared.Input;

namespace TruckingAdvisor.Presentation;

/// <summary>
/// One page of the built-in user guide. Lines are plain text with a little markup:
/// "## " starts a sub-heading, "• " a bullet, "› " an example to say or type, "key | text" a two-column row.
/// </summary>
public sealed record GuideTopic(string Title, string Summary, IReadOnlyList<string> Lines)
{
    public bool Matches(string query) =>
        string.IsNullOrWhiteSpace(query) ||
        Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Summary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Lines.Any(l => l.Contains(query, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The user guide (F1, the ? button, or More > User Guide). The Controls page is built from the
/// player's current bindings, so it always shows their own keys and wheel buttons.
/// </summary>
public static class Guide
{
    /// <summary>Every voice command, with example phrases. Each example is checked against the real parser by a test.</summary>
    public static readonly IReadOnlyList<(string What, string[] Say)> VoiceCommands = new[]
    {
        ("Change channel (1-40)", new[] { "TruckingAdvisor, switch to channel nineteen", "TruckingAdvisor, go to channel 30", "Advisor, channel one-nine" }),
        ("Call dispatch", new[] { "TruckingAdvisor, call dispatch" }),
        ("Fuel and range", new[] { "TruckingAdvisor, fuel report", "TruckingAdvisor, how's my range?" }),
        ("Distance and ETA", new[] { "TruckingAdvisor, how far to go?", "TruckingAdvisor, what's my ETA?" }),
        ("Radio volume", new[] { "TruckingAdvisor, turn it up", "TruckingAdvisor, turn it down" }),
        ("Mute / unmute", new[] { "TruckingAdvisor, mute", "TruckingAdvisor, unmute" }),
        ("Repeat the last advice", new[] { "TruckingAdvisor, repeat that" }),
    };

    public static IReadOnlyList<GuideTopic> Build(InputBindingSet? bindings = null)
    {
        string Key(string action)
        {
            var list = bindings?.For(action);
            return list is { Count: > 0 } ? string.Join(" or ", list.Select(b => b.Display)) : "not bound";
        }

        var controls = new List<string>
        {
            "Every function can go on keyboard keys and on buttons or D-pads of a wheel, button box, joystick or gamepad. Change them in More > Controls & Key Bindings: click Add, press the key or button. These are YOUR current bindings:",
            ""
        };
        foreach (var group in InputActions.All.GroupBy(a => a.Category))
        {
            controls.Add("## " + group.Key);
            foreach (var a in group) controls.Add($"{a.Label} | {Key(a.Id)}");
        }
        controls.AddRange(new[]
        {
            "",
            "• Keys and buttons still reach the game too, so pick ones the game isn't using.",
            "• Bindings belong to a device: button 1 on your wheel and button 1 on your button box are different.",
            "• Plug devices in any time; they're listed at the bottom of the Controls window within a few seconds.",
            "• F1 opens this guide whenever the TruckingAdvisor window is active.",
        });

        var voice = new List<string>
        {
            "Hold push-to-talk and say a command. It must START with \"TruckingAdvisor\" (or \"Trucking Advisor\", or \"Advisor,\" with a pause). Anything else goes out over the CB as normal talk.",
            ""
        };
        foreach (var (what, say) in VoiceCommands)
        {
            voice.Add("## " + what);
            voice.AddRange(say.Select(s => "› " + s));
        }
        voice.AddRange(new[]
        {
            "",
            "## Your CB handle",
            "Introduce yourself on the radio and the app offers to save it:",
            "› Call me Night Owl",
            "› My handle's Night Owl",
            "",
            "Voice needs a microphone (Settings > Use my microphone). Speech is understood by OpenAI when AI is on, or by Windows speech recognition offline if you turn that on in Settings.",
        });

        return new[]
        {
            new GuideTopic("Getting started", "Install, connect, first call, first delivery", new[]
            {
                "## 1. Connect to the game",
                "• TruckingAdvisor finds ATS and ETS2 on any drive. If the telemetry plugin is missing it offers to install it (More > Install Telemetry Plugin). The plugin download is checked against the official release.",
                "• Start the game and drive off. The header turns green: CONNECTED TO ATS (or ETS2).",
                "## 2. Say hello on the CB",
                $"• Hold push-to-talk ({Key(InputActions.PushToTalk)}) and talk, or type in the radio box and press Enter.",
                "› Breaker one-nine, anybody got their ears on?",
                "## 3. Take a job",
                "• Everything is recorded: your score, pay, fines, route. The delivery banner shows the result for 20 seconds.",
                "## 4. Optional: AI",
                "• Without AI the truckers answer with short offline lines. For real conversations: Settings > Enable AI, add your OpenAI key (stored encrypted for your Windows account), set spending limits.",
                "",
                "The checklist on the dashboard ticks these off and hides itself when you're done. No game handy? Start the \"TruckingAdvisor (demo data)\" shortcut or More > Try Demo Data.",
            }),

            new GuideTopic("Dashboard", "Gauges, delivery, coaching, banners", new[]
            {
                "• Gauges: speed (with a marker at the speed limit), engine RPM (with redline), fuel and live trip score. Numbers you can't trust show --, never a guess.",
                "• Active delivery: cargo, route, distance and ETA from the game's navigation, rest timer.",
                "• Coach, fuel & maintenance: advice from your real numbers, plus recent coaching events and game events (fines, tolls, ferries, refuels).",
                "• Rearrange or hide cards: More > Layout & Theme. Drag the divider between the dashboard and the radio to resize.",
                "• Banners at the top come and go on their own: delivery results (20 s), updates ready to install, the getting-started checklist.",
                "• Quiet 30 min (top right): nobody calls you unprompted for 30 minutes. Safety warnings and your own calls still work.",
            }),

            new GuideTopic("HUD overlay", "Speed, limit sign, fuel, channel picker over the game", new[]
            {
                $"• {Key("toggleOverlay")} shows or hides the HUD over the game (borderless or windowed mode; exclusive fullscreen hides other windows).",
                "• Big speed (red with an OVER badge when speeding), a US-style speed-limit sign, fuel bar, and whichever rows you pick in More > Layout & Theme, including an optional mini-map.",
                "• It never takes clicks or keyboard focus from the game. To move or resize it: More > Move/Resize HUD, drag it, then lock it again.",
                "• The channel picker under the HUD takes clicks without stealing focus. Click it to open the labeled channel list.",
                $"• Next / previous channel: {Key("cycleCbChannel")} / {Key("previousCbChannel")}. The list pops open for a few seconds so you see where you landed.",
            }),

            new GuideTopic("CB radio", "Channels, push-to-talk, truckers, conversation starters", new[]
            {
                "## Channels",
                "19 | Highway traffic: the main channel",
                "9 | Emergency: kept clear, no chatter",
                "17 | Regional traffic",
                "21 | Casual conversation",
                "25 | Truck stop talk",
                "30 | Company dispatch: private, only your dispatcher",
                "",
                $"• Talk: hold {Key(InputActions.PushToTalk)} (or the HOLD TO TALK button), or type and press Enter.",
                "• Call a trucker by saying their handle. Otherwise you keep talking to whoever you spoke to in the last 10 minutes.",
                "## The regulars",
                "Big Red | Texas veteran: mountain grades, classic Petes, diesel prices",
                "Highway Hank | Tennessee storyteller: truck stop breakfasts, wild loads",
                "Diesel Dave | Ohio mechanic: MPG, maintenance, DD15 tuning",
                "Midnight Mike | Nevada night driver: desert skies, empty highways",
                "Roadrunner Rachel | Arizona heavy-haul pro: permits, dock backing, inspections",
                "• New drivers turn up now and then and stay. Everyone remembers you; relationships grow over time (CB Contacts tab).",
                "## Conversation starters",
                "› Breaker one-nine, Big Red, you out there?",
                "› Diesel Dave, I'm only getting five and a half MPG. What am I doing wrong?",
                "› Rachel, any tips for backing into a tight dock?",
                "› Hank, what's the strangest load you ever hauled?",
                "› Anybody know a good truck stop for breakfast around here?",
                "› Hauling 40,000 pounds of lumber to Denver. Any advice?",
                "## What they know",
                "• Your speed, load, route and fuel from the game, and roughly where you are in the real world (after deliveries in two cities).",
                "• They're simulated voices: they can't see your truck, traffic, police or weather, and they'll say so.",
                "• How chatty they are: Settings > Conversation mode and Max unprompted calls / hour, or Quiet 30 min.",
            }),

            new GuideTopic("Voice commands", "Hands-free app control", voice),

            new GuideTopic("Controls & key bindings", "Your keys and wheel / button-box buttons", controls),

            new GuideTopic("Dispatch", "Your company dispatcher on channel 30", new[]
            {
                "• Dispatch calls you with load briefings, check-ins and reminders (fuel, deadline, damage) on channel 30, whatever channel you're on.",
                "› Dispatch, checking in. How am I looking?",
                "› Dispatch, I'm going to be a little late on this one.",
                $"• Quick check-in: {Key("quickDispatchCheck")} or say \"TruckingAdvisor, call dispatch\".",
                "• How often: Settings > Dispatch contact. Truckers never hear channel 30; dispatch never hears CB chatter.",
            }),

            new GuideTopic("Map & real places", "Self-drawing map, real towns and highways", new[]
            {
                "• The Map tab draws every road you drive, separately for ATS and ETS2, in any DLC region or map mod. Drag to pan, mouse wheel to zoom, double-click to follow the truck.",
                "• Cities appear once you've picked up or delivered there; your destination is marked if you've been there before.",
                "• After deliveries in two cities, the game map is matched to real geography (Natural Earth data, bundled). Then the header, the map and the CB truckers know the real-world area: nearest town, state or country, probable highway and the next town ahead. It's an estimate and gets better with every new city.",
                "• Steep downgrades you've driven before are announced ahead of time.",
            }),

            new GuideTopic("Deliveries, money & career", "Scores, pay, fines, reports, achievements", new[]
            {
                "• Every delivery gets a score out of 100 across ten categories (speed, braking, fuel, damage, punctuality and more).",
                "• Pay and XP, fines (with the offence), tolls, ferry and train fares and refuels come from the game. Reports show the net income.",
                "• Trips tab: export CSV, JSON, text, PDF or a shareable PNG image.",
                "• Career & Company: levels, XP, stats per truck, achievements and company milestones.",
                "• Statistics: trends for score, fuel economy, coaching events and trip distance.",
                "• Units: Settings > Units (Auto uses miles in ATS and kilometres in ETS2).",
            }),

            new GuideTopic("Settings, AI & cost", "AI on/off, voices, devices, spending", new[]
            {
                "• Everything except AI conversations works offline with no account.",
                "• AI uses your own OpenAI key, stored encrypted for your Windows account (never in a plain file). Daily request and spending limits stop it before it costs more than you allow.",
                "• No model names to choose: pick AI quality (Economy, Balanced, Best) and TruckingAdvisor uses the best built-in model your key can use, switching automatically if OpenAI retires one.",
                "• Voices: free Microsoft neural voices by default (each trucker, dispatch and the co-driver has their own), Windows voices offline, or OpenAI voices with your key. Settings > Test voices plays a sample of each.",
                "• Realtime voice mode answers faster (costs more).",
                "• Pick separate audio devices for the radio, dispatch and co-driver; clean audio turns off the radio static.",
                "• Co-driver personality: Professional, Friendly, Old-School Trucker, Strict Instructor, or Minimal (only important warnings).",
            }),

            new GuideTopic("Updates & troubleshooting", "Updates, logs, bug reports", new[]
            {
                "• Updates download in the background, are checked against their published checksum, and install when you click Restart & update or when you close the app. More > Check for Updates checks now.",
                "• Not connecting? Check the header: it says what's wrong (game not running, plugin missing, plugin version). More > Install Telemetry Plugin fixes most cases.",
                "• HUD not showing over the game: switch the game to borderless or windowed mode.",
                "• Truckers reply in text but you hear nothing? Settings > Test voices: it says which voice spoke or what failed. Check the output devices there too. Without internet, Windows voices take over automatically.",
                "• Found a bug? More > Copy Diagnostics copies version, status and the recent log (no keys, user name or personal paths) for a bug report.",
                "• Strange data? More > Record a Session saves the raw telemetry of a drive so the problem can be replayed exactly. Stop it from the same menu.",
                "• Project page: github.com/Jcedwar61/TruckingAdvisor",
            }),
        };
    }
}
