using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Rewired;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BigSteps;

// our keyboard / gamepad rebinding, on copies of big walk's own rebind screens (Settings > Rebinding): same title,
// rows (action ........ key), reset row and back. big walk's RebindMenu code is switched off on the copies (it would
// rebind its own rewired actions); the settings menu opens them through its own ActionGoToRebind* path, with our
// copy standing in for big walk's screen until we go back
static class RebindPage
{
    class Item
    {
        public string name;
        public Func<string> value;
        public Action capture;      // starts listening
        public RebindRow row;
    }

    class Screen
    {
        public RebindMenu menu;
        public List<Item> items = new();
        public bool pad;
    }

    static Screen kb, pad;
    static readonly Dictionary<IntPtr, Screen> screens = new();
    static readonly Dictionary<IntPtr, Item> rowItems = new();
    static SettingsMenu settings;
    static RebindMenu swappedOut;
    static bool swappedPad;

    // capture
    static ConfigEntry<KeyCode> keyTarget;
    static Action<string, int> padTarget;   // (button name, axis index or -1)
    static bool padAxes;
    static Item capturing;
    static int captureFrame;
    static KeyCode[] allKeys;
    static float[] axisRest;
    static Dictionary<int, bool> btnWas;

    internal static bool Built => kb != null && kb.menu != null && !kb.menu.WasCollected;

    // ---------------- building ----------------

    internal static void Build(SettingsMenu menu)
    {
        settings = menu;
        try { kb = Make(menu, menu.rebindMenuKeyboard, false); } catch (Exception e) { Plugin.L.LogWarning("rebind page: keyboard screen: " + e); }
        try { pad = Make(menu, menu.rebindMenuGamepad, true); } catch (Exception e) { Plugin.L.LogWarning("rebind page: gamepad screen: " + e); }
    }

    static Screen Make(SettingsMenu menu, RebindMenu src, bool isPad)
    {
        if (src == null || src.rowPrefab == null) return null;
        var go = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent);
        go.name = isPad ? "BigSteps Rebind Gamepad" : "BigSteps Rebind Keyboard";
        go.SetActive(false);
        var rm = go.GetComponent<RebindMenu>();
        var sc = new Screen { menu = rm, pad = isPad };
        screens[rm.Pointer] = sc;
        // big walk's generated rows (if any were made already) go
        foreach (var r in go.GetComponentsInChildren<RebindRow>(true))
            if (r.gameObject.name != src.rowPrefab.name) UnityEngine.Object.Destroy(r.gameObject);
        // title
        var title = go.transform.Find("Left/Title");
        if (title != null) SetRaw(title.gameObject, isPad ? "Big Steps\ngamepad" : "Big Steps\nkeyboard");
        var warn = go.transform.Find("Left/Warning"); if (warn != null) warn.gameObject.SetActive(false);

