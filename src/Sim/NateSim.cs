using System;
using System.Collections.Generic;

namespace BigSteps.Sim;

// what the world looks like under a point (big walk raycasts in game, a flat plane in the test bench)
public struct GroundHit
{
    public bool hit;
    public V3 point, normal;
    public float traction;    // Surface.GetTractionCoef
    public float quadMult;    // Surface.GetQuadraticMult
    public float stepSpeed;   // tall grass / mud drag on a lifted foot (baby steps: bushes/grass)
    public string name;
}

public interface IWorld
{
    GroundHit Probe(V3 from, float down);
    // straight ray for walls in the way of a swinging foot (worlds without walls keep the default)
    bool Cast(V3 from, V3 dir, float dist, out float hitDist, out V3 normal) { hitDist = 0; normal = V3.up; return false; }
}

public class Foot
{
    public int i;
    public V3 pos, pivot, prevPos, plantNormal = V3.up;
    public V3 slipVel, stickedMove, appliedAccel, lastAppliedTorque;
    public float dbgTan, dbgFric; public V3 dbgPush;
    public bool planted = true, prevPlanted = true, isLifting, wantsToLift, freshPlantInput, hitLiftedConstraint, plantedSinceStoodUp = true;
    public float plantedTime, liftedTime, trigPressedTimer, curDropSpd, slipTimer, fatigue, maxExert = 0.001f;
    public float traction = 0.91f, quadMult = 1, stepSpeed = 1, yaw;
    public float dbgLim, dbgFall, dbgReach, dbgF, dbgAng; public V3 dbgToSup, dbgFootDir, dbgL, dbgWant, dbgDw, dbgW;
    public GroundHit ground;
}

// a reimplementation of how baby steps' nate walks. index 0 = right foot (RTrig), 1 = left (LTrig).
// the hip is a point we integrate ourselves like nate's hipPivot; the game glue makes the body follow it
public class NateSim
{
    public IWorld World;
    public float S = 1;                       // nate -> character length scale

    public readonly Foot[] Feet = { new() { i = 0 }, new() { i = 1 } };
    public readonly float[] Trigs = new float[2], PrevTrigs = new float[2];
    readonly int[] framesTriggerPressed = new int[2];
    public V3 LStick;                         // camera relative, curve applied (GetInput output)
    public float CamYaw;

    public V3 HipPos, HipVel;
    public float HipYaw, HipAng, hipYawVel;
    public V3 TargetPt, TargetPtNoVel, BalancePt;
    public bool Momentuming, PlantedSinceStick, TryingToCrouch;
    public V3 MomentumStick;
    public float LeanTimer, MagicFallOffTimer, OffBalanceDeathTimer, DeathTimer, TimeWithBothFeetInAir, TimeSinceStandUp = 1;
    public bool HipIsColliding;               // set by the glue when the body hit something this step
    public float HipImpact;
    public V3 DbgAccSum, DbgAccT, DbgStick;
    public float DbgStrafeMove, DbgReachMove;
    public bool Dead;
    // baby steps: grip x1.5 on flat ground fading to x1 at 30 deg. big walk is full of 30-50 deg slopes you're meant
    // to climb, so the plugin keeps the x1.5 on steep ground (1 = exact baby steps)
    public float SteepGrip = 1f;
    public bool CrossPrevention = true;       // baby steps' StrafeConstrain (an option in the mod, off by default)
    public string DeadReason = "";

    float L => T.LegLength * S;
    V3 Fwd => V3.Yaw(HipYaw, V3.forward);
    V3 Right => V3.Yaw(HipYaw + 90, V3.forward);
    V3 Hips(int i) => HipPos + Right * ((i == 0 ? 1 : -1) * T.HipHalfWidth * S);
    Foot Other(Foot f) => Feet[1 - f.i];
    bool BothPlanted => Feet[0].planted && Feet[1].planted;
    V3 COM => HipPos;

    // footCenter (0,-0.037,0.07) in the ankle target's space; footSize z 0.22 -> heel 0.11 behind center
    V3 FootFwd(Foot f) => V3.Yaw(f.yaw, V3.forward);
    V3 Center(Foot f) => f.pos + FootFwd(f) * (0.07f * S);
    V3 Heel(Foot f) => Center(f) - FootFwd(f) * (T.FootLength * 0.5f * S);

    // ---------------- setup ----------------

    // moving ground: the glue moves everything nate has pinned in the world along with it (the hip rides the body)
    public void Carry(Func<V3, V3> map, float dYaw)
    {
        foreach (var f in Feet)
        {
            f.pos = map(f.pos); f.pivot = map(f.pivot); f.prevPos = map(f.prevPos);
            if (f.ground.hit) f.ground.point = map(f.ground.point);
            f.yaw += dYaw;
        }
        TargetPt = map(TargetPt); TargetPtNoVel = map(TargetPtNoVel); BalancePt = map(BalancePt);
        HipYaw += dYaw;
    }

    public void Reset(V3 hip, float yaw)
    {
        HipPos = hip; HipVel = V3.zero;
        HipYaw = HipAng = yaw; hipYawVel = 0;
        foreach (var f in Feet)
        {
            var at = Hips(f.i);
            f.ground = World.Probe(at + V3.up * 0.5f * S, 4f);
            f.pos = f.prevPos = f.ground.hit ? f.ground.point : at - V3.up * L;
            f.plantNormal = f.ground.hit ? f.ground.normal : V3.up;
            f.planted = f.prevPlanted = true;
            f.isLifting = f.wantsToLift = false;
            f.slipVel = f.stickedMove = f.appliedAccel = f.lastAppliedTorque = V3.zero;
            f.curDropSpd = f.fatigue = f.slipTimer = 0;
            f.yaw = yaw;
            f.pivot = Heel(f);
            f.plantedSinceStoodUp = true;
            ApplySurface(f, f.ground);
        }
        Trigs[0] = Trigs[1] = PrevTrigs[0] = PrevTrigs[1] = 0;
        Momentuming = PlantedSinceStick = false;
        LeanTimer = MagicFallOffTimer = OffBalanceDeathTimer = DeathTimer = TimeWithBothFeetInAir = 0;
        TimeSinceStandUp = 0;
        Dead = false; DeadReason = "";
    }

