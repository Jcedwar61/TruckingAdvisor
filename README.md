# TruckingAdvisor

A Windows companion app for **American Truck Simulator** (and ETS2), grown out of [SimCoach](https://github.com/Jcedwar61/SimCoach). It reads live game telemetry, coaches your driving, scores each delivery, tracks your career, and adds an AI-powered CB radio with recurring trucker characters.

Everything except the AI radio works offline with no account. If AI is off or unavailable, the radio falls back to short offline lines and nothing else is affected.

## Quick start

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download) (for the full app).
2. Run `Build_And_Install_From_Source.bat`. It builds the app, offers to install the telemetry plugin, and creates shortcuts.
   - Without the .NET SDK it builds `TruckingAdvisor-Lite.exe`, a simulator-only window (no live telemetry, no AI).
3. Start ATS, then TruckingAdvisor. The header shows **CONNECTED TO ATS** once data flows.
4. Optional: **AI & Radio Settings** → enable AI, paste your OpenAI key, turn on the microphone, set your CB handle.

No game handy? Click **Try Demo Data**. Demo values are clearly bannered and never saved.

## Features

| Area | What you get |
| :--- | :--- |
| Dashboard | Speed and posted limit (turns red when over), RPM, gear, cruise, fuel %, range, average MPG, air pressure, water temp, delivery progress, game time, rest timer. Unknown values show `--`. |
| Driving coach | Speeding tiers (1–5 / 6–10 / 11+ over), harsh braking, aggressive throttle, over-revving (based on your engine's max RPM), parking brake while rolling, low air while moving, overheating, critical fuel, serious damage. Deterministic rules with cooldowns. |
| Audio priority | One voice at a time. Critical alerts cut off radio chatter; normal messages wait while the radio is busy. |
| Deliveries | Detects job start, cargo attached, underway, trailer dropped, delivered, cancelled, and starting mid-delivery. Distance from the odometer, fuel from the tank, damage as the increase during the trip. |
| Scoring | 10 categories, normalized per 100 miles; ratings from Needs Improvement to Legendary Trucker. Delivery report as text, JSON or CSV. |
| Career & company | Your company's miles, deliveries, damage-free count, in-game income, MPG and cities, plus XP levels. Starts at zero and only counts trips this app recorded. |
| Fuel & maintenance | Low-fuel and range-to-destination advice, MPG vs. last trip, engine/trailer damage, service due (from your service log), rest reminders. |
| CB radio | Channels 19, 9, 17, 21, 25 and 30 (dispatch). Hold push-to-talk (Left Alt by default) or type. Five recurring truckers plus generated ones; they remember past conversations and relationships grow from Stranger to Old Friend based on conversation quality and coming back over days. Background chatter (Off/Quiet/Normal/Busy/Very Busy), AI-to-AI exchanges in a single API call, CB radio audio filter with a clean-audio option. |
| HUD overlay | Small click-through, always-on-top panel (F8). Works over the game in borderless/windowed mode. |

### About the radio

The truckers are simulated radio characters. ATS doesn't expose nearby traffic, so they never claim to see you, traffic, police or weather. They only mention your load, fuel or speed when the game reported it.

## AI setup and cost

Pipeline: speech-to-text → chat → text-to-speech, all through OpenAI. Model names are settings, so you can swap them without code changes:

| Setting | Default |
| :--- | :--- |
| Chat | `gpt-5.4-mini` |
| Transcription | `gpt-4o-mini-transcribe` |
| Voice | `gpt-4o-mini-tts` (or free Windows voices) |

Check OpenAI's model list for retirement dates and update these when needed. The app counts every request and token, enforces a daily request limit (default 300) and, once you enter prices from openai.com/api/pricing, a daily dollar limit. Your OpenAI dashboard is the source of truth for billing.

Your key is encrypted with Windows DPAPI. See [SECURITY.md](SECURITY.md).

## Telemetry sources

Requires [RenCloud/scs-sdk-plugin](https://github.com/RenCloud/scs-sdk-plugin) **V.1.12.x** (layout revision 12) in `<game>\bin\win_x64\plugins`. Other revisions are refused with a clear message rather than misread.

| Kind | Fields |
| :--- | :--- |
| Direct (from the game) | Speed, speed limit, RPM, max RPM, gear, throttle/brake/clutch, engine/electric state, parking brake, engine brake, retarder, cruise state and speed, fuel and capacity, average consumption, game range, air/oil pressure (psi), water temp, warning lights, engine/transmission/cabin/chassis/wheel wear, trailer attached and wear, cargo damage, cargo name and mass, origin/destination city and company, planned distance, navigation distance and time, job income, deadline, delivered/cancelled events, world position and heading, odometer, game time, time scale, minutes until rest |
| Calculated by TruckingAdvisor | MPG from game consumption, instant MPG (fuel vs. odometer), range fallback, road grade (from position, approximate), trip distance/fuel/time/averages, damage added during a trip, scores, career stats |
| Not available | Current city and state (the SDK doesn't report them), nearby traffic, weather, road grades ahead. A map integration could add these later. |

## Project layout

```
TruckingAdvisor.sln
src/
  TruckingAdvisor.Shared        models, enums, interfaces
  TruckingAdvisor.Telemetry     shared-memory layout, parser, reader, plugin installer
  TruckingAdvisor.Core          driving coach, scoring, trip tracker, career, fuel/maintenance advisor, audio priority
  TruckingAdvisor.AI            OpenAI client, CB radio, personalities, memory/relationships, context builder, usage limits
  TruckingAdvisor.Audio         Windows playback with CB filter, microphone, push-to-talk hook
  TruckingAdvisor.Data          SQLite storage and report exports
  TruckingAdvisor.Presentation  dashboard view-model (no WPF dependency, so it's testable)
  TruckingAdvisor.UI            WPF window, settings dialog, HUD overlay
tests/TruckingAdvisor.Core.Tests
Launcher/TruckingAdvisorDesktopHost.cs   lightweight simulator window (C# 5, no SDK needed)
App/TruckingAdvisor_App.html             browser cockpit / simulator (cannot read the game)
Plugins/                                  plugin manifest and download script
```

## Not done yet

- Achievements list, trend charts and PDF/image export.
- Widget rearranging and theme picker.
- Grade warnings ahead and map-based location (need a map integration).
- Dispatcher check-ins on a schedule (dispatch currently answers when you call on channel 30 or press F10).
- Real-time speech-to-speech (the current pipeline is modular; a realtime model could be added behind the same interfaces).
