using System;
using System.Collections.Generic;
using UnityEngine;

namespace BigSteps;

// other modded players' legs. each one sends its nate state ~20 times a second over Net (walking: hip yaw + feet;
// fallen: the ragdoll-driven bones), relative to its body so it sits on the body big walk syncs. we play the
// snapshots ~0.1 s behind, interpolated (the Baby Steps Multiplayer mod's snapshot buffer idea), and pose that
// player's bird after big walk has animated it. vanilla players never get these packets and see big walk's walk
static class Remotes
{
    const float SendEvery = 0.05f, Delay = 0.1f, Stale = 1f;
    const byte FWalk = 1, FFallen = 2;

    internal struct Snap
    {
        public float t;                 // sender clock
        public byte flags;
        public float yaw;
        public Vector3 fR, fL;          // walking feet, relative to the body
        public float hR, hL;            // their height above the ground under them
        public Vector3[] pos;           // fallen: pelvis, chest, head (relative)
        public Quaternion[] rot;
        public Vector3[] tips;          // fallen: foot R, foot L, hand R, hand L (relative)
    }

    class Remote
    {
        public Net.Peer peer;
        public PlayerCharacter pc;
        public BirdRig rig;
        public readonly List<Snap> buf = new();
        public float offset = float.NaN;     // our clock - theirs, smoothed from the fastest packets
        public float lastRecv;
        public bool told;
    }

    static readonly Dictionary<Net.Peer, Remote> remotes = new();
    static bool hooked;
    static float nextSend;
    static BirdRig localRig;
    static PlayerCharacter localRigPc;

    internal static void Hook()
    {
        if (hooked) return;
        hooked = true;
        Net.Received += OnReceived;
    }

    // ---------------- sending (our legs) ----------------

    // after Legs.LateUpdate has posed our bird
    internal static void SendLocal()
    {
        Hook();
        if (Time.unscaledTime < nextSend || Net.ModdedCount == 0) return;
        nextSend = Time.unscaledTime + SendEvery;
        var pc = Game.Pc;
        if (pc == null || pc.rb == null) return;
        Vector3 body = pc.rb.transform.position;
        var w = new Net.Writer(Net.Kind.Legs);
        w.F(Time.unscaledTime);
        if (Legs.IsActive && !Legs.Paused && !Legs.IsFallen)
        {
            w.U8(FWalk);
            w.F(Legs.HipYaw);
            w.V(Legs.FootPos(0) - body);
            w.V(Legs.FootPos(1) - body);
            w.F(Legs.FootLift(0));
            w.F(Legs.FootLift(1));
        }
        else if (Legs.IsActive && Legs.IsFallen)
        {
            if (localRig == null || localRigPc == null || localRigPc.Pointer != pc.Pointer) { localRig = new BirdRig(pc); localRigPc = pc; }
            var r = localRig;
            if (r.pelvis == null || r.chest == null || r.head == null) { w.U8(0); }
            else
            {
                w.U8(FFallen);
                foreach (var t in new[] { r.pelvis, r.chest, r.head }) { w.V(t.position - body); Q(w, t.rotation); }
                foreach (var t in new[] { r.legs[0].target, r.legs[1].target, r.hands[0], r.hands[1] }) w.V((t != null ? t.position : body) - body);
            }
        }
        else w.U8(0);     // legs off / seated: big walk's own animation
        Net.SendAll(w.Done(), false);
    }

    static void Q(Net.Writer w, Quaternion q) { w.F(q.x); w.F(q.y); w.F(q.z); w.F(q.w); }
    static Quaternion Q(Net.Reader r) { var q = new Quaternion(r.F(), r.F(), r.F(), r.F()); return Quaternion.Normalize(q); }

    // ---------------- receiving ----------------

    static void OnReceived(Net.Peer p, Net.Kind k, byte[] d, int start)
    {
        if (k == Net.Kind.Bye) { Drop(p); return; }
        if (k != Net.Kind.Legs) return;
        var r = new Net.Reader(d, d.Length, start);
        var s = new Snap { t = r.F(), flags = r.U8() };
        if ((s.flags & FWalk) != 0) { s.yaw = r.F(); s.fR = r.V(); s.fL = r.V(); s.hR = r.F(); s.hL = r.F(); }
        else if ((s.flags & FFallen) != 0)
        {
            s.pos = new Vector3[3]; s.rot = new Quaternion[3]; s.tips = new Vector3[4];
            for (int i = 0; i < 3; i++) { s.pos[i] = r.V(); s.rot[i] = Q(r); }
            for (int i = 0; i < 4; i++) s.tips[i] = r.V();
        }
        if (!remotes.TryGetValue(p, out var rm)) remotes[p] = rm = new Remote { peer = p };
        float now = Time.unscaledTime;
        float off = now - s.t;
        // the fastest packet sets the clock offset; it drifts up slowly so a one-off lucky packet doesn't stick
        rm.offset = float.IsNaN(rm.offset) || off < rm.offset ? off : rm.offset + (off - rm.offset) * 0.01f;
        rm.lastRecv = now;
        if (rm.buf.Count > 0 && s.t <= rm.buf[rm.buf.Count - 1].t) return;    // late / duplicate
        rm.buf.Add(s);
        if (rm.buf.Count > 40) rm.buf.RemoveRange(0, rm.buf.Count - 40);
        if (!rm.told) { rm.told = true; Plugin.L.LogInfo($"net: getting {p.name}'s legs"); }
    }

