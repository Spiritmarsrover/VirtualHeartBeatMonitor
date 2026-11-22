using Valve.VR;

namespace HeartBeat.Tracking;

/// <summary>Rigid 3×4 transforms (rotation + translation) in OpenVR's row-major layout.</summary>
public static class Rigid
{
    public static readonly HmdMatrix34_t Identity = new() { m0 = 1, m5 = 1, m10 = 1 };

    /// <summary>a · b (apply b first, then a).</summary>
    public static HmdMatrix34_t Multiply(in HmdMatrix34_t a, in HmdMatrix34_t b) => new()
    {
        m0 = a.m0 * b.m0 + a.m1 * b.m4 + a.m2 * b.m8,
        m1 = a.m0 * b.m1 + a.m1 * b.m5 + a.m2 * b.m9,
        m2 = a.m0 * b.m2 + a.m1 * b.m6 + a.m2 * b.m10,
        m3 = a.m0 * b.m3 + a.m1 * b.m7 + a.m2 * b.m11 + a.m3,
        m4 = a.m4 * b.m0 + a.m5 * b.m4 + a.m6 * b.m8,
        m5 = a.m4 * b.m1 + a.m5 * b.m5 + a.m6 * b.m9,
        m6 = a.m4 * b.m2 + a.m5 * b.m6 + a.m6 * b.m10,
        m7 = a.m4 * b.m3 + a.m5 * b.m7 + a.m6 * b.m11 + a.m7,
        m8 = a.m8 * b.m0 + a.m9 * b.m4 + a.m10 * b.m8,
        m9 = a.m8 * b.m1 + a.m9 * b.m5 + a.m10 * b.m9,
        m10 = a.m8 * b.m2 + a.m9 * b.m6 + a.m10 * b.m10,
        m11 = a.m8 * b.m3 + a.m9 * b.m7 + a.m10 * b.m11 + a.m11,
    };

    /// <summary>Inverse of a rigid transform: transposed rotation, rotated negative translation.</summary>
    public static HmdMatrix34_t Inverse(in HmdMatrix34_t m) => new()
    {
        m0 = m.m0, m1 = m.m4, m2 = m.m8, m3 = -(m.m0 * m.m3 + m.m4 * m.m7 + m.m8 * m.m11),
        m4 = m.m1, m5 = m.m5, m6 = m.m9, m7 = -(m.m1 * m.m3 + m.m5 * m.m7 + m.m9 * m.m11),
        m8 = m.m2, m9 = m.m6, m10 = m.m10, m11 = -(m.m2 * m.m3 + m.m6 * m.m7 + m.m10 * m.m11),
    };

    /// <summary>Mirror left/right (reflect across the controller's YZ plane), to use a left-hand placement on the right hand.</summary>
    public static HmdMatrix34_t MirrorX(in HmdMatrix34_t m) => new()
    {
        m0 = m.m0, m1 = -m.m1, m2 = -m.m2, m3 = -m.m3,
        m4 = -m.m4, m5 = m.m5, m6 = m.m6, m7 = m.m7,
        m8 = -m.m8, m9 = m.m9, m10 = m.m10, m11 = m.m11,
    };

    /// <summary>Rotation about x by <paramref name="degrees"/>, then translation.</summary>
    public static HmdMatrix34_t FromTiltAndOffset(float degrees, float x, float y, float z)
    {
        double a = degrees * Math.PI / 180;
        float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
        return new HmdMatrix34_t { m0 = 1, m3 = x, m5 = c, m6 = -s, m7 = y, m9 = s, m10 = c, m11 = z };
    }

    public static float Distance(in HmdMatrix34_t a, in HmdMatrix34_t b)
    {
        float dx = a.m3 - b.m3, dy = a.m7 - b.m7, dz = a.m11 - b.m11;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public static float[] ToArray(in HmdMatrix34_t m) =>
        [m.m0, m.m1, m.m2, m.m3, m.m4, m.m5, m.m6, m.m7, m.m8, m.m9, m.m10, m.m11];

    public static HmdMatrix34_t FromArray(IReadOnlyList<float> a) => new()
    {
        m0 = a[0], m1 = a[1], m2 = a[2], m3 = a[3],
        m4 = a[4], m5 = a[5], m6 = a[6], m7 = a[7],
        m8 = a[8], m9 = a[9], m10 = a[10], m11 = a[11],
    };
}
