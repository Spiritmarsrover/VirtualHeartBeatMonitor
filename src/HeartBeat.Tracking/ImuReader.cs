using Valve.VR;

namespace HeartBeat.Tracking;

/// <summary>Drains a set of <see cref="ImuStream"/>s on a background thread.</summary>
public sealed class ImuReader : IDisposable
{
    public delegate void SampleHandler(ImuStream stream, double hostTime, in ImuSample_t sample);

    private readonly IReadOnlyList<ImuStream> _streams;
    private readonly SampleHandler _handler;
    private readonly Thread _thread;
    private readonly Dictionary<ImuStream, long> _readErrors = new();
    private volatile bool _running;

    /// <summary>Called on the reader thread after every polling pass (about every 2 ms), even without samples.</summary>
    public Action? Tick { get; init; }

    public ImuReader(IReadOnlyList<ImuStream> streams, SampleHandler handler)
    {
        _streams = streams;
        _handler = handler;
        foreach (var s in streams) _readErrors[s] = 0;
        _thread = new Thread(Run) { IsBackground = true, Name = "ImuReader", Priority = ThreadPriority.AboveNormal };
    }

    public long ReadErrors(ImuStream stream) => _readErrors.GetValueOrDefault(stream);

    public void Start()
    {
        _running = true;
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        if (_thread.IsAlive) _thread.Join();
    }

    private void Run()
    {
        var batch = new List<ImuSample_t>(1024);
        while (_running)
        {
            foreach (var stream in _streams)
            {
                batch.Clear();
                int n = stream.ReadAvailable(batch, out _);
                if (n < 0)
                {
                    _readErrors[stream]++;
                    continue;
                }
                double t = HostClock.Now;
                foreach (var sample in batch)
                    _handler(stream, t, sample);
            }
            Tick?.Invoke();
            Thread.Sleep(2);
        }
    }

    public void Dispose() => Stop();
}
