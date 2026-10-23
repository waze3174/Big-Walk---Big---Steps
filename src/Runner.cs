using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace BigSteps;

// third person camera + ground hud
public class Runner : MonoBehaviour
{
    public Runner(IntPtr ptr) : base(ptr) { }

    bool thirdPerson, hud, hooked, camTooClose;     // the F7 readout starts hidden
    // looking through binoculars: big walk's first person view and overlay, the third person camera steps aside
    bool scoped;
    bool View => thirdPerson && !scoped;
    float savedUpper = float.NaN, savedLower = float.NaN;

    // camera pose saved before render so the game's own logic never sees our offset
    Vector3 savedPos;
    bool moved;
    int savedMask;

    string groundText = "";
    bool netErrorLogged;

    void OnApplicationQuit() { try { Net.Shutdown(); } catch { } }
    bool fadeBroken;

    internal static Runner Inst;
    internal static bool IsThirdPerson => Inst != null && Inst.thirdPerson;
    internal void SetThirdPersonFromMenu(bool on) { if (Game.Pc != null && Game.Cam != null && on != thirdPerson) SetThirdPerson(on); }

    void Update()
    {
        Inst = this;
        Game.Refresh();
        if (!hooked) Hook();

        if (Input.GetKeyDown(Plugin.ThirdPersonKey.Value)) SetThirdPerson(!thirdPerson);
        try { NewBird(); } catch (Exception e) { if (!birdErr) { birdErr = true; Plugin.L.LogWarning("new bird: " + e.Message); } }
        try { PauseRef.Update(); } catch (Exception e) { if (!pauseErr) { pauseErr = true; Plugin.L.LogWarning("pause reference: " + e); } }
        try { RebindPage.Update(); } catch (Exception e) { if (!rebindErr) { rebindErr = true; Plugin.L.LogWarning("rebind page: " + e); } }
        try { SettingsPage.Update(); } catch (Exception e) { if (!pageErr) { pageErr = true; Plugin.L.LogWarning("settings page: " + e); } }
        try { DropWatch.Step(); } catch { }
        try { Settings.Update(); } catch (Exception e) { if (!setErr) { setErr = true; Plugin.L.LogWarning("lobby settings: " + e); } }
        Scope();
        if (Input.GetKeyDown(Plugin.HudKey.Value)) hud = !hud;

        try { Net.Update(); } catch (Exception e) { if (!netErrorLogged) { netErrorLogged = true; Plugin.L.LogWarning("net: " + e); } }
        if (hud && Game.Pc != null) groundText = ReadGround() + "\n" + Net.Status + "  " + Remotes.Status;
        try { CameraSide(); } catch (Exception e) { if (!sideErrorLogged) { sideErrorLogged = true; Plugin.L.LogWarning("camera side: " + e.Message); } }

        try
        {
            if (fadeBroken) { }
            else if (View && Game.Pc != null && Game.Cam != null)
            {
                // only once the view is pitched (nearly) all the way down, i.e. the camera is at the top of its orbit
                float pitch = Mathf.Asin(Mathf.Clamp(-Game.Cam.transform.forward.y, -1, 1)) * Mathf.Rad2Deg;
                BodyFade.Apply(Game.Pc, pitch >= Plugin.HideBodyPitch.Value || camTooClose ? 0f : 1f);
            }
            else BodyFade.Restore();
        }
        catch (Exception e) { Plugin.L.LogWarning("body fade failed, off: " + e.Message); fadeBroken = true; }
    }

    void Hook()
    {
        RenderPipelineManager.beginCameraRendering += (Il2CppSystem.Action<ScriptableRenderContext, Camera>)new Action<ScriptableRenderContext, Camera>(BeginRender);
        RenderPipelineManager.endCameraRendering += (Il2CppSystem.Action<ScriptableRenderContext, Camera>)new Action<ScriptableRenderContext, Camera>(EndRender);
        hooked = true;
    }

    void SetThirdPerson(bool on)
    {
        var pc = Game.Pc;
        if (pc == null || Game.Cam == null) { Plugin.L.LogWarning("no local player/camera yet"); return; }
        thirdPerson = on;
        followInit = false;
        if (!on) RestoreCrosshair();
        try
        {
            var tu = pc.tunings;
            if (on) { savedUpper = tu.upperLookLimit; savedLower = tu.lowerLookLimit; tu.lowerLookLimit = Mathf.Sign(savedLower == 0 ? 1 : savedLower) * 89f; }
            else if (!float.IsNaN(savedLower)) { tu.upperLookLimit = savedUpper; tu.lowerLookLimit = savedLower; }
        }
        catch (Exception e) { Plugin.L.LogWarning("look limit change failed: " + e.Message); }
        // remote mode is how other players see you: full body, face included
        try { if (on) pc.looks.SetBodyToRemoteMode(); else pc.looks.SetBodyToLocalMode(); }
        catch (Exception e) { Plugin.L.LogWarning("body mode switch failed: " + e.Message); }
        Plugin.L.LogInfo("third person " + (on ? "on" : "off"));
    }

