using System;
using System.Collections.Generic;
using Rewired;
using TMPro;
using UnityEngine;

namespace BigSteps;

// big walk's pause menu lists its controls (ControlsList: a CONTROLS title and verbN / buttonN text pairs on its paper).
// ours sits beside it on a second sheet of the same paper, built from copies of those pieces, listing the legs'
// controls with big walk's glyphs (gamepad) or key names (keyboard), refreshed whenever the pause menu opens
static class PauseRef
{
    static readonly Dictionary<IntPtr, GameObject> built = new();
    static readonly Dictionary<IntPtr, bool> wasOpen = new();
    static bool told;

    internal static void Update()
    {
        var wm = WorldMenuManager.instance;
        if (wm == null) return;
        foreach (var pm in new[] { wm.pauseMenuHost, wm.pauseMenuClient })
        {
            if (pm == null) continue;
            bool open = pm.gameObject.activeInHierarchy;
            wasOpen.TryGetValue(pm.Pointer, out bool was);
            wasOpen[pm.Pointer] = open;
            if (!open || was) continue;
            // just opened
            if (!built.TryGetValue(pm.Pointer, out var ours) || ours == null || ours.WasCollected)
            {
                ours = Build(pm);
                built[pm.Pointer] = ours;
            }
            if (ours != null) Fill(ours);
        }
        // switching between keyboard and gamepad while the pause menu is open: the list follows, like big walk's
        if (Time.unscaledTime >= nextCheck)
        {
            nextCheck = Time.unscaledTime + 0.25f;
            bool pad = PadInUse(null);
            if (pad != lastPad)
                foreach (var kv in built)
                    if (kv.Value != null && !kv.Value.WasCollected && kv.Value.activeInHierarchy) Fill(kv.Value);
        }
    }
    static float nextCheck;
    static bool lastPad;

    static GameObject Build(PauseMenu pm)
    {
        try
        {
            var list = pm.transform.Find("ControlsList");
            var paper = pm.transform.Find("paper (1)");
            if (list == null || paper == null) { if (!told) { told = true; Plugin.L.LogWarning("pause reference: big walk's ControlsList / paper not found, skipped"); } return null; }
            var prt = paper.GetComponent<RectTransform>();

            // the card: big walk's quit dialog sheet (one torn sheet, unlike the pause paper image which holds two),
            // sized like big walk's controls sheet and set just right of it. sheet measured on the owner's screenshot:
            // the pause image's right 452 units, from 117 under its top
            const float SheetW = 452f, SheetTop = 117f, OurW = 380f, Gap = 16f;
            float pw = prt.sizeDelta.x, ph = prt.sizeDelta.y;
            float left = prt.anchoredPosition.x - pw * prt.pivot.x, top = prt.anchoredPosition.y + ph * (1 - prt.pivot.y);
            float right = left + pw;
            float origCx = right - SheetW * 0.5f;
            float cx = right + Gap + OurW * 0.5f;
            float h = ph - SheetTop, cy = top - SheetTop - h * 0.5f;
            Transform torn = null;
            foreach (var t in WorldMenuManager.instance.GetComponentsInChildren<Transform>(true))
                if (t.name == "tornpaper" && t.parent != null && t.parent.name.StartsWith("PausePart_Quit")) { torn = t; break; }
            var src = torn != null ? torn : paper;
            var sheet = UnityEngine.Object.Instantiate(src.gameObject, pm.transform);
            sheet.name = "BigSteps sheet";
            var srt = sheet.GetComponent<RectTransform>();
            srt.anchorMin = prt.anchorMin; srt.anchorMax = prt.anchorMax; srt.pivot = new Vector2(0.5f, 0.5f);
            srt.localScale = Vector3.one; srt.localRotation = Quaternion.identity;
            srt.sizeDelta = new Vector2(OurW, h);
            srt.anchoredPosition = new Vector2(cx, cy);

            // big walk's list, moved by as much as the sheet's centre moved
            var copy = UnityEngine.Object.Instantiate(list.gameObject, pm.transform);
            copy.name = "BigSteps ControlsList";
            copy.transform.localPosition = list.localPosition + new Vector3(cx - origCx, 0, 0);
            sheet.transform.SetParent(copy.transform, true);     // they come and go together
            sheet.transform.SetAsFirstSibling();
            if (torn == null) Plugin.L.LogWarning("pause reference: big walk's quit sheet not found, using the pause paper");
            var title = copy.transform.Find("title");
            if (title != null) Set(title.gameObject, "BIG STEPS v" + Plugin.Version);
            Plugin.L.LogInfo("pause reference: Big Steps controls added beside big walk's");
            return copy;
        }
        catch (Exception e) { Plugin.L.LogWarning("pause reference: " + e.Message); return null; }
    }

