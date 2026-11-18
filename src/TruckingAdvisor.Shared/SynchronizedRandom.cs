namespace TruckingAdvisor.Shared;

/// <summary>
/// A <see cref="Random"/> that is safe to share between threads. Wraps another instance (so a seeded one still
/// gives repeatable results in tests) and serialises every call to it.
/// </summary>
public sealed class SynchronizedRandom : Random
{
    private readonly Random _inner;
    private readonly object _lock = new();

    public SynchronizedRandom(Random inner) => _inner = inner is SynchronizedRandom s ? s._inner : inner;

    public override int Next() { lock (_lock) return _inner.Next(); }
    public override int Next(int maxValue) { lock (_lock) return _inner.Next(maxValue); }
    public override int Next(int minValue, int maxValue) { lock (_lock) return _inner.Next(minValue, maxValue); }
    public override long NextInt64() { lock (_lock) return _inner.NextInt64(); }
    public override long NextInt64(long maxValue) { lock (_lock) return _inner.NextInt64(maxValue); }
    public override long NextInt64(long minValue, long maxValue) { lock (_lock) return _inner.NextInt64(minValue, maxValue); }
    public override double NextDouble() { lock (_lock) return _inner.NextDouble(); }
    public override float NextSingle() { lock (_lock) return _inner.NextSingle(); }
    public override void NextBytes(byte[] buffer) { lock (_lock) _inner.NextBytes(buffer); }
    public override void NextBytes(Span<byte> buffer) { lock (_lock) _inner.NextBytes(buffer); }
    protected override double Sample() { lock (_lock) return _inner.NextDouble(); }
}
