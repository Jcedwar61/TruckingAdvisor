using System.Text.Json;

namespace TruckingAdvisor.AI;

/// <summary>
/// Counts AI requests, tokens, audio seconds and characters, and estimates cost per model actually used
/// (the player's prices when entered, otherwise conservative built-in estimates, see <see cref="AiModelCatalog"/>).
/// Persisted so the daily limit survives restarts. Cost is an estimate; the provider's dashboard is the source of truth.
///
/// Every billable call reserves its slot first with <see cref="TryReserve"/>: the request is counted and its estimated
/// cost held under the lock before anything is sent, so concurrent callers can't all slip under the limit together.
/// </summary>
public sealed class AiUsageTracker
{
    private readonly object _sync = new();
    private readonly string? _path;
    private readonly Func<DateOnly> _today;
    /// <summary>Estimated cost of requests in flight (reserved, not yet recorded). Counts against the spend limit.</summary>
    private decimal _pending;

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

    /// <summary>
    /// One billable request that passed the limit check. Record its usage through the tracker's Record* methods with
    /// this reservation (the request is then not counted twice). Disposing it without recording releases the held
    /// estimate; <see cref="CommitEstimate"/> charges the estimate instead (the call may have been billed, e.g. a timeout).
    /// </summary>
    public sealed class Reservation : IDisposable
    {
        private readonly AiUsageTracker _owner;
        internal readonly DateOnly Day;
        internal decimal Held;
        internal bool Done;

        internal Reservation(AiUsageTracker owner, DateOnly day, decimal held)
        {
            _owner = owner;
            Day = day;
            Held = held;
        }

        /// <summary>The cost estimate held for this request.</summary>
        public decimal EstimatedCostUsd => Held;

        /// <summary>Charges the held estimate as spent (no usage report came back, but the provider may still bill it).</summary>
        public void CommitEstimate() => _owner.Record(this, t => t.EstimatedCostUsd += Held);

        public void Dispose() => _owner.Release(this);
    }

    public Ledger Data { get; private set; } = new();

    public AiUsageTracker(string? path = null, Func<DateOnly>? today = null)
    {
        _path = path;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        if (_path is not null)
            Data = LoadLedger(_path) ?? LoadLedger(_path + ".bak") ?? new Ledger();
        RollDay();
    }

    private static Ledger? LoadLedger(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            TruckingAdvisor.Shared.Diagnostics.AppLog.Warn($"AI usage file unreadable ({ex.GetType().Name}); trying the backup.");
            return null;
        }
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TruckingAdvisor", "ai-usage.json");

    public bool IsOverLimit(AiSettings s)
    {
        lock (_sync)
        {
            RollDay();
            return OverLimit(s, 0m);
        }
    }

    private bool OverLimit(AiSettings s, decimal extra)
    {
        if (s.DailyRequestLimit > 0 && Data.Today.Requests >= s.DailyRequestLimit) return true;
        if (s.DailySpendLimitUsd <= 0) return false;
        decimal committed = Data.Today.EstimatedCostUsd + _pending;
        return committed >= s.DailySpendLimitUsd || committed + extra > s.DailySpendLimitUsd;
    }

    /// <summary>
    /// Checks the daily limits and, if there's room, counts one request and holds <paramref name="estimatedCostUsd"/>
    /// against the spend limit, atomically. Null means the limit is reached: don't send the request.
    /// </summary>
    public Reservation? TryReserve(AiSettings s, decimal estimatedCostUsd = 0m)
    {
        if (estimatedCostUsd < 0) estimatedCostUsd = 0;
        lock (_sync)
        {
            RollDay();
            if (OverLimit(s, estimatedCostUsd)) return null;
            Data.Today.Requests++;
            Data.Lifetime.Requests++;
            _pending += estimatedCostUsd;
            Save();
            return new Reservation(this, Data.Day, estimatedCostUsd);
        }
    }

    // ---- Recording actual usage. Without a reservation the request is counted here (older callers, tests). ----

    /// <summary>An OpenAI chat request, priced for the model that answered (null: the current chat model).</summary>
    public void RecordChat(AiSettings s, int inputTokens, int outputTokens, string? model = null, Reservation? reservation = null)
    {
        var (pin, pout) = AiModelCatalog.EffectiveChatPrice(s, model ?? s.ChatModel);
        RecordChatPriced(inputTokens, outputTokens, pin, pout, reservation);
    }