    // ---------------- input ----------------

    // raw stick (x right, y forward, -1..1), raw trigger axes 0..1 -> nate's processed values
    public static V3 ProcessStick(float x, float y, bool keyboard, bool slowFoot, float camYaw)
    {
        V3 s = new(x, 0, y);
        if (keyboard && s.sqrMagnitude > 0) s = s.normalized;
        if (slowFoot && s.sqrMagnitude > 0) s = s.normalized * MathF.Min(s.magnitude, 0.5f);
        if (s.magnitude < 0.2f) return V3.zero;
        float m = MathF.Min(s.magnitude, 1);
        return V3.Yaw(camYaw, s).normalized * T.LStickCurve.Evaluate(m);
    }

    public static float ProcessTrigger(float raw) => M.Clamp01((raw - 0.05f) / 0.95f);

    public void SetInput(float trigR, float trigL, V3 stick, float camYaw)
    {
        PrevTrigs[0] = Trigs[0]; PrevTrigs[1] = Trigs[1];
        Trigs[0] = trigR; Trigs[1] = trigL;
        LStick = stick;
        CamYaw = camYaw;
        for (int i = 0; i < 2; i++)
        {
            framesTriggerPressed[i] = Trigs[i] > 0 ? framesTriggerPressed[i] + 1 : 0;
            var f = Feet[i];
            // just stood up: a held trigger doesn't count until the foot has planted once
            if (TimeSinceStandUp < 0.15f && !f.plantedSinceStoodUp) Trigs[i] = 0;
            if (Trigs[i] <= 0 && PrevTrigs[i] > 0) f.freshPlantInput = true;
            if (Trigs[i] > 0 && PrevTrigs[i] <= 0) { f.freshPlantInput = false; f.trigPressedTimer = 0; }
        }
    }

    // ---------------- main step ----------------

    public void Step(float dt)
    {
        if (Dead) return;
        TimeSinceStandUp += dt;
        GroundedCheck(Feet[0], dt);
        GroundedCheck(Feet[1], dt);
        TryingToCrouch = Trigs[0] > 0 && Trigs[1] > 0 && BothPlanted;
        WhoWantsToLift();
        HipForces(dt);
        TorsoRotation(dt);
        foreach (var f in Feet)
        {
            f.prevPos = f.pos;
            GetIsLifting(f, dt);
            if (!f.planted || f.isLifting) MoveLiftedFoot(f, dt);
            else f.stickedMove = V3.zero;
        }
        DeadCheck(dt);
        if (LStick.sqrMagnitude == 0) PlantedSinceStick = false;
        TimeWithBothFeetInAir = !Feet[0].planted && !Feet[1].planted ? TimeWithBothFeetInAir + dt : 0;
        foreach (var f in Feet)
        {
            if (f.planted && !f.prevPlanted && TimeSinceStandUp > 0.05f && LStick.sqrMagnitude > 0) PlantedSinceStick = true;
            f.prevPlanted = f.planted;
        }
    }

    // ---------------- ground (GroundedCheck / GetGrounded) ----------------

    void ApplySurface(Foot f, GroundHit g)
    {
        if (!g.hit) return;
        float slope = V3.Angle(g.normal, V3.up);
        f.traction = g.traction * M.RemapClamped(slope, 0, 30, 1.5f, SteepGrip);
        f.quadMult = g.quadMult;
        f.stepSpeed = g.stepSpeed;
    }

    bool GetGrounded(Foot f)
    {
        f.ground = World.Probe(f.pos + V3.up * 0.3f * S, 0.6f * S);
        if (!f.ground.hit) return false;
        float dist = f.pos.y - f.ground.point.y;
        float thresh = (f.planted && !f.isLifting ? 0.075f : 0.035f) * S;
        if (dist >= thresh) return false;
        if (f.ground.normal.y < 0.1f) return false;
        f.plantNormal = f.ground.normal;
        ApplySurface(f, f.ground);
        return true;
    }

    void GroundedCheck(Foot f, float dt)
    {
        var other = Other(f);
        if (!GetGrounded(f))
        {
            if (f.planted)
            {
                f.freshPlantInput = false; f.plantedTime = 0; f.isLifting = false;
            }
            f.planted = false;
            f.liftedTime += dt;
            if (PlantedSinceStick) { MomentumStick = LStick; Momentuming = true; }
            return;
        }
        if (f.planted) { f.plantedTime += dt; return; }
        if (f.wantsToLift && !TryingToCrouch && !(Trigs[0] > 0 && Trigs[1] > 0))
        {
            if (!other.planted || !WantsToSoftPlant(f) || V3.Angle(f.plantNormal, V3.up) >= 50) { f.plantedTime += dt; return; }
        }
        // plant
        f.planted = true;
        f.curDropSpd = 0;
        f.liftedTime = 0;
        f.plantedSinceStoodUp = true;
        f.pos = new V3(f.pos.x, f.ground.point.y, f.pos.z);
        f.pivot = Heel(f);
        f.plantedTime += dt;
    }

    void WhoWantsToLift()
    {
        Feet[0].wantsToLift = Feet[1].wantsToLift = false;
        if (MathF.Abs(Trigs[0] - Trigs[1]) > 0.25f && (Feet[0].pos - Feet[1].pos).magnitude < 0.5f * S)
        {
            var k = Feet[Trigs[0] > Trigs[1] ? 0 : 1];
            if (!k.planted || !WantsToSoftPlant(k)) k.wantsToLift = true;
        }
        foreach (var f in Feet)
        {
            if (f.planted && WantsToSoftPlant(f)) continue;
            if (Trigs[f.i] > 0 && Trigs[1 - f.i] == 0) f.wantsToLift = true;
            if (!f.planted && Trigs[f.i] > 0) f.wantsToLift = true;
        }
    }

