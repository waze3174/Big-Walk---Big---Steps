using System;
using System.Collections.Generic;
using UnityEngine;

namespace BigSteps;

// floppy fall for the bird (baby steps: puppetmaster goes limp, body tumbles, then gets up).
// temporary bodies are built from the bird's own segment colliders (bottom/torso/head) plus feet and hands on
// leash joints; the real rigidbody turns kinematic and follows the pelvis so big walk keeps tracking the player
class Ragdoll
{
    class Part
    {
        public GameObject go;
        public Rigidbody rb;
        public Transform bone;          // bird bone this part drives
        public Quaternion boneOffset;   // bone rotation relative to the part
        public Vector3 boneLocalPos;    // bone position in part space
    }

    public bool Active { get; private set; }
    public bool GettingUp { get; private set; }
    public bool Done { get; private set; }

    Part pelvis, torso, head;
    readonly Part[] feet = new Part[2], hands = new Part[2];
    // the bird's solid colliders go non-solid (trigger) while down. switching them off made big walk's ground check
    // (which casts the bird's own collider every physics step) find no rigidbody on it and throw ~30 times a second,
    // and that also cut off big walk's look update: the camera froze while down
    readonly List<Collider> disabled = new();
    PlayerCharacter pc;
    float startTime, upStart;
    float scale, startY, voidSince = -1, nextVoidCheck;
    // parts got destroyed (scene change) or fell out of the world: give up and respawn at the last safe spot
    public bool Lost { get; private set; }
    // baby steps' BehaviourPuppet: get up once getUpDelay (0.55 s) has passed and the hip muscle is slower than
    // maxGetUpVelocity (0.2). the get-up takes longer the longer you were down: in recordings 3.33 s / GetUpSpeedOverDuration(time down)
    const float GetUpDelay = 0.55f, MaxGetUpVelocity = 0.2f, GetUpBase = 3.33f;
    static readonly BigSteps.Sim.Curve GetUpSpeed = new(
        -0.014665f, 1.5324786f, 0.024473239f, 0.024473239f,
        2.9049942f, 1.2666106f, -0.13229683f, -0.13229683f,
        5.001263f, 1.0038245f, -0.16116248f, -0.16116248f);
    float GetUpTime = 2.2f;
    // get-up pose capture
    Vector3 upPelvisFrom, upPelvisTo;
    Quaternion upRotFrom, upRotTo, torsoFrom, headFrom;
    readonly Vector3[] upFeetFrom = new Vector3[2], upFeetTo = new Vector3[2];
    float upYaw;

