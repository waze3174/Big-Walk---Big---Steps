using System;
using System.Collections.Generic;
using BigSteps.Sim;
using UnityEngine;

namespace BigSteps;

// unity only sees this thin driver; il2cpp can't register methods taking our own types
public class LegsDriver : MonoBehaviour
{
    public LegsDriver(IntPtr ptr) : base(ptr) { }
    void Update() => Legs.Inst.Update();
    void FixedUpdate() => Legs.Inst.FixedUpdate();
    void LateUpdate()
    {
        Legs.Inst.LateUpdate();
        try { Remotes.SendLocal(); } catch (Exception e) { if (!sendErr) { sendErr = true; Plugin.L.LogWarning("net: sending legs: " + e); } }
        try { Remotes.Pose(); } catch (Exception e) { if (!poseErr) { poseErr = true; Plugin.L.LogWarning("net: posing remote legs: " + e); } }
    }
    static bool sendErr, poseErr;
}

// big walk side of nate's walking: feeds input + world into Sim.NateSim (the baby steps port, shared with the
// offline bench) and makes the bird follow it. index 0 = right foot (RTrig), 1 = left (LTrig)
class Legs
{
    internal static readonly Legs Inst = new();

    // the sim's world: big walk raycasts, ignoring the bird's own colliders
    class BirdWorld : IWorld
    {
        public GroundHit Probe(V3 from, float down)
        {
            var s = Surface.Probe(U(from), down, Mask);
            if (!s.hit) return default;
            return new GroundHit { hit = true, point = V(s.point), normal = V(s.normal), traction = s.grip, quadMult = s.quadMult, stepSpeed = s.stepSpeed, name = s.name };
        }

        public bool Cast(V3 from, V3 dir, float dist, out float hitDist, out V3 normal)
        {
            hitDist = 0; normal = V3.up;
            if (!Surface.Ray(U(from), U(dir), dist, Mask, out var h)) return false;
            hitDist = h.distance; normal = V(h.normal);
            return true;
        }
    }

    static V3 V(Vector3 v) => new(v.x, v.y, v.z);
    static Vector3 U(V3 v) => new(v.x, v.y, v.z);

    // bird visuals per foot
    class FootRig
    {
        public Transform bone;                    // foot IK target
        public Transform upper, lower, lowerEnd;  // leg bones
        public Transform knee;                    // SH_knee_bone: the leg spline's handle
    }

    readonly NateSim sim = new() { World = new BirdWorld() };
    readonly FootRig[] rigs = { new(), new() };
    static bool active;
    internal static bool IsActive => active;
    internal static bool IsFallen => Inst.fallen;
    internal static bool Paused => Inst.seated;
    internal static float HipYaw => Inst.sim.HipYaw;
    internal static NateSim SimForTools => Inst.sim;     // read-only use by the dev tools
    internal static bool FootDown => active && !Inst.fallen && !Inst.seated && (Inst.sim.Feet[0].planted || Inst.sim.Feet[1].planted);
    // both feet off the ground for a moment (dropping off something): no sitting down mid-air
    internal static bool Airborne => active && !Inst.fallen && !Inst.seated && Inst.sim.TimeWithBothFeetInAir > 0.2f;
    // a foot's height above the ground under it (sent to other players, who put it on their own ground)
    internal static float FootLift(int i) { var f = Inst.sim.Feet[i]; return f.ground.hit ? Mathf.Max(0, f.pos.y - f.ground.point.y) : 0f; }
    internal static Vector3 FootPos(int i) => U(Inst.sim.Feet[i].pos);
    bool bodyLocked;
    float accumulator;
    float mouseLiftHeightMod;
    Vector3 lastSetVel;

    // falling (rigid tumble until the ragdoll lands)
    bool fallen;
    string fallReason = "";
    RigidbodyConstraints savedConstraints;
    bool savedGravity, savedIgnoreFalling;
    // last spot where both feet stood on something: respawn point if a fall goes out of the world
    Vector3 safePos; float safeYaw; bool haveSafe;
    int settle;
    Vector3 settlePos;
    bool seated;
    readonly Carrier carrier = new();
    Vector3 lastPlatVel;
    // big walk's drop-what-you-hold on a long fall: height we last stood at, and whether this fall already dropped
    float groundedY, fallFar; bool fallDropped;

    static int Mask => Game.Pc.ground.layerMask.value;
    // body size: how big the bird is overall (camera, shadows, body capsule, ragdoll) - its body centre stands ~0.75 up
    float bodyScale = 1;
    // nate's hip pivot sits on the bird's leg roots, this far below the rigidbody's centre
    float hipOffset;
    Vector3 HipUp => Vector3.up * hipOffset;
    float L => T.LegLength * bodyScale;

    internal static string Status => !active ? "legs off (F9)" :
        Inst.seated ? "seated: big walk has the body, legs resume when you get up" :
        Inst.bodyLocked ? "BODY LOCKED by big walk (sitting? carried?)" :
        Inst.fallen ? $"FELL: {Inst.fallReason}" :
        $"R {Inst.FootState(Inst.sim.Feet[0])}  L {Inst.FootState(Inst.sim.Feet[1])}  trig {Inst.sim.Trigs[0]:F2}/{Inst.sim.Trigs[1]:F2}  fall {Inst.sim.OffBalanceDeathTimer:F2}/{Inst.sim.MagicFallOffTimer:F2}";

    string FootState(Foot f) => (f.planted ? (f.isLifting ? "lift" : "down") : "UP") + $" {f.ground.name} grip {f.traction:F2}" + (f.slipVel.sqrMagnitude > 0 ? " SLIP" : "") + (f.fatigue > 0.3f ? $" tired {f.fatigue:F1}" : "");

    // big walk's own movement runs while it has us seated (chairs, ski lift): the legs step aside until we get up
    internal static bool Owns(PlayerCharacter p) => active && !Inst.seated && Game.IsLocal(p);
    internal static float Scale => active ? Inst.bodyScale : 1f;