    void GetIsLifting(Foot f, float dt)
    {
        if (Trigs[f.i] == 0 || !f.wantsToLift || (f.planted && WantsToSoftPlant(f))) { f.isLifting = false; return; }
        f.trigPressedTimer += dt;
        if (f.isLifting && Trigs[f.i] > 0) return;
        if (!f.planted) return;
        var other = Other(f);
        float dThis = (COM - Center(f)).XZ.magnitude, dOther = (COM - Center(other)).XZ.magnitude;
        float weightOnThis = other.planted ? 1 - dThis / MathF.Max(1e-4f, dOther + dThis) : 1;
        float thresh = T.PushOffWeightPercentThresh;
        if (LStick.sqrMagnitude > 0 && V3.Dot(LStick.normalized, (Hips(f.i) - f.pivot).XZ.normalized) < -0.5f)
            thresh = T.PushOffWeightThreshOppositeDir;
        bool comOutside = !InsideSupport(COM) && V3.Dot((COM - Center(f)).XZ, (COM - Center(other)).XZ) > 0;
        bool weightOk = f.trigPressedTimer > T.MaxTimeBeforeLift || (TargetPt - HipPos).XZ.magnitude < 0.09f * S || weightOnThis < thresh;
        if (!comOutside && !weightOk) { f.isLifting = false; return; }
        if (!f.isLifting && other.planted) DoPushOff(f);
        f.isLifting = true;
    }

    // DoPushOffForce: lifting the lower foot to step up onto the other pushes the body up/over it
    void DoPushOff(Foot f)
    {
        var o = Other(f);
        if (f.plantedTime < 0.1f) return;
        float dy = o.pivot.y - f.pivot.y;
        V3 toward = new V3(o.pos.x, o.pos.y + L, o.pos.z) - COM;
        V3 dir = toward.normalized;
        if (dir.y <= 0) dir = new V3(dir.x, 0, dir.z).normalized;
        float mag = MathF.Sqrt(MathF.Abs(dy) * T.PushOffStrength * T.Grav) * MathF.Sign(dy) + toward.XZ.magnitude * 0;
        if (mag <= 0) return;
        V3 push = dir * mag;
        float cap = MathF.Min(0.5f, f.traction * 0.5f);
        HipVel += V3.ClampMagnitude(push, cap * 3);
    }

    // ---------------- lifted foot ----------------

    float GetFootTargetY(Foot f, float trig)
    {
        var hip = Hips(f.i);
        float behind = M.Clamp01(V3.Dot(-Fwd, f.pos - hip) / (L * 0.5f));
        float maxH = M.Lerp(T.MaxLegHeight, T.MaxLegHeightBehind, behind) * S;
        V3 c = hip + V3.up * T.SphereOffset * S;
        V3 dir = (f.pos - c).normalized;
        float y = c.y + dir.y * ((1 - trig) * (L - maxH) + maxH + T.SphereOffset * S);
        return MathF.Min(y, HipPos.y - T.FootMinYToHip * S);
    }

    float GetMaxFootHeight(Foot f)
    {
        var hip = Hips(f.i);
        float behind = M.Clamp01(V3.Dot(-Fwd, f.pos - hip) / (L * 0.5f));
        float maxH = M.Lerp(T.MaxLegHeight, T.MaxLegHeightBehind, behind) * S;
        V3 c = hip + V3.up * T.SphereOffset * S;
        return c.y + (f.pos - c).normalized.y * (maxH + T.SphereOffset * S);
    }

    bool WantsToSoftPlant(Foot f)
    {
        float ty = GetFootTargetY(f, Trigs[f.i]);
        float fy = f.planted ? f.pivot.y + 0.075f * S : f.pos.y;
        if ((L + Other(f).pos.y - HipPos.y) + ty - 0.05f * S < fy + 0.05f * S)
            return Trigs[f.i] < 0.925f - (Momentuming ? 0.25f : 0);
        return false;
    }

    float TriggerMove(Foot f, float dt)
    {
        float y = f.pos.y, t = Trigs[f.i];
        if (t > 0.05f) return M.MoveTowards(y, GetFootTargetY(f, t), dt * 4 * S);
        if (!Other(f).planted)
            return M.MoveTowards(y, HipPos.y - L, dt * T.FootPlantSpeed * S) + dt * HipVel.y;
        f.curDropSpd = f.freshPlantInput ? T.FootPlantSpeed * S : M.MoveTowards(f.curDropSpd, T.FootPlantSpeed * S, dt * T.Grav);
        if (HipVel.y < 0) y += dt * HipVel.y;
        return y - dt * f.curDropSpd;
    }

    V3 StickMove(Foot f, float dt, V3 hipDelta)
    {
        // released: the foot stays put in xz while it drops (only a ridden rigidbody carries it)
        if (Trigs[f.i] == 0) { f.stickedMove = V3.zero; return f.pos; }
        V3 basePos = f.pos + hipDelta;      // held up: the foot travels with the hip
        V3 d = (f.pos - Hips(f.i)).XZ;
        float outward = 1;
        if (V3.Dot(LStick, d) > 0)
        {
            float c = V3.Dot(d.normalized, LStick.normalized);
            outward = T.FootSpeedCurve.Evaluate(d.magnitude / S) * c + (1 - c);
        }
        float a = M.Clamp(M.Repeat(V3.Angle(Fwd, LStick.normalized), 180), 0, 180);
        float side = Momentuming ? M.RemapClamped(90 - MathF.Abs(a - 90), 20, 80, 1.1f, 0.8f) : 0.75f;
        float spd = MathF.Min(LStick.magnitude * T.FootSpeed, T.FootSpeed) * S;
        V3 p = basePos + LStick.normalized * (dt * spd * side * f.stepSpeed * outward);
        f.stickedMove = p - basePos;
        return p;
    }