    static void Drop(Net.Peer p)
    {
        if (!remotes.TryGetValue(p, out var rm)) return;
        try { rm.rig?.Restore(); } catch { }
        remotes.Remove(p);
    }

    // ---------------- posing their birds ----------------

    // after big walk's LateUpdate animation, and again right before our camera renders (its arm code runs late)
    internal static void Pose()
    {
        if (remotes.Count == 0) return;
        List<Net.Peer> gone = null;
        foreach (var kv in remotes)
        {
            var rm = kv.Value;
            if (!Net.Peers.ContainsValue(rm.peer) || !rm.peer.modded) { (gone ??= new()).Add(kv.Key); continue; }
            try { PoseOne(rm); }
            catch (Exception e) { Plugin.L.LogWarning($"net: posing {rm.peer.name}: {e.Message}"); (gone ??= new()).Add(kv.Key); }
        }
        if (gone != null) foreach (var p in gone) Drop(p);
    }

    static void PoseOne(Remote rm)
    {
        var pc = rm.peer.pc;
        if (pc == null || pc.WasCollected) { rm.rig = null; return; }
        if (rm.rig == null || rm.pc == null || rm.pc.Pointer != pc.Pointer)
        {
            rm.pc = pc; rm.rig = new BirdRig(pc);
            if (!rm.rig.Ok) Plugin.L.LogWarning($"net: {rm.peer.name}'s bird is missing leg bones, showing big walk's walk");
        }
        if (!rm.rig.Ok) return;
        if (rm.buf.Count == 0 || Time.unscaledTime - rm.lastRecv > Stale) { rm.rig.Restore(); return; }

        // their clock time we show now
        float t = Time.unscaledTime - rm.offset - Delay;
        int i = rm.buf.Count - 1;
        while (i > 0 && rm.buf[i - 1].t > t) i--;
        Snap b = rm.buf[i], a = i > 0 ? rm.buf[i - 1] : b;
        float k = b.t > a.t ? Mathf.Clamp01((t - a.t) / (b.t - a.t)) : 1;
        // drop what's long played
        if (i > 2) rm.buf.RemoveRange(0, i - 2);

        Vector3 body = pc.rb != null ? pc.rb.transform.position : pc.transform.position;
        int mask = Game.Pc != null ? Game.Pc.ground.layerMask.value : ~0;
        var s = k < 0.5f ? a : b;     // state switches at the midpoint
        if ((s.flags & FWalk) != 0 && (a.flags & FWalk) != 0 && (b.flags & FWalk) != 0)
        {
            float yaw = Mathf.LerpAngle(a.yaw, b.yaw, k);
            rm.rig.PoseWalk(yaw, OnGround(body + Vector3.Lerp(a.fR, b.fR, k), Mathf.Lerp(a.hR, b.hR, k), body, mask, pc.transform),
                                 OnGround(body + Vector3.Lerp(a.fL, b.fL, k), Mathf.Lerp(a.hL, b.hL, k), body, mask, pc.transform), mask);
        }
        else if ((s.flags & FWalk) != 0) rm.rig.PoseWalk(s.yaw, OnGround(body + s.fR, s.hR, body, mask, pc.transform), OnGround(body + s.fL, s.hL, body, mask, pc.transform), mask);
        else if ((s.flags & FFallen) != 0)
        {
            bool both = (a.flags & FFallen) != 0 && (b.flags & FFallen) != 0;
            var pos = new Vector3[3]; var rot = new Quaternion[3]; var tips = new Vector3[4];
            for (int j = 0; j < 3; j++)
            {
                pos[j] = body + (both ? Vector3.Lerp(a.pos[j], b.pos[j], k) : s.pos[j]);
                rot[j] = both ? Quaternion.Slerp(a.rot[j], b.rot[j], k) : s.rot[j];
            }
            for (int j = 0; j < 4; j++) tips[j] = body + (both ? Vector3.Lerp(a.tips[j], b.tips[j], k) : s.tips[j]);
            rm.rig.PoseFallen(pos, rot, tips);
        }
        else rm.rig.Restore();
    }

    // the sender's body stands at its own height (height setting), big walk shows it at the standard one here: a foot
    // placed relative to the body went through the floor. it goes onto the ground under it, as high as it was there
    static Vector3 OnGround(Vector3 foot, float lift, Vector3 body, int mask, Transform bird)
    {
        if (Surface.Ray(new Vector3(foot.x, body.y + 0.5f, foot.z), Vector3.down, 3f, mask, out var h, bird)) foot.y = h.point.y + lift;
        return foot;
    }

    internal static string Status
    {
        get
        {
            int live = 0;
            foreach (var rm in remotes.Values) if (Time.unscaledTime - rm.lastRecv < Stale) live++;
            return $"legs from {live}";
        }
    }
}
