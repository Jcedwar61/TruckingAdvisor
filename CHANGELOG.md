# Changelog — TruckingAdvisor

## [v3.4.0] — 2026-11-14

### Speech recognition, rebuilt
- **Whisper on your PC** (the offline recognizer SimCoach uses) replaces Windows dictation: free, private, no key, and far better with casual speech, accents and radio slang. The English model downloads once (Fast ~60 MB by default, or Accurate ~180 MB) and is checked against a pinned checksum.
- **No more clipped first or last words.** While the mic is enabled it stays ready, holding only the last half second in memory (nothing is saved or sent). Push-to-talk includes that half second, and recording continues 0.3 s after release.
- **Cleaner audio:** silence trimmed, quiet microphones boosted, DC offset removed; near-silent clips are never sent (no more phantom "Thank you." transcripts), and known recognizer hallucinations are dropped.
- **Names get recognized:** every recognizer is told your handle, the truckers' handles, your dispatcher and company, your current cities and CB slang. OpenAI transcription is also locked to English.
- **Settings > Speech recognition:** Automatic (OpenAI when AI is on, otherwise Whisper), Whisper only, OpenAI first, or Windows basic; offline model size; keep-mic-ready; and **Test microphone**, which shows your level and exactly what was heard and by which recognizer.
- Clearer hints when nothing is heard ("your microphone is very quiet or muted" vs "hold push-to-talk a moment before you speak").
- CI checks it end to end on Windows: a line spoken by the neural voice is transcribed by Whisper in the installed app and must come back right.

## [v3.3.0] — 2026-11-14

### Fixed
- **Calling a different CB contact caused a flood of pop-ups and a crash.** A trucker's memories were being updated by the radio while the contacts list copied them on the screen, which threw an error every time. Trucker state is now updated and read under a lock (reproduced and covered by a test), the contacts list updates in place and keeps your selection, and calling while the radio is busy just shows a short notice.
- **Error pop-ups can no longer pile up.** At most one error message box at a time (and one a minute); further errors show as a notice and go to the log.
- **Truckers replied in text but couldn't be heard.** Voice failures used to be silently swallowed. Now every voice problem is logged and shown, and if one voice source fails another takes over.

