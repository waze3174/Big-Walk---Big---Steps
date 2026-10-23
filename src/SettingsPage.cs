using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace BigSteps;

// the mod's options as a category of big walk's own settings menu (beside General, Graphics, Controls...): its
// category button, panel and rows are clones of big walk's, so they look, sound and navigate (gamepad included) like
// the rest. our rows' SettingsRow methods are patched to read and write our config instead of a big walk setting.
// every change saves right away; the last row resets everything to defaults
static class SettingsPage
{
    enum Kind { Slider, Cycle, Button, Text }

    class Def
    {
        public Kind kind;
        public string title;
        public Func<string> label;              // value text (cycle / button rows)
        public Action<int> cycle;               // left / right / select
        public float min, max, step;            // slider
        public Func<float> get; public Action<float> set; public string fmt = "0.00";
        public Func<bool> locked = () => false; // host's shared values
        public string group;
        public SettingsRow row;
        public GameObject go;                   // the row's object, or a text paragraph's
        public TMP_Text tmp;                    // text paragraph
        public Func<string> text;
    }

    static readonly Dictionary<IntPtr, Def> rows = new();
    static readonly HashSet<IntPtr> injected = new();
    static SettingsCatagory ourCat;
    static SettingsMenu ourMenu;
    internal static bool Injected => ourCat != null && !ourCat.WasCollected;

    // ---------------- building ----------------

