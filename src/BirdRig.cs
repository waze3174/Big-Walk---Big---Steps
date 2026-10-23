using System.Collections.Generic;
using UnityEngine;

namespace BigSteps;

// the bones of one bird that the legs and the ragdoll pose: used for other players' birds (Remote) and to read our own
// fallen pose for sending. same bone picks as Legs.FindBones / Ragdoll.Find
class BirdRig
{
    public class Leg { public Transform target, upper, lower, lowerEnd, knee; }
    public readonly Leg[] legs = { new(), new() };          // 0 = right, 1 = left
    public Transform model, pelvis, chest, head;
    public readonly Transform[] hands = new Transform[2];
    public readonly List<LimbSpline> legSplines = new(), allSplines = new();
    Quaternion modelLocalRot = Quaternion.identity;
    bool modelRotSaved, posed;
    public bool Ok => model != null && legs[0].target != null && legs[1].target != null && legs[0].upper != null && legs[1].upper != null;

    public BirdRig(PlayerCharacter pc)
    {
        foreach (var t in pc.GetComponentsInChildren<Transform>(true))
        {
            switch (t.name)
            {
                case "dummy": if (model == null && t.GetComponentInChildren<LimbSpline>(true) == null && t.Find("Armature") != null) model = t; break;
                case "foot_Target_bone_R": legs[0].target = t; break;
                case "foot_Target_bone_L": legs[1].target = t; break;
                case "SH_leg_upper_bone_R": if (t.parent != null && t.parent.name == "hipBone_bone_R") legs[0].upper = t; break;
                case "SH_leg_upper_bone_L": if (t.parent != null && t.parent.name == "hipBone_bone_L") legs[1].upper = t; break;
                case "pelvis_bone": pelvis ??= t; break;
                case "chest_bone": chest ??= t; break;
                case "head_bone": head ??= t; break;
                case "hand_Target_bone_R": hands[0] ??= t; break;
                case "hand_Target_bone_L": hands[1] ??= t; break;
            }
        }
        foreach (var l in legs)
        {
            if (l.upper == null) continue;
            for (int i = 0; i < l.upper.childCount; i++)
            {
                var c = l.upper.GetChild(i);
                if (c.name.StartsWith("leg_lower_bone")) { l.lower = c; if (c.childCount > 0) l.lowerEnd = c.GetChild(0); }
            }
        }
        foreach (var ls in pc.GetComponentsInChildren<LimbSpline>(true))
        {
            allSplines.Add(ls);
            string n = ls.gameObject.name;
            if (n.ToLower().Contains("leg")) legSplines.Add(ls);
            if (n == "rightLeg") legs[0].knee = ls.handleTransform;
            if (n == "leftLeg") legs[1].knee = ls.handleTransform;
        }
    }

    // walking: model at the hip yaw, foot targets on the feet, knees bent forward (like Legs.LateUpdate)
    public void PoseWalk(float hipYaw, Vector3 footR, Vector3 footL, int mask)
    {
        posed = true;
        if (model != null)
        {
            if (!modelRotSaved) { modelLocalRot = model.localRotation; modelRotSaved = true; }
            var parent = model.parent;
            var parentYaw = Quaternion.Euler(0, parent.eulerAngles.y, 0);
            model.rotation = Quaternion.Euler(0, hipYaw, 0) * Quaternion.Inverse(parentYaw) * (parent.rotation * modelLocalRot);
        }
        Vector3 fwd = Quaternion.Euler(0, hipYaw, 0) * Vector3.forward;
        Foot(legs[0], footR, fwd, mask);
        Foot(legs[1], footL, fwd, mask);
        foreach (var ls in legSplines) try { ls.Refresh(); } catch { }
    }

    // fallen: the sender's ragdoll-driven bones, parents first
    public void PoseFallen(Vector3[] pos, Quaternion[] rot, Vector3[] tips)
    {
        if (model != null && modelRotSaved) model.localRotation = modelLocalRot;
        posed = true;
        if (pelvis != null) pelvis.SetPositionAndRotation(pos[0], rot[0]);
        if (chest != null) chest.SetPositionAndRotation(pos[1], rot[1]);
        if (head != null) head.SetPositionAndRotation(pos[2], rot[2]);
        if (legs[0].target != null) legs[0].target.position = tips[0];
        if (legs[1].target != null) legs[1].target.position = tips[1];
        if (hands[0] != null) hands[0].position = tips[2];
        if (hands[1] != null) hands[1].position = tips[3];
        foreach (var ls in allSplines) try { ls.Refresh(); } catch { }
    }

    public void Restore()
    {
        if (!posed) return;
        posed = false;
        if (model != null && modelRotSaved) model.localRotation = modelLocalRot;
    }

    static void Foot(Leg r, Vector3 foot, Vector3 fwd, int mask)
    {
        if (r.target != null) r.target.position = foot;
        if (r.upper == null || r.lower == null || r.lowerEnd == null) return;
        Vector3 a = r.upper.position;
        float la = (r.lower.position - a).magnitude, lb = (r.lowerEnd.position - r.lower.position).magnitude;
        Vector3 toT = foot - a;
        float d = Mathf.Clamp(toT.magnitude, 1e-3f, (la + lb) * 0.999f);
        Vector3 dir = toT.sqrMagnitude > 1e-8f ? toT.normalized : Vector3.down;
        Vector3 pole = Vector3.ProjectOnPlane(fwd, dir);
        if (pole.sqrMagnitude < 1e-6f) pole = Vector3.ProjectOnPlane(Vector3.forward, dir);
        pole.Normalize();
        float cos = Mathf.Clamp((la * la + d * d - lb * lb) / (2 * la * d), -1, 1);
        Vector3 knee = a + dir * (la * cos) + pole * (la * Mathf.Sqrt(1 - cos * cos));
        var g = Surface.Probe(knee + Vector3.up * 0.5f, 1.5f, mask);
        if (g.hit && knee.y < g.point.y + 0.05f) knee.y = g.point.y + 0.05f;
        Vector3 end = a + dir * d;
        if (r.knee != null) r.knee.position = knee;
        r.upper.rotation = Quaternion.FromToRotation(r.upper.up, knee - a) * r.upper.rotation;
        r.lower.rotation = Quaternion.FromToRotation(r.lower.up, end - knee) * r.lower.rotation;
    }
}
