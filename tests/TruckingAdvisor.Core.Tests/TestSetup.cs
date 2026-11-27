using System.Runtime.CompilerServices;

namespace TruckingAdvisor.Core.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Many tests run in parallel and some (WPF dispatcher, audio, sockets, timers) briefly block a thread-pool thread.
    /// On a small CI runner the pool then grows slowly, so a 20 ms delay or a socket read can stall for seconds and
    /// time-based tests fail at random. Start the pool with enough threads for the suite.
    /// </summary>
    [ModuleInitializer]
    internal static void RaiseThreadPoolMinimum()
    {
        ThreadPool.GetMinThreads(out int workers, out int io);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(io, 64));
    }
}
