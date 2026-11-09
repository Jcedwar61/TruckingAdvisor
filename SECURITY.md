# Security & Privacy

TruckingAdvisor is open source. Everything it does on your PC and on the network is listed here.

## What runs where

| Area | What it does | Boundaries |
| :--- | :--- | :--- |
| Privileges | Runs as a normal user from `%LOCALAPPDATA%\Programs\TruckingAdvisor`. | Never asks for admin. Writing the plugin into a Steam folder under `Program Files` may need admin; the app reports that instead of failing silently. |
| Game telemetry | Reads the `Local\SCSTelemetry` shared memory created by [RenCloud/scs-sdk-plugin](https://github.com/RenCloud/scs-sdk-plugin). | Read-only. Never touches game files or game memory. Refuses unknown plugin layouts. |
| Hotkeys | Low-level keyboard hook (`WH_KEYBOARD_LL`) so push-to-talk works while the game has focus. | Only bound keys are acted on. Keystrokes are never stored or sent anywhere. |
| Microphone | Records only while push-to-talk is held, and only after you turn the mic on in AI & Radio Settings. | Audio is sent to OpenAI for transcription only when AI is enabled. Nothing is saved to disk. |
| HUD overlay | Topmost window with `WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE`. | Click-through; never steals focus. |
| Plugin install | Downloads `RenCloud/scs-sdk-plugin` release **V.1.12.1** (pinned) from GitHub, only when you click Install. | Shows the file's SHA-256 and asks you to confirm (or verifies it against `Plugins/plugin_manifest.json` when a hash is set). Backs up any existing plugin DLL. Extracts only the DLL to a fixed path. |
| OpenAI | HTTPS calls to `api.openai.com/v1` (chat, transcription, speech) only when you enable AI and save your own key. | Daily request and spend limits; usage shown in settings. |

## Your API key

- The desktop app encrypts it with Windows DPAPI (current user) in `%LOCALAPPDATA%\TruckingAdvisor\openai_key.dpapi`. There is no plaintext fallback.
- It is only ever sent in the `Authorization` header to the API endpoint, and never logged or shown again.
- The web cockpit (`App/TruckingAdvisor_App.html`) keeps a key in memory for the current page session only.

## Local data

`%LOCALAPPDATA%\TruckingAdvisor\` holds `truckingadvisor.db` (trips, trucker memories, company and service log), `ai-settings.json`, `ai-usage.json`, and `keybindings.json`. Delete the folder to reset everything.

## Releases

GitHub Actions (`.github/workflows/build.yml`) builds, tests and publishes `TruckingAdvisor.exe` and `TruckingAdvisor-Lite.exe` with a `SHA256SUMS.txt`, and adds build-provenance attestation on `v*` tags. The executables are not code-signed, so Windows SmartScreen may warn on first run.

## Reporting a problem

Open an issue at https://github.com/Jcedwar61/TruckingAdvisor/issues. Please don't post API keys or other secrets in issues.
