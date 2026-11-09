# Changelog — TruckingAdvisor

## [v2.3.0] — 2026-11-09 — Spec completion

Reviewed with GPT, Gemini and DeepSeek; their confirmed findings are fixed and covered by tests (91 total).

### Added
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
