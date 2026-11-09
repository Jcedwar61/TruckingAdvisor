# Third-Party Notices

- **SimCoach** — https://github.com/Jcedwar61/SimCoach (MIT). The project TruckingAdvisor grew out of.
- **scs-sdk-plugin (RenCloud)** — https://github.com/RenCloud/scs-sdk-plugin (MIT). Provides `scs-telemetry.dll` and the `Local\SCSTelemetry` shared memory. Not bundled; downloaded on request. TruckingAdvisor's offsets come from its `scs-telemetry-common.hpp` (layout revision 12).
- **NAudio** — https://github.com/naudio/NAudio (MIT). Audio playback and microphone capture.
- **Microsoft.Data.Sqlite / SQLitePCLRaw** — https://github.com/dotnet/efcore (MIT), https://github.com/ericsink/SQLitePCL.raw (Apache-2.0). Local database.
- **System.Speech**, **System.Security.Cryptography.ProtectedData** — Microsoft (MIT). Windows voices and DPAPI.
- **xUnit** — https://github.com/xunit/xunit (Apache-2.0). Tests only.

Optional local text-to-speech servers listed in `FreeNeuralTtsService.cs` (Kokoro-82M, Piper, Edge-TTS, Chatterbox) are not bundled or used by default.
