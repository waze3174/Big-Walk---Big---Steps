using System;

namespace BigSteps.Sim;

// self-contained vector math so the sim runs the same in big walk and in the offline test bench
public struct V3
{
    public float x, y, z;
    public V3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

    public static readonly V3 zero = new(0, 0, 0), up = new(0, 1, 0), down = new(0, -1, 0), forward = new(0, 0, 1);

    public static V3 operator +(V3 a, V3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
    public static V3 operator -(V3 a, V3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
    public static V3 operator -(V3 a) => new(-a.x, -a.y, -a.z);
    public static V3 operator *(V3 a, float s) => new(a.x * s, a.y * s, a.z * s);
    public static V3 operator *(float s, V3 a) => new(a.x * s, a.y * s, a.z * s);
    public static V3 operator /(V3 a, float s) => new(a.x / s, a.y / s, a.z / s);

    public float sqrMagnitude => x * x + y * y + z * z;
    public float magnitude => MathF.Sqrt(sqrMagnitude);
    public V3 normalized { get { float m = magnitude; return m > 1e-5f ? this / m : zero; } }
    public V3 XZ => new(x, 0, z);

    public static float Dot(V3 a, V3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    public static V3 Cross(V3 a, V3 b) => new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
    public static float Distance(V3 a, V3 b) => (a - b).magnitude;

    // unity semantics: 0 when either vector is ~zero
    public static float Angle(V3 a, V3 b)
    {
        float d = MathF.Sqrt(a.sqrMagnitude * b.sqrMagnitude);
        if (d < 1e-15f) return 0;
        return MathF.Acos(M.Clamp(Dot(a, b) / d, -1, 1)) * M.Rad2Deg;
    }

    public static V3 Project(V3 v, V3 onto)
    {
        float s = onto.sqrMagnitude;
        return s < 1e-15f ? zero : onto * (Dot(v, onto) / s);
    }

    public static V3 ClampMagnitude(V3 v, float max)
    {
        float m = v.sqrMagnitude;
        return m > max * max ? v.normalized * max : v;
    }

    public static V3 Lerp(V3 a, V3 b, float t) { t = M.Clamp01(t); return a + (b - a) * t; }

    public static V3 MoveTowards(V3 a, V3 b, float maxDelta)
    {
        V3 d = b - a; float m = d.magnitude;
        return m <= maxDelta || m == 0 ? b : a + d / m * maxDelta;
    }

    // rotate around world up by yaw degrees (unity: Quaternion.Euler(0, yaw, 0) * v)
    public static V3 Yaw(float yawDeg, V3 v)
    {
        float r = yawDeg * M.Deg2Rad, c = MathF.Cos(r), s = MathF.Sin(r);
        return new(v.x * c + v.z * s, v.y, -v.x * s + v.z * c);
    }

    public static float YawOf(V3 v) => MathF.Atan2(v.x, v.z) * M.Rad2Deg;

    public override string ToString() => $"({x:F3}, {y:F3}, {z:F3})";
}

public static class M
{
    public const float Deg2Rad = MathF.PI / 180f, Rad2Deg = 180f / MathF.PI;
    public static float Clamp(float v, float a, float b) => v < a ? a : v > b ? b : v;
    public static float Clamp01(float v) => Clamp(v, 0, 1);
    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
    public static float MoveTowards(float a, float b, float d) => MathF.Abs(b - a) <= d ? b : a + MathF.Sign(b - a) * d;
    public static float Repeat(float t, float len) => Clamp(t - MathF.Floor(t / len) * len, 0, len);
    public static float DeltaAngle(float a, float b) { float d = Repeat(b - a, 360); return d > 180 ? d - 360 : d; }
    public static float MoveTowardsAngle(float a, float b, float d) { float da = DeltaAngle(a, b); return -d < da && da < d ? b : MoveTowards(a, a + da, d); }

    // baby steps' Utils.RemapRangeClamped(v, inMin, inMax, outMin, outMax)
    public static float RemapClamped(float v, float a, float b, float c, float d)
    {
        v = Clamp(v, MathF.Min(a, b), MathF.Max(a, b));
        return b - a == 0 ? d : (v - a) * (d - c) / (b - a) + c;
    }

    // baby steps' ClampAngDist: keep angle within maxDist degrees of center
    public static float ClampAngDist(float ang, float center, float maxDist)
    {
        float d = DeltaAngle(center, ang);
        return d > maxDist ? center + maxDist : d < -maxDist ? center - maxDist : ang;
    }
}
