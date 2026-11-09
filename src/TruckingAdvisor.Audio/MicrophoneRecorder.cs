using System.Runtime.Versioning;
using NAudio.Wave;

namespace TruckingAdvisor.Audio;

/// <summary>
/// Records the microphone only while push-to-talk is held (16 kHz mono WAV, max 30 s).
/// Nothing is recorded unless the user turned the microphone on in settings.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MicrophoneRecorder : IDisposable
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(30);
    private WaveInEvent? _waveIn;
    private MemoryStream? _buffer;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<byte[]>? _stopped;
    private DateTime _startedAt;

    public int DeviceNumber { get; set; }
    public bool IsRecording => _waveIn is not null;
    /// <summary>Peak level 0..1 of the last block, for the mic meter.</summary>
    public float Level { get; private set; }

    public static IReadOnlyList<string> ListDevices() =>
        Enumerable.Range(0, WaveInEvent.DeviceCount).Select(i => WaveInEvent.GetCapabilities(i).ProductName).ToList();

    public void Start()
    {
        if (IsRecording) return;
        _buffer = new MemoryStream();
        var format = new WaveFormat(16000, 16, 1);
        _writer = new WaveFileWriter(new NonClosingStream(_buffer), format);
        _stopped = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waveIn = new WaveInEvent { DeviceNumber = DeviceNumber, WaveFormat = format, BufferMilliseconds = 50 };
        _waveIn.DataAvailable += (_, e) =>
        {
            if (DateTime.UtcNow - _startedAt > MaxDuration) { _waveIn?.StopRecording(); return; }
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
            short peak = 0;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(e.Buffer, i) == short.MinValue ? short.MaxValue : BitConverter.ToInt16(e.Buffer, i)));
            Level = peak / 32768f;
        };
        _waveIn.RecordingStopped += (_, _) =>
        {
            _writer?.Dispose(); // finalizes the WAV header
            _writer = null;
            var bytes = _buffer?.ToArray() ?? Array.Empty<byte>();
            _waveIn?.Dispose();
            _waveIn = null;
            Level = 0;
            _stopped?.TrySetResult(bytes);
        };
        _startedAt = DateTime.UtcNow;
        _waveIn.StartRecording();
    }

    /// <summary>Stops and returns the WAV bytes (empty if nothing was recording).</summary>
    public Task<byte[]> StopAsync()
    {
        if (_waveIn is null) return Task.FromResult(_stopped?.Task.IsCompleted == true ? _stopped.Task.Result : Array.Empty<byte>());
        var task = _stopped!.Task;
        _waveIn.StopRecording();
        return task;
    }

    public void Dispose()
    {
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _writer?.Dispose();
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
