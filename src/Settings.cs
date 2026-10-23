using System;
using UnityEngine;

namespace BigSteps;

// the gameplay values the legs run on. your own config, unless a modded host shares theirs: then everyone using the
// mod in that lobby walks the same (height, steep grip, bird fit). camera, keys and visuals always stay personal
static class Settings
{
    internal class Shared { public float height, grip; public bool fit; public string from; public float lastHeard; }
    internal static Shared Host;                 // set while a modded host shares its values
    const float ShareEvery = 5f, ShareTimeout = 15f;
    static float nextShare;
    static bool hooked;
    static (float, float, bool) applied = (float.NaN, float.NaN, false);

    internal static float HeightBoost => Host?.height ?? Plugin.HeightBoost.Value;
    internal static float SteepGrip => Host?.grip ?? Plugin.SteepSlopeGrip.Value;
    internal static bool FitToBird => Host?.fit ?? Plugin.FitToBirdLegs.Value;

    internal static bool IsHost { get { try { return Mirror.NetworkServer.active; } catch { return false; } } }
    internal static bool Sharing => IsHost && Plugin.ShareWithLobby.Value;

    internal static void Hook()
    {
        if (hooked) return;
        hooked = true;
        Net.Received += OnReceived;
        Plugin.HeightBoost.SettingChanged += (_, _) => { ShareNow(); };
        Plugin.SteepSlopeGrip.SettingChanged += (_, _) => { ShareNow(); };
        Plugin.FitToBirdLegs.SettingChanged += (_, _) => { ShareNow(); };
        Plugin.ShareWithLobby.SettingChanged += (_, _) => { ShareNow(); };
    }

    internal static void ShareNow() => nextShare = 0;

    // every frame, from Runner
    internal static void Update()
    {
        Hook();
        if (Host != null && (Time.unscaledTime - Host.lastHeard > ShareTimeout || IsHost))
        {
            Plugin.L.LogInfo($"lobby: {Host.from}'s shared settings dropped, back to your own");
            Host = null;
        }
        if (IsHost && Net.ModdedCount > 0 && Time.unscaledTime >= nextShare)
        {
            nextShare = Time.unscaledTime + ShareEvery;
            var w = new Net.Writer(Net.Kind.Settings);
            w.U8((byte)((Plugin.ShareWithLobby.Value ? 1 : 0) | (Plugin.FitToBirdLegs.Value ? 2 : 0)));
            w.F(Plugin.HeightBoost.Value);
            w.F(Plugin.SteepSlopeGrip.Value);
            Net.SendAll(w.Done(), true);
        }
        // the legs re-fit when what they run on changes (it only matters at the bird fit, so stand-up/legs-on time)
        var now = (HeightBoost, SteepGrip, FitToBird);
        if (!now.Equals(applied))
        {
            bool first = float.IsNaN(applied.Item1);
            applied = now;
            if (!first) Legs.Inst.Refit();
        }
    }

    // a new modded player got the handshake: tell them straight away
    internal static void PeerJoined() => ShareNow();

    static void OnReceived(Net.Peer p, Net.Kind k, byte[] d, int start)
    {
        if (k != Net.Kind.Settings || IsHost) return;
        var r = new Net.Reader(d, d.Length, start);
        byte flags = r.U8();
        float h = r.F(), g = r.F();
        if ((flags & 1) == 0)
        {
            if (Host != null) { Plugin.L.LogInfo($"lobby: {p.name} stopped sharing settings, back to your own"); Host = null; }
            return;
        }
        bool fresh = Host == null || Host.from != p.name || Host.height != h || Host.grip != g || Host.fit != ((flags & 2) != 0);
        Host ??= new Shared();
        Host.height = Mathf.Clamp(h, 0.5f, 2f); Host.grip = Mathf.Clamp(g, 0.5f, 3f); Host.fit = (flags & 2) != 0;
        Host.from = p.name; Host.lastHeard = Time.unscaledTime;
        if (fresh) Plugin.L.LogInfo($"lobby: using {p.name}'s settings (height x{Host.height:F2}, steep grip x{Host.grip:F2}, bird fit {Host.fit})");
    }
}
