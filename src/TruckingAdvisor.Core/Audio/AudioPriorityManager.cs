using System.Collections.Concurrent;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;

namespace TruckingAdvisor.Core.Audio;

/// <summary>
/// One playback worker for every voice in the app (coach, dispatcher, CB truckers, advisors).
///
/// - Never overlaps: the worker awaits the sink until playback really ends.
/// - Critical items stop whatever is playing (unless it is also Critical) and jump the queue.
/// - High items cut off Low chatter that is playing.
/// - Normal and Low wait while the CB radio is busy; new Low items are dropped while it is busy.
/// - StopAll clears the queue and stops current playback.
/// Enqueue never blocks, so it is safe to call from the telemetry thread.
/// </summary>
public sealed class AudioPriorityManager : IAudioPriorityManager, IDisposable
{
    private readonly IAudioSink _sink;
    private readonly object _sync = new();
    private readonly List<(AudioItem Item, long Seq, TaskCompletionSource? Done, DateTime QueuedAt)> _queue = new();
    /// <summary>Items held back by a busy radio longer than this are dropped so nothing waits forever.</summary>
    public TimeSpan MaxHoldTime { get; set; } = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, float> _volumes = new(new Dictionary<string, float>
    {
        ["cb"] = 0.85f, ["dispatcher"] = 0.9f, ["advisor"] = 0.95f, ["notifications"] = 0.8f, ["effects"] = 0.75f
    }, StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _currentCts;
    private AudioItem? _current;
    private TaskCompletionSource? _currentDone;
    private volatile bool _radioBusy;
    private long _seq;
    private readonly Task _worker;

    public bool IsRadioChannelBusy { get => _radioBusy; set => _radioBusy = value; }
    public AudioPriority? CurrentlyPlayingPriority => _current?.Priority;
    public int QueuedCount { get { lock (_sync) return _queue.Count; } }

    /// <summary>Raised when an item starts playing (for UI captions).</summary>
    public event EventHandler<AudioItem>? AudioStarted;

    public AudioPriorityManager(IAudioSink sink)
    {
        _sink = sink;
        _worker = Task.Run(WorkerLoopAsync);
    }

    public void EnqueueAnnouncement(string text, AudioPriority priority, string voiceProfile = "advisor", bool applyCbEffects = false)
        => Enqueue(new AudioItem(text, priority, voiceProfile, applyCbEffects));

    public void Enqueue(AudioItem item) => EnqueueCore(item, null);

    public Task EnqueueAndWaitAsync(AudioItem item, CancellationToken cancellationToken = default)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueCore(item, done);
        return cancellationToken.CanBeCanceled ? done.Task.WaitAsync(cancellationToken) : done.Task;
    }

    private void EnqueueCore(AudioItem item, TaskCompletionSource? done)
    {
        if ((string.IsNullOrWhiteSpace(item.Text) && item.WavAudio is null) ||
            (item.Priority == AudioPriority.Low && IsRadioChannelBusy))
        {
            done?.TrySetResult();
            return;
        }

        lock (_sync)
        {
            // Skip duplicate announcements, but never for a caller waiting on its own playback.
            if (done is null && _queue.Any(q => q.Item.WavAudio is null && q.Item.Text == item.Text)) return;
            if (item.Priority == AudioPriority.Critical)
            {
                // Stale chatter is pointless now.
                foreach (var dropped in _queue.Where(q => q.Item.Priority < AudioPriority.High)) dropped.Done?.TrySetResult();
                _queue.RemoveAll(q => q.Item.Priority < AudioPriority.High);
            }
            _queue.Add((item, _seq++, done, DateTime.UtcNow));

            var playing = _current;
            if (playing is not null && ShouldPreempt(item.Priority, playing.Priority))
                _currentCts?.Cancel();
        }
        _signal.Release();
    }

    private static bool ShouldPreempt(AudioPriority incoming, AudioPriority playing) =>
        (incoming == AudioPriority.Critical && playing != AudioPriority.Critical) ||
        (incoming == AudioPriority.High && playing == AudioPriority.Low);

    public void SetChannelVolume(string channel, float volume0To1) =>
        _volumes[channel] = Math.Clamp(volume0To1, 0f, 1f); // ConcurrentDictionary: safe from the UI thread

    public float GetChannelVolume(string channel) =>
        _volumes.TryGetValue(channel, out float v) ? v : 0.85f;

    public void StopAll()
    {
        lock (_sync)
        {
            foreach (var q in _queue) q.Done?.TrySetResult();
            _queue.Clear();
            _currentCts?.Cancel();
        }
    }

    private bool TryTakeNext(out AudioItem item)
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                if (now - _queue[i].QueuedAt <= MaxHoldTime) continue;
                _queue[i].Done?.TrySetResult(); // too stale to be useful; release the waiter
                _queue.RemoveAt(i);
            }
            int best = -1;
            for (int i = 0; i < _queue.Count; i++)
            {
                var q = _queue[i];
                if (IsRadioChannelBusy && q.Item.Priority <= AudioPriority.Normal) continue; // wait for radio
                if (best < 0 || q.Item.Priority > _queue[best].Item.Priority ||
                    (q.Item.Priority == _queue[best].Item.Priority && q.Seq < _queue[best].Seq))
                    best = i;
            }
            if (best < 0) { item = null!; return false; }
            item = _queue[best].Item;
            _currentDone = _queue[best].Done;
            _queue.RemoveAt(best);
            _current = item;
            _currentCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            return true;
        }
    }

    private async Task WorkerLoopAsync()
    {
        var ct = _shutdown.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wake on new items, or every 250 ms to re-check items held back by a busy radio.
                await _signal.WaitAsync(TimeSpan.FromMilliseconds(250), ct);
                while (TryTakeNext(out var item))
                {
                    var itemCts = _currentCts!;
                    try
                    {
                        AudioStarted?.Invoke(this, item);
                        await _sink.PlayAsync(item, GetChannelVolume(item.Channel), itemCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* preempted */ }
                    catch (Exception) { /* a broken voice must not kill the audio worker */ }
                    finally
                    {
                        TaskCompletionSource? done;
                        lock (_sync) { done = _currentDone; _current = null; _currentCts = null; _currentDone = null; }
                        itemCts.Dispose();
                        done?.TrySetResult();
                    }
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
        lock (_sync)
        {
            foreach (var q in _queue) q.Done?.TrySetResult();
            _queue.Clear();
            _currentDone?.TrySetResult(); // in case playback is stuck
        }
        _shutdown.Dispose();
        _signal.Dispose();
    }
}