        sc.items = isPad ? PadItems() : KeyItems();
        foreach (var it in sc.items)
        {
            var rgo = UnityEngine.Object.Instantiate(src.rowPrefab, rm.rowParent);
            rgo.name = "bbs_rebind " + it.name;
            rgo.SetActive(true);
            var rr = rgo.GetComponent<RebindRow>();
            it.row = rr;
            rowItems[rr.Pointer] = it;
        }
        Nav(sc);
        Refresh(sc);
        Plugin.L.LogInfo($"rebind page: {(isPad ? "gamepad" : "keyboard")} screen copied from {src.name}, {sc.items.Count} actions");
        return sc;
    }

    // up / down through the rows and the reset row, wrapping around
    static void Nav(Screen sc)
    {
        var sels = new List<Selectable>();
        if (sc.menu.resetButton != null) sels.Add(sc.menu.resetButton);
        foreach (var it in sc.items) if (it.row?.button != null) sels.Add(it.row.button);
        for (int i = 0; i < sels.Count; i++)
        {
            var n = sels[i].navigation;
            n.mode = Navigation.Mode.Explicit;
            n.selectOnUp = sels[(i - 1 + sels.Count) % sels.Count];
            n.selectOnDown = sels[(i + 1) % sels.Count];
            sels[i].navigation = n;
        }
    }

    static void SetRaw(GameObject go, string s)
    {
        var lt = go.GetComponentInChildren<LocalizedText>(true);
        if (lt != null) { lt.Change(s, LocalizedText.DisplayType.RawValue); return; }
        var t = go.GetComponentInChildren<TMP_Text>(true); if (t != null) t.text = s;
    }

    static void Refresh(Screen sc)
    {
        if (sc == null) return;
        foreach (var it in sc.items)
        {
            if (it.row == null || it.row.WasCollected) continue;
            try
            {
                if (it.row.label != null) it.row.label.Change(it.name, LocalizedText.DisplayType.RawValue);
                string v = capturing == it ? (sc.pad ? "press a button…" : "press a key…") : it.value();
                if (it.row.status != null) it.row.status.Change(v, LocalizedText.DisplayType.RawValue);
            }
            catch { }
        }
    }

    // ---------------- the actions ----------------

    internal static string KeyName(KeyCode k) => k switch
    {
        KeyCode.Mouse0 => "Left mouse", KeyCode.Mouse1 => "Right mouse", KeyCode.Mouse2 => "Middle mouse",
        KeyCode.Mouse3 => "Mouse 4", KeyCode.Mouse4 => "Mouse 5", KeyCode.LeftShift => "Left Shift", KeyCode.RightShift => "Right Shift",
        KeyCode.LeftControl => "Left Ctrl", KeyCode.RightControl => "Right Ctrl", KeyCode.LeftAlt => "Left Alt", KeyCode.None => "-", _ => k.ToString(),
    };

    static Item Key(string name, ConfigEntry<KeyCode> e) => new()
    {
        name = name, value = () => KeyName(e.Value),
        capture = () => { keyTarget = e; },
    };

    static List<Item> KeyItems() => new()
    {
        Key("Lift left leg", Plugin.LiftLeftKey),
        Key("Lift right leg", Plugin.LiftRightKey),
        Key("Slow, careful foot", Plugin.SlowFootKey),
        Key("Use / left arm reach (was left mouse)", Plugin.MouseLeftMovedTo),
        Key("Drop / right arm reach (was right mouse)", Plugin.MouseRightMovedTo),
        Key("Switch camera side", Plugin.CamSideKey),
        Key("Legs on / off", Plugin.LegsKey),
        Key("Third person on / off", Plugin.ThirdPersonKey),
        Key("Debug readout", Plugin.HudKey),
        Key("Record the legs (debug)", Plugin.RecordKey),
    };

    // the pad straight from rewired (works in the main menu, where there's no bird)
    static Joystick Joy()
    {
        try { return ReInput.isReady && ReInput.controllers.joystickCount > 0 ? ReInput.controllers.Joysticks[0] : null; } catch { return null; }
    }

    // big walk's own controller glyphs (the sprites its rebind screen shows), else the element's name. matched by the
    // element's name: element ids differ per controller (ids gave A/B for the triggers)
    static ControllerGlyphSet glyphs;
    static GlyphButtonType TypeFor(string name)
    {
        string n = (name ?? "").ToLowerInvariant().Replace("-", " ").Replace("_", " ");
        bool left = n.Contains("left"), right = n.Contains("right");
        if (n == "a" || n.Contains("cross") || n.Contains("south")) return GlyphButtonType.FaceDown;
        if (n == "b" || n.Contains("circle") || n.Contains("east")) return GlyphButtonType.FaceRight;
        if (n == "x" || n.Contains("square") || n.Contains("west")) return GlyphButtonType.FaceLeft;
        if (n == "y" || n.Contains("triangle") || n.Contains("north")) return GlyphButtonType.FaceUp;
        if (n.Contains("trigger") || n == "lt" || n == "rt" || n == "l2" || n == "r2" || n == "zl" || n == "zr")
            return left || n.StartsWith("l") || n == "zl" ? GlyphButtonType.TriggerLeft : GlyphButtonType.TriggerRight;
        if (n.Contains("shoulder") || n.Contains("bumper") || n == "lb" || n == "rb" || n == "l1" || n == "r1")
            return left || n.StartsWith("l") ? GlyphButtonType.ShoulderLeft : GlyphButtonType.ShoulderRight;
        if (n.Contains("dpad") || n.Contains("d pad"))
            return n.Contains("up") ? GlyphButtonType.DpadUp : n.Contains("down") ? GlyphButtonType.DpadDown : left ? GlyphButtonType.DpadLeft : GlyphButtonType.DpadRight;
        if (n.Contains("stick")) return left ? GlyphButtonType.StickLeft : GlyphButtonType.StickRight;
        if (n.Contains("start") || n.Contains("menu") || n.Contains("options")) return GlyphButtonType.Start;
        if (n.Contains("back") || n.Contains("select") || n.Contains("view") || n.Contains("share")) return GlyphButtonType.Select;
        return GlyphButtonType.None;
    }

    internal static string Glyph(string elementName, string fallback)
    {
        try
        {
            glyphs ??= DevSettings.currentSettings?.controllerGlyphSet;
            var j = Joy();
            if (glyphs == null || j == null) return fallback;
            var bt = TypeFor(elementName);
            if (bt == GlyphButtonType.None) return fallback;
            var g = glyphs.GetGamepadGlyph(GlyphReplacer.GetGlyphControllerType(j), bt);
            var s = glyphs.WrapInTags(g, LocalizedText.MaterialType.Transparent);
            return string.IsNullOrEmpty(s) ? fallback : s;
        }
        catch { return fallback; }
    }

    // the glyph characters of every gamepad button for the connected pad (to tell what big walk's own list shows)
    static HashSet<string> padChars;
    static GlyphControllerType padCharsFor = (GlyphControllerType)(-1);
    internal static HashSet<string> PadGlyphChars()
    {
        try
        {
            glyphs ??= DevSettings.currentSettings?.controllerGlyphSet;
            var j = Joy();
            if (glyphs == null || j == null) return null;
            var ct = GlyphReplacer.GetGlyphControllerType(j);
            if (padChars != null && ct == padCharsFor) return padChars;
            padChars = new HashSet<string>(); padCharsFor = ct;
            foreach (GlyphButtonType bt in Enum.GetValues(typeof(GlyphButtonType)))
            {
                if (bt == GlyphButtonType.None) continue;
                try { var g = glyphs.GetGamepadGlyph(ct, bt).glyphString; if (!string.IsNullOrEmpty(g)) padChars.Add(g); } catch { }
            }
            return padChars;
        }
        catch { return null; }
    }

    // big walk's keyboard / mouse glyphs (a key in a box, as its own controls list shows them), else the key's name
    internal static string KeyGlyph(KeyCode k)
    {
        try
        {
            glyphs ??= DevSettings.currentSettings?.controllerGlyphSet;
            if (glyphs == null) return KeyName(k);
            GlyphText g;
            if (k == KeyCode.Mouse0) g = glyphs.GetMouseGlyphForElementName("Left Mouse Button");
            else if (k == KeyCode.Mouse1) g = glyphs.GetMouseGlyphForElementName("Right Mouse Button");
            else if (k == KeyCode.Mouse2) g = glyphs.GetMouseGlyphForElementName("Middle Mouse Button");
            else if (Enum.TryParse<KeyboardKeyCode>(k.ToString(), out var kk)) g = glyphs.GetKeyboardGlyphForKeyCode(kk);
            else return KeyName(k);
            if (string.IsNullOrEmpty(g.glyphString)) return KeyName(k);
            var s = glyphs.WrapInTags(g, LocalizedText.MaterialType.Transparent);
            return string.IsNullOrEmpty(s) ? KeyName(k) : s;
        }
        catch { return KeyName(k); }
    }

    internal static string ButtonShown(string name)
    {
        var j = Joy();
        if (j == null || string.IsNullOrEmpty(name)) return string.IsNullOrEmpty(name) ? "-" : name;
        return Glyph(name, name);
    }

    internal static string AxisName(int i)
    {
        var j = Joy();
        try { if (j != null && i >= 0 && i < j.axisCount) { var id = j.AxisElementIdentifiers[i]; return Glyph(id.name, id.name); } } catch { }
        return i >= 0 ? "axis " + i : "-";
    }

    // a leg lifts from a trigger (axis) or any button
    static Item Leg(string name, ConfigEntry<int> axis, ConfigEntry<string> button) => new()
    {
        name = name,
        value = () => !string.IsNullOrEmpty(button.Value) ? ButtonShown(button.Value) : AxisName(axis.Value),
        capture = () => { padAxes = true; padTarget = (b, a) => { if (a >= 0) { axis.Value = a; button.Value = ""; } else { button.Value = b; axis.Value = -1; } }; },
    };

    static Item Button(string name, ConfigEntry<string> e) => new()
    {
        name = name, value = () => ButtonShown(e.Value),
        capture = () => { padAxes = false; padTarget = (b, a) => { if (b != null) e.Value = b; }; },
    };

    static List<Item> PadItems() => new()
    {
        Leg("Lift left leg", Plugin.LeftTriggerAxis, Plugin.LiftLeftButton),
        Leg("Lift right leg", Plugin.RightTriggerAxis, Plugin.LiftRightButton),
        Button("Raise left arm", Plugin.PadArmLeft),
        Button("Raise right arm", Plugin.PadArmRight),
        Button("Switch camera side", Plugin.CamSideButton),
    };

    static void ResetScreen(Screen sc)
    {
        var entries = sc.pad
            ? new ConfigEntryBase[] { Plugin.LeftTriggerAxis, Plugin.RightTriggerAxis, Plugin.LiftLeftButton, Plugin.LiftRightButton, Plugin.PadArmLeft, Plugin.PadArmRight, Plugin.CamSideButton }
            : new ConfigEntryBase[] { Plugin.LiftLeftKey, Plugin.LiftRightKey, Plugin.SlowFootKey, Plugin.MouseLeftMovedTo, Plugin.MouseRightMovedTo,
                Plugin.CamSideKey, Plugin.LegsKey, Plugin.ThirdPersonKey, Plugin.HudKey, Plugin.RecordKey };
        foreach (var e in entries) e.BoxedValue = e.DefaultValue;
        Changed();
        Refresh(sc);
        Plugin.L.LogInfo($"rebind page: {(sc.pad ? "gamepad" : "keyboard")} back to defaults");
    }

    static void Changed()
    {
        Runner.PadButtonsChanged();
        Legs.Inst.PadButtonsChanged();
        if (Legs.IsActive) Arms.Remap(true);
    }

    // ---------------- opening / closing (through big walk's own navigation) ----------------

    internal static void Open(bool isPad)
    {
        var sc = isPad ? pad : kb;
        if (sc == null || settings == null) { Plugin.L.LogWarning("rebind page: no screen to open"); return; }
        if (swappedOut != null) return;     // already open (one click can arrive twice)
        try
        {
            if (isPad) { swappedOut = settings.rebindMenuGamepad; settings.rebindMenuGamepad = sc.menu; settings.ActionGoToRebindGamepad(); }
            else { swappedOut = settings.rebindMenuKeyboard; settings.rebindMenuKeyboard = sc.menu; settings.ActionGoToRebindKeyboard(); }
            swappedPad = isPad;
            Refresh(sc);
            var first = sc.items.Count > 0 ? sc.items[0].row?.button : null;
            if (first != null) EventSystem.current?.SetSelectedGameObject(first.gameObject);
        }
        catch (Exception e) { Plugin.L.LogWarning("rebind page: open failed: " + e.Message); SwapBack(); }
    }

    static void Close(Screen sc)
    {
        try { settings?.GoToPostRebindState(); } catch (Exception e) { Plugin.L.LogWarning("rebind page: big walk's way back: " + e.Message); }
        try { sc.menu.gameObject.SetActive(false); } catch { }
        SwapBack();
        try { if (settings?.contents != null) settings.contents.gameObject.SetActive(true); } catch { }
        SettingsPage.BackFromRebind();
    }

    static void SwapBack()
    {
        if (swappedOut == null || settings == null) return;
        if (swappedPad) settings.rebindMenuGamepad = swappedOut; else settings.rebindMenuKeyboard = swappedOut;
        swappedOut = null;
    }

    // ---------------- every frame ----------------

    internal static void Update()
    {
        if (capturing == null || Time.frameCount <= captureFrame + 1) return;
        if (keyTarget != null)
        {
            allKeys ??= (KeyCode[])Enum.GetValues(typeof(KeyCode));
            if (Input.GetKeyDown(KeyCode.Escape)) { Stop(); return; }
            foreach (var k in allKeys)
            {
                if (k == KeyCode.None || k == KeyCode.Escape || k >= KeyCode.JoystickButton0 || !Input.GetKeyDown(k)) continue;
                keyTarget.Value = k; Changed(); Stop(); return;
            }
            return;
        }
        if (padTarget != null)
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { Stop(); return; }
            var j = Joy();
            if (j == null) return;
            // a fresh press on any button (held ones at the start don't count)
            var ids = j.ButtonElementIdentifiers;
            bool first = btnWas == null;
            btnWas ??= new Dictionary<int, bool>();
            for (int i = 0; i < j.buttonCount; i++)
            {
                int id = ids[i].id;
                bool now = j.GetButtonById(id);
                bool was = btnWas.TryGetValue(id, out var w) && w;
                btnWas[id] = now;
                if (!first && now && !was) { padTarget(ids[i].name, -1); Changed(); Stop(); return; }
            }
            if (padAxes)
            {
                axisRest ??= new float[j.axisCount];
                for (int i = 0; i < j.axisCount && i < axisRest.Length; i++)
                {
                    float v = Mathf.Abs(j.GetAxis(i));
                    // a trigger pulled from rest (sticks count too: anything that moves past 0.6 from below 0.3)
                    if (v > 0.6f && axisRest[i] < 0.3f) { padTarget(null, i); Changed(); Stop(); return; }
                    axisRest[i] = v;
                }
            }
        }
    }

    static void Begin(Item it)
    {
        Stop();
        capturing = it; captureFrame = Time.frameCount;
        it.capture();
        axisRest = null; btnWas = null;
        // the menu mustn't wander off while the next button is the new binding
        try { if (EventSystem.current != null) EventSystem.current.sendNavigationEvents = false; } catch { }
        Refresh(kb); Refresh(pad);
    }

    static int stoppedFrame = -10;
    static void Stop()
    {
        if (capturing != null) stoppedFrame = Time.frameCount;
        capturing = null; keyTarget = null; padTarget = null;
        try { if (EventSystem.current != null) EventSystem.current.sendNavigationEvents = true; } catch { }
        Refresh(kb); Refresh(pad);
    }

    // ---------------- big walk's rebind code, switched off on our copies ----------------

    static bool Ours(RebindMenu m, out Screen sc) { sc = null; return m != null && screens.TryGetValue(m.Pointer, out sc); }
    static bool Ours(RebindRow r, out Item it) { it = null; return r != null && rowItems.TryGetValue(r.Pointer, out it); }

    [HarmonyPatch(typeof(RebindMenu), nameof(RebindMenu.Start))] static class MStart { static bool Prefix(RebindMenu __instance) => !Ours(__instance, out _); }
    [HarmonyPatch(typeof(RebindMenu), nameof(RebindMenu.Initialize))] static class MInit { static bool Prefix(RebindMenu __instance) => !Ours(__instance, out _); }
    [HarmonyPatch(typeof(RebindMenu), nameof(RebindMenu.OnEnable))] static class MEnable { static bool Prefix(RebindMenu __instance) { if (!Ours(__instance, out var sc)) return true; Refresh(sc); return false; } }
    [HarmonyPatch(typeof(RebindMenu), nameof(RebindMenu.OnDisable))] static class MDisable { static bool Prefix(RebindMenu __instance) { if (!Ours(__instance, out _)) return true; Stop(); return false; } }
    [HarmonyPatch(typeof(RebindMenu), nameof(RebindMenu.Update))] static class MUpdate { static bool Prefix(RebindMenu __instance) => !Ours(__instance, out _); }
    [HarmonyPatch(typeof(RebindMenu), nameof(RebindMenu.RefreshAll))] static class MRefresh { static bool Prefix(RebindMenu __instance) { if (!Ours(__instance, out var sc)) return true; Refresh(sc); return false; } }
    [HarmonyPatch(typeof(RebindMenu), nameof(RebindMenu.RefreshWarnings))] static class MWarn { static bool Prefix(RebindMenu __instance) => !Ours(__instance, out _); }
    [HarmonyPatch(typeof(RebindMenu), nameof(RebindMenu.RecordChange))] static class MRecord { static bool Prefix(RebindMenu __instance) => !Ours(__instance, out _); }
    [HarmonyPatch(typeof(RebindMenu), nameof(RebindMenu.Save))] static class MSave { static bool Prefix(RebindMenu __instance) => !Ours(__instance, out _); }
    [HarmonyPatch(typeof(RebindMenu), nameof(RebindMenu.OnControlsChange))] static class MCtl { static bool Prefix(RebindMenu __instance) => !Ours(__instance, out _); }
    [HarmonyPatch(typeof(RebindMenu), nameof(RebindMenu.ResetToDefault))] static class MReset { static bool Prefix(RebindMenu __instance) { if (!Ours(__instance, out var sc)) return true; ResetScreen(sc); return false; } }
    [HarmonyPatch(typeof(RebindMenu), nameof(RebindMenu.AreBindingsValid))] static class MValid { static bool Prefix(RebindMenu __instance, ref bool __result) { if (!Ours(__instance, out _)) return true; __result = true; return false; } }
    // back: big walk's own way out of a rebind screen, then its own screen goes back in place
    [HarmonyPatch(typeof(RebindMenu), nameof(RebindMenu.ActionBack))]
    static class MBack
    {
        // while waiting for the new binding, back (B / escape) is a binding or the cancel, not a way out.
        // otherwise our own way back (big walk's would save rebinds through machinery that's off on the copies)
        static bool Prefix(RebindMenu __instance)
        {
            if (!Ours(__instance, out var sc)) return true;
            if (capturing == null && Time.frameCount - stoppedFrame > 1) Close(sc);
            return false;
        }
    }

    [HarmonyPatch(typeof(RebindRow), nameof(RebindRow.SelectToRebind))] static class RSel { static bool Prefix(RebindRow __instance) { if (!Ours(__instance, out var it)) return true; Begin(it); return false; } }
    [HarmonyPatch(typeof(RebindRow), nameof(RebindRow.Clear))] static class RClear { static bool Prefix(RebindRow __instance) => !Ours(__instance, out _); }
}
