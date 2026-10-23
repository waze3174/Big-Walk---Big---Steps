using System.Collections.Generic;
using UnityEngine;

namespace BigSteps;

// the round body hides the ground under you when looking down: fade it out as the camera pitches down.
// uses a transparent copy of each body material when the shader has urp's _Surface switch, else hides the body
static class BodyFade
{
    class Entry { public Renderer r; public Material orig, fade; }
    static readonly List<Entry> entries = new();
    static PlayerCharacter owner;
    static float current = 1;

    static bool Skip(Transform t, Transform stop)
    {
        for (; t != null && t != stop; t = t.parent)
            if (t.name.StartsWith("SH_hand_bone") || t.name.StartsWith("SHL_hand_bone")) return true;
        return false;
    }

    static void Build(PlayerCharacter pc)
    {
        current = 0; Restore();
        entries.Clear();
        owner = pc;
        Transform pelvis = null;
        foreach (var t in pc.GetComponentsInChildren<Transform>(true)) if (t.name == "pelvis_bone") { pelvis = t; break; }
        if (pelvis == null) return;
        foreach (var r in pelvis.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (Skip(r.transform, pelvis)) continue;
            var m = r.sharedMaterial;
            if (m == null || m.shader == null || m.shader.name.Contains("Text")) continue;
            var e = new Entry { r = r, orig = m };
            if (m.HasProperty("_Surface"))
            {
                var f = new Material(m);
                f.SetFloat("_Surface", 1);
                f.SetFloat("_Blend", 0);
                if (f.HasProperty("_SrcBlend")) f.SetFloat("_SrcBlend", 5);    // SrcAlpha
                if (f.HasProperty("_DstBlend")) f.SetFloat("_DstBlend", 10);   // OneMinusSrcAlpha
                if (f.HasProperty("_ZWrite")) f.SetFloat("_ZWrite", 0);
                f.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                f.SetOverrideTag("RenderType", "Transparent");
                f.renderQueue = 3000;
                e.fade = f;
            }
            entries.Add(e);
        }
    }

    static Color Col(Material m)
    {
        foreach (var c in new[] { "_BaseColor", "_Color", "_MainColor", "_Tint" }) if (m.HasProperty(c)) return m.GetColor(c);
        return Color.white;
    }

    // alpha 1 = normal, lower = see through
    public static void Apply(PlayerCharacter pc, float alpha)
    {
        if (pc == null) return;
        if (owner == null || owner.WasCollected || owner != pc) Build(pc);
        if (Mathf.Abs(alpha - current) < 0.01f) return;
        current = alpha;
        foreach (var e in entries)
        {
            if (e.r == null) continue;
            if (alpha >= 0.99f) { e.r.sharedMaterial = e.orig; e.r.forceRenderingOff = false; continue; }
            if (e.fade != null)
            {
                e.r.sharedMaterial = e.fade;
                foreach (var p in new[] { "_BaseColor", "_Color" })
                    if (e.fade.HasProperty(p)) { var c = Col(e.orig); c.a = alpha; e.fade.SetColor(p, c); }
            }
            else e.r.forceRenderingOff = alpha < 0.6f;
        }
    }

    public static void Restore()
    {
        if (current >= 0.99f) return;
        foreach (var e in entries) if (e.r != null) { e.r.sharedMaterial = e.orig; e.r.forceRenderingOff = false; }
        current = 1;
    }
}