    // baby steps' camera follow target: hip xz, 0.5 above the lowest body part (the feet)
    internal static Vector3 CameraFollowPoint(out Vector3 vel)
    {
        var rb = Game.Pc.rb;
        // down: the drawn pelvis, no velocity lead (the body chasing the ragdoll has a jumpy velocity: the camera stuttered)
        if (active && Inst.fallen && Inst.ragdoll.Active) { vel = Vector3.zero; return Inst.ragdoll.PelvisDrawn + Vector3.down * 0.25f; }
        if (!active || Inst.fallen || Inst.seated) { vel = rb.linearVelocity; return rb.position + Vector3.down * 0.25f; }
        var s = Inst.sim;
        vel = U(s.HipVel);
        float low = Mathf.Min(s.Feet[0].pos.y, s.Feet[1].pos.y);
        return new Vector3(s.HipPos.x, low + 0.5f * Inst.bodyScale, s.HipPos.z);
    }

    // ---------------- F12 recorder ----------------

    System.IO.StreamWriter rec;

    void ToggleRecord()
    {
        if (rec != null) { rec.Dispose(); rec = null; Plugin.L.LogInfo("recording stopped"); return; }
        string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "BigStepsDump");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"sim-{DateTime.Now:HHmmss}.csv");
        rec = new System.IO.StreamWriter(path);
        rec.WriteLine("t,trigR,trigL,stickX,stickZ,R_pl,R_lift,R_want,L_pl,L_lift,L_want,hipX,hipY,hipZ,velX,velY,velZ,tgtX,tgtY,tgtZ,balX,balZ,RfX,RfY,RfZ,LfX,LfY,LfZ,R_gnd,L_gnd,rbVelIn_Y,lastSetVel_Y,offBal,magic,fallen,hipYaw,camYaw,Rtrac,Ltrac,Rslip,Lslip,Rslope,Lslope,Rsurf,Lsurf");
        Plugin.L.LogInfo("recording to " + path);
    }

    static float Slope(Foot f) => f.ground.hit ? V3.Angle(f.ground.normal, V3.up) : -1;
    static string Csv(string n) => (n ?? "").Replace(',', ' ');

    void Record(Vector3 rbVelIn)
    {
        if (rec == null) return;
        var a = sim.Feet[0]; var b = sim.Feet[1];
        string F(float v) => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        string B(bool v) => v ? "1" : "0";
        rec.WriteLine(string.Join(",", F(Time.time), F(sim.Trigs[0]), F(sim.Trigs[1]), F(sim.LStick.x), F(sim.LStick.z),
            B(a.planted), B(a.isLifting), B(a.wantsToLift), B(b.planted), B(b.isLifting), B(b.wantsToLift),
            F(sim.HipPos.x), F(sim.HipPos.y), F(sim.HipPos.z), F(sim.HipVel.x), F(sim.HipVel.y), F(sim.HipVel.z),
            F(sim.TargetPt.x), F(sim.TargetPt.y), F(sim.TargetPt.z), F(sim.BalancePt.x), F(sim.BalancePt.z),
            F(a.pos.x), F(a.pos.y), F(a.pos.z), F(b.pos.x), F(b.pos.y), F(b.pos.z),
            F(a.ground.hit ? a.ground.point.y : -999), F(b.ground.hit ? b.ground.point.y : -999),
            F(rbVelIn.y), F(lastSetVel.y), F(sim.OffBalanceDeathTimer), F(sim.MagicFallOffTimer), B(fallen), F(sim.HipYaw), F(sim.CamYaw),
            F(a.traction), F(b.traction), F(a.slipVel.magnitude), F(b.slipVel.magnitude), F(Slope(a)), F(Slope(b)), Csv(a.ground.name), Csv(b.ground.name)));
    }

    // ---------------- on / off ----------------

    internal void Update()
    {
        if (Input.GetKeyDown(Plugin.RecordKey.Value)) ToggleRecord();
        if (Input.GetKeyDown(Plugin.LegsKey.Value)) Toggle();
        if (active && (Game.Pc == null || Game.Pc.WasCollected)) SetActive(false);
        if (active && Game.Pc != null && Game.InputUp) try { FallLook(); } catch (Exception e) { if (!lookErr) { lookErr = true; Plugin.L.LogWarning("fall look: " + e.Message); } }
    }

    // fallback for the camera freezing while down: if big walk ignores the right stick during a fall, turn the look
    // ourselves at the rate big walk itself turned it for the stick while walking (learned), else its stickLookSpeed
    float lastYaw = float.NaN, lastPitch, kYaw = float.NaN, kPitch = float.NaN;
    int frozenFrames;
    bool manualLook, lookErr, loggedManual;

    static float SignedPitch(float x) => x > 180 ? x - 360 : x;

    void FallLook()
    {
        var pc = Game.Pc;
        var p = pc.inputPlayer;
        float sx = p.GetAxis(RewiredConsts.Action.stickLookX), sy = p.GetAxis(RewiredConsts.Action.stickLookY);
        var pivot = pc.cameraTransform;
        float yaw = pc.transform.eulerAngles.y, pitch = SignedPitch(pivot.localEulerAngles.x), dt = Time.deltaTime;
        if (float.IsNaN(lastYaw) || dt <= 0) { lastYaw = yaw; lastPitch = pitch; return; }
        float dYaw = Mathf.DeltaAngle(lastYaw, yaw), dPitch = pitch - lastPitch;
        if (!fallen)
        {
            manualLook = false; frozenFrames = 0;
            // learn big walk's stick look rate (deg per second at full stick)
            if (Mathf.Abs(sx) > 0.3f && Mathf.Abs(dYaw) > 0.01f) { float k = dYaw / (sx * dt); kYaw = float.IsNaN(kYaw) ? k : Mathf.Lerp(kYaw, k, 0.1f); }
            if (Mathf.Abs(sy) > 0.3f && Mathf.Abs(dPitch) > 0.01f) { float k = dPitch / (sy * dt); kPitch = float.IsNaN(kPitch) ? k : Mathf.Lerp(kPitch, k, 0.1f); }
        }
        else
        {
            bool input = Mathf.Abs(sx) > 0.2f || Mathf.Abs(sy) > 0.2f;
            if (!manualLook && input) frozenFrames = Mathf.Abs(dYaw) < 0.001f && Mathf.Abs(dPitch) < 0.001f ? frozenFrames + 1 : 0;
            if (!manualLook && frozenFrames >= 3)
            {
                manualLook = true;
                if (!loggedManual) { loggedManual = true; Plugin.L.LogInfo($"big walk froze the look while down: turning it ourselves (yaw {Rate(kYaw, false):F0}, pitch {Rate(kPitch, true):F0} deg/s)"); }
            }
            if (manualLook && input)
            {
                float turn = Rate(kYaw, false) * sx * dt;
                var q = Quaternion.Euler(0, turn, 0);
                pc.rb.rotation = q * pc.rb.rotation;
                pc.transform.rotation = q * pc.transform.rotation;
                var tu = pc.tunings;
                float np = Mathf.Clamp(pitch + Rate(kPitch, true) * sy * dt, Mathf.Min(tu.upperLookLimit, tu.lowerLookLimit), Mathf.Max(tu.upperLookLimit, tu.lowerLookLimit));
                var e = pivot.localEulerAngles;
                pivot.localEulerAngles = new Vector3(np, e.y, e.z);
                yaw = pc.transform.eulerAngles.y; pitch = np;
            }
        }
        lastYaw = yaw; lastPitch = pitch;
    }

    // learned rate, else big walk's tuning (stick up looks up: pitch goes negative)
    float Rate(float learned, bool pitch)
    {
        if (!float.IsNaN(learned)) return learned;
        float s = Game.Pc.tunings.stickLookSpeed;
        return pitch ? -s : s;
    }

    // legs off while down waits until you're up: switching right away skipped the fall (big walk just stood the bird
    // up). pressing again while it waits cancels
    bool offWhenUp;
    internal void Toggle()
    {
        if (active && fallen)
        {
            offWhenUp = !offWhenUp;
            Plugin.L.LogInfo(offWhenUp ? "legs go off once you're back up" : "legs stay on");
            return;
        }
        offWhenUp = false;
        SetActive(!active);
    }

    // a player joining later gets our bird's movement state from big walk's movement code, which the legs switch off:
    // vanilla players then saw it slide until we sat down and stood up. so a newcomer gets the same: big walk drives
    // the bird for ~0.3 s (as when seated), then the legs take over again where we stand. while down it waits
    int handover;
    internal void Handover()
    {
        if (!active || Game.Pc == null) return;
        handover = Mathf.Max(handover, 10);
        Plugin.L.LogInfo("player joined: big walk's movement takes the bird for a moment so they get its state");
    }
    internal void TurnOn() { if (!active) SetActive(true); }

    void SetActive(bool on)
    {
        offWhenUp = false;
        var pc = Game.Pc;
        if (on && pc == null) { Plugin.L.LogWarning("no local player yet"); return; }
        if (on && !RigOk(pc)) return;
        seated = false;     // re-detected next physics step, after the body is set up for the new state
        if (!on && fallen)
        {
            try { HandBackMidFall(); } catch (Exception e) { Plugin.L.LogWarning("legs off while down: " + e.Message); }
            ragdoll.End();
            fallen = false;
        }
        active = on;
        if (!on && pc == null) { Arms.Remap(false); return; }
        var rb = pc?.rb;
        if (on)
        {
            var g = Surface.Probe(rb.position + Vector3.up * 0.5f, 3f, Mask);
            float h = g.hit ? rb.position.y - g.point.y : 0.75f;
            bodyScale = Mathf.Clamp(h / 0.75f, 0.3f, 2f);
            if (Mathf.Abs(bodyScale - 1) > 0.25f) { Plugin.L.LogWarning($"ground {h:F2} below the body (standing on something the probe misses?): sized as a normal bird"); h = 0.75f; bodyScale = 1; }
            savedGravity = rb.useGravity;
            rb.useGravity = false;
            try { savedIgnoreFalling = pc.faller.ignoreFalling; pc.faller.ignoreFalling = true; } catch (Exception e) { Plugin.L.LogWarning("couldn't switch off big walk's fall: " + e.Message); }
            FindBones(pc);
            FitToBird(pc, rb, h);
            ShrinkBody(pc, true);
            sim.Reset(V(rb.position - HipUp), BodyYaw());
            safePos = rb.position; safeYaw = sim.HipYaw; haveSafe = true;
            Plugin.L.LogInfo($"legs on: rb height {h:F2}, body scale {bodyScale:F2}, sim scale {sim.S:F2}, hip offset {hipOffset:F3}, rb mass {rb.mass} constraints {rb.constraints}");
        }
        else if (rb != null)
        {
            rb.useGravity = savedGravity; ShrinkBody(pc, false);
            // start big walk's fall tracking fresh from here, not from where legs were switched on
            try { pc.faller.ClearNextFall(); pc.faller.heightLastGrounded = rb.position.y; pc.faller.ignoreFalling = savedIgnoreFalling; } catch { }
        }
        if (!on) RestoreModel();
        Arms.Remap(on);
        Plugin.L.LogInfo("legs " + (on ? "on" : "off"));
    }

    static float BodyYaw()
    {
        var f = Game.Pc.cameraTransform.forward; f.y = 0;
        return f.sqrMagnitude < 1e-6f ? 0 : Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
    }

    // ---------------- physics step ----------------

    internal void FixedUpdate()
    {
        if (Game.Pc == null || Game.Pc.rb == null) return;
        var rb = Game.Pc.rb;
        // the platform is followed even with the legs off, so turning them on aboard a moving train knows its speed
        if (!active || seated) { try { carrier.Step(Game.Pc, rb.position, 1.5f, Mask); } catch { } }
        if (!active) return;
        if (offWhenUp && !fallen) { offWhenUp = false; SetActive(false); return; }
        bool sit = IsSeated(Game.Pc) || (handover > 0 && !fallen);
        if (handover > 0 && !fallen) handover--;
        if (sit != seated) SetSeated(sit);
        if (seated) return;
        if (fallen)
        {
            FallenStep();
            Record(rb.linearVelocity);   // keep rows while down so fall/get-up times compare with baby steps
            SyncMover(rb, Vector3.zero);
            return;
        }

        bool locked = rb.isKinematic || (rb.constraints & RigidbodyConstraints.FreezePosition) != 0;
        if (locked != bodyLocked)
        {
            bodyLocked = locked;
            Plugin.L.LogWarning(locked ? $"body is locked by big walk (kinematic {rb.isKinematic}, constraints {rb.constraints})" : "body unlocked");
        }

        // moving ground: nate works in the platform's frame, the body gets the platform's velocity on top
        carrier.Step(Game.Pc, rb.position, 1.5f, Mask);
        if (carrier.Active)
        {
            sim.Carry(p => V(carrier.Map(U(p))), carrier.DYaw);
            if (settle > 0) settlePos = carrier.Map(settlePos);
        }
        Vector3 platVel = carrier.Vel;
        lastPlatVel = platVel;

        // physx resolved collisions since last step: take its result as the truth
        Vector3 rbVelIn = rb.linearVelocity;
        // just stood up: physx is still shoving the capsule out of the slope, don't feed that kick to the legs
        // ...nor any push-out of its position: put it where the sim left it
        if (settle > 0) { rb.position = settlePos; rbVelIn = lastSetVel; }
        sim.HipPos = V(rb.position - HipUp);
        sim.HipVel = V(rbVelIn - platVel);
        // the body got stopped/deflected by the world: that's nate's hip collision
        Vector3 hit = lastSetVel - rbVelIn;
        sim.HipIsColliding = hit.magnitude > 0.5f;
        sim.HipImpact = hit.magnitude;
        V3 start = sim.HipPos;

        ReadInput();
        sim.SteepGrip = Settings.SteepGrip;
        sim.CrossPrevention = Plugin.CrossPrevention.Value;

        // big walk steps physics at 30hz, nate was tuned at 60hz: substep the sim
        accumulator += Time.fixedDeltaTime;
        int steps = 0;
        while (accumulator >= T.SimDt && steps < 4)
        {
            sim.Step(T.SimDt);
            accumulator -= T.SimDt;
            steps++;
            if (sim.Dead) break;
        }
        if (sim.Dead) Die(sim.DeadReason);
        else rb.linearVelocity = lastSetVel = U((sim.HipPos - start) / Time.fixedDeltaTime) + platVel;
        if (settle > 0) { settle--; settlePos = U(sim.HipPos) + HipUp; }
        Record(rbVelIn);
        SyncMover(rb, U(sim.HipVel));
        if (sim.Feet[0].planted || sim.Feet[1].planted) groundedY = rb.position.y;
        if (!sim.Dead && sim.Feet[0].planted && sim.Feet[1].planted && !rb.isKinematic)
        { safePos = rb.position; safeYaw = sim.HipYaw; haveSafe = true; }
    }

    // seated (chairs) or carried kinematic (the ski lift locks the body without counting as sitting)
    static bool IsSeated(PlayerCharacter pc)
    {
        if (pc.rb != null && pc.rb.isKinematic) return true;
        try { var s = pc.sitter; return s != null && s.isSittingLocal; } catch { return false; }
    }

    // sitting down hands the body back to big walk (gravity, full capsule, its own fall tracking off as before);
    // getting up starts nate fresh on the spot, like a stand up
    void SetSeated(bool on)
    {
        seated = on;
        var pc = Game.Pc; var rb = pc.rb;
        if (on)
        {
            if (fallen) { ragdoll.End(); fallen = false; rb.constraints = savedConstraints; if (!rb.isKinematic) rb.angularVelocity = Vector3.zero; }
            rb.useGravity = savedGravity;
            ShrinkBody(pc, false);
            RestoreModel();
            UpdateShadows();
            Arms.Remap(false);
            Plugin.L.LogInfo($"seated: legs paused (sitting {(pc.sitter != null && pc.sitter.isSittingLocal)}, kinematic {rb.isKinematic})");
            return;
        }
        rb.useGravity = false;
        ShrinkBody(pc, true);
        Vector3 pos = rb.position;
        var g = Surface.Probe(pos + Vector3.up * 1f, 4f, Mask);
        if (g.hit) pos = new Vector3(pos.x, Mathf.Max(pos.y, g.point.y + 0.75f * sim.S + hipOffset), pos.z);
        rb.position = pos;
        lastSetVel = carrier.Vel;
        if (!rb.isKinematic) rb.linearVelocity = lastSetVel;
        settle = 6; settlePos = pos;
        sim.Reset(V(pos - HipUp), BodyYaw());
        Arms.Remap(true);
        if (refitPending) Refit();
        Plugin.L.LogInfo("got up from the seat: legs back on");
    }

    // big walk's PlayerMover (skipped while the legs own the body) keeps the kernal position / velocity other systems read
    // (the train's car culling among them) and sends the "controls velocity" every client animates our walk from: a
    // vanilla player saw a modded bird slide with no steps. both kept up here from nate's movement
    Vector3 lastCtl;
    float nextCtl, nextAwake;
    bool moverErr;

    void SyncMover(Rigidbody rb, Vector3 relVel)
    {
        var pc = Game.Pc;
        try
        {
            var mv = pc.mover;
            if (mv != null)
            {
                mv.cachedKernalPos = pc.kernal != null ? pc.kernal.position : rb.position;
                mv.localKernalVelocityMinusPlatform = relVel;
                mv.localControlsVelocity = new Vector3(relVel.x, 0, relVel.z);
            }
            // big walk's sleeper puts a player to sleep after a while without recorded actions, and every client then
            // shows that bird asleep: its movement code (skipped) records walking. vanilla players saw us slide
            // until a sit / stand (an action) woke the bird
            bool busy = relVel.sqrMagnitude > 0.0025f || sim.Trigs[0] > 0.05f || sim.Trigs[1] > 0.05f || sim.LStick.sqrMagnitude > 0.01f;
            if (busy && Time.time >= nextAwake) { nextAwake = Time.time + 0.5f; pc.sleeper?.RecordAction(); }
            Vector3 ctl = new(relVel.x, 0, relVel.z);
            if (Time.time >= nextCtl && (ctl - lastCtl).sqrMagnitude > 0.0025f)
            {
                nextCtl = Time.time + 0.1f;
                lastCtl = ctl;
                pc.playerNetworking?.CmdSetControlsVelocity(ctl);
            }
        }
        catch (Exception e) { if (!moverErr) { moverErr = true; Plugin.L.LogWarning("keeping big walk's mover state: " + e.Message); } }
    }

    // a leg lifts from its trigger axis, or from a button when one is bound instead (Big Steps > Rebind gamepad)
    int liftBtnL = -2, liftBtnR = -2;
    internal void PadButtonsChanged() { liftBtnL = liftBtnR = -2; }

    static float PadLift(Rewired.Joystick j, int axis, string button, ref int id)
    {
        if (!string.IsNullOrEmpty(button))
        {
            if (id == -2) id = Arms.FindButton(j, button);
            return id >= 0 && j.GetButtonById(id) ? 1f : 0f;
        }
        return axis >= 0 && axis < j.axisCount ? Mathf.Abs(j.GetAxis(axis)) : 0f;
    }

    // raw inputs -> nate's processed triggers/stick
    void ReadInput()
    {
        if (!Game.InputUp) return;
        var p = Game.Pc.inputPlayer;
        float x = p.GetAxis(RewiredConsts.Action.moveX), y = p.GetAxis(RewiredConsts.Action.moveY);
        bool keyboard = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D);
        float rt = Input.GetKey(Plugin.LiftRightKey.Value) ? 1 : 0, lt = Input.GetKey(Plugin.LiftLeftKey.Value) ? 1 : 0;
        try
        {
            if (p.controllers.joystickCount > 0)
            {
                var j = p.controllers.Joysticks[0];
                rt = Mathf.Max(rt, PadLift(j, Plugin.RightTriggerAxis.Value, Plugin.LiftRightButton.Value, ref liftBtnR));
                lt = Mathf.Max(lt, PadLift(j, Plugin.LeftTriggerAxis.Value, Plugin.LiftLeftButton.Value, ref liftBtnL));
            }
        }
        catch { }
        float tr = NateSim.ProcessTrigger(rt), tl = NateSim.ProcessTrigger(lt);
        // mouse wheel trims lift height while a leg is up
        mouseLiftHeightMod = Mathf.Clamp(mouseLiftHeightMod + Input.mouseScrollDelta.y * 0.1f, -1, 0);
        if (tr < 0.25f && tl < 0.25f) mouseLiftHeightMod = 0;
        if (tr > 0) tr = Mathf.Max(0.01f, tr + mouseLiftHeightMod);
        if (tl > 0) tl = Mathf.Max(0.01f, tl + mouseLiftHeightMod);
        float camYaw = BodyYaw();
        var stick = NateSim.ProcessStick(x, y, keyboard, Input.GetKey(Plugin.SlowFootKey.Value), camYaw);
        sim.SetInput(tr, tl, stick, camYaw);
    }

    // ---------------- falling: floppy ragdoll + get up (Ragdoll.cs) ----------------

    readonly Ragdoll ragdoll = new();
    internal static void RagdollReleaseItem(Prop p) { if (Inst.fallen) Inst.ragdoll.ReleaseItem(p); }

    void Die(string why)
    {
        fallen = true;
        fallReason = why;
        fallDropped = false; fallFar = 0;
        var rb = Game.Pc.rb;
        savedConstraints = rb.constraints;
        var feet = sim.Feet;
        Vector3 away = U((sim.HipPos - (feet[0].pos + feet[1].pos) * 0.5f).XZ);
        if (away.sqrMagnitude < 1e-4f) away = U(sim.HipVel.XZ);
        if (away.sqrMagnitude < 1e-4f) away = U(V3.Yaw(sim.HipYaw, V3.forward));
        // on a moving floor the parts are moved along with it (physx doesn't push them: they fell through the train);
        // the ragdoll switches itself to riding by velocity if physx turns out to drag them too
        // a platform physx moves (the train carriage) drags the parts itself: ride by velocity from the start (moving
        // them along as well for the first half second shot them towards the front of the train)
        bool byVel = lastPlatVel.sqrMagnitude < 0.01f || carrier.PlatformHasBody;
        Vector3 launch = U(sim.HipVel) + (byVel ? lastPlatVel : Vector3.zero);
        try { ragdoll.byVelocity = byVel; ragdoll.Begin(Game.Pc, launch, away, U(feet[0].pos), U(feet[1].pos), bodyScale, sim.HipYaw); ragdoll.platVel = byVel ? lastPlatVel : Vector3.zero; }
        catch (Exception e) { Plugin.L.LogError("ragdoll failed, falling rigid: " + e); RigidFall(away); }
        Plugin.L.LogInfo("fell: " + why);
    }

    // fallback if the ragdoll can't be built: tumble the whole body for 2.5 s
    void RigidFall(Vector3 away)
    {
        ragdoll.End();
        var rb = Game.Pc.rb;
        rb.constraints = RigidbodyConstraints.None;
        rb.useGravity = true;
        ShrinkBody(Game.Pc, false);
        rb.angularVelocity = Vector3.Cross(Vector3.up, away.normalized) * 4f;
        rb.linearVelocity = U(sim.HipVel) + lastPlatVel;
        rigidUntil = Time.time + 2.5f;
    }

    float rigidUntil = -1;

    (bool, Vector3) GroundAt(Vector3 from)
    {
        var g = Surface.Probe(from, 4f, Mask);
        return (g.hit, g.point);
    }

    void FallenStep()
    {
        if (ragdoll.Active)
        {
            carrier.Step(Game.Pc, ragdoll.PelvisPos + Vector3.up * 0.2f * bodyScale, 1.5f, Mask);
            ragdoll.Carry(carrier);
            if (carrier.Active) groundedY += (carrier.Map(ragdoll.PelvisPos) - ragdoll.PelvisPos).y;
            float drop = groundedY - ragdoll.PelvisPos.y;
            // like big walk, on landing: dropped mid-fall the item kept the fall's speed and went through the map
            if (drop > FallDrop.Distance(Game.Pc)) fallFar = Mathf.Max(fallFar, drop);
            if (!fallDropped && fallFar > 0 && ragdoll.PelvisSpeed < 1.5f) { fallDropped = true; FallDrop.Run(Game.Pc, fallFar); }
            try { ragdoll.FixedStep(GroundAt); }
            catch (Exception e) { Plugin.L.LogError("ragdoll step failed: " + e); RigidFall(U(sim.HipVel.XZ)); return; }
            if (ragdoll.Lost) { Plugin.L.LogWarning("ragdoll lost (out of the world or destroyed), back to last safe spot"); StandUp(); }
            else if (ragdoll.Done) StandUp();
        }
        else if (Time.time > rigidUntil) StandUp();
    }

    // legs off while down: big walk gets the bird where the body is, still moving as it was, and its own fall takes
    // over. (standing up found no ground under a bird in mid-air and sent it back to the last place it stood)
    void HandBackMidFall()
    {
        var pc = Game.Pc;
        if (pc == null || pc.WasCollected || pc.rb == null) return;
        var rb = pc.rb;
        bool rag = ragdoll.Active && !ragdoll.Lost;
        Vector3 pos = rag ? ragdoll.PelvisPos : rb.position;
        Vector3 vel = rag ? ragdoll.PelvisVel : rb.linearVelocity;
        ragdoll.End();
        // lying on the ground: big walk's body stands on its own capsule, so it goes up to its standing height
        var g = Surface.Probe(pos + Vector3.up * 1f, 1f + 0.75f, Mask);
        if (g.hit && pos.y < g.point.y + 0.75f) pos.y = g.point.y + 0.75f;
        var f = rb.rotation * Vector3.forward; f.y = 0;
        if (f.sqrMagnitude > 1e-6f) rb.rotation = Quaternion.LookRotation(f.normalized, Vector3.up);
        rb.angularVelocity = Vector3.zero;
        rb.constraints = savedConstraints;
        rb.position = pos;
        rb.linearVelocity = vel;
        Plugin.L.LogInfo($"legs off while down: big walk takes the bird here ({(g.hit ? $"{pos.y - g.point.y:F1} m over the ground" : "in the air")}, {vel.magnitude:F1} m/s)");
    }

    void StandUp()
    {
        bool lost = ragdoll.Lost;
        bool fromRagdoll = ragdoll.Active && !lost;
        Vector3 standPos = ragdoll.StandPos; float upYaw = ragdoll.UpYaw;
        ragdoll.End();
        fallen = false;
        if (Game.Pc == null || Game.Pc.WasCollected || Game.Pc.rb == null) return;
        var rb = Game.Pc.rb;
        Vector3 pos = rb.position;
        float yaw = sim.HipYaw;
        if (fromRagdoll) { pos = standPos; yaw = upYaw; }
        // nothing under us (fell through / off the world): back to the last place we stood
        bool groundUnder = Surface.Probe(pos + Vector3.up * 1f, 4f, Mask).hit;
        if ((lost || !groundUnder) && haveSafe) { pos = safePos; yaw = safeYaw; fromRagdoll = false; }
        // the body's rotation is big walk's look yaw: leave it, so the camera stays where you turned it while down
        rb.angularVelocity = Vector3.zero;
        rb.constraints = savedConstraints;
        rb.useGravity = false;
        ShrinkBody(Game.Pc, true);
        if (!fromRagdoll)
        {
            var g = Surface.Probe(pos + Vector3.up * 1f, 4f, Mask);
            if (g.hit) pos = new Vector3(pos.x, g.point.y + 0.75f * sim.S + hipOffset, pos.z);
        }
        rb.position = pos;
        // standing up on a moving platform: start at its speed, not still in the world
        rb.linearVelocity = lastSetVel = carrier.Vel;
        settle = 6; settlePos = pos;
        sim.Reset(V(pos - HipUp), yaw);
        if (fromRagdoll) for (int i = 0; i < 2; i++) { sim.Feet[i].pos = sim.Feet[i].pivot = V(ragdoll.FootPos(i)); }
        if (refitPending) Refit();
    }

    // ---------------- body collider ----------------

    // big walk's capsule reaches the ground so the bird stands on it; nate stands on his legs.
    // while legs are on the capsule stops above the feet and the low spheres are off
    CapsuleCollider capsule;
    SphereCollider lowSphere;
    Collider bottomSegment;
    float capHeight; Vector3 capCenter;

    void ShrinkBody(PlayerCharacter pc, bool on)
    {
        if (capsule == null)
        {
            foreach (var t in pc.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != "playerCollider") continue;
                capsule = t.GetComponent<CapsuleCollider>();
                lowSphere = t.GetComponent<SphereCollider>();
                if (capsule != null) { capHeight = capsule.height; capCenter = capsule.center; }
                break;
            }
            if (capsule == null) { Plugin.L.LogWarning("playerCollider capsule not found"); return; }
        }
        if (on)
        {
            float bottom = capCenter.y - capHeight * 0.5f;
            float top = capCenter.y + capHeight * 0.5f;
            float newBottom = bottom + L * 0.55f;
            capsule.height = Mathf.Max(capsule.radius * 2, top - newBottom);
            capsule.center = new Vector3(capCenter.x, (top + newBottom) * 0.5f, capCenter.z);
        }
        else { capsule.height = capHeight; capsule.center = capCenter; }
        if (lowSphere != null) lowSphere.enabled = !on;
        if (bottomSegment == null)
            foreach (var t in pc.GetComponentsInChildren<Transform>(true))
                if (t.name == "bottomSegmentCollider") { bottomSegment = t.GetComponent<Collider>(); break; }
        if (bottomSegment != null) bottomSegment.enabled = !on;
    }

    // ---------------- visuals ----------------

    readonly List<LimbSpline> legSplines = new();
    Transform model;
    Quaternion modelLocalRot = Quaternion.identity;
    bool modelRotSaved;

    void FindBones(PlayerCharacter pc)
    {
        legSplines.Clear();
        foreach (var t in pc.GetComponentsInChildren<Transform>(true))
        {
            switch (t.name)
            {
                case "dummy": if (model == null && t.GetComponentInChildren<LimbSpline>(true) == null && t.Find("Armature") != null) model = t; break;
                case "foot_Target_bone_R": rigs[0].bone = t; break;
                case "foot_Target_bone_L": rigs[1].bone = t; break;
                // the visible legs, not the first person "local" copies
                case "SH_leg_upper_bone_R": if (t.parent != null && t.parent.name == "hipBone_bone_R") rigs[0].upper = t; break;
                case "SH_leg_upper_bone_L": if (t.parent != null && t.parent.name == "hipBone_bone_L") rigs[1].upper = t; break;
            }
        }
        foreach (var r in rigs)
        {
            if (r.upper == null) continue;
            for (int i = 0; i < r.upper.childCount; i++)
            {
                var c = r.upper.GetChild(i);
                if (c.name.StartsWith("leg_lower_bone")) { r.lower = c; if (c.childCount > 0) r.lowerEnd = c.GetChild(0); }
            }
        }
        foreach (var ls in pc.GetComponentsInChildren<LimbSpline>(true))
        {
            string n = ls.gameObject.name;
            if (n.ToLower().Contains("leg")) legSplines.Add(ls);
            if (n == "rightLeg") rigs[0].knee = ls.handleTransform;
            if (n == "leftLeg") rigs[1].knee = ls.handleTransform;
        }
    }

    // nate's walking at the bird's size: the bird is a scaled nate (hip height / leg length 0.93 vs nate's 0.95) with
    // ~0.59 m legs whose roots sit ~0.2 below the body centre. running nate full size from the body centre put a
    // lifted foot inside the belly. sim scale = bird leg length / nate's, sim hip = the leg roots
    // height / bird fit changed (menu or the host's shared values): fit again where we stand. while down or seated
    // it waits for the stand up
    bool refitPending;
    float fitLen;
    internal void Refit()
    {
        if (!active || Game.Pc == null) return;
        if (fallen || seated) { refitPending = true; return; }
        refitPending = false;
        var rb = Game.Pc.rb;
        // only the scale: the leg length and hip offset measured when the legs came on stay. re-measuring them here
        // caught the bird mid-pose (hip offset swung 0.18..0.24) and left the body at the wrong height on its legs
        sim.S = fitLen > 0 && Settings.FitToBird ? Mathf.Clamp(fitLen / T.LegLength * Mathf.Clamp(Settings.HeightBoost, 0.5f, 2f), 0.3f, 2f) : bodyScale;
        Plugin.L.LogInfo($"legs re-fitted: height x{Settings.HeightBoost:F2}, sim scale {sim.S:F2}");
        sim.Reset(V(rb.position - HipUp), sim.HipYaw);
        lastSetVel = carrier.Vel; settle = 6; settlePos = rb.position;
    }

    void FitToBird(PlayerCharacter pc, Rigidbody rb, float rbHeight)
    {
        float len = 0, rootY = 0; int n = 0;
        foreach (var r in rigs)
        {
            if (r.upper == null || r.lower == null || r.lowerEnd == null) continue;
            len += (r.lower.position - r.upper.position).magnitude + (r.lowerEnd.position - r.lower.position).magnitude;
            rootY += r.upper.position.y; n++;
        }
        float ground = rb.position.y - rbHeight;
        float belly = float.NaN;
        foreach (var t in pc.GetComponentsInChildren<Transform>(true))
            if (t.name == "bottomSegmentCollider") { var c = t.GetComponent<Collider>(); if (c != null) belly = c.bounds.min.y - ground; break; }
        if (n > 0) { len /= n; rootY /= n; }
        float rootH = rootY - ground;
        bool fit = Settings.FitToBird && n > 0 && len > 0.1f;
        fitLen = fit ? len : 0;
        sim.S = fit ? Mathf.Clamp(len / T.LegLength * Mathf.Clamp(Settings.HeightBoost, 0.5f, 2f), 0.3f, 2f) : bodyScale;
        hipOffset = fit ? Mathf.Clamp(rb.position.y - rootY, 0f, 0.5f) : 0f;
        Plugin.L.LogInfo($"bird legs: length {len:F3} (nate {T.LegLength:F3}), roots {rootH:F3} over ground, belly bottom {belly:F3}, body centre {rbHeight:F3}, " +
                         $"root half width {(n == 2 ? (rigs[0].upper.position - rigs[1].upper.position).magnitude / 2 : 0):F3} (sim {T.HipHalfWidth * sim.S:F3}) " +
                         $"-> {(fit ? "fitted" : "NOT fitted")} x{Settings.HeightBoost:F2}: sim scale {sim.S:F2}, hip {hipOffset:F3} below centre, top lift {0.75f * sim.S - 0.2f * sim.S:F3} over ground");
    }

    // big walk updates can rename the bird's parts; say exactly what's gone instead of half working.
    // required: legs can't run without them. optional: the ragdoll / body shrink fall back without them
    static readonly string[] OptionalParts = { "pelvis_bone", "chest_bone", "head_bone", "bottomSegmentCollider", "torsoSegmentCollider",
        "headSegmentCollider", "hand_Target_bone_R", "hand_Target_bone_L", "arm_upright_bone" };
    bool warnedOptional;

    bool RigOk(PlayerCharacter pc)
    {
        model = null;
        foreach (var r in rigs) { r.bone = r.upper = r.lower = r.lowerEnd = r.knee = null; }
        FindBones(pc);
        var missing = new List<string>();
        if (model == null) missing.Add("dummy (model root with Armature)");
        string[] side = { "R", "L" };
        for (int i = 0; i < 2; i++)
        {
            var r = rigs[i];
            if (r.bone == null) missing.Add("foot_Target_bone_" + side[i]);
            if (r.upper == null) missing.Add($"SH_leg_upper_bone_{side[i]} under hipBone_bone_{side[i]}");
            else if (r.lower == null || r.lowerEnd == null) missing.Add($"leg_lower_bone (+ child) under SH_leg_upper_bone_{side[i]}");
            if (r.knee == null) missing.Add((i == 0 ? "rightLeg" : "leftLeg") + " LimbSpline handle");
        }
        bool capsuleOk = false;
        var names = new HashSet<string>();
        foreach (var t in pc.GetComponentsInChildren<Transform>(true))
        {
            names.Add(t.name);
            if (t.name == "playerCollider" && t.GetComponent<CapsuleCollider>() != null) capsuleOk = true;
        }
        if (!capsuleOk) missing.Add("playerCollider (CapsuleCollider)");
        if (missing.Count > 0)
        {
            if (model == null && capsuleOk) Plugin.L.LogWarning("legs NOT turned on: the bird's model isn't on its body right now (riding, sitting or posed?) - try again in a moment. missing: " + string.Join(", ", missing));
            else Plugin.L.LogError("legs NOT turned on, big walk's bird changed. missing: " + string.Join(", ", missing));
            return false;
        }
        if (!warnedOptional)
        {
            warnedOptional = true;
            var opt = new List<string>();
            foreach (var n in OptionalParts) if (!names.Contains(n)) opt.Add(n);
            if (opt.Count > 0) Plugin.L.LogWarning("bird parts missing, falls/body fit will be rougher: " + string.Join(", ", opt));
        }
        return true;
    }

    // right before the player camera renders (after every LateUpdate, big walk's arm code included):
    // put the fallen body's bones back on the ragdoll so nothing pulls the arms upright
    internal void BeforeRender()
    {
        if (!active || !fallen || !ragdoll.Active) return;
        try { ragdoll.ApplyVisual(legSplines); } catch { }
    }

    // after the animator: model at the hip yaw, foot targets at nate's feet, our own knee bend, redraw the legs
    internal void LateUpdate()
    {
        if (active && fallen && ragdoll.Active)
        {
            RestoreModel();
            try { ragdoll.ApplyVisual(legSplines); }
            catch (Exception e) { Plugin.L.LogError("ragdoll visual failed: " + e); RigidFall(U(sim.HipVel.XZ)); }
            UpdateShadows();
            return;
        }
        if (!active || fallen || seated) { RestoreModel(); UpdateShadows(); return; }
        if (model != null)
        {
            if (!modelRotSaved) { modelLocalRot = model.localRotation; modelRotSaved = true; }
            modelPosed = true;
            var parent = model.parent;
            var parentYaw = Quaternion.Euler(0, parent.eulerAngles.y, 0);
            model.rotation = Quaternion.Euler(0, sim.HipYaw, 0) * Quaternion.Inverse(parentYaw) * (parent.rotation * modelLocalRot);
        }
        for (int i = 0; i < 2; i++)
        {
            if (rigs[i].bone != null) rigs[i].bone.position = U(sim.Feet[i].pos);
            SolveKnee(rigs[i], U(sim.Feet[i].pos));
        }
        foreach (var ls in legSplines) try { ls.Refresh(); } catch { }
        UpdateShadows();
    }

    // hands the model's yaw back once when we stop driving it; forcing it every frame fought big walk's own poses
    // (sitting on the floor the bird faced behind where you looked)
    bool modelPosed;
    void RestoreModel()
    {
        if (!modelPosed) return;
        modelPosed = false;
        if (model != null && modelRotSaved) model.localRotation = modelLocalRot;
    }

    // two bone ik: upper/lower leg bones point along their local +y; knee bends forward and stays above the ground
    void SolveKnee(FootRig r, Vector3 foot)
    {
        if (r.upper == null || r.lower == null || r.lowerEnd == null) return;
        Vector3 a = r.upper.position;
        float la = (r.lower.position - a).magnitude, lb = (r.lowerEnd.position - r.lower.position).magnitude;
        Vector3 toT = foot - a;
        float d = Mathf.Clamp(toT.magnitude, 1e-3f, (la + lb) * 0.999f);
        Vector3 dir = toT.sqrMagnitude > 1e-8f ? toT.normalized : Vector3.down;
        Vector3 fwd = U(V3.Yaw(sim.HipYaw, V3.forward));
        Vector3 pole = Vector3.ProjectOnPlane(fwd, dir);
        if (pole.sqrMagnitude < 1e-6f) pole = Vector3.ProjectOnPlane(Vector3.forward, dir);
        pole.Normalize();
        float cos = Mathf.Clamp((la * la + d * d - lb * lb) / (2 * la * d), -1, 1);
        Vector3 knee = a + dir * (la * cos) + pole * (la * Mathf.Sqrt(1 - cos * cos));
        // never bend the knee into the floor: lift it to just above the ground under it
        var g = Surface.Probe(knee + Vector3.up * 0.5f, 1.5f, Mask);
        if (g.hit && knee.y < g.point.y + 0.05f) knee.y = g.point.y + 0.05f;
        Vector3 end = a + dir * d;
        if (r.knee != null) r.knee.position = knee;
        r.upper.rotation = Quaternion.FromToRotation(r.upper.up, knee - a) * r.upper.rotation;
        r.lower.rotation = Quaternion.FromToRotation(r.lower.up, end - knee) * r.lower.rotation;
    }

    // ---------------- foot shadows (you can't see your feet past the round body) ----------------

    readonly GameObject[] shadows = new GameObject[2];
    static Material shadowMat;

    static Material ShadowMaterial()
    {
        if (shadowMat != null) return shadowMat;
        var dark = new Color(0.05f, 0.03f, 0.07f, 1f);
        Shader sh = null;
        foreach (var n in new[] { "Universal Render Pipeline/Unlit", "Unlit/Color", "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Lit" })
        {
            sh = Shader.Find(n);
            if (sh != null && sh.name == n) break;   // stripped shaders can come back as something else
            sh = null;
        }
        if (sh != null) shadowMat = new Material(sh);
        else
        {
            // copy the bird's own body material (the first renderer is the name tag's text, skip anything TMP)
            foreach (var r in Game.Pc.GetComponentsInChildren<MeshRenderer>(true))
            {
                var m = r.sharedMaterial;
                if (m == null || m.shader == null) continue;
                var n = m.shader.name;
                if (n.Contains("TextMesh") || n.Contains("TMP") || n.Contains("Text")) continue;
                shadowMat = new Material(m);
                break;
            }
            if (shadowMat == null) return null;
        }
        foreach (var prop in new[] { "_BaseColor", "_Color", "_MainColor", "_Tint", "_TintColor", "_ShadowColor" })
            if (shadowMat.HasProperty(prop)) shadowMat.SetColor(prop, dark);
        foreach (var prop in new[] { "_EmissionColor", "_RimColor", "_SpecColor", "_HighlightColor" })
            if (shadowMat.HasProperty(prop)) shadowMat.SetColor(prop, Color.black);
        foreach (var prop in new[] { "_BaseMap", "_MainTex" })
            if (shadowMat.HasProperty(prop)) shadowMat.SetTexture(prop, null);
        return shadowMat;
    }

    void UpdateShadows()
    {
        for (int i = 0; i < 2; i++)
        {
            var f = sim.Feet[i];
            if (shadows[i] == null)
            {
                if (Game.Pc == null || ShadowMaterial() == null) return;
                var m = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                UnityEngine.Object.Destroy(m.GetComponent<Collider>());
                UnityEngine.Object.DontDestroyOnLoad(m);
                var r = m.GetComponent<Renderer>();
                if (r != null) { r.material = ShadowMaterial(); r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; }
                shadows[i] = m;
            }
            var sh = shadows[i];
            bool show = active && !fallen && !seated && Plugin.ShowFootShadows.Value && Game.Pc != null;
            if (!show) { sh.SetActive(false); continue; }
            // the whole foot: centered 0.07 forward of the ankle, footSize 0.08 x 0.22
            V3 fwd = V3.Yaw(f.yaw, V3.forward);
            Vector3 center = U(f.pos + fwd * (0.07f * bodyScale));
            var g = Surface.Probe(center + Vector3.up * 0.3f, 3f, Mask);
            sh.SetActive(g.hit);
            if (!g.hit) continue;
            float h = Mathf.Max(0, f.pos.y - g.point.y);
            float k = Mathf.Lerp(1f, 0.6f, Mathf.Clamp01(h / 0.4f));      // shrinks as the foot rises
            sh.transform.position = g.point + g.normal * 0.01f;
            sh.transform.rotation = Quaternion.FromToRotation(Vector3.up, g.normal) * Quaternion.Euler(0, f.yaw, 0);
            sh.transform.localScale = new Vector3(0.11f * bodyScale * k, 0.002f, 0.26f * bodyScale * k);
        }
    }
}