    Transform Find(string name)
    {
        foreach (var t in pc.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
        return null;
    }

    Part MakePart(string name, Transform bone, Vector3 at, Quaternion rot, float radius, float mass, int layer)
    {
        var go = new GameObject("bbs_" + name);
        go.layer = layer;
        go.transform.SetPositionAndRotation(at, rot);
        var sc = go.AddComponent<SphereCollider>();
        sc.radius = radius;
        try { sc.sharedMaterial = Mat(); } catch { }
        var rb = go.AddComponent<Rigidbody>();
        rb.mass = mass;
        rb.linearDamping = 0.05f;
        // round parts would roll down any slope forever: damp the spin so they slide and friction stops them
        rb.angularDamping = radius > 0.1f * scale ? 2.5f : 1f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.solverIterations = 12;
        var p = new Part { go = go, rb = rb, bone = bone };
        if (bone != null)
        {
            p.boneOffset = Quaternion.Inverse(rot) * bone.rotation;
            p.boneLocalPos = go.transform.InverseTransformPoint(bone.position);
        }
        return p;
    }

    // baby steps' ragdoll: DynamicMaterial 0.4/0.4, no bounce. scaled by the grip of what we're lying on
    // (ice slides on, wood holds) and combined Maximum so big walk's own ground materials don't decide it
    PhysicsMaterial mat;
    PhysicsMaterial Mat()
    {
        if (mat != null) return mat;
        mat = new PhysicsMaterial { name = "bbs_ragdoll" };
        mat.dynamicFriction = mat.staticFriction = 0.4f;
        mat.bounciness = 0;
        mat.frictionCombine = PhysicsMaterialCombine.Maximum;
        mat.bounceCombine = PhysicsMaterialCombine.Minimum;
        return mat;
    }

    void UpdateFriction(Func<Vector3, (bool hit, Vector3 point)> ground)
    {
        if (mat == null) return;
        var s = Surface.Probe(pelvis.rb.position + Vector3.up * 0.2f * scale, 1.5f * scale, Game.Pc.ground.layerMask.value);
        float f = 0.4f * (s.hit ? s.grip / 0.91f : 1f);
        mat.dynamicFriction = f;
        mat.staticFriction = f * 1.1f;
    }

    // big walk's build strips ConfigurableJoint limits/drives (struct properties), so: lock the position with a
    // ConfigurableJoint and drive the rotation ourselves (Drive below), like a slerp drive with a swing limit
    class Socket { public Part child, parent; public Quaternion rest; public float swing, spring, damper; }
    readonly List<Socket> sockets = new();

    void Hinge(Part child, Part parent, float swing, float spring, float damper)
    {
        var j = child.go.AddComponent<ConfigurableJoint>();
        j.connectedBody = parent.rb;
        j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
        j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Free;
        j.enablePreprocessing = false;
        sockets.Add(new Socket
        {
            child = child, parent = parent, swing = swing, spring = spring, damper = damper,
            rest = Quaternion.Inverse(parent.go.transform.rotation) * child.go.transform.rotation,
        });
    }

    void Drive()
    {
        foreach (var k in sockets)
        {
            var want = k.parent.rb.rotation * k.rest;
            var delta = want * Quaternion.Inverse(k.child.rb.rotation);
            delta.ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180) ang -= 360;
            if (float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-6f) continue;
            float gain = k.spring * (Mathf.Abs(ang) > k.swing ? 6f : 1f);   // past the swing limit: stiff
            Vector3 rel = k.child.rb.angularVelocity - k.parent.rb.angularVelocity;
            Vector3 torque = axis * (ang * Mathf.Deg2Rad * gain) - rel * k.damper;
            k.child.rb.AddTorque(torque, ForceMode.Acceleration);
            k.parent.rb.AddTorque(-torque * (k.child.rb.mass / k.parent.rb.mass), ForceMode.Acceleration);
        }
    }

    // a rope: free inside its length, pulls back beyond it (done by hand, SpringJoint may be stripped from the game)
    class Rope { public Part a, b; public float length; }
    readonly List<Rope> ropes = new();

    void Leash(Part a, Part b, float length) => ropes.Add(new Rope { a = a, b = b, length = length });

    void PullRopes()
    {
        foreach (var r in ropes)
        {
            Vector3 d = r.a.rb.position - r.b.rb.position;
            float m = d.magnitude;
            if (m <= r.length || m < 1e-5f) continue;
            Vector3 n = d / m;
            float stretch = m - r.length;
            float relV = Vector3.Dot(r.a.rb.linearVelocity - r.b.rb.linearVelocity, n);
            float acc = stretch * 900f + Mathf.Max(0, relV) * 25f;
            float wa = r.b.rb.mass / (r.a.rb.mass + r.b.rb.mass);
            r.a.rb.AddForce(-n * acc * wa, ForceMode.Acceleration);
            r.b.rb.AddForce(n * acc * (1 - wa) * (r.a.rb.mass / r.b.rb.mass), ForceMode.Acceleration);
        }
    }

    // a layer that collides with the ground under us. big walk's layer matrix is its own: Default goes straight
    // through its terrain (0.3.3), the rigidbody's VoiceTriggers hits nothing solid. prefer a non-player layer,
    // fall back to the playerCollider's layer, which is known to collide
    static readonly string[] Prefer = { "Ragdoll", "Debris", "PropsDynamic", "Props", "Default" };
    static readonly HashSet<string> Avoid = new() { "VoiceTriggers", "CameraTriggers", "TextChatUI", "UI", "Ignore Raycast", "Water", "TransparentFX" };

