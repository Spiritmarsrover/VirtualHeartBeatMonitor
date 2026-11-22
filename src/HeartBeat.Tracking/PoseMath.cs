using Valve.VR;

namespace HeartBeat.Tracking;

public static class PoseMath
{
    public static (double X, double Y, double Z) Position(in HmdMatrix34_t m) => (m.m3, m.m7, m.m11);

    /// <summary>Rotation part of a 3x4 row-major pose matrix as a unit quaternion (w, x, y, z).</summary>
    public static (double W, double X, double Y, double Z) Rotation(in HmdMatrix34_t m)
    {
        double r00 = m.m0, r01 = m.m1, r02 = m.m2;
        double r10 = m.m4, r11 = m.m5, r12 = m.m6;
        double r20 = m.m8, r21 = m.m9, r22 = m.m10;
        double trace = r00 + r11 + r22;
        double w, x, y, z;

        if (trace > 0)
        {
            double s = Math.Sqrt(trace + 1.0) * 2;
            w = 0.25 * s;
            x = (r21 - r12) / s;
            y = (r02 - r20) / s;
            z = (r10 - r01) / s;
        }
        else if (r00 > r11 && r00 > r22)
        {
            double s = Math.Sqrt(1.0 + r00 - r11 - r22) * 2;
            w = (r21 - r12) / s;
            x = 0.25 * s;
            y = (r01 + r10) / s;
            z = (r02 + r20) / s;
        }
        else if (r11 > r22)
        {
            double s = Math.Sqrt(1.0 + r11 - r00 - r22) * 2;
            w = (r02 - r20) / s;
            x = (r01 + r10) / s;
            y = 0.25 * s;
            z = (r12 + r21) / s;
        }
        else
        {
            double s = Math.Sqrt(1.0 + r22 - r00 - r11) * 2;
            w = (r10 - r01) / s;
            x = (r02 + r20) / s;
            y = (r12 + r21) / s;
            z = 0.25 * s;
        }

        double n = Math.Sqrt(w * w + x * x + y * y + z * z);
        return (w / n, x / n, y / n, z / n);
    }

    public static bool BitEqual(in HmdVector3_t a, in HmdVector3_t b) =>
        a.v0.Equals(b.v0) && a.v1.Equals(b.v1) && a.v2.Equals(b.v2);

    public static bool BitEqual(in HmdMatrix34_t a, in HmdMatrix34_t b) =>
        a.m0.Equals(b.m0) && a.m1.Equals(b.m1) && a.m2.Equals(b.m2) && a.m3.Equals(b.m3) &&
        a.m4.Equals(b.m4) && a.m5.Equals(b.m5) && a.m6.Equals(b.m6) && a.m7.Equals(b.m7) &&
        a.m8.Equals(b.m8) && a.m9.Equals(b.m9) && a.m10.Equals(b.m10) && a.m11.Equals(b.m11);
}
