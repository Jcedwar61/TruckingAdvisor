using System.Runtime.Versioning;
using SherpaOnnx;
using TruckingAdvisor.Core.Speech;
using TruckingAdvisor.Shared.Diagnostics;

namespace TruckingAdvisor.Audio.Local;

/// <summary>
/// Speaks text on this PC with an on-device model: Supertonic 3 by default, or Kokoro (better voices,
/// slower) when the player picks it. No internet and no key once the model is downloaded, and the
/// <see cref="IsReady"/> check tells callers to fall back to a free voice until then.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LocalVoiceSynthesizer : IDisposable
{
    private readonly string _folder;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ModelDownloader _downloader;
    private OfflineTts? _tts;
    private Task? _download;
    private LocalVoiceModelKind _loadedKind;

    public LocalVoiceSynthesizer(string modelFolder)
    {
        _folder = modelFolder;
        _downloader = new ModelDownloader(modelFolder);
    }

    public bool Enabled { get; set; } = true;

    /// <summary>Which on-device model to speak with. Changing it reloads on the next line.</summary>
    public LocalVoiceModelKind Kind
    {
        get => _kind;
        set { if (_kind != value) { _kind = value; Reset(); } }
    }
    private LocalVoiceModelKind _kind = LocalVoiceModelKind.Supertonic;

    private LocalVoiceModelFile[] Files => _kind == LocalVoiceModelKind.Kokoro ? KokoroVoiceModel.Files : LocalVoiceModel.Files;
    private string Label => _kind == LocalVoiceModelKind.Kokoro ? KokoroVoiceModel.Label : LocalVoiceModel.Label;

    public bool IsReady => Enabled && Files.All(f => File.Exists(Path.Combine(_folder, f.FileName)));

    /// <summary>Download progress 0..1.</summary>
    public event Action<double>? DownloadProgress;
    public event Action<string>? StatusChanged;

    public Task EnsureModelAsync()
    {
        if (IsReady) return Task.CompletedTask;
        if (_download is { IsCompleted: false }) return _download;
        _download = Task.Run(async () =>
        {
            try
            {
                var files = Files;
                AppLog.Info($"Downloading the on-device voice model ({_kind})");
                StatusChanged?.Invoke("Downloading the on-device voice (one time only)...");
                var done = 0L;
                long total = files.Sum(f => f.Bytes);
                foreach (var file in files)
                {
                    await _downloader.EnsureFileAsync(file.Url, file.FileName, file.Sha256, file.Bytes,
                        new Progress<double>(p => DownloadProgress?.Invoke((done + p * file.Bytes) / total)));
                    done += file.Bytes;
                }
                StatusChanged?.Invoke("On-device voices are ready.");
                AppLog.Info("On-device voice model ready");
            }
            catch (Exception ex)
            {
                AppLog.Error("On-device voice download failed", ex);
                StatusChanged?.Invoke($"Couldn't download the on-device voice ({ex.Message}). It will try again next time.");
            }
        });
        return _download;
    }

    /// <summary>Loads the model in the background so the first line isn't the slow one.</summary>
    public Task WarmUpAsync() => Task.Run(async () =>
    {
        if (!IsReady) return;
        await _lock.WaitAsync();
        try { Load(); }
        catch (Exception ex) { AppLog.Error("Loading the on-device voice failed", ex); }
        finally { _lock.Release(); }
    });

    private void Reset()
    {
        // Only touched from the UI thread; a line in flight keeps its own reference.
        var old = _tts;
        _tts = null;
        _ = Task.Run(() => old?.Dispose());
    }

    private OfflineTts Load()
    {
        if (_tts is not null && _loadedKind == _kind) return _tts;
        var cfg = new OfflineTtsConfig();
        if (_kind == LocalVoiceModelKind.Kokoro)
        {
            // Model order matters here: it decides which speaker index is which voice.
            cfg.Model.Kokoro = new OfflineTtsKokoroModelConfig
            {
                Model = Path.Combine(_folder, "model.int8.onnx"),
                Voices = Path.Combine(_folder, "voices.bin"),
                Tokens = Path.Combine(_folder, "tokens.txt"),
                Lexicon = Path.Combine(_folder, "lexicon-us-en.txt")
            };
            // Kokoro runs several times slower than Supertonic, so it gets the whole CPU.
            cfg.Model.NumThreads = Math.Clamp(Environment.ProcessorCount - 2, 2, 12);
        }
        else
        {
            cfg.Model.Supertonic = new OfflineTtsSupertonicModelConfig
            {
                DurationPredictor = Path.Combine(_folder, "duration_predictor.int8.onnx"),
                TextEncoder = Path.Combine(_folder, "text_encoder.int8.onnx"),
                VectorEstimator = Path.Combine(_folder, "vector_estimator.int8.onnx"),
                Vocoder = Path.Combine(_folder, "vocoder.int8.onnx"),
                TtsJson = Path.Combine(_folder, "tts.json"),
                UnicodeIndexer = Path.Combine(_folder, "unicode_indexer.bin"),
                VoiceStyle = Path.Combine(_folder, "voice.bin")
            };
            cfg.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        }
        cfg.Model.Provider = "cpu";
        _tts = new OfflineTts(cfg);
        _loadedKind = _kind;
        return _tts;
    }

    /// <summary>
    /// Speaks <paramref name="text"/> as mono floats. <paramref name="speaker"/> indexes the model's
    /// voices; <paramref name="speed"/> is 1 for the model's natural pace.
    /// </summary>
    public async Task<(float[] Samples, int SampleRate)> SynthesizeAsync(string text, int speaker, float speed, CancellationToken ct)
    {
        if (!IsReady) throw new InvalidOperationException("The on-device voice model isn't downloaded yet.");
        await _lock.WaitAsync(ct);
        try
        {
            var tts = Load();
            int sid = Math.Clamp(speaker, 0, Math.Max(0, tts.NumSpeakers - 1));
            var audio = await Task.Run(() => tts.Generate(text, Math.Clamp(speed, 0.5f, 1.5f), sid), ct);
            var samples = audio.Samples;
            int rate = audio.SampleRate;
            audio.Dispose();
            ct.ThrowIfCancellationRequested();
            return (samples, rate);
        }
        finally { _lock.Release(); }
    }

    public void Dispose()
    {
        _tts?.Dispose();
        _lock.Dispose();
        _downloader.Dispose();
    }
}