    void MoveLiftedFoot(Foot f, float dt)
    {
        V3 hipDelta = HipVel * dt;
        float y = TriggerMove(f, dt);
        V3 xz = StickMove(f, dt, hipDelta);
        V3 p = new(xz.x + T.LiftedSlipMult * f.slipVel.x * dt, y + T.LiftedSlipMult * f.slipVel.y * dt, xz.z + T.LiftedSlipMult * f.slipVel.z * dt);
        V3 before = p;

        // LiftedFootRotation: a lifted foot turns with the hips (toes splay out a little)
        f.yaw = M.MoveTowardsAngle(f.yaw, HipYaw, 360 * dt);

        if (CrossPrevention) p = StrafeConstrain(f, p);
        V3 afterStrafe = p;
        var hip = Hips(f.i);
        p.y = MathF.Min(p.y, MathF.Min(GetMaxFootHeight(f), hip.y));
        p = LegReach(f, p, hip + hipDelta);
        DbgStrafeMove = (afterStrafe - before).XZ.magnitude; DbgReachMove = (p - afterStrafe).XZ.magnitude;

        // hit the lifted-foot constraint while still pushing the stick away from the hip -> momentum may start
        if (LStick.sqrMagnitude > 0 && V3.Dot(LStick.normalized, (f.pos - hip).XZ.normalized) > 0.6f)
            f.hitLiftedConstraint = (p - before).XZ.magnitude > 0.01f * S || (hip - f.pos).XZ.magnitude > T.MaxFootMovingDistFromHip / 2.8f * S;
        else f.hitLiftedConstraint = false;

        f.pos = FootCollide(f.pos, p);
    }

    // FootCollisionContinuous: baby steps sweeps the foot box and pushes it out of whatever it overlaps
    // (ComputePenetration), so a wall pushes the foot back sideways and it only gets onto a ledge it is already
    // above. here: a wall (steeper than 60 deg) in the way stops the swing, slopes and small lips lift the foot
    V3 FootCollide(V3 from, V3 to)
    {
        V3 mv = (to - from).XZ;
        float len = mv.magnitude, pad = 0.06f * S;
        if (len > 1e-5f)
        {
            V3 dir = mv / len;
            V3 o = new(from.x, MathF.Min(from.y, to.y) + 0.03f * S, from.z);
            if (World.Cast(o, dir, len + pad, out float d, out V3 n) && n.y < 0.5f)
            {
                float ok = MathF.Max(0, d - pad);
                to = new V3(from.x + dir.x * ok, to.y, from.z + dir.z * ok);
            }
        }
        var g = World.Probe(new V3(to.x, MathF.Max(to.y, from.y) + 0.3f * S, to.z), 1.5f * S);
        if (g.hit && to.y < g.point.y)
        {
            // ground far above the foot = we are inside something the ray missed: don't climb it
            if (g.point.y - to.y > 0.12f * S && (to - from).XZ.sqrMagnitude > 1e-8f) return FootCollide(from, new V3(from.x, to.y, from.z));
            to.y = g.point.y;
        }
        return to;
    }

    // LiftedFootConstrain's leg-length part: a released foot reaching for the ground drags the hip toward it,
    // otherwise the foot is held on the leg-length sphere
    V3 LegReach(Foot f, V3 p, V3 hipPred)
    {
        V3 toHip = hipPred - p;
        float len = L - 0.01f * S;
        if (toHip.magnitude <= len) return p;
        var other = Other(f);
        bool hold = Trigs[f.i] > 0 || Momentuming || V3.Dot(-toHip, HipVel) < 0 || (LStick.sqrMagnitude > 0 && V3.Dot(-toHip, LStick) < 0) || !other.planted;
        if (!hold && other.pos.y - 0.3f * S < p.y)
        {
            V3 e = toHip.normalized;
            float upness = V3.Dot(e, V3.up);
            float over = toHip.magnitude - L;
            if (over > 0 && upness > 0.25f)
            {
                float move = MathF.Min(over * 0.5f, over / upness * 0.3f);
                HipPos -= e * move;
                hipPred -= e * move;
                toHip = hipPred - p;
                over = toHip.magnitude - L;
                float up2 = V3.Dot(toHip.normalized, V3.up);
                if (over <= 0.1f * S)
                {
                    if (over > 0 && up2 > 0.1f) p.y = MathF.Min(p.y + over / up2, MathF.Max(p.y, HipPos.y));
                    return p;
                }
            }
        }
        return hipPred + (p - hipPred).normalized * len;
    }

    // keeps a swinging foot from crossing under the body (StrafeConstrain + PlaneCurveConstrain):
    // allowed lateral offset from the hip line follows footCrossPreventionCurve over the forward offset, scaled 0.45,
    // measured from a plane 0.45 inside the hip; soft push of half the violation. hard plane vs the other foot only
    // when the stick points across the body (> 50 deg)
    V3 StrafeConstrain(Foot f, V3 p)
    {
        float side = f.i == 0 ? 1 : -1;
        V3 outward = V3.Yaw(HipAng + 90, V3.forward) * side;
        V3 fwd = V3.Yaw(HipAng, V3.forward);
        V3 rel = p - HipPos;
        float fwdOff = MathF.Abs(V3.Dot(rel, fwd));
        float allowed = T.FootCrossPreventionCurve.Evaluate(fwdOff / (0.35f * S)) * 0.45f * S - 0.45f * S;
        float lat = V3.Dot(rel, outward);
        if (lat < allowed) p += outward * ((allowed - lat) * T.SoftSideStepConstraintAmount);
        var other = Other(f);
        if (other.planted && LStick.sqrMagnitude > 0 && V3.Dot(LStick.normalized, -outward) > MathF.Cos(T.SideConstrainStickThresh * M.Deg2Rad))
        {
            float vs = V3.Dot(p - other.pos, outward);
            float min = T.FootCrossPreventionCurve.Evaluate(MathF.Abs(V3.Dot(p - other.pos, fwd)) / (0.35f * S)) * T.FootDontCrossDist * S;
            if (vs < min) p += outward * (min - vs);
        }
        return p;
    }

