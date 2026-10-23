using System.Collections.Generic;
using UnityEngine;

namespace BigSteps;

// moving ground: big walk's StickyPlatform (rail cars, ...). big walk carries the player in its own movement code,
// which the legs replace, so the legs carry themselves: every physics step, how the platform under the body moved
// since the last step is applied to everything nate has pinned in the world (feet, targets, the ragdoll)
class Carrier
{
    Transform plat;
    Vector3 prevPos, fromPos, toPos;
    Quaternion prevRot;
    float lastSeen;
    readonly Dictionary<int, Transform> byCollider = new();
    readonly HashSet<string> told = new();
    public bool Active;                           // the platform moved this step: Map/DYaw are worth applying
    public bool Same;                             // same platform as last step (Vel changes are real accelerations)
    public Vector3 Vel;                           // platform velocity at the probed point
    public Transform Platform => plat;
    public bool PlatformHasBody;                  // physx moves it, so it pushes what lies on it (the train carriage)
    public Quaternion DRot = Quaternion.identity;
    public float DYaw;

    // where a world point riding the platform is now
    public Vector3 Map(Vector3 p) => toPos + DRot * (p - fromPos);

    public void Step(PlayerCharacter pc, Vector3 at, float down, int mask)
    {
        Vector3 was = Vel;
        Active = false; Same = false; Vel = Vector3.zero; DRot = Quaternion.identity; DYaw = 0;
        var found = Find(pc, at, down, mask);
        if (found != null) lastSeen = Time.time;
        // nothing under us (a gap in the floor, a foot over the edge, airborne): we keep the platform's motion until we
        // land on something else. only a different floor ends the ride
        else if (plat != null && !plat.WasCollected && Time.time - lastSeen < 2f) found = plat;
        if (found == null || found.WasCollected) { plat = null; return; }
        if (plat == null || found.Pointer != plat.Pointer)
        {
            plat = found; prevPos = plat.position; prevRot = plat.rotation;
            PlatformHasBody = plat.GetComponentInParent<Rigidbody>() != null || plat.GetComponentInChildren<Rigidbody>() != null;
            // one floor to the next (train part to train part, or train to station): this step still moves as the
            // last one did, so the switch alone doesn't feel like a 3 m/s jolt
            Vel = was;
            return;
        }
        Vector3 now = plat.position; Quaternion nowRot = plat.rotation;
        fromPos = prevPos; toPos = now;
        DRot = nowRot * Quaternion.Inverse(prevRot);
        prevPos = now; prevRot = nowRot;
        if ((toPos - fromPos).sqrMagnitude > 9f) { DRot = Quaternion.identity; return; }   // teleported, don't drag us along
        Same = true;
        DYaw = Mathf.DeltaAngle(0, DRot.eulerAngles.y);
        Vel = (Map(at) - at) / Time.fixedDeltaTime;
        Active = (toPos - fromPos).sqrMagnitude > 1e-10f || Quaternion.Angle(DRot, Quaternion.identity) > 1e-4f;
        if (Active && Vel.sqrMagnitude > 0.01f && told.Add(plat.name)) Plugin.L.LogInfo($"on a moving platform: {plat.name} ({Vel.magnitude:F1} m/s)");
    }

    public void Clear() { plat = null; Active = false; Vel = Vector3.zero; }

    Transform Find(PlayerCharacter pc, Vector3 at, float down, int mask)
    {
        // what big walk itself thinks we ride (its ground check still runs with the legs on)
        try
        {
            var pf = pc.platformer;
            if (pf != null && pf.stickyPlatform != null)
            {
                var bt = pf.stickyPlatformer != null ? pf.stickyPlatformer.platformTransform : null;
                if (bt == null) bt = pf.stickyPlatform.transform;
                if (bt != null) return bt;
            }
        }
        catch { }
        if (!Surface.Ray(at, Vector3.down, down, mask, out var h) || h.collider == null) return null;
        int id = h.collider.GetInstanceID();
        if (byCollider.TryGetValue(id, out var t)) return t;
        t = null;
        try
        {
            var sp = PlayerPlatformer.CheckColliderForPlatforms(h.collider);
            // the transform big walk's sticky platformer follows for it (the train: CarriageBody, not the StickyPlatform child)
            if (sp != null) t = sp.platformingBody != null ? sp.platformingBody.transform : sp.transform;
            else
            {
                // the train: its floor grates aren't sticky platforms, the car itself moves
                var car = h.collider.GetComponentInParent<TrainCar>();
                if (car != null) t = car.mainBody != null ? car.mainBody.transform : car.transform;
                // anything else with a body that isn't us (boats, carts, props you stand on)
                else if (h.collider.attachedRigidbody != null && !h.collider.attachedRigidbody.transform.IsChildOf(pc.transform)) t = h.collider.attachedRigidbody.transform;
                // plain colliders moved by script (the train's floor proxies): their own transform. still ground never moves
                else t = h.collider.transform;
            }
        }
        catch { }
        if (byCollider.Count > 512) byCollider.Clear();
        byCollider[id] = t;
        return t;
    }
}