    int PickLayer(PlayerCharacter pc)
    {
        int playerLayer = -1;
        foreach (var c in pc.GetComponentsInChildren<Collider>(false))
            if (c.gameObject.name == "playerCollider") { playerLayer = c.gameObject.layer; break; }
        if (playerLayer < 0) playerLayer = LayerMask.NameToLayer("Player");
        try
        {
            if (!Surface.Ray(pc.rb.position + Vector3.up * 0.5f, Vector3.down, 5f, pc.ground.layerMask.value, out var hit)) return playerLayer;
            int ground = hit.collider.gameObject.layer;
            bool Hits(int l) => !Physics.GetIgnoreLayerCollision(l, ground);
            var birdLayers = new HashSet<int>();
            foreach (var c in pc.GetComponentsInChildren<Collider>(true)) birdLayers.Add(c.gameObject.layer);
            int pick = -1;
            foreach (var name in Prefer)
            {
                int l = LayerMask.NameToLayer(name);
                if (l >= 0 && !birdLayers.Contains(l) && Hits(l)) { pick = l; break; }
            }
            if (pick < 0)
                for (int l = 0; l < 32 && pick < 0; l++)
                {
                    string n = LayerMask.LayerToName(l);
                    if (!string.IsNullOrEmpty(n) && !birdLayers.Contains(l) && !Avoid.Contains(n) && Hits(l)) pick = l;
                }
            if (pick < 0) pick = playerLayer;
            return pick;
        }
        catch (Exception e) { Plugin.L.LogWarning("ragdoll layer pick failed, using the player's: " + e.Message); return playerLayer; }
    }


    // big walk's own ground/feet/crouch/cast checks scan the player layer too: they found the ragdoll parts, looked
    // for big walk data on them and threw every frame (~300 errors a fall), which also cut off its look update
    // (camera frozen while down). take the ragdoll's layer out of those masks while it exists
    readonly List<(Action<int> set, int mask)> savedMasks = new();

    void HideFromBirdChecks(PlayerCharacter pc, int layer)
    {
        savedMasks.Clear();
        int bit = 1 << layer;
        void Take(Func<int> get, Action<int> set)
        {
            try { int m = get(); if ((m & bit) == 0) return; savedMasks.Add((set, m)); set(m & ~bit); }
            catch (Exception e) { Plugin.L.LogWarning("mask: " + e.Message); }
        }
        Take(() => pc.ground.layerMask.value, v => pc.ground.layerMask = new LayerMask { value = v });
        Take(() => pc.feet.layerMask.value, v => pc.feet.layerMask = new LayerMask { value = v });
        Take(() => pc.croucher.layerMask.value, v => pc.croucher.layerMask = new LayerMask { value = v });
        Take(() => pc.caster.layerMask.value, v => pc.caster.layerMask = new LayerMask { value = v });
    }

    void RestoreBirdChecks()
    {
        foreach (var (set, mask) in savedMasks) try { set(mask); } catch { }
        savedMasks.Clear();
    }