    // ---------------- support polygon ----------------

    readonly List<(float x, float z)> hull = new();
    readonly List<(float x, float z)> pts = new();

    void BuildSupport()
    {
        pts.Clear();
        foreach (var f in Feet)
        {
            if (!f.planted) continue;
            V3 fw = V3.Yaw(f.yaw, V3.forward) * (T.FootLength * 0.5f * S), rt = V3.Yaw(f.yaw + 90, V3.forward) * (T.FootWidth * 0.5f * S);
            var ce = Center(f);
            foreach (var c in new[] { ce + fw + rt, ce + fw - rt, ce - fw + rt, ce - fw - rt }) pts.Add((c.x, c.z));
        }
        hull.Clear();
        if (pts.Count == 0) return;
        pts.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.z.CompareTo(b.z));
        static float Cross((float x, float z) o, (float x, float z) a, (float x, float z) b) => (a.x - o.x) * (b.z - o.z) - (a.z - o.z) * (b.x - o.x);
        var h = new List<(float x, float z)>();
        foreach (var q in pts) { while (h.Count >= 2 && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
        int lower = h.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--) { var q = pts[i]; while (h.Count >= lower && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
        if (h.Count > 1) h.RemoveAt(h.Count - 1);
        hull.AddRange(h);
    }

    bool InsideSupport(V3 w)
    {
        if (hull.Count < 3) return false;
        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i]; var b = hull[(i + 1) % hull.Count];
            if ((b.x - a.x) * (w.z - a.z) - (b.z - a.z) * (w.x - a.x) < 0) return false;
        }
        return true;
    }

    float SupportY()
    {
        float y = 0; int n = 0;
        foreach (var f in Feet) if (f.planted) { y += f.pos.y; n++; }
        return n > 0 ? y / n : HipPos.y - L;
    }

