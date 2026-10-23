using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace BigSteps;

[BepInPlugin(Guid, "Big Steps", Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "waze3174.bigsteps";
    public const string Version = "0.9.26";
    // the big walk build this was made and tested on (Settings shows v1.6.0)
    const string TestedBigWalk = "1.6.0";
    internal static ManualLogSource L;
    internal static ConfigEntry<KeyCode> ThirdPersonKey, HudKey, LegsKey, LiftLeftKey, LiftRightKey, SlowFootKey, MouseLeftMovedTo, MouseRightMovedTo, RecordKey;
    internal static ConfigEntry<float> CamDistance, HideBodyPitch, SteepSlopeGrip, HeightBoost;
    internal static ConfigEntry<string> PadArmLeft, PadArmRight, CamSideButton, CrosshairMode, LiftLeftButton, LiftRightButton;
    internal static ConfigEntry<KeyCode> CamSideKey;
    internal static ConfigFile Cfg;
    internal static ConfigEntry<float> CrosshairScale;
    internal static ConfigEntry<bool> ShowFootShadows, CrossPrevention, CamShoulder, FitToBirdLegs, ShareWithLobby, LegsByDefault, ThirdPersonByDefault;
    internal static ConfigEntry<int> LeftTriggerAxis, RightTriggerAxis;

    // up to 0.7 the mod was "Big Baby Steps" (waze3174.bigbabysteps.cfg, BigBabySteps.dll): keep its settings, and retire
    // an old dll left beside this one (both would run)
    void MigrateOldInstall()
    {
        try
        {
            string oldCfg = System.IO.Path.Combine(Paths.ConfigPath, "waze3174.bigbabysteps.cfg");
            if (!System.IO.File.Exists(Config.ConfigFilePath) && System.IO.File.Exists(oldCfg))
            {
                System.IO.File.Copy(oldCfg, Config.ConfigFilePath);
                Config.Reload();
                L.LogInfo("settings carried over from the old Big Baby Steps config");
            }
            string oldDll = System.IO.Path.Combine(Paths.PluginPath, "BigBabySteps.dll");
            if (System.IO.File.Exists(oldDll))
            {
                try { System.IO.File.Move(oldDll, oldDll + ".old"); L.LogWarning("the old BigBabySteps.dll was still in plugins: renamed it to BigBabySteps.dll.old - restart the game so only Big Steps runs"); }
                catch { L.LogError("the old BigBabySteps.dll is still in BepInEx/plugins and running beside Big Steps: delete it and restart"); }
            }
        }
        catch (System.Exception e) { L.LogWarning("carrying over the old install: " + e.Message); }
    }

    public override void Load()
    {
        L = Log;
        MigrateOldInstall();
        Cfg = Config;
        ShareWithLobby = Config.Bind("Lobby", "ShareWithLobby", true, "when you host: everyone using the mod in your lobby walks with your height / steep grip / bird fit");
        ThirdPersonKey = Config.Bind("Keys", "ThirdPerson", KeyCode.F6, "toggle third person camera");
        HudKey = Config.Bind("Keys", "GroundHud", KeyCode.F7, "toggle ground info readout");
        LegsKey = Config.Bind("Keys", "BabyLegs", KeyCode.F9, "toggle baby steps legs");
        LiftLeftKey = Config.Bind("Legs", "LeftLegKey", KeyCode.Mouse0, "keyboard/mouse: hold to lift left leg (baby steps: left mouse)");
        LiftRightKey = Config.Bind("Legs", "RightLegKey", KeyCode.Mouse1, "keyboard/mouse: hold to lift right leg (baby steps: right mouse)");
        RecordKey = Config.Bind("Debug", "RecordKey", KeyCode.F12, "start/stop recording the legs sim to BepInEx/BigStepsDump");
        SlowFootKey = Config.Bind("Legs", "SlowFootKey", KeyCode.LeftShift, "hold for slow foot mode (baby steps: left shift)");
        MouseLeftMovedTo = Config.Bind("Legs", "BigWalkLeftMouseMovesTo", KeyCode.F, "while legs are on, whatever big walk had on left mouse (use / left arm reach) goes here");
        MouseRightMovedTo = Config.Bind("Legs", "BigWalkRightMouseMovesTo", KeyCode.G, "while legs are on, whatever big walk had on right mouse (drop / right arm reach) goes here");
        // use / drop defaults were F / R up to 0.9.12 and X / C in 0.9.13: those old defaults become F / G
        if ((MouseLeftMovedTo.Value == KeyCode.F && MouseRightMovedTo.Value == KeyCode.R) || (MouseLeftMovedTo.Value == KeyCode.X && MouseRightMovedTo.Value == KeyCode.C))
        { MouseLeftMovedTo.Value = KeyCode.F; MouseRightMovedTo.Value = KeyCode.G; L.LogInfo("use / drop with the legs on: F / G"); }
        PadArmLeft = Config.Bind("Gamepad", "ArmRaiseLeftButton", "X", "gamepad button that raises the left arm while legs are on (the triggers are legs; X sits left, B right)");
        PadArmRight = Config.Bind("Gamepad", "ArmRaiseRightButton", "B", "gamepad button that raises the right arm while legs are on");
        LeftTriggerAxis = Config.Bind("Gamepad", "LeftTriggerAxis", 4, "rewired axis index of the left trigger");
        RightTriggerAxis = Config.Bind("Gamepad", "RightTriggerAxis", 5, "rewired axis index of the right trigger");
        LiftLeftButton = Config.Bind("Gamepad", "LiftLeftButton", "", "gamepad button that lifts the left leg instead of the trigger axis (empty = use LeftTriggerAxis)");
        LiftRightButton = Config.Bind("Gamepad", "LiftRightButton", "", "gamepad button that lifts the right leg instead of the trigger axis (empty = use RightTriggerAxis)");
        HeightBoost = Config.Bind("Legs", "HeightBoost", 1.2f, "nate's size on top of the bird fit: higher stands taller and steps higher (big walk's ledges are built for its own step-up). 1 = exact bird leg length, ~1.35 = nate's own");
        FitToBirdLegs = Config.Bind("Legs", "FitToBirdLegs", true, "run nate's walking at the bird's size (leg length ~0.59 vs nate 0.79) from its leg roots. false = full nate size from the body centre (feet lift into the belly)");
        SteepSlopeGrip = Config.Bind("Legs", "SteepSlopeGrip", 1.5f, "grip multiplier on 30+ deg slopes. baby steps fades its x1.5 flat-ground grip to x1 by 30 deg (set 1 for exact baby steps); big walk's steep hills need it kept");
        CrossPrevention = Config.Bind("Legs", "CrossPrevention", false, "baby steps' rule that keeps a lifted foot from crossing under the body (its StrafeConstrain). on = exact baby steps; off plays freer in big walk");
        ShowFootShadows = Config.Bind("Legs", "ShowFootShadows", true, "dark discs on the ground under nate's feet (shrink as the foot lifts)");
        LegsByDefault = Config.Bind("Legs", "DefaultWalkingState", false, "true = Baby Steps legs switch on by themselves whenever your bird spawns (false = Big Walk's walking until you press the legs key)");
        ThirdPersonByDefault = Config.Bind("Camera", "DefaultThirdPerson", false, "true = start in the third person camera whenever your bird spawns");
        CamDistance = Config.Bind("Camera", "BaseDistance", 2.15f, "camera distance (baby steps' PlaceCamera: 2.15, closing to 1.75 when looking down)");
        CamShoulder = Config.Bind("Camera", "ShoulderOffset", true, "over the shoulder framing: the camera sits to one side of the bird (switch sides with SwitchSideKey / SwitchSideButton)");
        CamSideKey = Config.Bind("Camera", "SwitchSideKey", KeyCode.Space, "keyboard: swap the over-the-shoulder side (baby steps' camera switch). gamepad: SwitchSideButton");
        CamSideButton = Config.Bind("Camera", "SwitchSideButton", "A", "gamepad button that swaps the camera side while legs are on");
        CrosshairMode = Config.Bind("Camera", "Crosshair", "aim", "third person crosshair: aim = drawn where the first person view really points, hide = no crosshair, game = untouched");
        CrosshairScale = Config.Bind("Camera", "CrosshairScale", 0.6f, "crosshair size in third person (1 = big walk's)");
        HideBodyPitch = Config.Bind("Camera", "HideBodyAtPitch", 85f, "third person: hide the body once the view pitches this far down (degrees, 89 = only fully down, 90 = never)");
        ClassInjector.RegisterTypeInIl2Cpp<Runner>();
        ClassInjector.RegisterTypeInIl2Cpp<LegsDriver>();
        AddComponent<Runner>();
        AddComponent<LegsDriver>();
        var harmony = new Harmony(Guid);
        // one hook at a time: a big walk update that renames one method only loses that feature (logged), instead of
        // PatchAll stopping at the first failure and leaving every hook after it unapplied
        int ok = 0; var failed = new System.Collections.Generic.List<string>();
        foreach (var t in typeof(Plugin).Assembly.GetTypes())
        {
            if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try { harmony.CreateClassProcessor(t).Patch(); ok++; }
            catch (System.Exception e) { failed.Add(t.Name); L.LogError($"hook {t.Name} failed (big walk changed?), that feature is off: {(e.InnerException ?? e).Message}"); }
        }
        L.LogInfo($"{ok} hooks in" + (failed.Count > 0 ? $", {failed.Count} failed: {string.Join(", ", failed)}" : ""));
        if (Application.version != TestedBigWalk)
            L.LogWarning($"Big Walk {Application.version}: Big Steps {Version} was made on {TestedBigWalk}. If something's off, the lines above say which part didn't find what it needs.");
        // dev builds carry src/Dev (DevTools); release builds don't, and this finds nothing
        var dev = typeof(Plugin).Assembly.GetType("BigSteps.DevTools");
        if (dev != null)
            try { dev.GetMethod("Install", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.Invoke(null, new object[] { this, harmony }); }
            catch (System.Exception e) { L.LogWarning("dev tools: " + (e.InnerException ?? e).Message); }
        L.LogInfo("loaded");
    }
}

// the game's own walking is skipped for the local bird while legs are on
[HarmonyPatch(typeof(PlayerMover), nameof(PlayerMover.FixedUpdate))]
static class MoverPatch
{
    static bool Prefix(PlayerMover __instance) => !Legs.Owns(__instance.pc);
}

// big walk's crouch resizes the body capsule the legs already shrank; baby steps crouches on both triggers instead
// big walk's own fall (long drop -> dazed sitting) fights the legs and soft-locks; the legs have their own falls.
// the whole fall tracker is skipped: blocking only TriggerFall left its Update throwing every frame
[HarmonyPatch(typeof(PlayerFaller), nameof(PlayerFaller.Update))]
static class FallerUpdatePatch
{
    static bool Prefix(PlayerFaller __instance) => !Legs.Owns(__instance.playerCharacter);
}

[HarmonyPatch(typeof(PlayerFaller), nameof(PlayerFaller.TriggerFall))]
static class FallerPatch
{
    static bool Prefix(PlayerFaller __instance) => !Legs.Owns(__instance.playerCharacter);
}

[HarmonyPatch(typeof(PlayerCroucher), nameof(PlayerCroucher.Update))]
static class CroucherPatch
{
    static bool Prefix(PlayerCroucher __instance) => !Legs.Owns(__instance.pc);
}

// moving platforms: the legs carry the bird themselves (Carrier) - big walk's sticky platform mustn't move it a second time
[HarmonyPatch(typeof(StickyPlatformer), nameof(StickyPlatformer.FixedUpdate))]
static class StickyPlatformerPatch
{
    static bool Prefix(StickyPlatformer __instance)
    {
        var pc = Game.Pc;
        if (pc == null || !Legs.Owns(pc)) return true;
        try { return __instance.rb == null || __instance.rb.Pointer != pc.rb.Pointer; } catch { return true; }
    }
}

// the train switches its cars' colliders off (culls them) away from players. riding one with the legs on, the car under
// us got culled ~13 s after boarding and we dropped through its floor; whatever position big walk checks evidently lags.
// a car near our body is kept solid (logged once per car, with the distance)
[HarmonyPatch(typeof(TrainCar), nameof(TrainCar.UpdateCullState))]
static class TrainCullPatch
{
    static readonly System.Collections.Generic.HashSet<string> told = new();

    static void Postfix(TrainCar trainCar, NetworkedTrain.CullMode cullMode)
    {
        try
        {
            if (trainCar == null || !trainCar.cullState || !Legs.IsActive || Game.Pc == null || Game.Pc.rb == null) return;
            float d = Vector3.Distance(trainCar.transform.position, Game.Pc.rb.position);
            if (d > 25f) return;
            TrainCar.SetCullState(trainCar, false, cullMode);
            if (told.Add(trainCar.name)) Plugin.L.LogInfo($"train: big walk culled the car {d:F1} m from us ({trainCar.name}), kept it solid");
        }
        catch { }
    }
}

// other players see where the bird's hips face, not where its camera looks: big walk sends the player's rotation as
// the look direction; with the legs on (and upright) our own sends carry nate's hip yaw instead
static class FacingSync
{
    internal static bool Ours(LobbyNetworking.HouseNetworkTransform t)
    {
        try { return Legs.IsActive && !Legs.IsFallen && !Legs.Paused && t != null && t.isOwned && Game.Pc != null && t.gameObject.Pointer == Game.Pc.gameObject.Pointer; }
        catch { return false; }
    }

    internal static uint Hips() => Mirror.Compression.CompressQuaternion(Quaternion.Euler(0, Legs.HipYaw, 0));
}

[HarmonyPatch(typeof(LobbyNetworking.HouseNetworkTransform), nameof(LobbyNetworking.HouseNetworkTransform.CmdMove))]
static class FacingCmdPatch
{
    static void Prefix(LobbyNetworking.HouseNetworkTransform __instance, ref uint packedRotation) { if (FacingSync.Ours(__instance)) packedRotation = FacingSync.Hips(); }
}

// the host sends its own moves straight out
[HarmonyPatch(typeof(LobbyNetworking.HouseNetworkTransform), nameof(LobbyNetworking.HouseNetworkTransform.RpcMove))]
static class FacingRpcPatch
{
    static void Prefix(LobbyNetworking.HouseNetworkTransform __instance, ref uint packedRotation) { if (FacingSync.Ours(__instance)) packedRotation = FacingSync.Hips(); }
}

// big walk stops sending a bird that stands still ("resting") and every other client then lets its copy settle at big
// walk's standing height: with a height setting, a still modded bird sank to the default height on everyone else's
// screen. with the legs on ours keeps sending (a few times a second while still), so it stays at its own height
[HarmonyPatch(typeof(LobbyNetworking.HouseNetworkTransform), nameof(LobbyNetworking.HouseNetworkTransform.HasMoved))]
static class KeepSyncingPatch
{
    static float last;
    static void Postfix(LobbyNetworking.HouseNetworkTransform __instance, ref bool __result)
    {
        if (!FacingSync.Ours(__instance)) return;
        if (__result) { last = Time.unscaledTime; return; }
        if (Time.unscaledTime - last >= 0.2f) { __result = true; last = Time.unscaledTime; }
    }
}

[HarmonyPatch(typeof(LobbyNetworking.HouseNetworkTransform), nameof(LobbyNetworking.HouseNetworkTransform.CanSleep))]
static class NoSleepPatch
{
    static void Postfix(LobbyNetworking.HouseNetworkTransform __instance, ref bool __result) { if (__result && FacingSync.Ours(__instance)) __result = false; }
}

[HarmonyPatch(typeof(LobbyNetworking.HouseNetworkTransform), nameof(LobbyNetworking.HouseNetworkTransform.IsRestingForPlayerMovement), MethodType.Getter)]
static class NotRestingPatch
{
    static void Postfix(LobbyNetworking.HouseNetworkTransform __instance, ref bool __result) { if (__result && FacingSync.Ours(__instance)) __result = false; }
}

// sitting down mid-fall (ragdolled or dropping off something) cancelled the fall: not while the legs have you falling
[HarmonyPatch(typeof(PlayerSitter), nameof(PlayerSitter.SetSittingLocal))]
static class NoSitWhileFallingPatch
{
    static bool Prefix(PlayerSitter __instance, bool isSitting)
    {
        try
        {
            if (!isSitting || !Game.IsLocal(__instance.playerCharacter)) return true;
            return !(Legs.IsFallen || Legs.Airborne);
        }
        catch { return true; }
    }
}

// big walk's ground check casts a fixed length under the bird's collider: with the legs on (taller stance, shrunk
// capsule) it misses the ground at higher heights, and big walk then refuses to let you sit. a planted foot is ground
[HarmonyPatch(typeof(PlayerGround), nameof(PlayerGround.FixedUpdate))]
static class GroundedPatch
{
    static void Postfix(PlayerGround __instance)
    {
        try { if (Legs.FootDown && Game.IsLocal(__instance.playerCharacter)) __instance.isGrounded = true; } catch { }
    }
}

// whatever drops the item (by hand, the landing drop, a throw), big walk lets it go from the hand. with the legs on the
// hand is wherever nate's body put it: crouched, fallen or deep in a ragdoll that's at or under the floor, and the item
// fell out of the world. so with the legs on it starts from where a standing bird's hand would hold it
[HarmonyPatch]
static class DropFromStandingPatch
{
    internal const float StandHeight = 0.6f;      // a standing big walk bird's held item, above the ground

    static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        foreach (var m in typeof(Prop).GetMethods())
            if (m.Name == nameof(Prop.SetDropped)) yield return m;
    }

    static void Postfix(Prop __instance, PlayerCharacter holder)
    {
        try
        {
            if (__instance == null || holder == null || Game.Pc == null || holder.Pointer != Game.Pc.Pointer) return;
            var t = __instance.transform;
            var p = t.position;
            if (!DropWatch.Ground(p, Mathf.Max(p.y, holder.transform.position.y) + 1.5f, __instance, out float g)) { Plugin.L.LogInfo($"drop: no ground found under the item at {p}"); return; }
            float above = p.y - g;
            if (!Legs.IsActive) { Plugin.L.LogInfo($"drop: big walk let it go {above:F2} m above the ground"); return; }
            Legs.RagdollReleaseItem(__instance);
            var to = above < StandHeight ? new Vector3(p.x, g + StandHeight, p.z) : p;
            if (above < StandHeight) DropWatch.Place(__instance, to);
            bool owned = false; try { owned = __instance.houseNetworkTransform != null && __instance.houseNetworkTransform.isOwned; } catch { }
            Plugin.L.LogInfo($"drop: let go {above:F2} m above the ground{(above < StandHeight ? ", started from standing height" : "")} (fallen {Legs.IsFallen}, we sync it {owned})");
            DropWatch.Add(__instance, to);
        }
        catch (System.Exception e) { Plugin.L.LogWarning("drop: " + e.Message); }
    }
}

// a dropped item of ours that still ends up under the ground in its first seconds goes back on top of it
static class DropWatch
{
    class W { public Prop prop; public Vector3 at; public float until; public int saves; }
    static readonly System.Collections.Generic.List<W> list = new();

    internal static void Add(Prop p, Vector3 at) => list.Add(new W { prop = p, at = at, until = Time.time + 4f });

    internal static bool Ground(Vector3 p, float fromY, Prop skip, out float y)
    {
        y = 0;
        int mask = Game.Pc != null ? Game.Pc.ground.layerMask.value : ~0;
        if (!Surface.Ray(new Vector3(p.x, fromY, p.z), Vector3.down, 8f, mask, out var h, skip != null ? skip.transform : null)) return false;
        y = h.point.y;
        return true;
    }

    internal static void Place(Prop prop, Vector3 to)
    {
        prop.transform.position = to;
        var rb = prop.GetComponent<Rigidbody>();
        if (rb != null) { rb.position = to; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
    }

    internal static void Step()
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var w = list[i];
            try
            {
                if (w.prop == null || w.prop.WasCollected || Time.time > w.until || w.saves >= 3) { list.RemoveAt(i); continue; }
                var pos = w.prop.transform.position;
                // ground right above it (an overhang further up doesn't count), or far below where it was let go
                bool under = Ground(pos, pos.y + 1f, w.prop, out float g) && pos.y < g - 0.05f;
                bool gone = pos.y < w.at.y - 3f;
                if (!under && !gone) continue;
                var to = Ground(w.at, w.at.y + 2f, w.prop, out float g0) ? new Vector3(w.at.x, g0 + DropFromStandingPatch.StandHeight, w.at.z) : w.at;
                Place(w.prop, to);
                w.saves++;
                Plugin.L.LogInfo($"drop: the item went {(under ? "under the ground" : "down through the world")} ({pos.y - g:F2} m), put back on top");
            }
            catch { list.RemoveAt(i); }
        }
    }
}

// dropping an item while ragdolled (by hand, or the landing drop): big walk places it from the body, which follows the
// pelvis down near the ground, so it spawned under the floor and fell out of the world. lifted to standing height
[HarmonyPatch(typeof(PlayerNetworking), nameof(PlayerNetworking.CmdPickUp))]
static class DropAboveGroundPatch
{
    static void Prefix(PlayerNetworking __instance, ref PlayerHeldInformation heldInformation)
    {
        try
        {
            if (!Legs.IsActive || Game.Pc == null || __instance.Pointer != Game.Pc.playerNetworking?.Pointer || !heldInformation.hasDropData) return;
            var p = heldInformation.dropPosition;
            if (!Surface.Ray(p + Vector3.up * 2f, Vector3.down, 6f, Game.Pc.ground.layerMask.value, out var h)) return;
            if (p.y >= h.point.y + DropFromStandingPatch.StandHeight) return;
            heldInformation.dropPosition = new Vector3(p.x, h.point.y + DropFromStandingPatch.StandHeight, p.z);
            Plugin.L.LogInfo($"drop: thrown from {p.y - h.point.y:F2} m above the ground, started from standing height instead");
        }
        catch { }
    }
}
