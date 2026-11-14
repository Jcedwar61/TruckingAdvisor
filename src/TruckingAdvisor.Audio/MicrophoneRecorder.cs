using System.Runtime.Versioning;
using NAudio.Wave;
using TruckingAdvisor.Shared.Diagnostics;

namespace TruckingAdvisor.Audio;

/// <summary>
/// Push-to-talk recording (16 kHz mono, max 30 s).
///
/// Ready mode (default while the microphone is enabled): the device stays open and only the last
/// half second is kept in memory, constantly overwritten. Nothing is stored or sent. When push-to-talk
/// is pressed that half second becomes the start of the recording, so the first word isn't clipped
/// by the time Windows needs to open a microphone; after release it keeps recording 0.3 s so the last
/// word isn't clipped either.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MicrophoneRecorder : IDisposable
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PreRoll = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(300);
    private const int Rate = 16000;

    private readonly object _sync = new();
    private WaveInEvent? _waveIn;
    private int _openDevice = int.MinValue;
    private readonly byte[] _ring = new byte[(int)(Rate * 2 * PreRoll.TotalSeconds)];
    private int _ringPos, _ringFill;
    private MemoryStream? _recording;
    private DateTime _startedAt;
    private DateTime? _stopAt;
    private TaskCompletionSource<byte[]>? _done;

    public int DeviceNumber { get; set; }
    /// <summary>Keep the device open between transmissions (pre-roll). Off = open only while talking.</summary>
    public bool KeepReady { get; set; } = true;
    public bool IsRecording { get { lock (_sync) return _recording is not null; } }
    /// <summary>Peak level 0..1 of the last block, for the mic meter.</summary>
    public float Level { get; private set; }

    public static IReadOnlyList<string> ListDevices() =>
        Enumerable.Range(0, WaveInEvent.DeviceCount).Select(i => WaveInEvent.GetCapabilities(i).ProductName).ToList();

    /// <summary>Opens the microphone in ready mode (call when the mic is enabled or the device changes).</summary>
    public void EnsureReady()
    {
        if (!KeepReady) { lock (_sync) if (_recording is null) CloseDevice(); return; }
        lock (_sync) OpenDevice();
    }

    /// <summary>Closes the device (mic turned off). A recording in progress is finished first.</summary>
    public void Close()
    {
        lock (_sync) if (_recording is null) CloseDevice();
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_recording is not null) return;
            OpenDevice();
            _recording = new MemoryStream();
            // Pre-roll: the last half second before the key went down.
            if (_ringFill > 0)
            {
                int start = (_ringPos - _ringFill + _ring.Length) % _ring.Length;
                for (int i = 0; i < _ringFill; i++) _recording.WriteByte(_ring[(start + i) % _ring.Length]);
            }
            _startedAt = DateTime.UtcNow;
            _stopAt = null;
            _done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Stops (after a short tail) and returns the WAV bytes; empty if nothing was recording.</summary>
    public Task<byte[]> StopAsync()
    {
        lock (_sync)
        {
            if (_recording is null || _done is null) return Task.FromResult(Array.Empty<byte>());
            _stopAt ??= DateTime.UtcNow + Tail;
            var task = _done.Task;
            // If the device stalls, don't wait forever.
            _ = Task.Delay(Tail + TimeSpan.FromMilliseconds(700)).ContinueWith(_ => { lock (_sync) Finish(); });
            return task;
        }
    }

    private void OpenDevice()
    {
        if (_waveIn is not null && _openDevice == DeviceNumber) return;
        CloseDevice();
        var waveIn = new WaveInEvent { DeviceNumber = DeviceNumber, WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = 40 };
        waveIn.DataAvailable += OnData;
        waveIn.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) AppLog.Error("Microphone stopped", e.Exception);
            lock (_sync)
            {
                if (ReferenceEquals(_waveIn, waveIn)) { _waveIn = null; _openDevice = int.MinValue; }
                Finish();
            }
            waveIn.Dispose();
        };
        waveIn.StartRecording();
        _waveIn = waveIn;
        _openDevice = DeviceNumber;
        _ringFill = 0;
    }

    private void CloseDevice()
    {
        var w = _waveIn;
        _waveIn = null;
        _openDevice = int.MinValue;
        _ringFill = 0;
        try { w?.StopRecording(); } catch { }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        short peak = 0;
        for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            short v = BitConverter.ToInt16(e.Buffer, i);
            peak = Math.Max(peak, v == short.MinValue ? short.MaxValue : Math.Abs(v));
        }
        Level = peak / 32768f;

        lock (_sync)
        {
            if (_recording is not null)
            {
                _recording.Write(e.Buffer, 0, e.BytesRecorded);
                var now = DateTime.UtcNow;
                if ((_stopAt is { } stop && now >= stop) || now - _startedAt > MaxDuration) Finish();
            }
            else
            {
                // Ready mode: keep only the last half second, overwritten continuously.
                for (int i = 0; i < e.BytesRecorded; i++)
                {
                    _ring[_ringPos] = e.Buffer[i];
                    _ringPos = (_ringPos + 1) % _ring.Length;
                }
                _ringFill = Math.Min(_ring.Length, _ringFill + e.BytesRecorded);
            }
        }
    }

    /// <summary>Wraps the recorded PCM in a WAV header and completes the stop (call under the lock).</summary>
    private void Finish()
    {
        if (_recording is null) return;
        var pcm = _recording.ToArray();
        _recording = null;
        _stopAt = null;
        var done = _done;
        _done = null;
        _ringFill = 0; // never reuse audio from a transmission as the next pre-roll
        if (!KeepReady) CloseDevice();
        done?.TrySetResult(Wav(pcm));
    }

    private static byte[] Wav(byte[] pcm)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using (var w = new WaveFileWriter(new NonClosingStream(ms), new WaveFormat(Rate, 16, 1))) w.Write(pcm, 0, pcm.Length);
        return ms.ToArray();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            Finish();
            CloseDevice();
        }
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) => Flush();
    }
}
