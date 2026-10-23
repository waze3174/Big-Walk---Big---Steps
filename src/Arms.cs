using System.Collections.Generic;
using Rewired;

namespace BigSteps;

// legs take the triggers and mouse buttons: big walk's arm raise (waveLeft/waveRight) moves to A/B on the gamepad,
// mouse use/drop move to keys, crouch/jump come off while legs are on.
// edits the live rewired joystick maps and puts them back exactly when legs turn off
static class Arms
{
    static readonly List<ActionElementMap> disabled = new();
    static readonly List<(ControllerMap map, int id)> added = new();

    internal static void Remap(bool legsOn)
    {
        try { if (legsOn) Apply(); else Restore(); }
        catch (System.Exception e) { Plugin.L.LogWarning("arm remap failed: " + e.Message); }
    }

    static void Apply()
    {
        Restore();
        var p = Game.Pc.inputPlayer;
        Keyboard(p);
        Mouse(p);
        if (p.controllers.joystickCount == 0) { Plugin.L.LogInfo("no gamepad"); return; }
        var joy = p.controllers.Joysticks[0];
        // big walk: triggers raise the arms (wave), bumpers reach (use/drop), both = arms out.
        // the legs take the triggers, so the raise goes to the face buttons whose jump/crouch the legs don't use
        int la = FindButton(joy, Plugin.PadArmLeft.Value), ra = FindButton(joy, Plugin.PadArmRight.Value);
        if (la < 0 || ra < 0) { Plugin.L.LogWarning("couldn't find the arm raise buttons, arms unchanged"); return; }

        var maps = new Il2CppSystem.Collections.Generic.List<ControllerMap>();
        p.controllers.maps.GetAllMaps(ControllerType.Joystick, maps);
        for (int i = 0; i < maps.Count; i++)
        {
            var m = maps[i];
            bool gameplay = m.GetElementMapsWithAction(RewiredConsts.Action.waveLeft)?.Length > 0;
            Unbind(m, RewiredConsts.Action.crouch);
            if (!gameplay) continue;
            Unbind(m, RewiredConsts.Action.jump);
            // anything else on the buttons / triggers the legs use (big walk's defaults, or the player's own rebinds) would
            // fire too: off while the legs are on
            var used = new HashSet<int> { la, ra };
            foreach (var b in new[] { Plugin.CamSideButton.Value, Plugin.LiftLeftButton.Value, Plugin.LiftRightButton.Value })
                if (!string.IsNullOrEmpty(b)) { int id = FindButton(joy, b); if (id >= 0) used.Add(id); }
            var axes = new HashSet<int>();
            foreach (var ax in new[] { Plugin.LeftTriggerAxis.Value, Plugin.RightTriggerAxis.Value })
                if (ax >= 0 && ax < joy.axisCount) axes.Add(joy.AxisElementIdentifiers[ax].id);
            foreach (var e in m.GetElementMaps())
            {
                if (!e.enabled) continue;
                bool clash = (e.elementType == ControllerElementType.Button && used.Contains(e.elementIdentifierId))
                          || (e.elementType == ControllerElementType.Axis && axes.Contains(e.elementIdentifierId)
                              && e.actionId != RewiredConsts.Action.waveLeft && e.actionId != RewiredConsts.Action.waveRight);
                if (!clash) continue;
                e.enabled = false; disabled.Add(e);
                Plugin.L.LogInfo($"freed {e.elementIdentifierName} from {e.actionDescriptiveName}");
            }
            Move(m, RewiredConsts.Action.waveLeft, la);
            Move(m, RewiredConsts.Action.waveRight, ra);
        }
        Plugin.L.LogInfo($"arm raise moved to {Plugin.PadArmLeft.Value}/{Plugin.PadArmRight.Value} ({disabled.Count} binds off, {added.Count} on), bumpers untouched");
    }

    static void Keyboard(Player p)
    {
        var maps = new Il2CppSystem.Collections.Generic.List<ControllerMap>();
        p.controllers.maps.GetAllMaps(ControllerType.Keyboard, maps);
        for (int i = 0; i < maps.Count; i++)
        {
            var m = maps[i];
            bool gameplay = m.GetElementMapsWithAction(RewiredConsts.Action.waveLeft)?.Length > 0 || m.GetElementMapsWithAction(RewiredConsts.Action.jump)?.Length > 0;
            Unbind(m, RewiredConsts.Action.crouch);
            Unbind(m, RewiredConsts.Action.jump);   // space switches the camera side instead
            if (gameplay) FreeKeys(m);
        }
    }

