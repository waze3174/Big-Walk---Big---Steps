using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime.XrefScans;

namespace BigSteps;

// big walk drops what you hold when you land from a long drop (its fall, which the legs switch off). the call isn't
// public API, so it's looked up the way big walk makes it: the methods its own fall calls (xref scan of
// PlayerFaller.TriggerFall / ShowFall), taking the drop among them
static class FallDrop
{
    static MethodInfo method;
    static bool searched, toldMissing;

    static void Find()
    {
        searched = true;
        foreach (var name in new[] { nameof(PlayerFaller.TriggerFall), nameof(PlayerFaller.ShowFall) })
        {
            var root = typeof(PlayerFaller).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (root == null) continue;
            try
            {
                foreach (var x in XrefScanner.XrefScan(root))
                {
                    if (x.Type != XrefType.Method) continue;
                    if (x.TryResolve() is not MethodInfo m || m.DeclaringType == null) continue;
                    if (method == null && !m.IsStatic && m.Name.Contains("Drop") && m.GetParameters().Length == 0 && Target(m.DeclaringType, Game.Pc) != null) method = m;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"fall drop: scanning {name} failed: {e.Message}"); }
        }
        if (method != null) Plugin.L.LogInfo($"fall drop: through {method.DeclaringType.Name}.{method.Name}");
    }

    static object Target(Type t, PlayerCharacter pc)
    {
        if (pc == null) return null;
        if (t == typeof(PlayerActions)) return pc.actions;     // big walk's fall: PlayerActions.ActionDropPropOrPlayer
        if (t == typeof(PlayerHands)) return pc.hands;
        if (t == typeof(PlayerNetworking)) return pc.playerNetworking;
        if (t == typeof(PlayerCharacter)) return pc;
        return null;
    }

    // the drop distance big walk itself uses
    internal static float Distance(PlayerCharacter pc)
    {
        try { float d = pc.faller.fallDistance; if (d > 0.5f) return d; } catch { }
        return 3f;
    }

    internal static void Run(PlayerCharacter pc, float dropped)
    {
        if (pc == null) return;
        bool holding = false;
        try { holding = pc.hands != null && pc.hands.isHoldingSomething; } catch { }
        if (!holding) return;
        if (!searched) Find();
        if (method == null)
        {
            if (!toldMissing) { toldMissing = true; Plugin.L.LogWarning("fall drop: couldn't find big walk's drop call, keeping the item"); }
            return;
        }
        try { method.Invoke(Target(method.DeclaringType, pc), null); Plugin.L.LogInfo($"fell {dropped:F1} m: dropped what we held"); }
        catch (Exception e) { Plugin.L.LogWarning("fall drop failed: " + (e.InnerException ?? e).Message); method = null; }
    }
}
