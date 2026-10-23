using System.Collections.Generic;
using UnityEngine;

namespace BigSteps;

// what's under a point and how it should feel
struct SurfaceInfo
{
    public bool hit;
    public Vector3 point, normal;
    public float angle;     // degrees from flat
    public string name;     // terrain layer or physics material or collider name
    public float grip;      // traction coefficient (baby steps Surface.GetTractionCoef)
    public float quadMult;  // slip drag multiplier (Surface.GetQuadraticMult)
    public Sim.Surf surf;
    public float stepSpeed; // multiplier on how fast a lifted foot moves (grass drags, like baby steps)
}

static class Surface
{
    internal static SurfaceInfo Probe(Vector3 from, float down, int mask)
    {
        var s = new SurfaceInfo { grip = 1f, stepSpeed = 1f, name = "none" };
        if (!RayDown(from, down, mask, out var h)) return s;
        s.hit = true;
        s.point = h.point;
        s.normal = h.normal;
        s.angle = Vector3.Angle(h.normal, Vector3.up);
        s.name = Name(h.collider, h.point, out _);
        Tune(ref s);
        return s;
    }

    // nearest hit below that isn't part of the local bird (its body/head/torso colliders sit in the ground mask)
    // il2cpp array: a managed array would be copied and the results lost
    static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit> hits = new(16);

    static bool RayDown(Vector3 from, float down, int mask, out RaycastHit best) => Ray(from, Vector3.down, down, mask, out best);

    internal static bool Ray(Vector3 from, Vector3 dir, float dist, int mask, out RaycastHit best, Transform alsoSkip = null)
    {
        best = default;
        int n = Physics.RaycastNonAlloc(from, dir, hits, dist, mask, QueryTriggerInteraction.Ignore);
        var root = Game.Pc != null ? Game.Pc.transform : null;
        float bestD = float.MaxValue;
        bool found = false;
        for (int i = 0; i < n; i++)
        {
            var c = hits[i].collider;
            if (c == null) continue;
            if (root != null && c.transform.IsChildOf(root)) continue;
            if (alsoSkip != null && c.transform.IsChildOf(alsoSkip)) continue;
            if (c.gameObject.name.StartsWith("bbs_")) continue;   // our own ragdoll parts
            if (hits[i].distance < bestD) { bestD = hits[i].distance; best = hits[i]; found = true; }
        }
        return found;
    }

    internal static string Name(Collider col, Vector3 pos, out string detail)
    {
        detail = "-";
        if (col == null) return "none";
        var tc = col.TryCast<TerrainCollider>();
        if (tc != null)
        {
            var terrain = tc.GetComponent<Terrain>();
            AudioUtility.GetTerrainTexture(terrain, pos, out int a, out int b, out float pa, out float pb, false);
            var layers = terrain.terrainData.terrainLayers;
            string ln(int i) => layers != null && i >= 0 && i < layers.Length && layers[i] != null ? layers[i].name : i.ToString();
            detail = $"{ln(a)} {pa:P0} / {ln(b)} {pb:P0}";
            return ln(a);
        }
        if (col.sharedMaterial != null) return col.sharedMaterial.name;
        return col.name;
    }

    // big walk terrain layer / physics material / collider names -> the closest baby steps surface.
    // matched on whole words (split on case changes, digits and symbols) so "Price" isn't ice or "Thousand" sand;
    // a word may carry a plural/suffix ("Rocks", "Stony") but must start with the keyword
    // big walk's terrain layers are named for how they look: its "mud" and "wetland grass" are ordinary ground you
    // walk up, not baby steps' slick mud and moss
    static readonly Dictionary<string, Sim.Surf> BigWalkTerrain = new()
    {
        ["11_Mud"] = Sim.Surf.Dirt, ["09_WetlandGrass"] = Sim.Surf.Grass, ["02_Woodland"] = Sim.Surf.Dirt,
        ["05_Grassland"] = Sim.Surf.Grass, ["06_Heathland"] = Sim.Surf.Grass, ["04_GraniteSand"] = Sim.Surf.Sand, ["10_Gravel"] = Sim.Surf.Gravel,
    };

    static Sim.Surf Classify(string name)
    {
        if (name != null && BigWalkTerrain.TryGetValue(name, out var known)) return known;
        var words = Words(name);
        bool Has(params string[] k)
        {
            foreach (var w in words) foreach (var x in k) if (w.StartsWith(x) && w.Length <= x.Length + (x.Length <= 3 ? 1 : 6)) return true;
            return false;
        }
        if (Has("ice", "icy")) return Sim.Surf.Ice;
        if (Has("snow")) return Sim.Surf.Snow;
        if (Has("mud", "muddy")) return Sim.Surf.Mud;
        if (Has("milk")) return Sim.Surf.Milk;
        if (Has("moss", "mossy", "wetland", "swamp", "algae")) return Sim.Surf.Moss;
        if (Has("sand", "beach")) return Sim.Surf.Sand;
        if (Has("gravel", "pebble", "scree")) return Sim.Surf.Gravel;
        if (Has("woodland", "forest")) return Sim.Surf.Dirt;
        if (Has("wood", "wooden", "plank", "timber", "bark", "log", "branch")) return Sim.Surf.Wood;
        if (Has("metal", "steel", "iron", "tin", "pipe")) return Sim.Surf.Metal;
        if (Has("granite", "rock", "stone", "stony", "cliff", "boulder", "concrete", "brick", "tile", "cobble")) return Sim.Surf.Rock;
        if (Has("water", "ocean", "river", "lake", "pond", "sea")) return Sim.Surf.Water;
        if (Has("grass", "grassy", "meadow", "lawn", "field")) return Sim.Surf.Grass;
        if (Has("dirt", "soil", "path", "earth", "woodland", "forest", "leaf", "leaves")) return Sim.Surf.Dirt;
        return Sim.Surf.Default;
    }

    static List<string> Words(string name)
    {
        var l = new List<string>();
        var cur = new System.Text.StringBuilder();
        void Flush() { if (cur.Length > 0) { l.Add(cur.ToString().ToLowerInvariant()); cur.Clear(); } }
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsLetter(c)) { Flush(); continue; }
            if (char.IsUpper(c) && i > 0 && char.IsLower(name[i - 1])) Flush();
            cur.Append(c);
        }
        Flush();
        return l;
    }

    // every surface name met, once, with what it became: shows what other maps call things
    static void Tune(ref SurfaceInfo s)
    {
        s.surf = Classify(s.name);
        s.grip = Sim.SurfTable.Traction(s.surf);
        s.quadMult = Sim.SurfTable.QuadMult(s.surf);
        s.stepSpeed = s.surf == Sim.Surf.Grass ? 0.7f : s.surf == Sim.Surf.Mud ? 0.8f : 1f;
    }
}
