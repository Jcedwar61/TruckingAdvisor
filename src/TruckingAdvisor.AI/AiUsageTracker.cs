using System.Text.Json;

namespace TruckingAdvisor.AI;

/// <summary>
/// Counts AI requests, tokens, audio seconds and characters, and estimates cost from the
/// prices the user entered. Persisted so the daily limit survives restarts.
/// Cost is an estimate; the OpenAI dashboard is the source of truth.
/// </summary>
public sealed class AiUsageTracker
{
    private readonly object _sync = new();
    private readonly string? _path;
    private readonly Func<DateOnly> _today;

    public sealed class Totals
    {
        public int Requests { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public double SpeechSeconds { get; set; }
        public long TtsCharacters { get; set; }
        public decimal EstimatedCostUsd { get; set; }
    }

    public sealed class Ledger
    {
        public DateOnly Day { get; set; }
        public Totals Today { get; set; } = new();
        public Totals Lifetime { get; set; } = new();
    }

    public Ledger Data { get; private set; } = new();

    public AiUsageTracker(string? path = null, Func<DateOnly>? today = null)
    {
        _path = path;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        if (_path is not null && File.Exists(_path))
        {
            try { Data = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(_path)) ?? new Ledger(); }
            catch { Data = new Ledger(); }
        }
        RollDay();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TruckingAdvisor", "ai-usage.json");

    public bool IsOverLimit(AiSettings s)
    {
        lock (_sync)
        {
            RollDay();
            if (s.DailyRequestLimit > 0 && Data.Today.Requests >= s.DailyRequestLimit) return true;
            return s.DailySpendLimitUsd > 0 && Data.Today.EstimatedCostUsd >= s.DailySpendLimitUsd;
        }
    }

    public void RecordChat(AiSettings s, int inputTokens, int outputTokens) => Record(t =>
    {
        t.InputTokens += inputTokens;
        t.OutputTokens += outputTokens;
        t.EstimatedCostUsd += inputTokens * s.ChatInputPricePerMillionTokens / 1_000_000m
                            + outputTokens * s.ChatOutputPricePerMillionTokens / 1_000_000m;
    });

    public void RecordSpeechToText(AiSettings s, double seconds) => Record(t =>
    {
        t.SpeechSeconds += seconds;
        t.EstimatedCostUsd += (decimal)(seconds / 60.0) * s.SpeechToTextPricePerMinute;
    });

    public void RecordTextToSpeech(AiSettings s, int characters) => Record(t =>
    {
        t.TtsCharacters += characters;
        t.EstimatedCostUsd += characters * s.TextToSpeechPricePerMillionChars / 1_000_000m;
    });

    private void Record(Action<Totals> apply)
    {
        lock (_sync)
        {
            RollDay();
            foreach (var t in new[] { Data.Today, Data.Lifetime })
            {
                t.Requests++;
                apply(t);
            }
            Save();
        }
    }

    private void RollDay()
    {
        var today = _today();
        if (Data.Day != today)
        {
            Data.Day = today;
            Data.Today = new Totals();
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Data));
        }
        catch { /* usage logging must never break the radio */ }
    }
}
