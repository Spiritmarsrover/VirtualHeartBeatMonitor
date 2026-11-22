namespace HeartBeat.Core;

public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public Vec3 Normalized() { double l = Length; return l > 0 ? this / l : Zero; }
    public double Dot(in Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;
    public double this[int axis] => axis switch { 0 => X, 1 => Y, _ => Z };

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);
    public static Vec3 Lerp(in Vec3 a, in Vec3 b, double t) => a + (b - a) * t;
}

/// <summary>One raw IMU sample. Time in seconds (device clock), accel in m/s², gyro in rad/s.</summary>
public readonly record struct ImuSample(double Time, Vec3 Accel, Vec3 Gyro);

/// <summary>Where a device sits on the body; decides which estimators it feeds.</summary>
public enum DeviceRole { Head, Hand, Chest, Other }