    void Scope()
    {
        bool now = false;
        try { var e = Game.Pc?.playerEyes; now = e != null && e.binocularsActive; } catch { }
        if (now == scoped) return;
        scoped = now;
        if (!thirdPerson) return;
        followInit = false;
        if (scoped) RestoreCrosshair();
        try { if (scoped) Game.Pc.looks.SetBodyToLocalMode(); else Game.Pc.looks.SetBodyToRemoteMode(); } catch { }
        Plugin.L.LogInfo(scoped ? "binoculars up: first person while looking" : "binoculars down: third person back");
    }

    void BeginRender(ScriptableRenderContext ctx, Camera c)
    {
        if (Game.Cam != null && c != null && c.Pointer == Game.Cam.Pointer) { Legs.Inst.BeforeRender(); try { Remotes.Pose(); } catch { } }
        try { MoveCamera(c); }
        catch (Exception e)
        {
            if (!camErrorLogged) { Plugin.L.LogError("third person camera: " + e); camErrorLogged = true; }
            if (moved && Game.Cam != null) { Game.Cam.transform.SetPositionAndRotation(savedPos, savedRot); Game.Cam.cullingMask = savedMask; moved = false; }
        }
    }

    // ---------------- camera side switch (baby steps: camRight toggle, sprung camRightAmount) ----------------

    bool camRight, sideErrorLogged;
    float camRightAmount, camRightVel, camRightTime;
    static int sideButton = -2;
    internal static void PadButtonsChanged() => sideButton = -2;

    void CameraSide()
    {
        if (!View || !Legs.IsActive || Legs.Paused || Game.Pc == null || Game.Cam == null || !Game.InputUp) return;
        bool press = Input.GetKeyDown(Plugin.CamSideKey.Value);
        var p = Game.Pc.inputPlayer;
        if (p.controllers.joystickCount > 0)
        {
            var j = p.controllers.Joysticks[0];
            if (sideButton == -2) sideButton = Arms.FindButton(j, Plugin.CamSideButton.Value);
            if (sideButton >= 0 && j.GetButtonDownById(sideButton)) press = true;
        }
        if (press) camRight = !camRight;
        // baby steps' spring, stepped at 480 hz
        float target = camRight ? 1 : 0;
        camRightTime += Time.unscaledDeltaTime;
        while (camRightTime > 1f / 480f)
        {
            camRightTime -= 1f / 480f;
            camRightVel = (camRightVel + (target - camRightAmount) * 0.04f * 0.0125f) * 0.96125f;
            camRightAmount += camRightVel;
        }
    }

    // ---------------- crosshair: where the first person view really points ----------------

    Transform crossRoot;
    Vector3 crossPos, crossScale;
    bool crossSaved;

