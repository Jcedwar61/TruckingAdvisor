# Building & Contributing

## Full app (.NET 8)

```bash
dotnet build TruckingAdvisor.sln
dotnet test TruckingAdvisor.sln
dotnet run --project src/TruckingAdvisor.UI            # add -- --demo for labeled demo data
```

On Linux/macOS, everything except running the WPF window works; build with `-p:EnableWindowsTargeting=true`.

## Windows installer

On Windows with the .NET 8 SDK and NSIS 3 (`winget install NSIS.NSIS`):

```powershell
./installer/build-installer.ps1 -Version 2.3.0
```

This writes `publish/bin/TruckingAdvisor.exe` (self-contained, single file) and `publish/TruckingAdvisor-Setup-2.3.0.exe`. CI builds the same installer and smoke-tests it (silent install, launch, uninstall) on every push.

## Ground rules

- Never show or send a telemetry value the game didn't report. Unknown is `0`/empty plus a `Has*` check, and the UI shows `--`.
- Coaching and safety alerts stay deterministic. AI may only add conversation, never decide warnings.
- Nothing AI-related may run on the telemetry thread or block a safety alert.
- New shared-memory offsets must come from `offsetof()` on the plugin header, with a test.
