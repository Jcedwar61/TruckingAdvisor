# TruckingAdvisor

**Your co-driver for American Truck Simulator: live telemetry dashboard, click-through HUD, 10-category delivery scoring, and an optional AI CB radio that remembers your name.**

[](https://github.com/Jcedwar61/TruckingAdvisor/releases/latest)
[](https://www.overtake.gg/downloads/truckingadvisor.88720/)
[](https://github.com/Jcedwar61/TruckingAdvisor)

**TruckingAdvisor** ([GitHub Repository](https://github.com/Jcedwar61/TruckingAdvisor) | [OverTake.gg Download](https://www.overtake.gg/downloads/truckingadvisor.88720/)) is a 100% free, native Windows companion app for **American Truck Simulator (ATS)**. Every metric is read directly from live game telemetry—nothing is invented. All core features (dashboard, click-through HUD, co-driver coaching, delivery reports, and career progression) run completely offline with no account required.

## Feature Breakdown

| Module | What It Does |
| :--- | :--- |
| **Live Telemetry Dashboard** | Displays gauges for speed (with speed-limit marker), RPM with redline, fuel, live trip score, range, MPG economy, air pressure, water temp, gear, cruise control, and rest timer across rearrangeable widgets, 3 themes, and 5 accent colors. |
| **Click-Through In-Game HUD** | Always-on-top overlay for borderless or windowed play that never steals mouse or wheel input. Shows a speed readout (turns red when speeding), US-style speed-limit sign, and fuel bar with customizable rows on any monitor. |
| **Live Co-Driver & 10-Category Scoring** | Flags harsh braking, hard acceleration, speeding, and over-revving in real time using 5 selectable co-driver personalities (from calm mentor to **Minimal**, which speaks only for critical warnings). Grades every load across 10 categories: speed, braking, acceleration, fuel, engine, truck damage, trailer damage, completion, punctuality, and consistency. |
| **Exportable Delivery Reports & Route Learning** | Exports detailed delivery scorecards as **PDF, PNG image, CSV, or JSON** with full trip history and trend charts. Learns steep grades and city locations from the roads you actually drive to give accurate fuel, maintenance, and rest advice. |
| **Virtual Company & Career Progression** | Lets you run your own virtual trucking company, earn XP, level up with every completed haul, and track driver achievements and company milestones with progress bars. |
| **Optional AI CB Radio & Dispatcher** | Simulates Channels `19`, `9`, `17`, `21`, `25`, and `30` (Company Dispatcher for load briefings and check-ins) with regional truckers who remember past conversations and only reference real game telemetry. Supports Push-to-Talk (key/button), typed chat, hands-free commands (`"TruckingAdvisor, switch to channel 30"`), radio-filtered audio, realtime speech-to-speech, and a one-click **Quiet 30 min** button. |

## Privacy, Offline Design & Cost Control

* **100% Offline Core**: The telemetry dashboard, HUD, co-driver voice, delivery scoring, career tracker, and built-in **Demo Mode** (test everything without ATS running) work completely offline.
* **Encrypted Bring-Your-Own-Key AI**: AI CB radio is off by default. When enabled, your OpenAI API key is encrypted locally via Windows Data Protection API (**DPAPI**) and never stored in plain text, paired with built-in spending caps and a live usage tracker.
* **Automatic Telemetry Plugin Setup**: Automatically locates your American Truck Simulator installation across any drive or custom Steam library and installs the free `scs-sdk-plugin` by RenCloud while backing up your existing files.

## Installation & Requirements

1. **System Requirements**: 64-bit Windows 10 or 11 and American Truck Simulator (no `.NET` runtime installation or administrator privileges required).
2. **Download & Run**: Download `TruckingAdvisor-Setup-2.6.0.exe` from [TruckingAdvisor Releases on GitHub](https://github.com/Jcedwar61/TruckingAdvisor/releases/latest) or [TruckingAdvisor on OverTake.gg](https://www.overtake.gg/downloads/truckingadvisor.88720/) and run the installer (if Windows SmartScreen prompts on an unsigned build, click **More info -> Run anyway**; you can verify the binary against `SHA256SUMS.txt` on the release page).
3. **Auto-Install Telemetry**: Launch TruckingAdvisor and let it auto-install `scs-sdk-plugin` into your ATS folder, or try **Demo Mode** immediately.

## Repository & OverTake.gg Short Descriptions (Copy-Paste Ready)

* **GitHub Repository Description (under 160 chars, replace current generic "A Windows companion app..." tagline)**:
  `Free Windows companion app for American Truck Simulator: click-through HUD, live driving coach, 10-category delivery reports (PDF/CSV) & optional AI CB radio.`
* **GitHub Topics (add in Repo Settings -> Gear Icon)**:
  `american-truck-simulator` `ats` `truck-simulator` `telemetry` `hud` `overlay` `dashboard` `cb-radio` `openai` `sim-trucking` `windows-app`
* **OverTake.gg Resource Tagline (under 100 chars)**:
  `Your ATS co-driver: click-through HUD, live coaching, 10-part delivery scores & AI CB radio.`

## Disclaimer

TruckingAdvisor is an independent fan-made companion tool and is not affiliated with or endorsed by SCS Software.
