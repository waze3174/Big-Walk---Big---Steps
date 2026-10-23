

namespace BigSteps.Sim;

// unity AnimationCurve evaluation (cubic hermite, non-weighted) for the curves measured from baby steps
public class Curve
{
    readonly float[] t, v, inT, outT;

    // keys as (time, value, inTangent, outTangent)
    public Curve(params float[] k)
    {
        int n = k.Length / 4;
        t = new float[n]; v = new float[n]; inT = new float[n]; outT = new float[n];
        for (int i = 0; i < n; i++) { t[i] = k[i * 4]; v[i] = k[i * 4 + 1]; inT[i] = k[i * 4 + 2]; outT[i] = k[i * 4 + 3]; }
    }

    public float Evaluate(float x)
    {
        int n = t.Length;
        if (x <= t[0]) return v[0];
        if (x >= t[n - 1]) return v[n - 1];
        int i = 0;
        while (x > t[i + 1]) i++;
        float dt = t[i + 1] - t[i];
        float s = (x - t[i]) / dt;
        float s2 = s * s, s3 = s2 * s;
        float h00 = 2 * s3 - 3 * s2 + 1, h10 = s3 - 2 * s2 + s, h01 = -2 * s3 + 3 * s2, h11 = s3 - s2;
        return h00 * v[i] + h10 * dt * outT[i] + h01 * v[i + 1] + h11 * dt * inT[i + 1];
    }
}