    // legs take the mouse buttons (like baby steps): move big walk's mouse button binds onto keys
    static void Mouse(Player p)
    {
        var kmaps = new Il2CppSystem.Collections.Generic.List<ControllerMap>();
        p.controllers.maps.GetAllMaps(ControllerType.Keyboard, kmaps);
        var maps = new Il2CppSystem.Collections.Generic.List<ControllerMap>();
        p.controllers.maps.GetAllMaps(ControllerType.Mouse, maps);
        for (int i = 0; i < maps.Count; i++)
        {
            var m = maps[i];
            foreach (var e in m.GetElementMaps())
            {
                string nm = e.elementIdentifierName ?? "";
                bool left = nm.Contains("Left Mouse"), right = nm.Contains("Right Mouse");
                if (!e.enabled || !(left || right)) continue;
                var to = left ? Plugin.MouseLeftMovedTo.Value : Plugin.MouseRightMovedTo.Value;
                // put it on the keyboard map of the same category
                ControllerMap km = null;
                for (int k = 0; k < kmaps.Count; k++) if (kmaps[k].categoryId == m.categoryId) { km = kmaps[k]; break; }
                if (km == null) continue;
                e.enabled = false;
                disabled.Add(e);
                ActionElementMap made = null;
                if (km.CreateElementMap(e.actionId, e.axisContribution, to, ModifierKeyFlags.None, out made) && made != null)
                {
                    added.Add((km, made.id));
                    Plugin.L.LogInfo($"{nm} ({e.actionDescriptiveName}) moved to {to}");
                }
            }
        }
    }

    // keys the legs use, in big walk's gameplay map: whatever big walk (or the player's own rebinding) put on them would
    // fire too. the arm extends and the moved mouse actions we created on our keys stay
    static void FreeKeys(ControllerMap m)
    {
        var ours = new HashSet<UnityEngine.KeyCode> { Plugin.SlowFootKey.Value, Plugin.CamSideKey.Value,
            Plugin.MouseLeftMovedTo.Value, Plugin.MouseRightMovedTo.Value, Plugin.LegsKey.Value, Plugin.ThirdPersonKey.Value };
        var mine = new HashSet<int>();
        foreach (var (am, id) in added) if (am.Pointer == m.Pointer) mine.Add(id);
        foreach (var e in m.GetElementMaps())
        {
            if (!e.enabled || mine.Contains(e.id) || !ours.Contains(e.keyCode)) continue;
            e.enabled = false; disabled.Add(e);
            Plugin.L.LogInfo($"freed {e.keyCode} from {e.actionDescriptiveName} (the legs use it)");
        }
    }

    // big walk's crouch squashes the body capsule the legs depend on; nate crouches on both triggers
    static void Unbind(ControllerMap m, int action)
    {
        var old = m.GetElementMapsWithAction(action);
        if (old == null) return;
        foreach (var e in old)
        {
            if (!e.enabled) continue;
            e.enabled = false;
            disabled.Add(e);
        }
    }

    static void Move(ControllerMap m, int action, int button)
    {
        var old = m.GetElementMapsWithAction(action);
        if (old == null || old.Length == 0) return;
        foreach (var e in old)
        {
            if (!e.enabled) continue;
            e.enabled = false;
            disabled.Add(e);
        }
        ActionElementMap made = null;
        if (m.CreateElementMap(action, Pole.Positive, button, ControllerElementType.Button, AxisRange.Positive, false, out made) && made != null)
            added.Add((m, made.id));
    }

    static void Restore()
    {
        foreach (var (m, id) in added) try { m.DeleteElementMap(id); } catch { }
        foreach (var e in disabled) try { e.enabled = true; } catch { }
        if (added.Count + disabled.Count > 0) Plugin.L.LogInfo("controls restored");
        added.Clear();
        disabled.Clear();
    }

    // button element id by name; "A"/"B" also match the usual alternates (south/east, cross/circle, bottom row)
    internal static int FindButton(Joystick joy, string want)
    {
        want = want.ToLowerInvariant();
        string[] alt = want switch
        {
            "a" => new[] { "a", "a button", "button a", "south", "button south", "cross", "action bottom row 1" },
            "b" => new[] { "b", "b button", "button b", "east", "button east", "circle", "action bottom row 2" },
            "x" => new[] { "x", "x button", "button x", "west", "button west", "square", "action top row 1" },
            "y" => new[] { "y", "y button", "button y", "north", "button north", "triangle", "action top row 2" },
            _ => new[] { want },
        };
        var ids = joy.ButtonElementIdentifiers;
        int n = joy.buttonCount;
        string names = "";
        foreach (var w in alt)
            for (int i = 0; i < n; i++)
            {
                var id = ids[i];
                if (id.name.ToLowerInvariant() == w) return id.id;
            }
        for (int i = 0; i < n; i++) names += ids[i].name + "(" + ids[i].id + ") ";
        Plugin.L.LogInfo("gamepad buttons: " + names);
        return -1;
    }
}
