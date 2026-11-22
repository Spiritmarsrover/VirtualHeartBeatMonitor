using Valve.VR;

namespace HeartBeat.Tracking;

/// <summary>One polled pose for one device.</summary>
/// <param name="VelocityChanged">
/// Velocity or angular velocity differs from the previous poll. Poses returned for "now" are
/// extrapolated between driver updates, so the matrix changes every poll; velocities only change
/// when the driver delivers a new update. This flag is therefore the best proxy for "new data".
/// </param>
/// <param name="MatrixChanged">The pose matrix differs from the previous poll.</param>
public readonly record struct PoseSample(
    double HostTime,
    uint Device,
    bool Valid,
    ETrackingResult Result,
    HmdMatrix34_t Matrix,
    HmdVector3_t Velocity,
    HmdVector3_t AngularVelocity,
    bool VelocityChanged,
    bool MatrixChanged);

/// <summary>
/// Polls GetDeviceToAbsoluteTrackingPose on a dedicated thread at a fixed rate, independent of
/// any render loop. Uses the raw, uncalibrated universe so playspace changes don't leak in.
/// </summary>
public sealed class PosePoller : IDisposable
{
    public delegate void SampleHandler(in PoseSample sample);

    private readonly CVRSystem _system;
    private readonly uint[] _devices;
    private readonly double _period;
    private readonly SampleHandler _handler;
    private readonly Thread _thread;
    private volatile bool _running;

    public double TargetHz { get; }
    public long PollCount { get; private set; }
    public long LatePolls { get; private set; }

    public PosePoller(CVRSystem system, IEnumerable<uint> devices, double hz, SampleHandler handler)
    {
        _system = system;
        _devices = devices.ToArray();
        TargetHz = hz;
        _period = 1.0 / hz;
        _handler = handler;
        _thread = new Thread(Run) { IsBackground = true, Name = "PosePoller", Priority = ThreadPriority.Highest };
    }

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
        var poses = new TrackedDevicePose_t[Valve.VR.OpenVR.k_unMaxTrackedDeviceCount];
        var last = new TrackedDevicePose_t[poses.Length];
        var seen = new bool[poses.Length];
        double next = HostClock.Now;

        while (_running)
        {
            double t = HostClock.Now;
            _system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated, 0f, poses);
            PollCount++;

            foreach (uint d in _devices)
            {
                ref readonly var p = ref poses[d];
                bool velChanged = !seen[d]
                    || !PoseMath.BitEqual(p.vVelocity, last[d].vVelocity)
                    || !PoseMath.BitEqual(p.vAngularVelocity, last[d].vAngularVelocity);
                bool matChanged = !seen[d] || !PoseMath.BitEqual(p.mDeviceToAbsoluteTracking, last[d].mDeviceToAbsoluteTracking);
                last[d] = p;
                seen[d] = true;

                _handler(new PoseSample(t, d, p.bPoseIsValid, p.eTrackingResult,
                    p.mDeviceToAbsoluteTracking, p.vVelocity, p.vAngularVelocity, velChanged, matChanged));
            }

            next += _period;
            double now = HostClock.Now;
            if (now - next > 0.02)
            {
                // Fell far behind (e.g. system hiccup): resynchronise instead of bursting.
                LatePolls++;
                next = now;
            }
            HostClock.WaitUntil(next);
        }
    }

    public void Dispose() => Stop();
}