    public void Begin(PlayerCharacter pc, Vector3 vel, Vector3 tipDir, Vector3 footR, Vector3 footL, float scale, float yaw)
    {
        upYaw = yaw;
        this.pc = pc;
        this.scale = scale;
        Active = true; GettingUp = false; Done = false; Lost = false;
        sockets.Clear();
        ropes.Clear();
        startTime = Time.time;
        voidSince = -1; nextVoidCheck = 0; draggedSteps = 0;
        platVel = Vector3.zero;

        var pelvisBone = Find("pelvis_bone");
        var chest = Find("chest_bone");
        var headBone = Find("head_bone");
        var bottomSeg = Find("bottomSegmentCollider");
        var torsoSeg = Find("torsoSegmentCollider");
        var headSeg = Find("headSegmentCollider");
        int layer = PickLayer(pc);
        HideFromBirdChecks(pc, layer);
        foreach (var c in pc.GetComponentsInChildren<Collider>(false))
        {
            if (!c.enabled || c.isTrigger) continue;
            c.isTrigger = true; disabled.Add(c);
        }
        startY = pc.rb.position.y;

        float R(Transform seg, float fallback)
        {
            var sc = seg != null ? seg.GetComponent<SphereCollider>() : null;
            if (sc == null) return fallback * scale;
            var ls = seg.lossyScale;
            return sc.radius * Mathf.Max(ls.x, Mathf.Max(ls.y, ls.z));
        }
        Vector3 P(Transform seg, Transform bone, Vector3 fallback) => seg != null ? seg.position : bone != null ? bone.position : fallback;

        var root = pc.rb.position;
        pelvis = MakePart("pelvis", pelvisBone, P(bottomSeg, pelvisBone, root), pelvisBone != null ? pelvisBone.rotation : pc.transform.rotation, Mathf.Min(R(bottomSeg, 0.3f), 0.35f * scale), 3f, layer);
        torso = MakePart("torso", chest, P(torsoSeg, chest, root + Vector3.up * 0.4f * scale), chest != null ? chest.rotation : pc.transform.rotation, R(torsoSeg, 0.24f), 2f, layer);
        head = MakePart("head", headBone, P(headSeg, headBone, root + Vector3.up * 0.8f * scale), headBone != null ? headBone.rotation : pc.transform.rotation, R(headSeg, 0.25f), 1.2f, layer);
        var unyaw = Quaternion.Inverse(Quaternion.Euler(0, yaw, 0));
        restPelvis = unyaw * pelvis.go.transform.rotation;
        restTorso = unyaw * torso.go.transform.rotation;
        restHead = unyaw * head.go.transform.rotation;
        Hinge(torso, pelvis, 35, 60, 6);
        Hinge(head, torso, 30, 40, 4);
        var fr = Find("foot_Target_bone_R"); var fl = Find("foot_Target_bone_L");
        feet[0] = MakePart("footR", fr, footR, Quaternion.identity, 0.06f * scale, 0.35f, layer);
        feet[1] = MakePart("footL", fl, footL, Quaternion.identity, 0.06f * scale, 0.35f, layer);
        foreach (var f in feet) Leash(f, pelvis, 0.75f * scale);
        var hr = Find("hand_Target_bone_R"); var hl = Find("hand_Target_bone_L");
        hands[0] = MakePart("handR", hr, hr != null ? hr.position : torso.go.transform.position, Quaternion.identity, 0.05f * scale, 0.2f, layer);
        hands[1] = MakePart("handL", hl, hl != null ? hl.position : torso.go.transform.position, Quaternion.identity, 0.05f * scale, 0.2f, layer);
        foreach (var h in hands) Leash(h, torso, 0.6f * scale);
        // arm splines: shoulders ride the torso part, hands on the hand parts, elbow sagging between
        arms.Clear();
        armSaved.Clear();
        foreach (var ls in pc.GetComponentsInChildren<LimbSpline>(true))
        {
            string n = ls.gameObject.name.ToLower();
            if (!n.Contains("arm") || ls.startTransform == null || ls.endTransform == null) continue;
            // PutArms moves these directly; nothing in big walk puts them back (first person arms drifted further each fall)
            foreach (var t in new[] { ls.startTransform, ls.endTransform, ls.handleTransform })
                if (t != null) armSaved.Add((t, t.localPosition, t.localRotation));
            bool left = n.Contains("left");
            arms.Add(new Arm
            {
                spline = ls, hand = hands[left ? 1 : 0],
                startLocal = torso.go.transform.InverseTransformPoint(ls.startTransform.position),
                handRest = torso.go.transform.InverseTransformPoint(hands[left ? 1 : 0].go.transform.position),
            });
        }

        var all = All();
        for (int i = 0; i < all.Count; i++)
            for (int k = i + 1; k < all.Count; k++)
                Physics.IgnoreCollision(all[i].go.GetComponent<Collider>(), all[k].go.GetComponent<Collider>(), true);
        // and no contact (or trigger messages) between the parts and the bird's own colliders
        foreach (var p in all)
        {
            var pc0 = p.go.GetComponent<Collider>();
            foreach (var c in disabled) if (c != null) Physics.IgnoreCollision(pc0, c, true);
        }
        try { IgnoreItem(pc.hands?.heldProp); } catch { }
        foreach (var p in all) p.rb.linearVelocity = vel;
        // tip over away from the feet, top heavy
        Vector3 axis = Vector3.Cross(Vector3.up, tipDir.sqrMagnitude > 1e-4f ? tipDir.normalized : pc.transform.forward);
        pelvis.rb.angularVelocity = axis * 3f;
        torso.rb.AddForce(tipDir.normalized * 2.5f + Vector3.up * 0.5f, ForceMode.VelocityChange);
        head.rb.AddForce(tipDir.normalized * 3f, ForceMode.VelocityChange);

        // the bird's body follows the pelvis by velocity, like walking does. making it kinematic froze big walk's look
        // (camera/right stick) for the whole fall. its colliders are off, so it can't bump into anything
        pc.rb.linearVelocity = Vector3.zero;
    }

