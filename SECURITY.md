# Security & Privacy

TruckingAdvisor is open source. Everything it does on your PC and on the network is listed here.

## What runs where

| Area | What it does | Boundaries |
| :--- | :--- | :--- |
| Privileges | Installed per user into `%LOCALAPPDATA%\Programs\TruckingAdvisor` and runs as a normal user. | Asks for admin only in two cases, both after you click a button: writing the telemetry plugin into a game folder under `Program Files` (the app reports when it can't), and installing the SimHub plugin into a SimHub folder under `Program Files`. For SimHub, TruckingAdvisor itself runs elevated (one UAC prompt), writes the plugin from the copy built into the app, writes only into a folder that holds `SimHubWPF.exe`, and exits. |
| Game telemetry | Reads the `Local\SCSTelemetry` shared memory created by [RenCloud/scs-sdk-plugin](https://github.com/RenCloud/scs-sdk-plugin). | Read-only. Never touches game files or game memory. Refuses unknown plugin layouts. Checks whether `amtrucks`/`eurotrucks2` is still running (process name only) when the game looks frozen while paused. |
| Hotkeys | Low-level keyboard hook (`WH_KEYBOARD_LL`) on its own background thread, so push-to-talk works while the game has focus. | Only bound keys are acted on. Keystrokes are never stored or sent anywhere. |
| Microphone | Records only while push-to-talk is held, and only after you turn the mic on in AI & Radio Settings. | Transcribed on your PC (Whisper) or, if you choose OpenAI speech recognition with AI on, sent to OpenAI. Nothing is saved to disk. |
| HUD overlay | Topmost window with `WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE`. | Click-through; never steals focus. |
| Game search | Reads Steam's registry entries and `libraryfolders.vdf`, Windows' installed-programs list, and checks common folders on each fixed drive for `bin\win_x64\amtrucks.exe`. | Read-only. Nothing is created until you choose to install the plugin. |
| Plugin install | Downloads `RenCloud/scs-sdk-plugin` release **V.1.12.1** (pinned) from GitHub, only when you click Install. | The file's SHA-256 is pinned in the app (`GitHubPluginInstallerService.cs`) and a mismatch is refused before anything is written. CI checks the pin against the official release asset and against `Plugins/plugin_manifest.json` on every build. Backs up any existing plugin DLL. Extracts only the DLL to a fixed path. |
| Updates | Asks `api.github.com` for the latest release about every 12 hours (turn off in Settings > Updates). Downloads the new installer from this repository's GitHub release. | Only HTTPS addresses on this repository's releases are accepted. The installer is checked against the release's `SHA256SUMS.txt` when downloaded, and checked again just before it runs while the app holds the file open; a changed file is deleted, never run. Installs as your user (no admin). The update installer gives up, without installing or relaunching, if the app hasn't closed within 60 seconds. |
| On-device voices and speech | Downloads Whisper, Supertonic or Kokoro model files from `huggingface.co`, only when you choose them. | HTTPS only; every file is checked against a SHA-256 pinned in the app and deleted on a mismatch. Interrupted downloads resume; a stalled one fails after 60 seconds without data. |
| Free Microsoft voices | The default voice engine sends each line to be spoken (trucker replies, dispatch, co-driver, and Twitch viewer lines read on air) to Microsoft's Edge read-aloud service (`speech.platform.bing.com`), even with AI off. | Text only, no account. Choose on-device or Windows voices in Settings to keep speech entirely on your PC. |
| OpenAI | HTTPS calls to `api.openai.com/v1` (chat, transcription, speech, realtime voice) only when you enable AI and save your own key. | Daily request and spend limits, checked before every request (fallbacks, voices, transcription and realtime included). Spending uses cautious built-in price estimates per model unless you enter your own prices. Usage shown in settings. |
| DeepSeek | Optional HTTPS calls to `api.deepseek.com` (trucker conversations) only when you save a DeepSeek key. | Counts against the same daily limits. |
| Twitch chat | When you turn it on, reads your channel's chat anonymously over Twitch's TLS chat connection (`irc.chat.twitch.tv`). | Read-only: no login or token, nothing is posted. Viewer names and messages are not written to the log or to Copy Diagnostics. |
| Stream overlay & SimHub panel | A small web server on `localhost:8789` for OBS / Streamlabs and SimHub. | Listens on this PC only unless you tick Allow LAN. Read-only. Answers only requests addressed to localhost (or, with LAN on, to an IP address or this PC's name) and refuses requests from other websites, so a web page you visit can't read your truck, location or radio lines. |
| Convoy CB | Relays encrypted radio lines between friends through `ntfy.sh` (or your own ntfy server). | Encrypted (AES-GCM) with a key derived from the convoy code; the relay sees only ciphertext. Anyone with the code can read and send. |

## Your API key

- The desktop app encrypts it with Windows DPAPI (current user) in `%LOCALAPPDATA%\TruckingAdvisor\openai_key.dpapi`. There is no plaintext fallback.
- A DeepSeek key, if you add one, is stored the same way in `deepseek_key.dpapi`.
- Each key is only ever sent in the `Authorization` header to the endpoint it was saved for, over HTTPS (plain HTTP only to a server on this PC), and never logged or shown again. Key files are written atomically, so a crash can't truncate them.

## Local data

`%LOCALAPPDATA%\TruckingAdvisor\` holds `logs\` (with your Windows user name, profile paths and keys scrubbed), `truckingadvisor.db` (trips, trucker memories, company and service log), `ai-settings.json`, `ai-usage.json`, and `keybindings.json`. Delete the folder to reset everything.

## Releases

GitHub Actions (`.github/workflows/build.yml`) builds, tests and smoke-tests `TruckingAdvisor-Setup-<version>.exe` with a `SHA256SUMS.txt`, and adds build-provenance attestation to every release (check with `gh attestation verify <file> --repo Jcedwar61/TruckingAdvisor`). Releases are made only from `main`: a pushed `v<version>` tag on a commit that is on `main`, or a manual run on `main`; the version must match the app's own. The installer and app are not code-signed, so Windows SmartScreen may warn on first run.

## Reporting a problem

Open an issue at https://github.com/Jcedwar61/TruckingAdvisor/issues. Please don't post API keys or other secrets in issues.
