# Building & Contributing

## Full app (.NET 8)

```bash
cd TruckingAdvisor
dotnet build TruckingAdvisor.sln
dotnet test TruckingAdvisor.sln
dotnet run --project src/TruckingAdvisor.UI            # add -- --demo for labeled demo data
```

On Linux/macOS, everything except running the WPF window works; build with `-p:EnableWindowsTargeting=true`.

## Lightweight window (no SDK needed)

`Launcher/TruckingAdvisorDesktopHost.cs` is a single-file WinForms simulator window that compiles with Windows' built-in C# 5 compiler. It does **not** read live telemetry or use AI. Keep it C# 5 compatible (no property initializers, `$"..."`, `?.`, `nameof`, or expression-bodied members):

```cmd
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /win32icon:Assets\TruckingAdvisor.ico /out:TruckingAdvisor-Lite.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll Launcher\TruckingAdvisorDesktopHost.cs
```

## Ground rules

- Never show or send a telemetry value the game didn't report. Unknown is `0`/empty plus a `Has*` check, and the UI shows `--`.
- Coaching and safety alerts stay deterministic. AI may only add conversation, never decide warnings.
- Nothing AI-related may run on the telemetry thread or block a safety alert.
- New shared-memory offsets must come from `offsetof()` on the plugin header, with a test.