    class Arm { public LimbSpline spline; public Part hand; public Vector3 startLocal, handRest, handFrom; }
    readonly List<Arm> arms = new();
    readonly List<(Transform t, Vector3 pos, Quaternion rot)> armSaved = new();

    void PutArms()
    {
        foreach (var a in arms)
        {
            var ls = a.spline;
            if (ls == null || a.hand?.go == null) continue;
            Vector3 sh = torso.go.transform.TransformPoint(a.startLocal);
            Vector3 hp = a.hand.go.transform.position;
            ls.startTransform.position = sh;
            ls.endTransform.position = hp;
            if (ls.handleTransform != null) ls.handleTransform.position = (sh + hp) * 0.5f + Vector3.down * (0.06f * scale);
            try { ls.Refresh(); } catch { }
        }
    }

    // the held item hangs under the bird, so the fall made its colliders non-solid too: dropped while down it went
    // through the world. dropped, it gets them back (and keeps not touching the parts)
    public void ReleaseItem(Prop prop)
    {
        if (prop == null) return;
        int n = 0;
        for (int i = disabled.Count - 1; i >= 0; i--)
        {
            var c = disabled[i];
            if (c == null || !c.transform.IsChildOf(prop.transform)) continue;
            c.isTrigger = false; disabled.RemoveAt(i); n++;
        }
        IgnoreItem(prop);
        if (n > 0) Plugin.L.LogInfo($"drop: gave the item its {n} collider(s) back (the fall had made them non-solid)");
    }

    // the held (or just dropped) item and the parts don't touch: an item wedged between a part and the floor got
    // squeezed through it. the pairs go when the parts are destroyed
    public void IgnoreItem(Prop prop)
    {
        if (prop == null || pelvis == null) return;
        var cols = prop.propColliders;
        var own = prop.GetComponentsInChildren<Collider>(true);
        foreach (var p in All())
        {
            var pc0 = p?.go != null ? p.go.GetComponent<Collider>() : null;
            if (pc0 == null) continue;
            if (cols != null) foreach (var c in cols) if (c != null) Physics.IgnoreCollision(pc0, c, true);
            foreach (var c in own) if (c != null) Physics.IgnoreCollision(pc0, c, true);
        }
    }

    List<Part> All()
    {
        var l = new List<Part> { pelvis, torso, head };
        l.AddRange(feet); l.AddRange(hands);
        return l;
    }