    /// <summary>A DeepSeek chat request (its own prices).</summary>
    public void RecordDeepSeek(AiSettings s, string model, int inputTokens, int outputTokens, Reservation? reservation = null)
    {
        var (pin, pout) = AiModelCatalog.EffectiveDeepSeekPrice(s, model);
        RecordChatPriced(inputTokens, outputTokens, pin, pout, reservation);
    }

    /// <summary>A chat request priced per million tokens.</summary>
    public void RecordChatPriced(int inputTokens, int outputTokens, decimal inputPerMillion, decimal outputPerMillion, Reservation? reservation = null) =>
        Record(reservation, t =>
        {
            t.InputTokens += inputTokens;
            t.OutputTokens += outputTokens;
            t.EstimatedCostUsd += inputTokens * inputPerMillion / 1_000_000m + outputTokens * outputPerMillion / 1_000_000m;
        });

    public void RecordSpeechToText(AiSettings s, double seconds, string? model = null, Reservation? reservation = null)
    {
        decimal perMinute = AiModelCatalog.EffectiveTranscriptionPrice(s, model ?? s.SpeechToTextModel);
        Record(reservation, t =>
        {
            t.SpeechSeconds += seconds;
            t.EstimatedCostUsd += (decimal)(seconds / 60.0) * perMinute;
        });
    }

    public void RecordTextToSpeech(AiSettings s, int characters, string? model = null, Reservation? reservation = null)
    {
        decimal perMillion = AiModelCatalog.EffectiveSpeechPrice(s, model ?? s.TextToSpeechModel);
        Record(reservation, t =>
        {
            t.TtsCharacters += characters;
            t.EstimatedCostUsd += characters * perMillion / 1_000_000m;
        });
    }

    /// <summary>Token counts from a realtime <c>response.done</c> usage block.</summary>
    public readonly record struct RealtimeTokens(int TextIn, int AudioIn, int CachedTextIn, int CachedAudioIn, int TextOut, int AudioOut)
    {
        public int Input => TextIn + AudioIn;
        public int Output => TextOut + AudioOut;
    }

    /// <summary>
    /// A realtime (speech-to-speech) response: audio and text tokens priced separately at the realtime model's prices,
    /// cached input at the cached rate. (Cached counts are part of the text/audio input counts.)
    /// </summary>
    public void RecordRealtime(string? model, RealtimeTokens u, Reservation? reservation = null)
    {
        var p = AiModelCatalog.RealtimePrice(model);
        decimal cost = (Math.Max(0, u.TextIn - u.CachedTextIn) * p.TextIn
                      + Math.Max(0, u.AudioIn - u.CachedAudioIn) * p.AudioIn
                      + (Math.Min(u.CachedTextIn, u.TextIn) + Math.Min(u.CachedAudioIn, u.AudioIn)) * p.CachedIn
                      + u.TextOut * p.TextOut
                      + u.AudioOut * p.AudioOut) / 1_000_000m;
        Record(reservation, t =>
        {
            t.InputTokens += u.Input;
            t.OutputTokens += u.Output;
            t.EstimatedCostUsd += cost;
        });
    }

    private void Record(Reservation? reservation, Action<Totals> apply)
    {
        lock (_sync)
        {
            RollDay();
            bool counted = false;
            if (reservation is not null)
            {
                if (reservation.Done) return; // already recorded
                ReleaseHold(reservation);
                counted = true;
            }
            foreach (var t in new[] { Data.Today, Data.Lifetime })
            {
                if (!counted) t.Requests++;
                apply(t);
            }
            Save();
        }
    }

    private void Release(Reservation reservation)
    {
        lock (_sync) ReleaseHold(reservation);
    }

    private void ReleaseHold(Reservation reservation)
    {
        if (reservation.Done) return;
        reservation.Done = true;
        // A hold from before midnight was already dropped with yesterday's totals.
        if (reservation.Day == Data.Day) _pending = Math.Max(0m, _pending - reservation.Held);
    }

    private void RollDay()
    {
        var today = _today();
        if (Data.Day != today)
        {
            Data.Day = today;
            Data.Today = new Totals();
            _pending = 0m;
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            // Atomic replace, keeping the previous good copy as .bak: a crash mid-write can't reset today's spend to zero.
            AtomicFile.Write(_path, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Data)), keepBackup: true);
        }
        catch { /* usage logging must never break the radio */ }
    }
}
