using System.Runtime.InteropServices;
using Valve.VR;

namespace HeartBeat.Tracking;

/// <summary>
/// Reader for a device's raw IMU stream exposed through IVRIOBuffer
/// (path "/devices/&lt;driver&gt;/&lt;serial&gt;/imu", elements of <see cref="ImuSample_t"/>).
/// Availability depends on the driver; this is what Phase 0 checks.
/// </summary>
public sealed class ImuStream : IDisposable
{
    public static readonly int SampleSize = Marshal.SizeOf<ImuSample_t>();
    private const int ChunkSamples = 256;

    private readonly CVRIOBuffer _io;
    private readonly IntPtr _buffer;
    private ulong _handle;

    public DeviceInfo Device { get; }
    public string Path { get; }

    private ImuStream(CVRIOBuffer io, DeviceInfo device, string path, ulong handle)
    {
        _io = io;
        Device = device;
        Path = path;
        _handle = handle;
        _buffer = Marshal.AllocHGlobal(SampleSize * ChunkSamples);
    }

    public static IEnumerable<string> CandidatePaths(DeviceInfo device)
    {
        var systems = new[] { device.TrackingSystem, device.TrackingSystem.ToLowerInvariant() }
            .Where(s => s.Length > 0).Distinct();
        foreach (var system in systems)
            yield return $"/devices/{system}/{device.Serial}/imu";
    }

    public sealed record OpenAttempt(string Path, string Result);

    /// <summary>Tries every candidate path. Returns the opened stream (or null) and each attempt's result.</summary>
    public static (ImuStream? Stream, List<OpenAttempt> Attempts) TryOpen(CVRIOBuffer io, DeviceInfo device)
    {
        var attempts = new List<OpenAttempt>();
        foreach (var path in CandidatePaths(device))
        {
            // Element count is driver-defined for readers; try 0 first, then a nominal size.
            foreach (uint elements in new uint[] { 0, 16 })
            {
                ulong handle = 0;
                var error = io.Open(path, EIOBufferMode.Read, (uint)SampleSize, elements, ref handle);
                attempts.Add(new OpenAttempt(path, $"{error} (elements={elements})"));
                if (error == EIOBufferError.IOBuffer_Success && handle != Valve.VR.OpenVR.k_ulInvalidIOBufferHandle)
                {
                    var stream = new ImuStream(io, device, path, handle);
                    stream.DiscardBacklog();
                    return (stream, attempts);
                }
                if (error != EIOBufferError.IOBuffer_InvalidArgument)
                    break;
            }
        }
        return (null, attempts);
    }

    /// <summary>
    /// The driver's ring buffer still holds samples from before we opened it (seconds old, e.g. left
    /// over from a previous reader). Drop them so the stream starts at "now".
    /// </summary>
    public int DiscardBacklog()
    {
        var scratch = new List<ImuSample_t>(4096);
        ReadAvailable(scratch, out _);
        return scratch.Count;
    }

    /// <summary>Reads all currently available samples into <paramref name="into"/>. Returns count, or -1 on error.</summary>
    public int ReadAvailable(List<ImuSample_t> into, out EIOBufferError lastError)
    {
        lastError = EIOBufferError.IOBuffer_Success;
        int total = 0;
        while (true)
        {
            uint bytesRead = 0;
            lastError = _io.Read(_handle, _buffer, (uint)(SampleSize * ChunkSamples), ref bytesRead);
            if (lastError != EIOBufferError.IOBuffer_Success) return total > 0 ? total : -1;

            int n = (int)(bytesRead / SampleSize);
            for (int i = 0; i < n; i++)
            {
                var sample = Marshal.PtrToStructure<ImuSample_t>(_buffer + i * SampleSize);
                if (!AcceptDuringStartup(sample, into, ref total)) continue;
                into.Add(sample);
                total++;
            }
            if (n < ChunkSamples) return total;
        }
    }

    // The driver's ring buffer can still hold samples from long before we opened it, and they
    // sometimes arrive after DiscardBacklog. During the first seconds, a jump forward of more than
    // StaleGapSeconds means everything before it was stale: drop it.
    private const double StaleGapSeconds = 1.0;
    private const int StartupSamples = 4000;
    private double _lastSampleTime = double.NaN;
    private int _accepted;

    private bool AcceptDuringStartup(in ImuSample_t sample, List<ImuSample_t> batch, ref int total)
    {
        if (_accepted < StartupSamples && !double.IsNaN(_lastSampleTime)
            && sample.fSampleTime - _lastSampleTime > StaleGapSeconds)
        {
            int stale = Math.Min(total, batch.Count);
            batch.RemoveRange(batch.Count - stale, stale);
            total -= stale;
            StaleSamplesDropped += _accepted;
            _accepted = 0;
        }
        _lastSampleTime = sample.fSampleTime;
        _accepted++;
        return true;
    }

    /// <summary>Samples discarded because they were left over from before this stream was opened.</summary>
    public long StaleSamplesDropped { get; private set; }

    public void Dispose()
    {
        if (_handle != 0)
        {
            _io.Close(_handle);
            _handle = 0;
            Marshal.FreeHGlobal(_buffer);
        }
    }
}
