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
            "Voice needs a microphone (Settings > Use my microphone). Speech is understood by Whisper on your PC (free, offline, private; the model downloads once) or by OpenAI when AI is on. Settings > Test microphone shows your level and what was heard.",
            "Tips: hold push-to-talk a moment before speaking and talk normally; names like Big Red, your handle and your cities are recognized best because the recognizer is told about them.",
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
                "• Without AI the truckers answer with short offline lines. For real conversations: Settings > Enable AI, add your OpenAI key and/or a DeepSeek key (stored encrypted for your Windows account), set spending limits.",
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
                "Carolina Chrome | Carolina long-hauler: east-coast runs, chrome, family",
                "Bayou Belle | Louisiana tanker driver: swamp roads, gumbo, hurricanes",
                "Glacier Gus | Montana flatbed veteran: ice, chains, mountain passes",
                "Texas Slim | Panhandle cattle hauler: lonely roads, rodeo, big skies",
                "Prairie Rose | Kansas grain hauler: harvest season, small towns, weather",
                "Cajun Coyote | Gulf-coast hotshot: hurry-up freight, back roads, bad coffee",
                "Flatbed Frank | Midwest steel hauler: tarps, straps, load securement",
                "Mama Bear | Missouri owner-operator: looking out for rookies, home cooking",
                "Vinegar Val | Pacific Northwest log hauler: sour streak, steep ground, rain",
                "Dusty Dakota | Dakota hay and livestock: wind, wide-open country, old trucks",
                "• Each has their own story, their own voice and their own opinions. Any of them will start a conversation now and then, about whatever is on their mind. They talk like truckers: swearing, crude jokes, no formality. Prefer it clean? Pick TV-friendly radio language.",
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
                "## Talking to each other",
                "• With AI on, the truckers hold real back-and-forth conversations: each reply builds on what the last driver said (stories, questions, friendly arguments, the road you're on). A busier Background radio activity means longer, more frequent conversations.",
                "• Sometimes another driver chimes in after a trucker answers you. Press push-to-talk any time to cut in; whoever spoke last answers you.",
                "• Radio language: R-rated by default (uncensored: heavy swearing, raunchy and dark humour; only slurs/hate and anything involving minors are off limits) or TV-friendly (clean). Settings > Radio language, or bind \"Radio language\" in More > Controls to flip it from your wheel.",
                "• Two AIs: add a DeepSeek key (optional) and DeepSeek writes the truckers' conversations while OpenAI answers you. Each covers for the other. The best model for each job is picked for you.",
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

            new GuideTopic("Twitch, overlay & convoy", "Let stream chat talk to the truckers; share the radio with friends", StreamingLines()),

            new GuideTopic("SimHub dashboards", "Second screen, tablet, or a triple-screen DDU with round screens", new[]
            {
                "Open More > Twitch, Overlay, SimHub & Convoy. SimHub is free from simhubdash.com.",
                "• SimHub draws your speed, speed limit, gear, RPM, fuel and warning lights from its own ATS / ETS2 data, so those keep working with TruckingAdvisor closed. TruckingAdvisor adds your driver score, the delivery, the CB radio, coaching and your career.",
                "• Tick \"Serve the SimHub panel\". It uses the same local port as the stream overlay (8789 unless you changed it).",
                "## One screen, tablet or phone",
                "• \"Add the dashboard to SimHub\" hands the 1280 x 720 \"TruckingAdvisor\" dashboard to SimHub, which asks to import it. Pick it in Dash Studio and show it on a monitor, or open SimHub's dashboard on a tablet or phone.",
                "• The right half is TruckingAdvisor's panel in SimHub's web view. It also works on its own in any browser: http://localhost:8789/simhub.",
                "## Triple-screen DDU (two rounds and a 4\" screen)",
                "• Made for units such as the DaSIMSIMMA triple: two 3.4\" round VoCore screens either side of a 4\" one. Three dashboards: Triple Left (speed ring, speed limit, cruise, driver score), Triple Center (portrait: CB channel and the last line heard, driver score, delivery progress, warnings) and Triple Right (RPM and fuel rings, gear, park brake and air, CB channel).",
                "• \"Add the triple-screen set to SimHub\" imports all three and installs TruckingAdvisor's SimHub plugin (Windows may ask for permission, because SimHub lives in Program Files). Restart SimHub and say yes when it asks to enable \"TruckingAdvisor\".",
                "• From then on the plugin switches the round screens and the 4\" screen to the triple set whenever TruckingAdvisor is running, and puts your own dashboards back a few seconds after TruckingAdvisor closes. Untick the automatic switch to choose dashboards yourself.",
                "• Left and right the wrong way round? Tick \"Swap the round screens\".",
                "• Triple Center is portrait (480 x 800). If it shows sideways, set the 4\" screen's rotation in SimHub (Devices > the screen > Rotation).",
                "• Without the plugin, give each screen its dashboard in SimHub (Devices > the screen > Dashboard).",
                "• The plugin keeps a log in SimHub's folder: PluginsData\\TruckingAdvisor\\plugin.log (which screens it found and what it switched).",
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
                "• AI uses your own OpenAI and/or DeepSeek key, stored encrypted for your Windows account (never in a plain file). Daily request and spending limits stop it before it costs more than you allow.",
                "• No model names to choose: pick AI quality (Economy, Balanced, Best) and TruckingAdvisor uses the best built-in model your key can use, switching automatically if OpenAI retires one.",
                "• Voices: the on-device voices are free and offline once downloaded (Kokoro is the most natural, one voice per character; Supertonic is smaller and faster). Free Microsoft neural voices, Windows voices or OpenAI voices with your key also work. Settings > Test voices plays a sample of each.",
                "• Realtime voice mode answers faster (costs more).",
                "• Pick separate audio devices for the radio, dispatch and co-driver; clean audio turns off the radio static.",
                "• Co-driver personality: Professional, Friendly, Old-School Trucker, Strict Instructor, or Minimal (only important warnings).",
            }),

            new GuideTopic("Updates & troubleshooting", "Updates, logs, bug reports", new[]
            {
                "• Updates download in the background, are checked against their published checksum, and install when you click Restart & update or when you close the app. More > Check for Updates checks now.",
                "• Not connecting? Check the header: it says what's wrong (game not running, plugin missing, plugin version). More > Install Telemetry Plugin fixes most cases.",
                "• HUD not showing over the game: switch the game to borderless or windowed mode.",
                "• Your words aren't understood? Settings > Test microphone: check the level (\"very quiet\" means a muted or wrong mic) and what was heard. OpenAI recognition is the most accurate with AI on; the Fast offline model answers quickest.",
                "• Truckers reply in text but you hear nothing? Settings > Test voices: it says which voice spoke or what failed. Check the output devices there too. Without internet, Windows voices take over automatically.",
                "• Found a bug? More > Copy Diagnostics copies version, status and the recent log (no keys, user name or personal paths) for a bug report.",
                "• Strange data? More > Record a Session saves the raw telemetry of a drive so the problem can be replayed exactly. Stop it from the same menu.",
                "• Project page: github.com/Jcedwar61/TruckingAdvisor",
            }),
        };
    }

    /// <summary>The streaming page lists the real chat commands, so it can't drift from what the app accepts.</summary>
    private static IReadOnlyList<string> StreamingLines()
    {
        var lines = new List<string>
        {
            "Open More > Twitch, Overlay, SimHub & Convoy.",
            "## Twitch chat on the CB",
            "• Tick \"Let Twitch chat talk on the CB radio\" and type your channel name. Chat is read anonymously: no Twitch login or token, and nothing is ever posted.",
            "• Viewers type a command and their message; the message is read out in a caller voice, then a trucker or crew member answers on the radio, out loud, using your live truck data.",
        };
        foreach (var c in TruckingAdvisor.AI.Streaming.ViewerCallGate.AllCommands)
            lines.Add($"{c.Command}{(c.Aliases.Length > 0 ? " (" + string.Join(", ", c.Aliases) + ")" : "")} | {c.Description}");
        lines.AddRange(new[]
        {
            "› !cb anybody got eyes on a bear?",
            "› !mechanic how are the tires holding up?",
            "• Cooldowns keep the cab from flooding (30 s between calls, 2 min per viewer by default; mods skip their own). Up to three calls wait in line.",
            "• Channel Points: make a reward with \"Require viewer to enter text\". Redeeming it keys up on the CB and skips the cooldown. A reward message can start with !mechanic etc. to pick who answers.",
            "• With AI on, replies come from your AI (OpenAI, or any OpenAI-compatible service such as Groq or a local Ollama). With AI off, built-in replies still use your real fuel, speed, load, wear and rest time.",
            "• Links, emoji and letter spam are stripped; add your own blocked words. Your push-to-talk always cuts in ahead of viewers.",
            "• \"Play a test call\" tries it without Twitch.",
            "## Stream overlay",
            "• Tick \"Run the stream overlay\", then in OBS add a Browser Source with http://localhost:8789/overlay at 1920 x 1080.",
            "• It shows the chat commands with a live \"Next call\" countdown, and a CB radio banner while a viewer is on the air.",
            "/overlay/commands | Commands panel only",
            "/overlay/banner | Caller banner only",
            "/overlay/feed | Recent radio lines",
            "?pos=br, tl or tr | Move the panel to another corner",
            "?mode=ticker | Compact strip that rotates through the commands",
            "?scale=1.3 | Bigger or smaller",
            "?demo=1 | Sample data, to position it while offline",
            "## Convoy CB",
            "• Friends who also run TruckingAdvisor tick \"Join a convoy\" and type the same convoy code. Everyone on the same CB channel sees each other's lines, and one shared AI answers so all of you hear the same reply.",
            "• Talk to the AI as usual (\"Big Red, how's my fuel?\"); a line just for a friend (\"Zay, you see that?\") gets no AI reply. Ask about \"my\" truck and the AI uses that driver's own numbers.",
            "• One PC answers for the AI: tick \"This PC answers\" on one of them, or let the app pick. If that PC goes quiet, the asker's own PC answers after a few seconds.",
            "• Your Twitch viewers' calls are heard in the whole convoy.",
            "• Messages are encrypted with the code and pass through ntfy.sh (free, a few hundred messages a day per PC) or your own ntfy server. Keep the code off stream.",
        });
        return lines;
    }
}