    void PlaceCrosshair(Camera cam, Vector3 firstPos, Quaternion firstRot, int env)
    {
        string mode = Plugin.CrosshairMode.Value.ToLowerInvariant();
        if (mode == "game") { RestoreCrosshair(); return; }
        if (crossRoot == null)
        {
            var ch = Crosshair.instance;
            if (ch == null || ch.crosshairNormal == null) return;
            crossRoot = ch.crosshairNormal.parent ?? ch.crosshairNormal;
            crossPos = crossRoot.position; crossScale = crossRoot.localScale; crossSaved = true;
        }
        if (mode == "hide") { crossRoot.localScale = Vector3.zero; return; }
        Vector3 fwd = firstRot * Vector3.forward;
        Vector3 aim = Physics.Raycast(firstPos, fwd, out var h, 30f, env, QueryTriggerInteraction.Ignore) ? h.point : firstPos + fwd * 30f;
        // what big walk will actually grab / peck / press (its cast is wider than a ray: thin poles and small buttons
        // disagreed): the crosshair sits on that
        try
        {
            var caster = Game.Pc?.caster;
            if (caster != null && (caster.castableTarget != null || caster.castProp != null || caster.castCharacter != null) && caster.hitPosition != Vector3.zero)
                aim = caster.hitPosition;
        }
        catch { }
        Vector3 sp = cam.WorldToScreenPoint(aim);
        if (sp.z <= 0) { crossRoot.localScale = Vector3.zero; return; }
        crossRoot.localScale = crossScale * Plugin.CrosshairScale.Value;
        var canvas = crossRoot.GetComponentInParent<Canvas>();
        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay && canvas.worldCamera != null)
            crossRoot.position = canvas.worldCamera.ScreenToWorldPoint(new Vector3(sp.x, sp.y, canvas.planeDistance));
        else crossRoot.position = new Vector3(sp.x, sp.y, crossRoot.position.z);
    }

    void RestoreCrosshair()
    {
        if (crossRoot != null && crossSaved) { crossRoot.position = crossPos; crossRoot.localScale = crossScale; }
    }

    bool camErrorLogged, crossErr;
    Vector3 follow, followVel, followDes;
    bool followInit;
    float collidedDist = -1;
    Quaternion savedRot;


    // baby steps' PlayerMovement.PlaceCamera, driven by big walk's look rotation:
    // camOrbit 0..1 = elevation -30..88 deg; view pitch = elevation - 10 (target sits below centre);
    // distance 2.15, shrinking to 1.75 over camOrbit 0.7..1; aim point slides up to 0.5 forward when looking down;
    // over the shoulder by sliding the camera sideways (not baby steps' yawed view); walls pull the camera in at once and let go slowly,
    // and closer than 1.5 the view tilts down up to 35 deg to keep the feet in shot
    void MoveCamera(Camera c)
    {
        var pc = Game.Pc; var cam = Game.Cam;
        if (!View || cam == null || c == null || c.Pointer != cam.Pointer || pc == null) return;
        var t = cam.transform;
        savedPos = t.position;
        savedRot = t.rotation;
        savedMask = cam.cullingMask;
        moved = true;

        // include every layer the bird's renderers live on
        int mask = savedMask;
        foreach (var r in pc.GetComponentsInChildren<Renderer>(false)) mask |= 1 << r.gameObject.layer;
        cam.cullingMask = mask;

        float S = Legs.Scale;
        Vector3 fwd = t.forward;
        float viewYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        float viewPitch = -Mathf.Asin(Mathf.Clamp(fwd.y, -1, 1)) * Mathf.Rad2Deg;   // + = looking down
        float elev = Mathf.Clamp(viewPitch + 10f, -30f, 88f);
        float orbit = (elev + 30f) / 118f;
        float lookDown = Mathf.Clamp01(orbit * 3f - 2f);                              // RemapRange(camOrbit, 0,1, -2,1)
        // over the shoulder: the camera slides sideways (up to 0.5, less looking down) and keeps looking where the
        // bird looks. baby steps turns the view ~20 deg past the orbit instead, which put the aim, the crosshair and
        // the stick's forward off to the side of the bird
        float shoulder = Plugin.CamShoulder.Value ? ((1 - lookDown) * 0.2f + 0.8f) * 0.5f * S * (1 - 2 * camRightAmount) : 0f;
        float orbitYaw = viewYaw;
        Quaternion yawQ = Quaternion.Euler(0, orbitYaw, 0);

        // follow target: hip xz, 0.5 above the lowest body part, velocity lead, kept 0.35 off the ground,
        // a 0.2 dead zone, then smoothed over 0.3 s
        Vector3 desired = Legs.CameraFollowPoint(out Vector3 vel);
        float lead = Mathf.Max(0, vel.magnitude - 1);
        desired += vel.normalized * (lead * 0.27f);
        int env = pc.ground.layerMask.value & ~BirdLayers(pc);
        if (Physics.Raycast(desired, Vector3.down, out var gh, 0.35f * S, env, QueryTriggerInteraction.Ignore))
            desired += Vector3.up * (0.35f * S - gh.distance);
        if (!followInit || (desired - follow).magnitude > 3f * S)
        { follow = followDes = desired; followVel = Vector3.zero; followInit = true; collidedDist = -1; }
        Vector3 off = desired - followDes;
        if (off.magnitude >= 0.2f * S) followDes = desired - off.normalized * (0.2f * S);
        follow = Vector3.SmoothDamp(follow, followDes, ref followVel, 0.3f, 9999f, Time.deltaTime);
        Vector3 pivot = follow + yawQ * Vector3.forward * (lookDown * 0.5f * S);

        float dist = Plugin.CamDistance.Value;
        if (orbit > 0.7f) dist = Mathf.Lerp(dist, 1.75f, (orbit - 0.7f) / 0.3f);
        dist *= S;
        Vector3 dir = Quaternion.AngleAxis(elev, yawQ * Vector3.right) * (yawQ * Vector3.back);
        float want = dist;
        // nearest wall behind, ignoring the bird's own layers (SphereCastAll and LayerMask.GetMask are stripped from big walk).
        // ground (looking up swings the camera low) doesn't pull it in: it rides over the floor at full distance below
        if (Physics.SphereCast(pivot, 0.2f * S, dir, out var hit, dist, env, QueryTriggerInteraction.Ignore) && hit.normal.y < 0.6f)
            want = Mathf.Max(0.3f * S, hit.distance - 0.1f * S);
        if (collidedDist < 0 || want < collidedDist) collidedDist = want;
        else collidedDist += (want - collidedDist) * Mathf.Clamp01(Time.deltaTime * 4f);
        Vector3 cp = pivot + dir * collidedDist;
        Vector3 side = yawQ * Vector3.right;
        float lat = shoulder;
        if (Mathf.Abs(lat) > 1e-3f && Physics.SphereCast(cp, 0.15f * S, side * Mathf.Sign(lat), out var sideHit, Mathf.Abs(lat), env, QueryTriggerInteraction.Ignore))
            lat = Mathf.Sign(lat) * Mathf.Max(0, sideHit.distance - 0.05f * S);
        cp += side * lat;
        if (Physics.Raycast(cp + Vector3.up * (1.5f * S), Vector3.down, out var gnd, 1.5f * S + 0.5f, env, QueryTriggerInteraction.Ignore) && gnd.point.y < pivot.y && cp.y < gnd.point.y + 0.2f * S)
            cp.y = gnd.point.y + 0.2f * S;
        t.position = cp;
        // pulled in tight by a wall: the round body would fill the screen
        camTooClose = (cp - pivot).magnitude < 0.7f * S;
        try { PlaceCrosshair(cam, savedPos, savedRot, env); } catch (Exception e) { if (!crossErr) { crossErr = true; Plugin.L.LogWarning("crosshair: " + e.Message); } }
        float d = collidedDist / S;
        float tilt = d < 1.5f ? (1.5f - d) / 1.5f * 35f : 0f;
        if (tilt > 0) t.rotation = Quaternion.AngleAxis(tilt, t.right) * t.rotation;
    }

    int birdLayers = -1;

    int BirdLayers(PlayerCharacter pc)
    {
        if (birdLayers != -1) return birdLayers;
        int m = 0;
        foreach (var c in pc.GetComponentsInChildren<Collider>(true)) m |= 1 << c.gameObject.layer;
        return birdLayers = m;
    }

    void EndRender(ScriptableRenderContext ctx, Camera c)
    {
        var cam = Game.Cam;
        if (!moved || cam == null || c == null || c.Pointer != cam.Pointer) return;
        cam.transform.SetPositionAndRotation(savedPos, savedRot);
        cam.cullingMask = savedMask;
        moved = false;
    }

    string ReadGround()
    {
        try
        {
            var pc = Game.Pc;
            var g = pc.ground;
            var col = g.groundCollider;
            string mat = col != null && col.sharedMaterial != null ? col.sharedMaterial.name : "-";
            Surface.Name(col, pc.transform.position, out string tex);
            // the game's own GetSteepness never moved, so measure it ourselves
            var s = Surface.Probe(pc.transform.position + Vector3.up * 0.5f, 3f, g.layerMask.value);
            return $"grounded {g.isGrounded}  swim {g.isSwimming}\n" +
                   $"collider {(col == null ? "none" : col.name)}  phys mat {mat}\nterrain {tex}\n" +
                   $"slope {s.angle:F0}°  surface {s.name}  grip {s.grip:F2}  speed {pc.rb.linearVelocity.magnitude:F2}\n" +
                   Legs.Status;
        }
        catch (Exception e) { return "read failed: " + e.Message; }
    }

    static bool setErr, pageErr, rebindErr, birdErr, pauseErr;

    // a new lobby / respawn is a new bird: third person has to be put on it again (it kept the flag but the new bird
    // was in first person body mode: only body, legs and arms showed), and the default camera / walking state apply
    IntPtr birdPtr;
    float defaultsUntil, nextDefaultTry;
    void NewBird()
    {
        var pc = Game.Pc;
        IntPtr now = pc == null ? IntPtr.Zero : pc.Pointer;
        if (now == birdPtr) { TryDefaultLegs(); return; }
        birdPtr = now;
        if (pc == null) return;
        pendingThird = thirdPerson || Plugin.ThirdPersonByDefault.Value;
        thirdPerson = false;
        defaultsUntil = Time.unscaledTime + 15f;   // the rig and camera can take a moment after spawning
    }

    bool pendingThird;
    void TryDefaultLegs()
    {
        if (pendingThird && Game.Pc != null && Game.Cam != null) { pendingThird = false; SetThirdPerson(true); }
        if (defaultsUntil <= 0 || Time.unscaledTime < nextDefaultTry) return;
        if (!Plugin.LegsByDefault.Value || Legs.IsActive || Time.unscaledTime > defaultsUntil) { defaultsUntil = 0; return; }
        nextDefaultTry = Time.unscaledTime + 1f;
        Legs.Inst.TurnOn();
        if (Legs.IsActive) { defaultsUntil = 0; Plugin.L.LogInfo("legs on by default"); }
    }

    void OnGUI()
    {
        if (!hud || Game.Pc == null) return;
        GUI.Box(new Rect(10, 10, 520, 148), "");
        GUI.Label(new Rect(18, 14, 510, 143), groundText);
    }
}
