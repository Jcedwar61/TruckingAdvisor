namespace TruckingAdvisor.Audio.Local;

/// <summary>One file of an on-device voice model, pinned by checksum and by repository commit.</summary>
public sealed record LocalVoiceModelFile(string FileName, long Bytes, string Sha256)
{
    public string Url => $"https://huggingface.co/{Repository}/resolve/{Revision}/{FileName}";

    public string Repository { get; init; } = LocalVoiceModel.Repository;

    /// <summary>The Hugging Face commit the file is fetched from (never a moving branch like main).</summary>
    public string Revision { get; init; } = LocalVoiceModel.Revision;
}

/// <summary>Which on-device model is in use.</summary>
public enum LocalVoiceModelKind { Supertonic, Kokoro }

/// <summary>
/// The offline voice model, file by file: Supertonic 3 (Supertone Inc., MIT) through ONNX Runtime.
/// On-device, no key, no network once downloaded, about 145 MB for all ten voices (they share one model).
/// </summary>
public sealed class LocalVoiceModel
{
    public const string Repository = "csukuangfj2/sherpa-onnx-supertonic-3-tts-int8-2026-05-11";
    /// <summary>The commit of <see cref="Repository"/> the checksums below were verified at.</summary>
    public const string Revision = "cca5a0e6c96e1d2c720986bf7e75fcc81dee3ae4";
    public const string Label = "On-device voice (Supertonic, ~145 MB, one-time download)";

    /// <summary>Model files, pinned by checksum. A download that doesn't match is deleted.</summary>
    public static readonly LocalVoiceModelFile[] Files =
    {
        new("tts.json", 8_253, "42078d3aef1cd43ab43021f3c54f47d2d75ceb4e75f627f118890128b06a0d09") { Repository = Repository, Revision = Revision },
        new("unicode_indexer.bin", 262_144, "8402ca48e5189a8950138580b0fff64db6f072f24ac07cd54ba8b2fbb9883b30"),
        new("duration_predictor.int8.onnx", 3_700_147, "c3eb91414d5ff8a7a239b7fe9e34e7e2bf8a8140d8375ffb14718b1c639325db"),
        new("text_encoder.int8.onnx", 36_416_150, "c7befd5ea8c3119769e8a6c1486c4edc6a3bc8365c67621c881bbb774b9902ff"),
        new("vector_estimator.int8.onnx", 78_400_833, "20cd86fa5c6effedfda0e7cffe5b0569ca401c440a0c3a1d72bf39286c0db3fd"),
        new("vocoder.int8.onnx", 25_991_073, "e923d60f53f95eb1ce235f1dc33ec56d9c057823c96fa6f8acf98f32b0da6152"),
        new("voice.bin", 517_168, "67d5209b0ee8ce6c74105ffbe12fe6a7628aea3b4ba2fcb308a4a67938a93ce8")
    };

    public static long TotalBytes => Files.Sum(f => f.Bytes);
}

/// <summary>
/// A second, better-sounding offline model: Kokoro 82M v1.0 (Apache-2.0) through the same ONNX runtime.
/// Fifty-four voices, so every trucker can keep their own, and the men actually sound like men.
/// Bigger (~150 MB) and about 8x slower than Supertonic, but still faster than realtime on a desktop.
/// </summary>
public sealed class KokoroVoiceModel
{
    public const string Repository = "csukuangfj/kokoro-int8-multi-lang-v1_0";
    /// <summary>The commit of <see cref="Repository"/> the checksums below were verified at.</summary>
    public const string Revision = "57adb37fc0e8d4bd430cfc1b8f8c2294945b9af2";
    public const string Label = "On-device voice, higher quality (Kokoro, ~150 MB, one-time download)";

    /// <summary>Only what English needs; the Chinese files are left out on purpose.</summary>
    public static readonly LocalVoiceModelFile[] Files =
    {
        new("model.int8.onnx", 114_202_993, "75635ea3b85a32d00094dcdd61a258aba87c160f7c264c304a7b3ae9d734195e") { Repository = Repository, Revision = Revision },
        new("voices.bin", 28_200_960, "1c5a5b983d3d50d8586d437a51f3faa2da7919ce76a013c081e65671a3447c29") { Repository = Repository, Revision = Revision },
        new("tokens.txt", 687, "6ebb6bb288f20f3ae8d004d3c2ca27697da27c037d75e81a60e2a6a663f95425") { Repository = Repository, Revision = Revision },
        new("lexicon-us-en.txt", 5_956_885, "7daaab53a181be9885b853a8582bf1838186317e5dadacbcef9c426d6fa0da14") { Repository = Repository, Revision = Revision },
        new("lexicon-gb-en.txt", 6_366_635, "c4cbb37316f62210dff52718a7afcaae24f50c032cc75ab47ae67b831d1049e7") { Repository = Repository, Revision = Revision }
    };

    public static long TotalBytes => Files.Sum(f => f.Bytes);

    /// <summary>Kokoro speakers that are men, measured at 109-175 Hz, for newcomers picked at random.</summary>
    public static readonly int[] MaleVoices = { 11, 12, 13, 14, 15, 16, 17, 18, 19, 24, 25, 26, 27 };
}