### Added
- **Free natural voices, like SimCoach.** Truckers, dispatch and your co-driver now speak with free Microsoft neural voices (the ones Microsoft Edge's Read Aloud uses), each with their own voice: Big Red is deep and unhurried, Highway Hank friendly, Rachel crisp, and newcomers get one of their own. No key or cost. Without internet, Windows voices take over automatically.
- **Settings > Voices**: free neural voices (default), Windows voices (offline) or OpenAI voices (your key), and a **Test voices** button that plays a trucker, dispatch and the co-driver and tells you which voice spoke or what failed.
- **No more model names.** Settings now has one **AI quality** choice (Economy, Balanced, Best). TruckingAdvisor has built-in lists of OpenAI models for chat, listening, voices and realtime mode, uses the best one your key can actually use (it asks OpenAI which models your account has), and if OpenAI rejects a model it moves to the next one and remembers.
- CI now synthesizes real speech with the installed app on Windows (neural voice required, Windows voice checked).

### Licensing
- The neural-voice code is a C# port of parts of edge-tts and lives in its own LGPL-3.0 library (`TruckingAdvisor.EdgeTts`); the rest of the app stays MIT. Details in THIRD_PARTY_NOTICES.md.

## [v3.2.0] — 2026-11-14

### Added
- **Built-in guide.** Press **F1**, click the small **?** in the header, or **More > User Guide**. A separate window (no space taken on the dashboard) with searchable pages: Getting started, Dashboard, HUD, CB radio (channels, the regulars, conversation starters), Voice commands, Controls & key bindings, Dispatch, Map & real places, Deliveries & money, Settings & AI cost, Updates & troubleshooting.
- The Controls page shows **your own** current keys and wheel / button-box buttons, and every voice-command example in the guide is checked against the real command parser by the test suite, so the guide can't drift from the app.

## [v3.1.0] — 2026-11-14

### Added
- **Real places on the CB.** TruckingAdvisor now matches the game map to real geography, so the AI truckers and dispatcher know roughly where you are in real-world terms: *"east of Flagstaff, Arizona, on or near I-40, heading east toward Winslow"* (ATS) or the country and E-road you're on (ETS2). Truckers can talk about that stretch the way truckers do ("sounds like you're out around Flagstaff...") but are told it's an estimate and never to invent exits, businesses or mile markers, or to claim they can see you.
- How it works: real towns, states / provinces / countries and numbered highways from **Natural Earth** (public domain) are bundled with the app (about 0.6 MB). Every pickup and delivery tells TruckingAdvisor where a real city is in game coordinates; after **deliveries in two cities** it fits the game map to the real one (separately for ATS and ETS2) and keeps improving with each new city. Nothing is read from the game's files.
- The header shows the real-world place, and the **Map** tab draws real highways and towns faintly under the roads you've driven once the match is ready.

## [v3.0.0] — 2026-11-14

### Added
- **Map tab — a map that draws itself.** Every road you drive is added to a map of your own, separately for ATS and ETS2, in every DLC region and even map mods. Cities appear once you've picked up or delivered there; your destination is marked (with a line to it) if you've been there before. Pan, zoom, follow the truck, or show everything. Re-driving a known road adds nothing, so the data stays small. Nothing is taken from the game's files. Optional **mini-map** HUD row (Layout & Theme).
- **What the game actually pays and charges.** Delivery pay and game XP, fines (with the offence), tolls, ferry and train fares, cancellation penalties and refuels are read from the telemetry plugin. Delivery reports, the trip list, CSV export and the delivery banner show pay, costs and **net income**. Career income uses real pay.
- **Fines get a reaction.** The co-driver says the fine out loud (turn off in Settings), and dispatch may mention a big one once. Tolls, fares and refuels are listed under coaching events, never spoken.
- **Euro Truck Simulator 2 and metric units.** Settings > Units: Auto (ATS miles, ETS2 kilometres), Imperial or Metric — everywhere: dials, HUD, reports, PDFs, charts, co-driver, dispatcher. Money shows as $ in ATS and € in ETS2. The header names the game you're playing.
- **Getting-started checklist** on the dashboard (game found, plugin installed, live data, first CB call, first delivery). Hides itself when done.
- **Diagnostics for bug reports.** A log file (with your user name, personal paths and any API keys removed), crash handling that keeps the app running, **More > Copy Diagnostics**, and **More > Record a Session**, which saves the raw telemetry of a drive so a bug can be replayed exactly (`--replay file`).
- HUD shows an **OVER** badge when speeding, not just red text.
- **Telemetry plugin download is pinned** to the official release checksum: a download that doesn't match is refused instead of asking you to confirm a hash. CI re-checks it against GitHub on every build.

### Fixed
- Learned cities and steep-grade warnings were shared between ATS and ETS2, whose world coordinates overlap, so ETS2 could say "near Denver". Each game now has its own (existing data becomes ATS's).
- A trip now closes one telemetry frame after the delivery event, so the game's final pay and XP are captured.

## [v2.9.0] — 2026-11-13

### Added
- **Automatic updates.** About 20 seconds after start (and every 12 hours) the app checks this project's GitHub releases. A new version is downloaded in the background and checked against the release's published SHA-256 checksum; anything that doesn't match is deleted and never run.
- When an update is ready, a banner appears in the app (never spoken, never a popup): **Restart & update**, **What's new**, **Later** or **Skip this version**. If you just keep driving, it installs by itself when you close the app.
- The installer has a silent update mode: it waits for the app to close, installs over it, keeps your desktop-shortcut choice, and reopens the app when you chose Restart & update. Trips, truckers, settings and your API key are untouched.
- **More > Check for Updates** checks right away. **Settings > Updates** turns automatic checks or install-on-close off.
- CI now tests the update path on Windows: installer waits for the running app, installs, relaunches it, and leaves the desktop shortcut alone.

## [v2.8.1] — 2026-11-12

### Changed
- Channels are changed by scrolling with **Next / Previous CB channel** (bind them to keys or wheel / button-box buttons). The six one-button-per-channel entries were removed from the Controls list; any saved bindings for them are dropped.

## [v2.8.0] — 2026-11-12

### Added
- **Bind every function to a key or a wheel / button-box button.** New **More > Controls & Key Bindings** window: click Add, press a key or a button (or D-pad direction) on any wheel, button box, joystick or gamepad, done. Several bindings per function; binding an input that's already used moves it and says so. Changes apply immediately.
- Bindable functions: push-to-talk (hold), next / previous channel, dispatch check-in, Quiet 30 min, radio volume up / down, stop announcement, mute all, repeat advice, fuel report, distance & ETA report, HUD on/off, show channel picker.
- Controllers are read through DirectInput in the background and non-exclusively, so they keep working while ATS has focus and the game still gets the same buttons. Plug-and-play: devices are picked up within a few seconds. Bindings are tied to the specific device, so button 1 on your wheel and button 1 on your button box are different.
- Channel labels everywhere: the HUD picker is now a labeled list ("19  Highway traffic", "30  Company dispatch", ...) and the main window's channel buttons show the name under the number.

### Changed
- "Rebind PTT Key" is replaced by the Controls window. Your existing key bindings are carried over automatically.

## [v2.7.0] — 2026-11-12

### Added
- **CB channel picker on the HUD.** A slim bar under the overlay shows the current channel (e.g. "CB 19 · HIGHWAY TRAFFIC"). Click it to open the channel list (19 Highway, 9 Emergency, 17 Regional, 21 Casual, 25 Truck stop, 30 Dispatch) and click one to switch. It follows the HUD when you move or resize it, flips above the HUD near the bottom of the screen, and never takes keyboard focus from the game.
- **`[` steps back a channel** (`]` still steps forward). Either key briefly opens the picker on the HUD with the new channel highlighted, then it closes on its own after 4 seconds. Both keys can be rebound in `keybindings.json`.
- Layout & Theme has a checkbox to hide the picker if you don't want it.

## [v2.6.0] — 2026-11-12

Calmer, not louder: everything new here is there when you look and quiet when you don't.

### Added
- **Quiet 30 min** button: one click and nobody calls you unprompted — no chatter, no routine dispatch check-ins, no achievement or insight voice lines. Safety warnings, important dispatch notices and your own calls still work. Click again to end it early; it ends on its own after 30 minutes.
- **Radio as chat bubbles**: your transmissions on the right, truckers on the left, dispatch highlighted, each with handle, channel and time. The newest message stays in view.
- **Delivery banner**: when a job ends, a one-line summary (score, rating, miles, MPG, level-ups, achievements) appears at the top of the app and hides itself after 20 seconds. No popup, no extra voice.
- **HUD**: big speed that turns red over the limit, a US-style white SPEED LIMIT sign beside it, and a fuel bar that turns red under 15%.
- **Tidier header**: Quiet, HUD and Settings stay visible; Layout & Theme, Move/Resize HUD, Rebind PTT, plugin install and demo data moved into a **More** menu.

### Fixed
- **Radio replies failed while hauling a load**: building the trucker's context crashed ("Rounding digits must be between 0 and 15") whenever cargo weight was known, so nobody answered. Found by the new screenshot run; covered by a test now.
- Achievement cards could show the progress percentage instead of "UNLOCKED".

## [v2.5.0] — 2026-11-12

### Added
- Dial gauges for speed (with a speed-limit marker), RPM (with redline), fuel and trip score.
- CB radio face: large LCD channel display, TX / RX / BUSY lamps, one-click channel presets, and a starter hint when the channel is quiet.
- First-run welcome screen: set your CB handle, company name and co-driver style, with the key shortcuts.
- Career tab redesign: level and XP bar, stat tiles, achievement cards with progress bars.
- CB Contacts redesign: full trucker profiles (personality, topics, usual routes, relationship bar) and a "Call on channel 19" button.
- App-wide dark styling for buttons, tabs, lists, text boxes and scrollbars; dialogs follow the theme.
- `--screenshot <folder>` renders every tab to PNG; CI publishes them to the `ci-screenshots` branch for the README.

### Fixed
- Settings dialogs had dark text on a dark background for checkboxes.
- Water temperature showed "--" in demo mode.


## [v2.4.0] — 2026-11-09

### Added
- GitHub Releases: pushing a version tag builds, tests and smoke-tests the installer, then publishes it with SHA256SUMS.txt.
- Statistics tab with trend charts (score, MPG, coaching events per 100 mi, distance) and a score-trend dashboard widget.
- Exports: delivery report and trip history as PDF, shareable PNG report card (plus CSV, JSON, text).
- Layout & Theme: three themes, five accent colors, reorder/hide dashboard widgets and gauges, choose HUD rows and opacity.
- Learned location: approximate "in/near City, ST" from cities learned on your own deliveries (no guessing; ambiguous city names get no state).
- Grade warnings: steep downgrades you've driven are remembered and announced when you head toward them again.
- Realtime speech-to-speech option (OpenAI Realtime API) behind the same radio interfaces, with commands still recognized and automatic fallback.
- Offline speech recognition fallback using Windows' built-in recognizer.


## [v2.3.0] — 2026-11-09 — Spec completion

Reviewed with GPT, Gemini and DeepSeek; their confirmed findings are fixed and covered by tests (91 total).

### Removed
- The HTML web cockpit, the WinForms "Lite" window and the PowerShell/batch installers. TruckingAdvisor is now one native Windows (WPF) app installed by the setup .exe.

### Added
- Real Windows installer (`TruckingAdvisor-Setup-<version>.exe`, NSIS): per-user, no admin, Windows 10/11 x64 check, Start-menu/desktop shortcuts, Installed-apps entry, uninstaller that asks before deleting your data. The app inside is a single self-contained native .exe.
- Finds ATS/ETS2 on any drive (Steam registry + library list, Windows installed programs, common folders on every fixed drive), lets you pick the folder by hand, and offers to install the plugin on first run.
- Resizable main window that remembers its size/position, draggable dashboard/radio divider, wrapping header, and a HUD overlay you can unlock to move and resize.
- Achievements (8) and company milestones (4) with progress, computed only from recorded trips.
- Proactive dispatcher with a persistent name: load briefings, check-ins (Off/Rare/Normal/Frequent), approaching destination, low fuel vs. distance, rest, new damage, deadline risk, wrap-ups. One-time notices retry if the radio was busy.
- Onboard co-driver personalities; personal driving insights; MPG per truck; deadline-risk advice; real-time session length.
- CB radio: conversation modes, hourly cap on unprompted calls, truck-stop talk when parked, truckers mention each other, overheard exchanges become shared memories, knowledge boundary between dispatch and CB, option to store no memories.
- Voice commands with a wake word, and handle introductions with confirmation.
- Tabs for Career & Company, CB Contacts (with "forget"), and Trips (CSV/JSON export, copy report).
- Separate output devices and volumes for CB, dispatcher and co-driver; microphone selection; settings note on what is sent to OpenAI.


## [v2.2.0] — 2026-11-09 — Review fixes

Reviewed by Claude with independent second opinions from GPT, Gemini and DeepSeek. Every item below was a confirmed defect in v2.1.

### Fixed — would not build or run
- Solution did not compile: invalid `"Local\SCSTelemetry"` escape, broken string quoting in the CSV export and radio log, missing DPAPI package, test project referencing a Windows-only project.
- The lightweight window did not compile with the installer's C# 5 compiler (property initializers, a line break inside a string), so setup produced no app.
- Live telemetry could never connect (header struct couldn't be read from shared memory; errors were swallowed).

### Fixed — wrong or invented data
- Most shared-memory offsets were wrong (e.g. speed limit read from the RPM field, fuel capacity used as max RPM). All offsets now come from `offsetof()` on the plugin header, with tests, and unknown plugin revisions are refused.
- Air and oil pressure were multiplied by 14.5 although the SDK already reports psi.
- MPG of 6.3/6.4, "Arizona/Flagstaff", a Phoenix→Denver job, 84,521 miles, 147 deliveries, $2.45M, career level 22 and pre-existing trucker friendships were invented defaults. Everything now starts empty and only real data is shown, with `--` for unknowns.
- Demo data is opt-in, bannered in the UI and never saved.
- Trip stats were computed from planned-minus-remaining distance with fixed placeholders; a trip tracker now builds them from the odometer, fuel level and timestamps, including mid-delivery starts, trailer drops and cancellations.

### Fixed — AI CB radio
- The radio never called any AI: replies were keyword-matched canned lines and push-to-talk sent a hard-coded sentence. It now records the mic while PTT is held and runs OpenAI transcription → chat → voice, with offline lines when AI is off.
- Default provider was Gemini and cost was reported as $0. OpenAI is now the default; usage is counted per request/token with daily request and spend limits.
- API key had a plaintext fallback (and the web cockpit saved it in browser storage). Keys are DPAPI-only; the web page keeps them in memory only.
- AI-to-AI banter crashed with two truckers; it now uses one API call per exchange.
- Truckers' memories and relationships now persist (SQLite) and grow from conversation quality and continuity.

### Fixed — found in the second review round
- The plugin flips the delivered/cancelled flags on each event, so every second delivery would have been logged as cancelled. Trips now compare against the flag value at trip start, wait 10 s for a late flag, re-baseline after a game restart, and close the old trip if a different job appears.
- Compass heading was mirrored (the SDK measures it counter-clockwise).
- Radio: a finishing reply or background chatter could reset a newer transmission; the radio went "idle" while a voice was still playing.
- Audio: waiting callers could hang if the radio stayed busy, and duplicate lines reported "done" before playing.

### Fixed — audio, UI, installer
- Audio "priority" didn't wait for playback or stop sound; a real playback queue now prevents overlap and lets critical alerts interrupt.
- UI collections were updated from the telemetry thread (WPF crash) and advisories were rebuilt 20 times a second.
- Plugin installers downloaded "latest", wrote two different plugins to the same DLL, extracted zips without path checks, and reported "verified" when nothing was verified. They now pin V.1.12.1, show/verify the SHA-256, back up existing DLLs and report permission problems.
- `TruckingAdvisor_Setup.exe` was a renamed ZIP described as a signed-looking PE installer; it was removed along with the inaccurate moderation manifest. CI now builds real executables.
- CI workflow moved to the repository root (it never ran), restricted to read permissions, and now builds and tests on Windows.


## [v2.1.0] — 2026-11-08
### Added
- **Custom Stylized Desktop Icon (`Assets/TruckingAdvisor.ico`)**: Multi-resolution Windows icon (`16x16` to `256x256`) embedded into `TruckingAdvisor_Setup.exe`, compiled into `TruckingAdvisor.exe` (`/win32icon`), applied to Desktop & Start Menu shortcuts, and integrated into the WebAudio cockpit.
- **Interactive GUI Keybindings Manager**: Live key and mouse-button binding inside both the native Windows Desktop GUI and WebAudio GUI for 8 core actions (`Push-to-Talk`, `Toggle HUD Overlay`, `Repeat Last Advisory`, `Cycle CB Channel`, `Quick Ch 30 Dispatch Check-In`, `Voice Fuel Report`, `Acknowledge Alert`, `Master Audio Mute`), plus selectable **Hold-to-Talk** vs. **Press-to-Toggle TX** mode and automatic conflict resolution.
- **Automatic GitHub Telemetry Plugin Installer**: Automatically queries GitHub Releases ([RenCloud/scs-sdk-plugin](https://github.com/RenCloud/scs-sdk-plugin/releases) and [truckermudgeon/scs-sdk-plugin](https://github.com/truckermudgeon/scs-sdk-plugin)) during setup and installs `scs-telemetry.dll` into detected Steam `bin\win_x64\plugins` folders.
- **OverTake.gg & GitHub Security & Source Compliance**: Added top-level `TruckingAdvisor_SourceCode.cs`, plain-text `Build_And_Install_From_Source.bat`, `LICENSE`, `SECURITY.md`, `THIRD_PARTY_NOTICES.md`, `SHA256SUMS.txt`, GitHub Actions SLSA build provenance workflow, and full PE `VS_VERSION_INFO` + `RT_MANIFEST` (`asInvoker`) + valid PE `CheckSum`.