    V3 ClosestOnSupport(V3 w)
    {
        if (hull.Count == 0) return w;
        if (InsideSupport(w)) return new V3(w.x, SupportY(), w.z);
        float best = float.MaxValue; V3 bp = w;
        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i]; var b = hull[(i + 1) % hull.Count];
            float abx = b.x - a.x, abz = b.z - a.z, l2 = abx * abx + abz * abz;
            float t = l2 < 1e-10f ? 0 : M.Clamp01(((w.x - a.x) * abx + (w.z - a.z) * abz) / l2);
            float cx = a.x + abx * t, cz = a.z + abz * t;
            float d = (cx - w.x) * (cx - w.x) + (cz - w.z) * (cz - w.z);
            if (d < best) { best = d; bp = new V3(cx, SupportY(), cz); }
        }
        return bp;
    }

    // ---------------- hip target (GetTargetPt / GetHipTargetFromStickAndBalance / GetDesHipHeight) ----------------

    V3 FootBalancePt(Foot f) => f.pos + (HipPos - Hips(f.i)) * T.BalanceHipWidth;

    void GetTargetPt(float dt)
    {
        var a = Feet[0]; var b = Feet[1];
        V3 mid = (a.pos + a.slipVel * 0.2f + b.pos + b.slipVel * 0.2f) * 0.5f;
        bool anyWants = a.wantsToLift || b.wantsToLift;
        bool notBoth = !BothPlanted;
        int stand = a.planted ? 0 : 1;
        bool standWants = notBoth && Feet[stand].wantsToLift;
        V3 bal;
        if (a.wantsToLift && b.wantsToLift && notBoth && LStick.sqrMagnitude == 0) bal = FootBalancePt(Feet[stand]);
        else if (!standWants)
        {
            if (!notBoth || anyWants)
            {
                bal = mid;
                if (!anyWants || TryingToCrouch)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        if (!(Trigs[i] > 0 && Trigs[1 - i] == 0)) continue;
                        var f = Feet[i];
                        float yT = GetFootTargetY(f, 1), y0 = GetFootTargetY(f, 0);
                        float lifted = yT == y0 ? 0 : M.Clamp01((f.pos.y - y0) / (yT - y0));
                        float pressed = lifted == 0 ? 1 : M.Clamp01(Trigs[i] / lifted);
                        float w = M.Clamp01(1 - pressed);
                        bal = V3.Lerp(FootBalancePt(Feet[1 - i]), mid, w);
                        break;
                    }
                }
                else
                {
                    foreach (var f in Feet)
                        if (f.wantsToLift && !Other(f).wantsToLift) bal = FootBalancePt(Other(f));
                }
            }
            // foot dropping onto the ground: lean into where it lands
            else bal = V3.Lerp(FootBalancePt(a), FootBalancePt(b), stand == 0 ? 0.7f : 0.3f);
        }
        else bal = V3.Lerp(FootBalancePt(a), FootBalancePt(b), stand == 0 ? 0.9f : 0.1f);
        BalancePt = bal;

        if (Momentuming && (LStick.magnitude < 0.75f || V3.Dot(LStick.normalized, MomentumStick.normalized) < 0.5f)) Momentuming = false;
        TargetPtNoVel = bal;
        TargetPt = HipTargetFromStick(bal);
        LeanTimer = LStick.sqrMagnitude > 0 ? LeanTimer + dt : 0;
        TargetPt = new V3(TargetPt.x, DesHipHeight(), TargetPt.z);
    }

    V3 HipTargetFromStick(V3 bal)
    {
        bool bothPlanted = BothPlanted;
        bool anyWants = Feet[0].wantsToLift || Feet[1].wantsToLift;
        // momentum: the lifted foot hit its limit while the stick keeps pushing that way
        var lifted = Feet[Feet[0].planted ? 1 : 0];
        if (!Momentuming && lifted.hitLiftedConstraint && LStick.magnitude > 0.35f &&
            V3.Dot((lifted.pos - Hips(lifted.i)).XZ.normalized, LStick.normalized) > 0.7f)
        { MomentumStick = LStick; Momentuming = true; }
        // a foot that wants to lift freezes the lean (the body holds still while you step), unless carrying momentum
        if (anyWants && !Momentuming) return bal;
        if (LStick.sqrMagnitude == 0) return bal;
        V3 n = LStick.normalized;
        V3 basePt = bal;
        float ahead = V3.Dot(n, (HipPos - bal).XZ);
        if (ahead > 0) basePt += n * ahead;
        float lean = bothPlanted ? T.LeanResponseCurve.Evaluate(LeanTimer / T.LeanCurveMaxTime) : 1;
        float strafe = MathF.Abs(V3.Dot(n, Fwd)) * T.StrafeLeanMult + (1 - T.StrafeLeanMult);
        float maxDist = T.MaxTargetDist * S;
        if (!bothPlanted)
        {
            var stand = Feet[0].planted ? Feet[0] : Feet[1];
            float d = M.Clamp((stand.pos - HipPos).XZ.magnitude / S, 0.25f, 1);
            maxDist -= M.RemapClamped(d, 0.3f, 0.75f, 0, T.MaxTargetDist - 0.075f) * S;
        }
        float amt = LStick.magnitude * T.CurStickTargetVel * S * lean * strafe - V3.Dot(HipVel.XZ, n);
        amt = M.Clamp(amt, 0, MathF.Max(0, maxDist));
        V3 tgt = basePt + n * amt;
        float feetDist = (Feet[0].pos - Feet[1].pos).XZ.magnitude;
        if (bothPlanted && feetDist > 0.7f * S)
        {
            V3 off = (tgt - bal).XZ;
            float lim = feetDist * 0.3f;
            if (off.magnitude > lim) tgt = new V3(bal.x, tgt.y, bal.z) + off.normalized * lim;
        }
        // don't squish: keep the target a leg's comfortable length away from each foot
        float desH = DesHipHeight();
        foreach (var f in Feet)
        {
            V3 t = new(tgt.x, desH, tgt.z);
            V3 d = t - f.pos;
            if (d.magnitude < T.DontSquishLength * S) { var p = f.pos + d.normalized * T.DontSquishLength * S; tgt = new V3(p.x, tgt.y, p.z); }
        }
        return tgt;
    }

    static float AngledLegHeight(V3 foot, V3 hip, float len)
    {
        float d = (foot - hip).XZ.magnitude;
        if (len <= d) len = d;
        return MathF.Max(MathF.Sqrt(MathF.Max(0, len * len - d * d)), 0.4f);
    }

    float DesHipHeight()
    {
        float best = HipPos.y + 1;
        float len = L + (BothPlanted ? T.BonusLegLength : T.BonusLegLengthOneFoot) * S;
        float behind = -999;
        foreach (var f in Feet) if (f.planted) behind = MathF.Max(behind, V3.Dot(FootFwd(f), f.pivot - Center(f)));
        if (behind > -999 && behind < 0) len += behind;
        foreach (var f in Feet)
        {
            var o = Other(f);
            V3 ht = TargetPtNoVel + (Hips(f.i) - HipPos);
            if (!f.planted)
            {
                if (f.wantsToLift) continue;
                var g = World.Probe(f.pos + V3.up * 0.1f * S, 40);
                if (g.hit)
                {
                    float y = g.point.y + AngledLegHeight(f.pos, ht, len) * 1;
                    y = MathF.Max(y, o.pivot.y + 0.5f * S);
                    best = MathF.Min(best, y);
                }
                else best = f.pos.y + 0.5f * S;
                continue;
            }
            if (f.wantsToLift && !o.wantsToLift && !TryingToCrouch) continue;
            bool soft = (!o.planted && Trigs[o.i] == 0) || (framesTriggerPressed[f.i] < 10 && LStick.magnitude > 0.1f) || (Trigs[f.i] > 0 && Trigs[o.i] == 0);
            float legLen = soft ? len : (1 - Trigs[f.i]) * (len - 0.57f * S) + 0.57f * S;
            float yy = f.pivot.y + AngledLegHeight(f.pivot, ht, legLen) * 1;
            yy = MathF.Max(yy, f.pivot.y + 0.57f * S);
            best = MathF.Min(best, yy);
        }
        return best;
    }

    // ---------------- hip forces ----------------

    void HipForces(float dt)
    {
        foreach (var f in Feet)
        {
            if (!f.planted) HipVel += f.stickedMove.XZ * (T.LegWeight / T.BodyWeight) * 6;
            if (f.planted) f.pos += f.slipVel * dt;
        }
        HipVel += V3.down * T.Grav * dt;

        BuildSupport();
        GetTargetPt(dt);
        bool both = BothPlanted;
        float legP = both ? T.TwoFootLegP : T.OneFootLegP;
        float legD = both ? T.TwoFootLegD : T.OneFootLegD;

        bool ok = true;
        if (both && !InsideSupport(COM)) ok = (ClosestOnSupport(COM) - COM).XZ.magnitude < 0.05f * S;
        MagicFallOffTimer = ok ? 0 : MagicFallOffTimer + dt;

        // legs as springs pushing the hip toward the target height/point along each leg
        V3 accSum = V3.zero;
        float desH = TargetPt.y;
        foreach (var f in Feet)
        {
            if (!f.planted || f.isLifting) continue;
            float side = f.i == 0 ? 1 : -1;
            V3 hip = Hips(f.i), footP = f.pos;
            float maxA = T.LegVStrengthExtendCurve.Evaluate((hip - footP).magnitude / L) * dt * T.LegVStrength / T.BodyWeight;
            V3 legTop = hip - Right * (side * 0.05f * S);
            if (f.fatigue > 0.95f) legTop += (Other(f).pos - f.pos).normalized * 0.07f * S;
            V3 legDir = (legTop - footP).normalized;
            V3 err = new(TargetPt.x - HipPos.x, desH - HipPos.y, TargetPt.z - HipPos.z);
            V3 acc = V3.Project(-HipVel * (1 - legD), legDir) + V3.Project(err, legDir) * legP * dt;
            V3 inward = (footP - legTop).normalized;
            float vin = MathF.Max(0, V3.Dot(HipVel, inward));
            if ((hip - footP).magnitude < 0.3f * L && maxA < vin)
            {
                var comp = -inward * vin * (both ? 0.5f : 1f);
                accSum += comp; f.appliedAccel = comp;
            }
            else if (acc.y > 0) { acc = V3.ClampMagnitude(acc, maxA); accSum += acc; f.appliedAccel = acc; }
            else f.appliedAccel = V3.zero;
        }

        // leg torque (inverted pendulum around the foot), limited by grip: too much push and the foot slips
        V3 accT = V3.zero;
        float magic = T.MagicFallOff.Evaluate(MagicFallOffTimer / T.MagicFallOffTime);
        foreach (var f in Feet)
        {
            if (!f.planted || f.isLifting)
            {
                f.fatigue = M.MoveTowards(f.fatigue, 0, dt * 0.6f);
                f.slipVel = V3.zero;
                continue;
            }
            V3 hip = Hips(f.i), footP = f.pos;
            V3 l = hip - footP;
            V3 want = (hip - HipPos + TargetPt) - footP;
            V3 w = V3.Cross(l, HipVel) / MathF.Max(1e-4f, l.sqrMagnitude);
            V3 axis = V3.Cross(l, want).normalized;
            float ang = V3.Angle(l, want);
            V3 n = new V3(LStick.z, 0, -LStick.x).normalized;
            if (n.sqrMagnitude > 0 && V3.Dot(n, w.normalized) > 0.2f) w -= n * (V3.Dot(w, n) * T.TorqueStickDotShit);
            V3 dw = w * (T.LegTorqueD - 1) + axis * (ang * M.Deg2Rad * T.LegTorqueP * dt);
            V3 F = V3.Cross(dw, l).normalized * (dw.magnitude * l.sqrMagnitude * T.BodyWeight);
            f.dbgL = l; f.dbgWant = want; f.dbgDw = dw; f.dbgW = w;

            // torque fades as the COM hangs past the support: full up to 13 deg from straight down, gone by 28
            V3 toSupport = ClosestOnSupport(COM) - COM;
            float fall = 1 - M.Clamp01((V3.Angle(V3.down, toSupport) - 13) / 15);
            float lim = magic * T.MaxFootTorque * S * S * fall * T.TorqueFatigueCurve.Evaluate(f.fatigue);
            f.dbgF = F.magnitude; f.dbgFall = fall; f.dbgAng = V3.Angle(V3.down, toSupport); f.dbgToSup = toSupport; f.dbgFootDir = (footP - hip).XZ; f.dbgReach = (footP - hip).magnitude / L;
            if ((footP - hip).magnitude > L + 0.075f * S) lim = 0;
            f.dbgLim = lim;
            F = V3.ClampMagnitude(F, lim);
            f.maxExert = T.MaxFootTorque * S * S * fall * magic;
            f.lastAppliedTorque = F;
            V3 tq = F / MathF.Max(1e-4f, l.magnitude) / T.BodyWeight;

            // traction: force pushed into the ground vs what the surface can hold
            // xz from the torque's xz, but y keeps the torque's vertical part (a leg pushing down grips harder)
            V3 Fg = (new V3(tq.x, tq.y, tq.z) + f.appliedAccel) * (T.BodyWeight / dt);
            Fg = new V3(Fg.x, MathF.Max(Fg.y, T.BodyWeight * 0.5f * T.Grav), Fg.z);
            V3 push = new(-Fg.x * 0.7f, -Fg.y, -Fg.z * 0.7f);
            V3 nrm = f.plantNormal;
            V3 tang = push - nrm * V3.Dot(nrm, push);
            V3 tdir = tang.normalized;
            float tangential = V3.Dot(push, tdir);
            float friction = V3.Dot(-nrm, push) * f.traction;
            f.dbgTan = tangential; f.dbgFric = friction; f.dbgPush = push;
            f.pos -= f.slipVel * dt;
            V3 hipCorr = V3.zero;
            if (tangential > friction)
            {
                float ex = tangential - friction;
                if (f.slipTimer > 2 * dt) f.slipVel += tdir * (ex / T.LegWeight * dt * 0.5f);
                hipCorr = tdir * (ex / T.BodyWeight);
                f.slipTimer += dt;
            }
            else
            {
                float dec = (friction - tangential) / T.LegWeight;
                float sm = f.slipVel.magnitude;
                if (sm > 0) f.slipVel -= f.slipVel.normalized * (MathF.Min(dec, sm / dt) * dt);
                if (f.slipVel.magnitude < 0.001f) f.slipVel = V3.zero;
                if (f.slipVel.sqrMagnitude < 1e-8f) f.slipTimer = 0;
            }
            if (f.slipVel.sqrMagnitude > 0)
            {
                float s2 = f.slipVel.sqrMagnitude;
                f.slipVel -= f.slipVel.normalized * (s2 * T.SlipQuadraticDrag * f.quadMult);
                V3 np = f.pos + f.slipVel * dt;
                // a sliding foot stretching the leg drags hip and foot toward each other by weight
                V3 d = np - (hip + HipVel * dt);
                if (d.magnitude > L)
                {
                    float over = d.magnitude - L; V3 dn = d.normalized;
                    HipPos += dn * (over * T.LegWeight / (T.TorsoWeight + T.LegWeight));
                    np -= dn * (over * T.TorsoWeight / (T.LegWeight + T.TorsoWeight));
                    float rel = V3.Dot(f.slipVel - HipVel, dn);
                    hipCorr += dn * (rel / dt * T.LegWeight / T.TorsoWeight * 2);
                    f.slipVel -= dn * rel;
                    f.slipVel -= nrm * V3.Dot(f.slipVel, nrm);
                }
                var g = World.Probe(np + V3.up * 0.3f * S, 0.6f * S);
                if (g.hit && np.y < g.point.y + 0.005f * S) np = new V3(np.x, g.point.y, np.z);
                float moved = (np - f.pos).magnitude;
                f.pos = np;
                if (moved > 0.005f * S) f.pivot = Heel(f);
            }
            else f.pos += f.slipVel * dt;
            accT += tq + hipCorr * dt;
        }
        DbgAccSum = accSum; DbgAccT = accT;
        HipVel += accSum + accT;

        // fatigue: a leg holding everything alone tires
        foreach (var f in Feet)
        {
            if (!f.planted) continue;
            var o = Other(f);
            V3 tqv = f.lastAppliedTorque;
            if (o.planted && V3.Dot(tqv.normalized, o.lastAppliedTorque.normalized) < 0)
                tqv += tqv.normalized * V3.Dot(o.lastAppliedTorque, tqv.normalized);
            if (!o.planted)
                f.fatigue = M.MoveTowards(f.fatigue, 1, T.FatigueAddCurve.Evaluate(tqv.magnitude / MathF.Max(0.001f, f.maxExert)) * dt * 0.5f);
            else f.fatigue = M.MoveTowards(f.fatigue, 0, dt * 0.6f);
        }

        HipPos += HipVel * dt;
    }

    // ---------------- hip yaw (TorsoRotation) ----------------

    void TorsoRotation(float dt)
    {
        HipAng = M.MoveTowardsAngle(HipAng, CamYaw, dt * 180);
        foreach (var f in Feet)
            if (f.planted) HipAng = M.ClampAngDist(HipAng, f.yaw, T.MaxHipTwist);
        V3 r = (Feet[0].pos - Feet[1].pos).XZ;
        float target = HipAng;
        if (r.sqrMagnitude > 1e-6f)
        {
            float feetYaw = V3.YawOf(V3.Cross(r, V3.up));
            if (MathF.Abs(M.DeltaAngle(HipAng, feetYaw)) > 90) feetYaw += 180;
            float ff = M.ClampAngDist(feetYaw, HipAng, 55);
            float d = M.DeltaAngle(ff, HipAng);
            target = HipAng - r.magnitude / S * T.HipYawFootMagnitudeFactor * d * T.FootHipYaw;
        }
        float err = M.DeltaAngle(HipYaw, target);
        hipYawVel = hipYawVel + err * T.HipYawP * dt - (1 - T.HipYawD) * hipYawVel;
        HipYaw += hipYawVel;
        foreach (var f in Feet) if (!f.planted || f.isLifting) f.yaw = M.MoveTowardsAngle(f.yaw, HipYaw, 360 * dt);
    }

    // ---------------- death (DeadCheck) ----------------

    void Die(string why) { Dead = true; DeadReason = why; }

    void DeadCheck(float dt)
    {
        var a = Feet[0]; var b = Feet[1];
        if (!a.planted && !b.planted)
        {
            if (HipIsColliding && HipImpact > T.HipCollisionDeathThresh) { Die("hit hard in the air"); return; }
            if (HipVel.y < -11) { Die("fell too far"); return; }
            return;
        }
        if (a.planted && b.planted && (a.pos - b.pos).magnitude > L * 1.95f) { Die("did the splits"); return; }

        // squashed: hip too low over the feet
        float minLeg = float.MaxValue;
        foreach (var f in Feet)
        {
            if (!f.planted) continue;
            V3 v = Hips(f.i) - f.pos; v = new V3(v.x, v.y * 2, v.z);
            minLeg = MathF.Min(minLeg, v.magnitude);
        }
        bool squashed = minLeg < L * 0.235f;
        if (HipVel.y < 0.05f && a.pos.y - Hips(0).y > -0.03f * S && b.pos.y - Hips(1).y > -0.03f * S) squashed = true;

        bool oneUp = !a.planted || !b.planted;
        bool supported = false;
        if (oneUp)
            foreach (var f in Feet)
                if (!HipIsColliding && f.planted && V3.Angle(Hips(f.i) - Center(f), V3.up) < T.OneLegDeadAngThresh) supported = true;
        if (supported) OffBalanceDeathTimer = 0;
        else
        {
            V3 legs = (Hips(0) - Center(a)) + (Hips(1) - Center(b));
            float ang = V3.Angle(legs.normalized, V3.up);
            if (ang <= T.DeadAngThresh || (HipVel.y >= -0.1f && !HipIsColliding) || ang <= T.DeadAngThresh * 0.75f || InsideSupport(COM))
                OffBalanceDeathTimer = 0;
            else
            {
                OffBalanceDeathTimer += dt;
                float insta = HipIsColliding ? T.HipCollidingDeadAngThresh : T.InstaDeadAngThresh;
                if (ang > insta && !oneUp) OffBalanceDeathTimer += 5;
            }
        }
        bool footAbove = false;
        foreach (var f in Feet) if (f.planted && f.pos.y >= HipPos.y + T.FootTooHighDistToHip * S) footAbove = true;
        if (squashed || footAbove) DeathTimer += dt * (footAbove ? 0.3f : 1f);
        else DeathTimer = 0;
        if (DeathTimer > T.DeathTime) { Die(squashed ? "squashed" : "feet over head"); return; }
        if (OffBalanceDeathTimer > T.OffBalanceDeathTime) Die("lost balance");
    }
}