    // physics step: big walk's body follows the pelvis; decide when to get up
    public void FixedStep(Func<Vector3, (bool hit, Vector3 point)> ground)
    {
        if (!Active) return;
        foreach (var p in All()) if (p == null || p.go == null || p.rb == null) { Lost = true; return; }
        // out of the world = deep below the start with nothing at all under it (a long fall onto ground is just a fall)
        if (pelvis.rb.position.y < startY - 15f * scale && Time.time > nextVoidCheck)
        {
            nextVoidCheck = Time.time + 0.25f;
            var below = Surface.Probe(pelvis.rb.position + Vector3.up * 0.5f, 1000f, Game.Pc.ground.layerMask.value);
            if (!below.hit) { if (voidSince < 0) voidSince = Time.time; if (Time.time - voidSince > 1f) { Lost = true; return; } }
            else voidSince = -1;
        }
        var body = pc.rb;
        if (!GettingUp)
        {
            Drive();
            PullRopes();
            try { UpdateFriction(ground); } catch { }
            Follow(body, pelvis.rb.position);
            float speed = (pelvis.rb.linearVelocity - platVel).magnitude;    // relative to the ground it lies on
            float t = Time.time - startTime;
            // like baby steps: keep sliding until friction stops you (steep slopes slide you all the way down)
            if ((t > GetUpDelay && speed < MaxGetUpVelocity * scale) || t > 40f)
            {
                GetUpTime = GetUpBase / Mathf.Max(0.5f, GetUpSpeed.Evaluate(t));
                StartGetUp(ground);
            }
        }
        else
        {
            float k = Mathf.Clamp01((Time.time - upStart) / GetUpTime);
            float e = k * k * (3 - 2 * k);
            var pos = Vector3.Lerp(upPelvisFrom, upPelvisTo, e);
            // up and over: a little arc so it reads as pushing up off the ground
            pos.y += Mathf.Sin(k * Mathf.PI) * 0.12f * scale;
            pelvis.go.transform.SetPositionAndRotation(pos, Quaternion.Slerp(upRotFrom, upRotTo, e));
            // torso/head ride on the pelvis, swinging upright a little ahead of it
            var pr = pelvis.go.transform.rotation;
            torso.go.transform.SetPositionAndRotation(pos + pr * torsoRel, Quaternion.Slerp(torsoFrom, upTorsoTo, Mathf.Clamp01(e * 1.2f)));
            head.go.transform.SetPositionAndRotation(pos + pr * headRel, Quaternion.Slerp(headFrom, upHeadTo, Mathf.Clamp01(e * 1.4f)));
            for (int i = 0; i < 2; i++)
            {
                // feet come in under the body in the first half, planted for the push
                float fk = Mathf.Clamp01(k * 2f);
                var fp = Vector3.Lerp(upFeetFrom[i], upFeetTo[i], fk * fk * (3 - 2 * fk));
                fp.y += Mathf.Sin(fk * Mathf.PI) * 0.15f * scale;
                feet[i].go.transform.position = fp;
            }
            // hands come back to where they hang off the torso
            foreach (var a in arms)
                if (a.hand?.go != null)
                    a.hand.go.transform.position = Vector3.Lerp(a.handFrom, torso.go.transform.TransformPoint(a.handRest), e);
            Follow(body, pos);
            if (k >= 1) Done = true;
        }
    }

    static void Follow(Rigidbody body, Vector3 to)
    {
        if (body.isKinematic) return;     // big walk has it (ski lift): it can't take velocities
        body.linearVelocity = (to - body.position) / Time.fixedDeltaTime;
        body.angularVelocity = Vector3.zero;
    }

