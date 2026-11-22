using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HeartBeat.Tracking;

/// <summary>Monotonic high-resolution clock, seconds since process start.</summary>
public static class HostClock
{
    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static readonly double TickToSeconds = 1.0 / Stopwatch.Frequency;

    public static double Now => (Stopwatch.GetTimestamp() - Origin) * TickToSeconds;

    /// <summary>UTC wall-clock time corresponding to <see cref="Now"/> == 0.</summary>
    public static readonly DateTime OriginUtc = DateTime.UtcNow - TimeSpan.FromSeconds(Now);

    /// <summary>Waits until <paramref name="targetTime"/> using coarse sleeps, then a short spin.</summary>
    public static void WaitUntil(double targetTime)
    {
        while (true)
        {
            double remaining = targetTime - Now;
            if (remaining <= 0) return;
            if (remaining > 0.0015) Thread.Sleep(1);
            else Thread.SpinWait(20);
        }
    }
}

/// <summary>Raises the Windows timer resolution to 1 ms while alive, so Thread.Sleep(1) is ~1 ms.</summary>
public sealed class TimerResolutionScope : IDisposable
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);

    private readonly uint _period;
    private bool _active;

    public TimerResolutionScope(uint periodMs = 1)
    {
        _period = periodMs;
        _active = OperatingSystem.IsWindows() && timeBeginPeriod(periodMs) == 0;
    }

    public void Dispose()
    {
        if (_active) timeEndPeriod(_period);
        _active = false;
    }
}
