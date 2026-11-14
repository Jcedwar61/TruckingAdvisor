using System.Runtime.Versioning;
using TruckingAdvisor.Core.Speech;
using TruckingAdvisor.Shared.Diagnostics;
using TruckingAdvisor.Shared.Interfaces;
using Whisper.net;

namespace TruckingAdvisor.Audio;

/// <summary>
/// Offline speech recognition with Whisper (whisper.cpp through Whisper.net), the same engine SimCoach uses.
/// Free, private (nothing leaves the PC), and far better than Windows dictation at casual speech and radio slang.
/// The model (English, ~60 or ~180 MB) is downloaded once on first use.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LocalWhisperSpeechToText : ISpeechToText, IDisposable
{
    private readonly ModelDownloader _downloader;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private WhisperFactory? _factory;
    private string? _loadedModel;
    private Task? _download;

    public LocalWhisperSpeechToText(string modelFolder) => _downloader = new ModelDownloader(modelFolder);

    public bool Enabled { get; set; } = true;
    /// <summary>Accurate (small.en) or Fast (base.en).</summary>
    public bool Accurate { get; set; } = true;
    /// <summary>Names and slang to bias recognition toward (handles, cities, CB terms).</summary>
    public Func<string>? PromptProvider { get; set; }

    public WhisperModelInfo Model => WhisperModelInfo.For(Accurate);
    public bool ModelReady => _downloader.IsReady(Model);
    public bool IsAvailable => Enabled && ModelReady;

    /// <summary>Download progress 0..1 (raised on a background thread).</summary>
    public event Action<double>? DownloadProgress;
    /// <summary>"Speech model downloaded", or what went wrong.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Starts the one-time model download if it isn't on disk yet (safe to call repeatedly).</summary>
    public Task EnsureModelAsync()
    {
        if (ModelReady) return Task.CompletedTask;
        if (_download is { IsCompleted: false }) return _download;
        var model = Model;
        _download = Task.Run(async () =>
        {
            try
            {
                AppLog.Info($"Downloading speech model {model.FileName}");
                StatusChanged?.Invoke($"Downloading the offline speech model ({model.Label})...");
                await _downloader.EnsureAsync(model, new Progress<double>(p => DownloadProgress?.Invoke(p)));
                AppLog.Info($"Speech model ready: {model.FileName} sha256 {await ModelDownloader.HashAsync(_downloader.PathFor(model))}");
                StatusChanged?.Invoke("Offline speech recognition is ready.");
            }
            catch (Exception ex)
            {
                AppLog.Error("Speech model download failed", ex);
                StatusChanged?.Invoke($"Couldn't download the speech model ({ex.Message}). It will try again next time.");
            }
        });
        return _download;
    }

    public async Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken cancellationToken)
    {
        if (!IsAvailable || wavAudio.Length < 64) return null;
        var (raw, rate) = SpeechAudio.ReadWav(wavAudio);
        var (samples, info) = SpeechAudio.Prepare(raw, rate);
        if (info.LooksSilent) return null;
        return await TranscribeSamplesAsync(samples, cancellationToken);
    }

    /// <summary>Recognizes 16 kHz mono samples that are already cleaned up.</summary>
    public async Task<string?> TranscribeSamplesAsync(float[] samples16k, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var factory = LoadFactory();
            string prompt = PromptProvider?.Invoke() ?? SpeechText.BuildPrompt(Array.Empty<string>());
            await using var processor = factory.CreateBuilder()
                .WithLanguage("en")
                .WithPrompt(prompt)
                .WithNoContext()
                .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
                .WithNoSpeechThreshold(0.6f)
                .Build();
            var text = new System.Text.StringBuilder();
            await foreach (var seg in processor.ProcessAsync(samples16k, ct))
                text.Append(seg.Text);
            return SpeechText.Clean(text.ToString(), prompt);
        }
        finally { _lock.Release(); }
    }

    private WhisperFactory LoadFactory()
    {
        string path = _downloader.PathFor(Model);
        if (_factory is not null && _loadedModel == path) return _factory;
        _factory?.Dispose();
        // The native engine ships as loose files next to the app (runtimes\win-x64), not inside the single exe.
        string native = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "whisper.dll");
        if (File.Exists(native)) Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath = native;
        _factory = WhisperFactory.FromPath(path);
        _loadedModel = path;
        AppLog.Info($"Whisper loaded: {Model.FileName} ({WhisperFactory.GetRuntimeInfo()?.Trim()})");
        return _factory;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
    }
}