    void StartGetUp(Func<Vector3, (bool hit, Vector3 point)> ground)
    {
        GettingUp = true;
        upStart = Time.time;
        foreach (var p in All()) { p.rb.isKinematic = true; }
        foreach (var a in arms) if (a.hand?.go != null) a.handFrom = a.hand.go.transform.position;
        upPelvisFrom = pelvis.rb.position;
        var inv = Quaternion.Inverse(pelvis.go.transform.rotation);
        torsoRel = inv * (torso.rb.position - upPelvisFrom);
        headRel = inv * (head.rb.position - upPelvisFrom);
        upRotFrom = pelvis.go.transform.rotation;
        torsoFrom = torso.go.transform.rotation;
        headFrom = head.go.transform.rotation;
        // stand up over the ground under the pelvis, facing the way we were walking
        var g = ground(upPelvisFrom + Vector3.up * 1f);
        float gy = g.hit ? g.point.y : upPelvisFrom.y - 0.3f * scale;
        upPelvisTo = new Vector3(upPelvisFrom.x, gy + 0.75f * scale, upPelvisFrom.z);
        var yawQ = Quaternion.Euler(0, upYaw, 0);
        upRotTo = yawQ * restPelvis;
        upTorsoTo = yawQ * restTorso;
        upHeadTo = yawQ * restHead;
        var right = Quaternion.Euler(0, upYaw, 0) * Vector3.right;
        for (int i = 0; i < 2; i++)
        {
            upFeetFrom[i] = feet[i].rb.position;
            var to = new Vector3(upPelvisTo.x, gy, upPelvisTo.z) + right * ((i == 0 ? 1 : -1) * 0.12f * scale);
            var gf = ground(to + Vector3.up * 1f);
            upFeetTo[i] = gf.hit ? gf.point : to;
        }
    }

    // upright rotations of the parts with the yaw taken out (captured when the fall starts, body still upright-ish)
    Quaternion restPelvis = Quaternion.identity, restTorso = Quaternion.identity, restHead = Quaternion.identity;
    Quaternion upTorsoTo, upHeadTo;
    Vector3 torsoRel, headRel;

    // a part that ended up under the moving floor (a curve or slope change outran physx): back on top of it, moving up
    // with it. checked against the platform's own colliders only
    void Untunnel(Carrier c)
    {
        var plat = c.Platform;
        if (plat == null || plat.WasCollected) return;
        int mask = Game.Pc != null ? Game.Pc.ground.layerMask.value : ~0;
        foreach (var p in All())
        {
            if (p?.rb == null || p.rb.isKinematic) continue;
            var sc = p.go.GetComponent<SphereCollider>();
            float r = sc != null ? sc.radius : 0.08f * scale;
            Vector3 at = p.rb.position;
            // from just above (a sunk part is a few cm under; a roof or crate further up mustn't count as the floor)
            if (!Surface.Ray(at + Vector3.up * 0.4f, Vector3.down, 0.4f + r, mask, out var h) || h.collider == null) continue;
            if (!h.collider.transform.IsChildOf(plat)) continue;
            if (h.point.y <= at.y - r * 0.5f) continue;
            p.rb.position = new Vector3(at.x, h.point.y + r, at.z);
            var v = p.rb.linearVelocity;
            if (v.y < c.Vel.y) { v.y = c.Vel.y; p.rb.linearVelocity = v; }
            if (!toldTunnel) { toldTunnel = true; Plugin.L.LogInfo($"ragdoll: a part slipped under the moving floor ({h.point.y - at.y:F2} m), put back on top"); }
        }
    }
    bool toldTunnel;

    // relative to the floor it lies on
    public float PelvisSpeed => pelvis?.rb != null ? (pelvis.rb.linearVelocity - platVel).magnitude : 0f;

    public Vector3 PelvisVel => pelvis?.rb != null ? pelvis.rb.linearVelocity : Vector3.zero;
    public Vector3 PelvisPos => pelvis?.rb != null ? pelvis.rb.position : Vector3.zero;
    // rendered (interpolated) pelvis, for the camera
    public Vector3 PelvisDrawn => pelvis?.go != null ? pelvis.go.transform.position : PelvisPos;

    // moving ground (Carrier). loose parts ride by velocity: they get the platform's change in speed, the rest is
    // physx's friction, so a train that already drags them isn't added twice. the planned get-up rides by position
    internal Vector3 platVel;     // set by Begin's caller to the platform speed the parts were launched with
    // floors a script moves (the train's carriage, ski lift chairs) teleport under the parts, physx can't push them
    // along and they fall through on a curve: then the parts are moved with the floor instead, their velocities
    // staying relative to it (platVel 0). set by the caller per fall: moving floor = moved along
    internal bool byVelocity = true;
    int draggedSteps;