    static void Inject(SettingsMenu menu)
    {
        if (menu == null || !injected.Add(menu.Pointer)) return;
        try
        {
            var cats = new[] { menu.catagoryGeneral, menu.catagoryInterface, menu.catagoryControls, menu.catagoryAudio, menu.catagoryGraphics, menu.catagoryDisplay, menu.catagoryMicrophone };
            SettingsCatagory src = null;
            SettingsRow slider = null, cycle = null, button = null;
            foreach (var c in cats)
            {
                if (c == null || c.rows == null) continue;
                src ??= c;
                foreach (var r in c.rows)
                {
                    if (r == null) continue;
                    if (slider == null && r.slider != null) slider = r;
                    else if (cycle == null && r.leftButton != null && r.rightButton != null && r.arrayLabel != null && r.slider == null) cycle = r;
                    else if (button == null && r.boringButton != null && r.slider == null && r.leftButton == null) button = r;
                }
            }
            if (src == null || cycle == null) { Plugin.L.LogWarning($"settings page: big walk's settings menu has no row to copy (cycle {cycle != null}, slider {slider != null}), the options page is off (the config file still works)"); return; }
            button ??= cycle;


            // the panel: a copy of a category, emptied of every row it had (reset button included)
            var catGo = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent);
            catGo.name = "BigStepsCategory";
            catGo.SetActive(false);
            var cat = catGo.GetComponent<SettingsCatagory>();
            Transform rowParent = null;
            foreach (var r in catGo.GetComponentsInChildren<SettingsRow>(true))
            {
                rowParent ??= r.transform.parent;
                r.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(r.gameObject);
            }
            rowParent ??= catGo.transform;

            // big walk places rows by hand or with a layout group: measure its spacing
            var srcRows = new List<RectTransform>();
            foreach (var r in src.rows) if (r != null) srcRows.Add(r.GetComponent<RectTransform>());
            rowTop = srcRows.Count > 0 ? srcRows[0].anchoredPosition : Vector2.zero;
            rowStep = srcRows.Count > 1 ? srcRows[1].anchoredPosition.y - srcRows[0].anchoredPosition.y : -80f;
            rowLayout = rowParent.GetComponent<LayoutGroup>() != null;

            // the category button: each of big walk's sits in its own holder in a list, so the holder is copied and
            // goes after the last category's holder
            var srcBtn = src.catagoryButton;
            var allCats = new[] { menu.catagoryGeneral, menu.catagoryInterface, menu.catagoryAudio, menu.catagoryMicrophone, menu.catagoryGraphics, menu.catagoryDisplay, menu.catagoryControls, menu.catagoryRebind };
            var others = new List<Transform>();
            foreach (var c in allCats) if (c?.catagoryButton != null && c.catagoryButton.Pointer != srcBtn.Pointer) others.Add(c.catagoryButton.transform);
            Transform holder = srcBtn.transform;
            while (holder.parent != null && !others.Exists(o => o.IsChildOf(holder.parent))) holder = holder.parent;
            var list = holder.parent;
            // the last category's holder in that list
            Transform lastHolder = holder;
            foreach (var o in others)
            {
                var h = o;
                while (h.parent != null && h.parent != list) h = h.parent;
                if (h.parent == list && h.GetSiblingIndex() > lastHolder.GetSiblingIndex()) lastHolder = h;
            }
            var holderGo = UnityEngine.Object.Instantiate(holder.gameObject, list);
            holderGo.name = "BigStepsCategoryButton";
            holderGo.transform.SetSiblingIndex(lastHolder.GetSiblingIndex() + 1);
            if (list.GetComponent<LayoutGroup>() == null)
            {
                // placed by hand: one step past the last, at the spacing between the last two
                var lr = lastHolder.GetComponent<RectTransform>();
                Transform prev = null;
                foreach (var o in others)
                {
                    var h = o; while (h.parent != null && h.parent != list) h = h.parent;
                    if (h.parent == list && h != lastHolder && (prev == null || h.GetSiblingIndex() > prev.GetSiblingIndex())) prev = h;
                }
                Vector2 step = prev != null ? lr.anchoredPosition - prev.GetComponent<RectTransform>().anchoredPosition : new Vector2(0, -60);
                holderGo.GetComponent<RectTransform>().anchoredPosition = lr.anchoredPosition + step;
            }
            // the copy still carries what made it General's: its event links to General's page / the menu are cut,
            // everything inside it (highlight, sounds, big walk's own button navigation) stays
            CutOutside(holderGo);
            SetText(holderGo, "Big Steps");
            var btn = holderGo.GetComponentInChildren<Button>(true);
            if (btn != null)
            {
                for (int i = 0; i < btn.onClick.GetPersistentEventCount(); i++) btn.onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
                btn.onClick.RemoveAllListeners();
                var m = menu;
                btn.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => { Open(m, cat); SelectFirstRow(); })));
                try { menu.buttonList?.Add(btn); } catch { }
            }
            ourBtn = btn != null ? btn : holderGo.GetComponentInChildren<Selectable>(true);
            cat.catagoryButton = ourBtn;
            // gamepad: down from big walk's last category reaches ours
            lastCatSel = lastHolder.GetComponentInChildren<Selectable>(true);
            navCaught = false;
            // right from our category goes into our rows (its copied right-press went to General's)
            var omb = holderGo.GetComponentInChildren<ManagedButton>(true);
            omb?.onRight?.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(SelectFirstRow)));

            // the rows, all built once; the section row on top flips which page of them shows
            ourCat = cat; ourMenu = menu;
            RebindPage.Build(menu);
            firstCatSel = menu.catagoryGeneral?.catagoryButton;
            var defs = Defs();
            section = new Def { kind = Kind.Cycle, title = "Page", label = () => pages.Count > 0 ? pages[page].name : "", cycle = d => { page = (page + d + pages.Count) % pages.Count; Relayout(); } };
            defs.Insert(0, section);
            allDefs.Clear();
            allDefs.AddRange(defs);
            var titleTpl = cycle.transform.Find("Title");
            foreach (var d in defs)
            {
                if (d.kind == Kind.Text)
                {
                    // a paragraph: big walk's row title text, sentence case, larger, wrapping
                    var tgo = UnityEngine.Object.Instantiate(titleTpl.gameObject, rowParent);
                    tgo.name = "bbs_text";
                    var tlt = tgo.GetComponent<LocalizedText>(); if (tlt != null) tlt.enabled = false;
                    var tmp = tgo.GetComponent<TMP_Text>();
                    tmp.fontStyle &= ~FontStyles.UpperCase; tmp.fontStyle &= ~FontStyles.SmallCaps;
                    tmp.fontSize *= 1.35f;
                    tmp.enableWordWrapping = true;
                    tmp.alignment = TextAlignmentOptions.TopLeft;
                    var trt = tgo.GetComponent<RectTransform>(); trt.sizeDelta = new Vector2(800, 150);
                    var le = tgo.AddComponent<LayoutElement>(); le.preferredHeight = le.minHeight = 150; le.preferredWidth = 800;
                    d.go = tgo; d.tmp = tmp;
                    continue;
                }
                var tpl = d.kind == Kind.Slider ? (slider ?? cycle) : d.kind == Kind.Button ? button : cycle;
                if (d.kind == Kind.Slider && slider == null) d.kind = Kind.Cycle;      // no slider to copy: step it with the arrows
                var go = UnityEngine.Object.Instantiate(tpl.gameObject, rowParent);
                go.name = "bbs_row " + d.title;
                var row = go.GetComponent<SettingsRow>();
                d.row = row; d.go = go;
                rows[row.Pointer] = d;
                SetTitle(row, d.title);
                CutOutside(go);
                if (d.kind == Kind.Button && row.boringButton != null)
                {
                    var dd = d;
                    row.boringButton.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => Act(dd, 1))));
                }
                if (d.kind == Kind.Slider && row.slider != null)
                {
                    row.slider.minValue = 0; row.slider.maxValue = Mathf.Round((d.max - d.min) / d.step); row.slider.wholeNumbers = true;
                }
            }
            pages.Clear();
            var groups = new List<string>();
            foreach (var d in defs) if (d != section && !groups.Contains(d.group)) groups.Add(d.group);
            foreach (var g in groups)
            {
                var inGroup = defs.FindAll(d => d != section && d.group == g);
                int n = (inGroup.Count + PerPage - 1) / PerPage;
                for (int i = 0; i < n; i++)
                    pages.Add((n > 1 ? $"{g} {i + 1}/{n}" : g, inGroup.GetRange(i * PerPage, Math.Min(PerPage, inGroup.Count - i * PerPage))));
            }
            page = 0;
            Relayout();
            Plugin.L.LogInfo($"settings page: Big Steps added to big walk's settings ({defs.Count - 1} rows on {pages.Count} pages)");
        }
        catch (Exception e) { Plugin.L.LogWarning("settings page: couldn't add the category, the options page is off (the config file still works): " + e); }
    }

    // ---------------- pages ----------------

    const int PerPage = 5;
    static readonly List<Def> allDefs = new();
    static readonly List<(string name, List<Def> defs)> pages = new();
    static int page;
    static Def section;
    static Selectable ourBtn;
    static Vector2 rowTop;
    static float rowStep;
    static bool rowLayout;

    static void Relayout()
    {
        if (!Injected || pages.Count == 0) return;
        var vis = new List<Def> { section };
        vis.AddRange(pages[page].defs);
        foreach (var d in allDefs) if (d.go != null && !d.go.WasCollected) d.go.SetActive(vis.Contains(d));
        var built = new List<SettingsRow>();
        for (int i = 0; i < vis.Count; i++)
        {
            var t = vis[i].go.transform;
            if (rowLayout) t.SetSiblingIndex(i);
            else t.GetComponent<RectTransform>().anchoredPosition = new Vector2(rowTop.x, rowTop.y + i * rowStep);
            if (vis[i].row != null) built.Add(vis[i].row);
        }
        ourCat.rows = new Il2CppReferenceArray<SettingsRow>(built.ToArray());
        // down from the last row comes back to the first, up from the first goes to the last
        for (int i = 0; i < built.Count; i++) built[i].SetNavigation(built[(i - 1 + built.Count) % built.Count], built[(i + 1) % built.Count]);
        try
        {
            if (ourBtn != null && built[0].firstSelectable != null) { var on = ourBtn.navigation; on.selectOnRight = built[0].firstSelectable; ourBtn.navigation = on; }
        }
        catch { }
        foreach (var d in vis) Refresh(d);
    }

    // a row's title: its own localization is switched off (it put big walk's label back on enable) and the text written
    static void SetTitle(SettingsRow r, string s)
    {
        var t = r.transform.Find("Title");
        if (t == null || !t.gameObject.activeSelf) return;
        var lt = t.GetComponent<LocalizedText>(); if (lt != null && lt.enabled) lt.enabled = false;
        var tmp = t.GetComponent<TMP_Text>(); if (tmp != null && tmp.text != s) tmp.text = s;
    }

    static string KeyName(KeyCode k) => RebindPage.KeyName(k);

    // a copied object keeps its UnityEvent links: those into the copy were re-pointed at it by Instantiate, those
    // outside still reach the original (General's page, the settings menu). the outside ones are switched off
    static string CutOutside(GameObject root)
    {
        var cut = new List<string>();
        void Ev(UnityEventBase ev, string where)
        {
            if (ev == null) return;
            for (int i = 0; i < ev.GetPersistentEventCount(); i++)
            {
                var tgt = ev.GetPersistentTarget(i);
                if (tgt == null) continue;
                Transform tt = null;
                var c = tgt.TryCast<Component>(); if (c != null) tt = c.transform;
                var g = tgt.TryCast<GameObject>(); if (g != null) tt = g.transform;
                if (tt != null && tt.IsChildOf(root.transform)) continue;
                // backing out of the menu is the same for us as for the original
                if (ev.GetPersistentMethodName(i).Contains("Back")) continue;
                ev.SetPersistentListenerState(i, UnityEventCallState.Off);
                cut.Add($"{where} -> {tgt.name}.{ev.GetPersistentMethodName(i)}");
            }
        }
        foreach (var b in root.GetComponentsInChildren<Button>(true)) Ev(b.onClick, "click");
        foreach (var sl in root.GetComponentsInChildren<Slider>(true)) Ev(sl.onValueChanged, "slider");
        foreach (var et in root.GetComponentsInChildren<UnityEngine.EventSystems.EventTrigger>(true))
            foreach (var en in et.triggers) Ev(en.callback, en.eventID.ToString());
        foreach (var mb in root.GetComponentsInChildren<ManagedButton>(true)) { Ev(mb.onLeft, "left"); Ev(mb.onRight, "right"); }
        return string.Join(", ", cut);
    }

    // the category column wraps (Big Steps <-> General). big walk's buttons navigate through ManagedButton, not
    // unity's navigation; it can reset them, so this is kept up while the menu is open
    static Selectable firstCatSel, lastCatSel;
    static bool navCaught;
    static (Selectable, Selectable, Selectable) firstNav, lastNav;
    static float nextNav;
    static void CategoryNav()
    {
        if (ourBtn == null || firstCatSel == null || lastCatSel == null) return;
        var rowsFirst = ourCat.rows != null && ourCat.rows.Length > 0 ? ourCat.rows[0]?.firstSelectable : null;
        Set(ourBtn, lastCatSel, firstCatSel, null, rowsFirst);
        // their other neighbours as big walk had them when we arrived
        if (!navCaught)
        {
            navCaught = true;
            firstNav = (firstCatSel.FindSelectableOnDown(), firstCatSel.FindSelectableOnLeft(), firstCatSel.FindSelectableOnRight());
            lastNav = (lastCatSel.FindSelectableOnUp(), lastCatSel.FindSelectableOnLeft(), lastCatSel.FindSelectableOnRight());
        }
        Set(firstCatSel, ourBtn, firstNav.Item1, firstNav.Item2, firstNav.Item3);
        Set(lastCatSel, lastNav.Item1, ourBtn, lastNav.Item2, lastNav.Item3);
    }

    static void Set(Selectable s, Selectable up, Selectable down, Selectable left, Selectable right)
    {
        var n = s.navigation; n.mode = Navigation.Mode.Explicit;
        n.selectOnUp = up; n.selectOnDown = down; n.selectOnLeft = left; n.selectOnRight = right;
        s.navigation = n;
        var mb = s.GetComponent<ManagedButton>();
        if (mb != null) mb.SetNavagitationTargets(up?.gameObject, down?.gameObject, left?.gameObject, right?.gameObject);
    }

    static void SetText(GameObject go, string s)
    {
        var lt = go.GetComponentInChildren<LocalizedText>(true);
        if (lt != null) { lt.Change(s, LocalizedText.DisplayType.RawValue); return; }
        var t = go.GetComponentInChildren<TMP_Text>(true);
        if (t != null) t.text = s;
    }

    // big walk's own buttons switch category when selected (pointer over / gamepad focus), clicking moves into the rows
    static void Open(SettingsMenu m, SettingsCatagory cat)
    {
        try
        {
            if (m.activeCatagory == null || m.activeCatagory.Pointer != cat.Pointer)
                try { m.SwapToCatagory(cat); } catch (Exception e) { Plugin.L.LogWarning("settings page: big walk's category switch: " + e.Message); }
            // whatever big walk's switch did with a category it doesn't know: only ours shows
            foreach (var c in new[] { m.catagoryGeneral, m.catagoryInterface, m.catagoryAudio, m.catagoryMicrophone, m.catagoryGraphics, m.catagoryDisplay, m.catagoryControls, m.catagoryRebind })
                if (c != null && c.Pointer != cat.Pointer) c.gameObject.SetActive(false);
            cat.gameObject.SetActive(true);
            foreach (var d in rows.Values) Refresh(d);
        }
        catch (Exception e) { Plugin.L.LogWarning("settings page: open failed: " + e.Message); }
    }

    // back from our rebind screens: our page, on the Controls rows
    internal static void BackFromRebind()
    {
        if (!Injected || ourMenu == null) return;
        int i = pages.FindIndex(pg => pg.defs.Exists(d => d.title.StartsWith("Controls")));
        if (i >= 0 && i != page) { page = i; Relayout(); }
        Open(ourMenu, ourCat);
        try
        {
            var d = pages[page].defs.Find(x => x.title.StartsWith("Controls"));
            var sel = d?.row?.firstSelectable;
            if (sel != null) UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(sel.gameObject);
        }
        catch { }
    }

    static void SelectFirstRow()
    {
        try
        {
            var first = ourCat?.rows != null && ourCat.rows.Length > 0 ? ourCat.rows[0]?.firstSelectable : null;
            if (first != null) UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(first.gameObject);
        }
        catch { }
    }

    // ---------------- the rows ----------------

    static string OnOff(bool b) => b ? "On" : "Off";
    static Def Toggle(string title, ConfigEntry<bool> e, Func<bool> locked = null, Func<bool> shown = null) => new()
    {
        kind = Kind.Cycle, title = title, label = () => OnOff(shown?.Invoke() ?? e.Value) + (locked?.Invoke() == true ? "  (host)" : ""),
        cycle = _ => e.Value = !e.Value, locked = locked ?? (() => false),
    };
    static Def Slide(string title, ConfigEntry<float> e, float min, float max, float step, string fmt, Func<bool> locked = null, Func<float> shown = null) => new()
    {
        kind = Kind.Slider, title = title, min = min, max = max, step = step, fmt = fmt,
        get = () => shown?.Invoke() ?? e.Value, set = v => e.Value = (float)Math.Round(v, 3), locked = locked ?? (() => false),
    };
    static Def Choice(string title, ConfigEntry<string> e, string[] opts) => new()
    {
        kind = Kind.Cycle, title = title, label = () => e.Value,
        cycle = d => { int i = Array.FindIndex(opts, o => string.Equals(o, e.Value, StringComparison.OrdinalIgnoreCase)); e.Value = opts[((i < 0 ? 0 : i) + d + opts.Length) % opts.Length]; Rebound(); },
    };
    static bool HostLocks() => Settings.Host != null;
    static Def Para(Func<string> text) => new() { kind = Kind.Text, title = "", text = text };

    static List<Def> Defs()
    {
        var host = (Func<bool>)HostLocks;
        var l = new List<Def>
        {
            Para(() => $"Big Steps {Plugin.Version}: Baby Steps legs for Big Walk. Press {KeyName(Plugin.LegsKey.Value)} to switch the legs on or off. Hold a trigger (or {KeyName(Plugin.LiftLeftKey.Value)} / {KeyName(Plugin.LiftRightKey.Value)}) to lift that leg, steer it with the stick or WASD, and let go to put it down."),
            Para(() => $"Your weight follows the planted foot: lean too far or stretch too wide and you fall. Both triggers crouch, {KeyName(Plugin.SlowFootKey.Value)} places a foot carefully, the mouse wheel trims how high it lifts. Big Walk's own controls come back while you sit or hang on."),
            Para(() => $"Arms stay Big Walk's. Gamepad: bumpers reach, raising moves to {Plugin.PadArmLeft.Value} / {Plugin.PadArmRight.Value}. Keyboard: use / drop move to {KeyName(Plugin.MouseLeftMovedTo.Value)} / {KeyName(Plugin.MouseRightMovedTo.Value)}. {KeyName(Plugin.ThirdPersonKey.Value)} third person. Everything can be changed under Controls."),
            // "now" switches what's running (in a game), "default" is saved and used whenever your bird spawns
            new() { kind = Kind.Cycle, title = "Walking now", label = () => Game.Pc == null ? "in a game only" : Legs.IsActive ? "Baby Steps" : "Big Walk",
                    cycle = _ => { if (Game.Pc != null) Legs.Inst.Toggle(); } },
            new() { kind = Kind.Cycle, title = "Default walking state", label = () => Plugin.LegsByDefault.Value ? "Baby Steps" : "Big Walk",
                    cycle = _ => Plugin.LegsByDefault.Value = !Plugin.LegsByDefault.Value },
            Slide("Height (taller steps over higher ledges)", Plugin.HeightBoost, 0.8f, 1.6f, 0.05f, "0.00", host, () => Settings.HeightBoost),
            Slide("Grip on steep slopes (Baby Steps = 1)", Plugin.SteepSlopeGrip, 1f, 2.5f, 0.1f, "0.0", host, () => Settings.SteepGrip),
            Toggle("Keep a lifted foot from crossing under the body (Baby Steps = on)", Plugin.CrossPrevention),
            Toggle("Shadows under the feet", Plugin.ShowFootShadows),
            Toggle("Share my walking settings when hosting", Plugin.ShareWithLobby),
            new() { kind = Kind.Cycle, title = "Lobby", label = LobbyText, cycle = _ => { } },
            new() { kind = Kind.Cycle, title = "Camera now", label = () => Game.Pc == null ? "in a game only" : Runner.IsThirdPerson ? "Third person" : "First person",
                    cycle = _ => Runner.Inst?.SetThirdPersonFromMenu(!Runner.IsThirdPerson) },
            new() { kind = Kind.Cycle, title = "Default camera", label = () => Plugin.ThirdPersonByDefault.Value ? "Third person" : "First person",
                    cycle = _ => Plugin.ThirdPersonByDefault.Value = !Plugin.ThirdPersonByDefault.Value },
            Slide("Camera distance", Plugin.CamDistance, 1.2f, 4f, 0.05f, "0.00"),
            Toggle("Over the shoulder camera", Plugin.CamShoulder),
            Choice("Crosshair", Plugin.CrosshairMode, new[] { "aim", "hide", "game" }),
            Slide("Crosshair size", Plugin.CrosshairScale, 0.2f, 1.5f, 0.05f, "0.00"),
            Slide("Hide the body when looking down past", Plugin.HideBodyPitch, 60f, 90f, 1f, "0°"),
            new() { kind = Kind.Button, title = "Controls keyboard", label = () => "Rebind keyboard & mouse", cycle = _ => RebindPage.Open(false) },
            new() { kind = Kind.Button, title = "Controls gamepad", label = () => "Rebind gamepad", cycle = _ => RebindPage.Open(true) },
            new() { kind = Kind.Button, title = "Reset everything to defaults", label = () => "Reset", cycle = _ => ResetAll() },
        };
        string g = "How it plays";
        foreach (var d in l)
        {
            if (d.title == "Walking now") g = "Walking";
            else if (d.title.StartsWith("Share my")) g = "Lobby";
            else if (d.title == "Camera now") g = "Camera";
            else if (d.title.StartsWith("Controls")) g = "Controls";
            else if (d.title.StartsWith("Reset")) g = "Reset";
            d.group = g;
        }
        return l;
    }

    static string LobbyText()
    {
        if (Settings.IsHost) return Plugin.ShareWithLobby.Value ? $"hosting, sharing with {Net.ModdedCount} modded" : $"hosting, {Net.ModdedCount} modded";
        if (Settings.Host != null) return $"{Settings.Host.from}'s walking settings";
        return Net.ModdedCount > 0 ? $"{Net.ModdedCount} modded, own settings" : "own settings";
    }

    static void ResetAll()
    {
        foreach (var e in Plugin.Cfg.Keys) { var c = Plugin.Cfg[e]; c.BoxedValue = c.DefaultValue; }
        Plugin.Cfg.Save();
        Rebound();
        Plugin.L.LogInfo("settings: everything back to defaults");
        foreach (var d in rows.Values) Refresh(d);
    }

    // key / button changes the arm remap and camera read once
    static void Rebound()
    {
        Runner.PadButtonsChanged();
        if (Legs.IsActive) Arms.Remap(true);
    }

    static void Refresh(Def d)
    {
        if (d.kind == Kind.Text) { try { if (d.tmp != null) d.tmp.text = d.text(); } catch { } return; }
        var r = d.row;
        if (r == null || r.WasCollected) return;
        try
        {
            SetTitle(r, d.title);
            if (d.kind == Kind.Slider && r.slider != null)
            {
                float v = d.get();
                r.slider.SetValueWithoutNotify(Mathf.Round((v - d.min) / d.step));
                r.slider.interactable = !d.locked();
                r.sliderLabel?.Change(v.ToString(d.fmt) + (d.locked() ? "  (host)" : ""), LocalizedText.DisplayType.RawValue);
            }
            else
            {
                string s = d.kind == Kind.Slider ? d.get().ToString(d.fmt) : d.label();
                if (r.arrayLabel != null) r.arrayLabel.Change(s, LocalizedText.DisplayType.RawValue);
                else if (r.boringButton != null) SetText(r.boringButton.gameObject, s);
            }
        }
        catch { }
    }

    static void Act(Def d, int delta)
    {
        if (d.locked()) { Refresh(d); return; }
        if (d.kind == Kind.Slider) d.set(Mathf.Clamp(d.get() + delta * d.step, d.min, d.max));
        else d.cycle?.Invoke(delta);
        foreach (var x in rows.Values) Refresh(x);
    }

    // ---------------- every frame (Runner) ----------------

    internal static void Update()
    {
        // focus on our category button (pointer over it, or the gamepad) opens our page, like big walk's own
        try
        {
            if (Injected && ourMenu != null && ourMenu.gameObject.activeInHierarchy && Time.unscaledTime >= nextNav) { nextNav = Time.unscaledTime + 0.5f; CategoryNav(); }
            if (Injected && ourBtn != null && ourMenu != null && ourMenu.gameObject.activeInHierarchy)
            {
                var sel = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
                if (sel != null && sel.Pointer == ourBtn.gameObject.Pointer && !ourCat.gameObject.activeSelf) Open(ourMenu, ourCat);
            }
        }
        catch { }
    }

    // ---------------- big walk hooks ----------------

    [HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.OnEnable))]
    static class MenuEnable { static void Postfix(SettingsMenu __instance) => Inject(__instance); }

    // big walk only knows its own categories: ours hides when another opens and shows when picked
    [HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.SwapToCatagory))]
    static class Swap
    {
        static void Postfix(SettingsCatagory newCatagory)
        {
            if (!Injected) return;
            bool mine = newCatagory != null && newCatagory.Pointer == ourCat.Pointer;
            ourCat.gameObject.SetActive(mine);
            if (mine) foreach (var d in rows.Values) Refresh(d);
        }
    }

    static bool Mine(SettingsRow r, out Def d) { d = null; return r != null && rows.TryGetValue(r.Pointer, out d); }

    [HarmonyPatch(typeof(SettingsRow), nameof(SettingsRow.Start))] static class RStart { static bool Prefix(SettingsRow __instance) { if (!Mine(__instance, out var d)) return true; Refresh(d); return false; } }
    [HarmonyPatch(typeof(SettingsRow), nameof(SettingsRow.OnEnable))] static class REnable { static bool Prefix(SettingsRow __instance) { if (!Mine(__instance, out var d)) return true; Refresh(d); return false; } }
    [HarmonyPatch(typeof(SettingsRow), nameof(SettingsRow.Refresh))] static class RRefresh { static bool Prefix(SettingsRow __instance) { if (!Mine(__instance, out var d)) return true; Refresh(d); return false; } }
    [HarmonyPatch(typeof(SettingsRow), nameof(SettingsRow.ActionLeft))] static class RLeft { static bool Prefix(SettingsRow __instance) { if (!Mine(__instance, out var d)) return true; Act(d, -1); return false; } }
    [HarmonyPatch(typeof(SettingsRow), nameof(SettingsRow.ActionRight))] static class RRight { static bool Prefix(SettingsRow __instance) { if (!Mine(__instance, out var d)) return true; Act(d, 1); return false; } }
    [HarmonyPatch(typeof(SettingsRow), nameof(SettingsRow.ActionCycle))] static class RCycle { static bool Prefix(SettingsRow __instance, int delta) { if (!Mine(__instance, out var d)) return true; Act(d, delta); return false; } }
    [HarmonyPatch(typeof(SettingsRow), nameof(SettingsRow.ActionSelect))] static class RSel { static bool Prefix(SettingsRow __instance) { if (!Mine(__instance, out var d)) return true; Act(d, 1); return false; } }
    [HarmonyPatch(typeof(SettingsRow), nameof(SettingsRow.ActionSelect0))] static class RSel0 { static bool Prefix(SettingsRow __instance) { if (!Mine(__instance, out var d)) return true; Act(d, 1); return false; } }
    [HarmonyPatch(typeof(SettingsRow), nameof(SettingsRow.ActionSelect1))] static class RSel1 { static bool Prefix(SettingsRow __instance) { if (!Mine(__instance, out var d)) return true; Act(d, 1); return false; } }
    [HarmonyPatch(typeof(SettingsRow), nameof(SettingsRow.ActionSelect2))] static class RSel2 { static bool Prefix(SettingsRow __instance) { if (!Mine(__instance, out var d)) return true; Act(d, 1); return false; } }
    [HarmonyPatch(typeof(SettingsRow), nameof(SettingsRow.ActionSetFromSlider))]
    static class RSlider
    {
        static bool Prefix(SettingsRow __instance)
        {
            if (!Mine(__instance, out var d)) return true;
            if (!d.locked() && d.kind == Kind.Slider && __instance.slider != null) d.set(d.min + __instance.slider.value * d.step);
            Refresh(d);
            return false;
        }
    }
}