    static void Set(GameObject go, string s)
    {
        var lt = go.GetComponent<LocalizedText>(); if (lt != null && lt.enabled) lt.enabled = false;
        var t = go.GetComponent<TMP_Text>(); if (t != null) t.text = s;
    }

    // whatever big walk's own list beside ours shows: gamepad glyphs in it -> gamepad. (rewired's "last active
    // controller" said keyboard while big walk showed the pad, the deck's trackpad counting as a mouse.) without a
    // readable list, rewired's answer
    static bool PadInUse(PauseMenu only)
    {
        var chars = RebindPage.PadGlyphChars();
        var wm = WorldMenuManager.instance;
        if (chars != null && chars.Count > 0 && wm != null)
            foreach (var pm in new[] { wm.pauseMenuHost, wm.pauseMenuClient })
            {
                if (pm == null || !pm.gameObject.activeInHierarchy || (only != null && pm.Pointer != only.Pointer)) continue;
                var box = pm.transform.Find("ControlsList/ControlsContainer");
                if (box == null) continue;
                foreach (var t in box.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (t == null || !t.gameObject.name.StartsWith("button")) continue;
                    string s = t.text ?? "";
                    foreach (var c in chars) if (s.Contains(c)) return true;
                }
                return false;
            }
        try
        {
            var c = Game.Pc?.inputPlayer?.controllers.GetLastActiveController() ?? ReInput.controllers.GetLastActiveController();
            if (c != null) return c.type == ControllerType.Joystick;
            return ReInput.controllers.joystickCount > 0;
        }
        catch { return false; }
    }

    static string K(KeyCode k) => RebindPage.KeyGlyph(k);
    static string G(string element) => RebindPage.Glyph(element, element);

    // only what the legs add: big walk's own list already shows the arms (raise / use / drop) where the legs moved them
    static void Fill(GameObject root)
    {
        var box = root.transform.Find("ControlsContainer");
        if (box == null) return;
        bool pad = PadInUse(null);
        lastPad = pad;
        string lt = !string.IsNullOrEmpty(Plugin.LiftLeftButton.Value) ? RebindPage.ButtonShown(Plugin.LiftLeftButton.Value) : RebindPage.AxisName(Plugin.LeftTriggerAxis.Value);
        string rt = !string.IsNullOrEmpty(Plugin.LiftRightButton.Value) ? RebindPage.ButtonShown(Plugin.LiftRightButton.Value) : RebindPage.AxisName(Plugin.RightTriggerAxis.Value);
        var rows = pad
            ? new (string, string)[]
            {
                ("Lift left leg", lt), ("Lift right leg", rt), ("Place foot", G("Left Stick")), ("Crouch", lt + " & " + rt),
                ("Camera side", RebindPage.ButtonShown(Plugin.CamSideButton.Value)), ("Legs on / off", K(Plugin.LegsKey.Value)),
                ("Third person", K(Plugin.ThirdPersonKey.Value)),
            }
            : new (string, string)[]
            {
                ("Lift left leg", K(Plugin.LiftLeftKey.Value)), ("Lift right leg", K(Plugin.LiftRightKey.Value)),
                ("Place foot", K(KeyCode.W) + K(KeyCode.A) + K(KeyCode.S) + K(KeyCode.D)),
                ("Careful foot", K(Plugin.SlowFootKey.Value)), ("Camera side", K(Plugin.CamSideKey.Value)),
                ("Legs on / off", K(Plugin.LegsKey.Value)), ("Third person", K(Plugin.ThirdPersonKey.Value)),
            };
        for (int i = 1; i <= 10; i++)
        {
            var verb = box.Find("verb" + i); var btn = box.Find("button" + i);
            bool on = i <= rows.Length;
            if (verb != null) { verb.gameObject.SetActive(on); if (on) Set(verb.gameObject, rows[i - 1].Item1); }
            if (btn != null) { btn.gameObject.SetActive(on); if (on) Set(btn.gameObject, rows[i - 1].Item2); }
        }
    }
}