    public void Carry(Carrier c)
    {
        if (!Active) return;
        if (c.Active) startY += (c.Map(pelvis.rb.position) - pelvis.rb.position).y;
        if (!GettingUp) Untunnel(c);
        if (!GettingUp)
        {
            if (byVelocity)
            {
                // only a platform we stayed on accelerates us; sliding off one keeps the momentum
                Vector3 dv = c.Same ? c.Vel - platVel : Vector3.zero;
                platVel = c.Vel;
                foreach (var p in All()) if (p?.rb != null && !p.rb.isKinematic) p.rb.linearVelocity += dv;
                return;
            }
            platVel = Vector3.zero;
            if (!c.Active) return;
            foreach (var p in All())
            {
                if (p?.rb == null || p.rb.isKinematic) continue;
                p.rb.position = c.Map(p.rb.position);
                p.rb.rotation = c.DRot * p.rb.rotation;
            }
            // physx drags them along after all (pelvis keeps the floor's speed on top of our move): don't move them twice
            float pv = c.Vel.magnitude;
            if (pv > 0.5f && (pelvis.rb.linearVelocity - c.Vel).magnitude < 0.3f * pv) draggedSteps++; else draggedSteps = 0;
            if (draggedSteps > 15)
            {
                byVelocity = true; platVel = c.Vel;
                Plugin.L.LogInfo("ragdoll: physx already carries it on this floor, riding by velocity");
            }
            return;
        }
        if (!c.Active) return;
        upPelvisFrom = c.Map(upPelvisFrom); upPelvisTo = c.Map(upPelvisTo);
        for (int i = 0; i < 2; i++) { upFeetFrom[i] = c.Map(upFeetFrom[i]); upFeetTo[i] = c.Map(upFeetTo[i]); }
        foreach (var a in arms) a.handFrom = c.Map(a.handFrom);
        upRotFrom = c.DRot * upRotFrom; upRotTo = c.DRot * upRotTo;
        torsoFrom = c.DRot * torsoFrom; upTorsoTo = c.DRot * upTorsoTo;
        headFrom = c.DRot * headFrom; upHeadTo = c.DRot * upHeadTo;
        upYaw += c.DYaw;
    }

    public float UpYaw => upYaw;
    public Vector3 StandPos => upPelvisTo;
    public Vector3 FootPos(int i) => upFeetTo[i];

    // after the animator: put the bird's bones on the parts
    public void ApplyVisual(List<LimbSpline> splines)
    {
        if (!Active) return;
        Put(pelvis); Put(torso); Put(head);
        foreach (var f in feet) if (f.bone != null) f.bone.position = f.go.transform.position;
        foreach (var h in hands) if (h.bone != null) h.bone.position = h.go.transform.position;
        foreach (var ls in splines) try { ls.Refresh(); } catch { }
        PutArms();
    }

    static void Put(Part p)
    {
        if (p.bone == null) return;
        var t = p.go.transform;
        p.bone.SetPositionAndRotation(t.TransformPoint(p.boneLocalPos), t.rotation * p.boneOffset);
    }

    public void End()
    {
        if (!Active) return;
        // Destroy waits for the end of the frame: switch the parts off now, or they sit inside the bird's re-enabled
        // collider for a physics step and shove it over as it stands up
        foreach (var p in All()) if (p?.go != null) { p.go.SetActive(false); UnityEngine.Object.Destroy(p.go); }
        RestoreBirdChecks();
        foreach (var (t, lp, lr) in armSaved) if (t != null) t.SetLocalPositionAndRotation(lp, lr);
        armSaved.Clear();
        foreach (var c in disabled) if (c != null) c.isTrigger = false;
        disabled.Clear();
        if (pc != null && pc.rb != null && !pc.rb.isKinematic) pc.rb.linearVelocity = Vector3.zero;
        Active = false; GettingUp = false;
    }
}
