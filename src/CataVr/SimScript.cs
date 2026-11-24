using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using NVec = System.Numerics.Vector3;

namespace CataVr;

// Headset-free test harness (Debug.SimulateVr + RunTestScript). Drives the
// hands through a scripted sequence and logs what the game does: menu clicks
// through the panel (AutoLoad gets into a level that way), the laser's world
// hit, hover and clicks, grip drags and panning. Screenshots of both the eye
// view and the UI panel go to BepInEx/catavr-shots.
static class SimScript
{
    static readonly Pose LeftHandRest = new(new NVec(-0.2f, 1.0f, 0.3f), System.Numerics.Quaternion.Identity);
    static readonly SimRunner runner = new();
    // The core's simulated hands; the right hand aims from (0.15, 1.1, 0.25), the left rests at LeftHandRest.
    static readonly SimHands hands = new();
    // The right B button, held (SimHand's presses last one frame).
    static bool secondary;
    static readonly Pose StandingHead = new(new NVec(0, 1.5f, 0), Pose.Pitch(45));

    public static void Update(VrSession vr, CataRig rig, Plugin cfg)
    {
        hands.PointerPitch ??= () => cfg.PointerPitch.Value;   // before any step aims
        if (!runner.Running && !runner.Finished) runner.StartOnce(Run(vr, rig, cfg));
        // The core's stepper: waits in unscaled time; nested sequences (Click, Chord) run to their end first.
        runner.Step();
        hands.Right.SecondaryPress = secondary;
        hands.Apply(vr);
    }

    static void Log(string msg) => Plugin.Logger.LogInfo($"[sim] {msg}");

    // The game camera's pivot, what its screen centre shows, and where the table is.
    static void LogView(string label, CataRig rig)
    {
        var gameplay = Game.Gameplay;
        var cam = Game.MainCamera;
        if (gameplay == null || cam == null) return;
        var t = cam.transform;
        string centre = Physics.Raycast(t.position, t.forward, out var hit, 100000f) ? hit.point.ToString() : "-";
        string gameHit = gameplay.GetRaycastHitPosition(out var gh, false) ? gh.ToString() : "-";
        Game.TryGetView(out var focal, out var gameYaw);
        var pivotView = cam.WorldToViewportPoint(new Vector3(focal.X, focal.Y, focal.Z));
        var a = rig.Anchor;
        var anchorView = cam.WorldToViewportPoint(new Vector3(a.X, a.Y, a.Z));
        Log($"{label}: pivot {focal} (viewport {pivotView}), screen centre {centre}, game hit {gameHit}, camera {t.position} yaw {gameYaw:F1} pitch {t.eulerAngles.x:F1}, following {gameplay.IsFollowingTarget()} moving {gameplay._moveToInProgress} | table anchor {a} (viewport {anchorView}) yaw {rig.Yaw:F1}");
    }

    static void AimAtTracking(Plugin cfg, NVec target) => hands.AimAtTracking(target);

    static void AimAtWorld(CataRig rig, Plugin cfg, NVec world) => hands.AimAtTracking(rig.WorldToTracking(world));

    // Point at a screen pixel on the menu panel (y up) and pull the trigger.
    static float Click(Plugin cfg, Vector2 pixel, float hold = 0.15f)
    {
        return runner.Nest(ClickSteps(cfg, pixel, hold));
    }

    static IEnumerator<float> ClickSteps(Plugin cfg, Vector2 pixel, float hold) =>
        hands.Click(VrMenuPanel.PixelToTracking(pixel), settle: 0.4f, hold: hold, after: 0.5f);

    // Both grips, then both triggers: the VR menu's chord.
    static float Chord()
    {
        return runner.Nest(ChordSteps());
    }

    static IEnumerator<float> ChordSteps()
    {
        hands.Left.Grip = hands.Right.Grip = true;
        yield return 0.2f;
        hands.Left.Trigger = hands.Right.Trigger = true;
        yield return 0.2f;
        hands.Left.Trigger = hands.Right.Trigger = false;
        hands.Left.Grip = hands.Right.Grip = false;
        yield return 0.5f;
    }

    static IEnumerator<float> Run(VrSession vr, CataRig rig, Plugin cfg)
    {
        vr.SimulateHead(StandingHead);
        while (!rig.Active || Time.unscaledTime < 10f) yield return 0.5f;
        yield return 2f;
        Log($"started: mode {rig.ModeName}, level ready {Game.LevelReady}, screen {Screen.width}x{Screen.height}");
        LogButtons();
        // The main menu: the panel straight ahead, the laser on Campaign.
        vr.SimulateHead(new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(5)));
        var campaign = FindButton(new[] { "Campaign" }, out _);
        if (campaign != null) AimAtTracking(cfg, VrMenuPanel.PixelToTracking(ScreenPixel(campaign.transform)));
        yield return 0.6f;
        SimShots.Request("00_start");
        yield return 1f;
        vr.SimulateHead(StandingHead);

        // Main menu: click through to a level with the laser on the panel.
        if (cfg.AutoLoad.Value)
        {
            var labels = cfg.AutoLoadButtons.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // The intro splash can cover the menu for a while: clicks before it ends do nothing.
            for (int step = 0; step < 12 && !Game.LevelReady; step++)
            {
                if (Camp.InCamp) break;
                var button = FindButton(labels, out var label, cfg.AutoLoadLowest.Value);
                if (button == null)
                {
                    Log($"auto-load: no button labelled {string.Join("/", labels)}");
                    LogButtons();
                    yield return 3f;
                    continue;
                }
                var pixel = ScreenPixel(button.transform);
                Log($"auto-load: clicking '{label}' ({button.name}) at pixel {pixel}");
                yield return Click(cfg, pixel);
                Log($"auto-load: after click, hovered {Hovered()}, level ready {Game.LevelReady}");
                SimShots.Request($"01_autoload_{step}");
                yield return 4f;
            }
            float waitStart = Time.unscaledTime;
            while (!Game.LevelReady && !Camp.InCamp && Time.unscaledTime - waitStart < 150f) yield return 1f;
            Log($"level ready {Game.LevelReady} after {Time.unscaledTime - waitStart:F0}s");
            yield return 6f;
        }

        if (!rig.InWorld)
        {
            Log($"not in a level (mode {rig.ModeName}); skipping the world steps");
            SceneDump.Log(rig.Eyes);
            LogButtons();
            vr.SimulateHead(new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(5)));
            yield return 1f;
            SimShots.Request("02_not_level");
            yield return 2f;
            if (Camp.InCamp)
            {
                // The camp between levels (the "war table"): hover and click its
                // 3D objects (Hermes, the map, the level nodes) through the cinema screen.
                Log($"camp: state {Camp.State}, camera '{Camp.Camera?.name}'");
                foreach (var (name, element) in Camp.Elements())
                {
                    var pixel = Camp.ScreenPixel(element);
                    if (pixel == null) { Log($"camp: '{name}' not on screen"); continue; }
                    AimAtTracking(cfg, VrMenuPanel.PixelToTracking(pixel.Value));
                    yield return 0.8f;
                    Log($"camp: aim at '{name}' pixel {pixel.Value}: onMenu {VrPointer.OnMenu}, mouse {NativeInput.MousePixel}, hovered {element.IsHovered}, clickable {element.IsClickable()}, under pointer {Camp.UnderPointer()}");
                }
                SimShots.Request("10_camp_hover");
                yield return 1f;
                var mapPixel = Camp.Map != null ? Camp.ScreenPixel(Camp.Map) : null;
                if (mapPixel != null)
                {
                    yield return Click(cfg, mapPixel.Value);
                    yield return 3f;
                    Log($"camp: clicked the map: state {Camp.State}");
                    SimShots.Request("11_camp_map");
                    yield return 1f;
                    LogButtons();
                }
                // The tutorial card: close it, then the camp should show through the panel.
                Log($"camp: UI colour {ColourSum():F0}, alpha {Alpha()}");
                Selectable close = null;
                foreach (var s in UnityEngine.Object.FindObjectsOfType<Selectable>())
                    if (s.isActiveAndEnabled && s.interactable && s.GetComponentInParent<Canvas>()?.name.StartsWith("CanvasTutorialPanel") == true)
                    {
                        close = s;
                        Log($"camp: tutorial button '{s.name}' at {ScreenPixel(s.transform)}");
                    }
                if (close != null)
                {
                    yield return Click(cfg, ScreenPixel(close.transform));
                    yield return 2f;
                    Log($"camp: closed the tutorial: state {Camp.State}, UI alpha {Alpha()}");
                    SimShots.Request("12_camp_tutorial_closed");
                    yield return 1f;
                }
                foreach (var (name, element) in Camp.Elements())
                {
                    if (!name.StartsWith("level")) continue;
                    var pixel = Camp.ScreenPixel(element);
                    if (pixel == null) continue;
                    AimAtTracking(cfg, VrMenuPanel.PixelToTracking(pixel.Value));
                    yield return 0.8f;
                    Log($"camp: aim at '{name}': hovered {element.IsHovered}, clickable {element.IsClickable()}, under pointer {Camp.UnderPointer()}");
                    SimShots.Request("13_camp_level_hover");
                    yield return 1f;
                    yield return Click(cfg, pixel.Value);
                    yield return 3f;
                    Log($"camp: clicked '{name}': state {Camp.State}");
                    SimShots.Request("14_camp_level_clicked");
                    yield return 1f;
                    LogButtons();
                    break;
                }
            }
            yield return runner.Nest(SimRunner.Finish(cfg.QuitAfterTest.Value, 0));
            yield break;
        }
        Log($"in level: mode {rig.ModeName}, anchor {rig.Anchor}, scale {rig.Scale}, pivot {Game.Gameplay?.GetPivotPosition()}");
        SceneDump.Log(rig.Eyes);
        SceneDump.LogHud();
        LogButtons();
        rig.Recenter();
        yield return 0.5f;
        SimShots.Request("02_level");
        yield return 1f;

        // Select a unit: the laser on the player unit nearest the screen centre,
        // a short trigger pull, then watch the selection and the camera.
        var selection = UnityEngine.Object.FindObjectOfType<DigitalSun.Castle.SelectionManager>();
        var unit = NearestPlayerEntity(selection);
        if (unit != null)
        {
            var p = unit.transform.position;
            Log($"select test: unit '{unit.name}' at {p}, selected {selection.GetSelectedEntities().Count}");
            AimAtWorld(rig, cfg, new NVec(p.x, p.y + 0.3f, p.z));
            yield return 0.6f;
            LogView("before unit click", rig);
            Log($"laser hit {VrPointer.WorldHit}, mouse {NativeInput.MousePixel}");
            hands.Right.Trigger = true;
            yield return 0.15f;
            hands.Right.Trigger = false;
            for (int i = 0; i < 6; i++)
            {
                yield return 0.5f;
                Log($"after unit click +{(i + 1) * 0.5f:F1}s: selected {selection.GetSelectedEntities().Count}, mouse {NativeInput.MousePixel}, anchor {rig.Anchor}, shot {GameShots.Active}");
            }
            LogView("after unit click", rig);
            SimShots.Request("02b_unit_selected");
            yield return 1f;

            // A real hand turns a little while pulling the trigger: the same
            // click with the laser swinging sideways during the press.
            foreach (float degrees in new[] { 0.3f, 1f, 2f, 4f, 8f })
            {
                selection.ClearCurrentSelectedEntities(true);
                AimAtWorld(rig, cfg, new NVec(p.x, p.y + 0.3f, p.z));
                yield return 0.6f;
                var still = hands.Right.Pose;
                var startPixel = NativeInput.MousePixel;
                hands.Right.Trigger = true;
                for (int i = 1; i <= 3; i++)
                {
                    yield return 0.05f;
                    hands.Right.Pose = new Pose(still.Position, System.Numerics.Quaternion.Normalize(Pose.Yaw(degrees * i / 3f) * still.Rotation));
                    Log($"  step {i}: mouse {NativeInput.MousePixel}, onMenu {VrPointer.OnMenu}, onVrMenu {VrPointer.OnVrMenu}, hit {VrPointer.WorldHit}");
                }
                yield return 0.05f;
                var endPixel = NativeInput.MousePixel;
                hands.Right.Trigger = false;
                yield return 0.6f;
                Log($"wobbly click {degrees}°: mouse {startPixel} -> {endPixel} ({(endPixel - startPixel).magnitude:F0} px), selected {selection.GetSelectedEntities().Count}, anchor {rig.Anchor}, shot {GameShots.Active}");
            }

            // Drag a box on the table: one around the unit, one beside it.
            foreach (var (name, from, to) in new[] { ("around", new NVec(-6, 0, -6), new NVec(6, 0, 6)), ("beside", new NVec(3, 0, 3), new NVec(10, 0, 10)) })
            {
                selection.ClearCurrentSelectedEntities(true);
                var a = new NVec(p.x, p.y, p.z) + from;
                var b = new NVec(p.x, p.y, p.z) + to;
                AimAtWorld(rig, cfg, a);
                yield return 0.6f;
                hands.Right.Trigger = true;
                for (int i = 1; i <= 10; i++)
                {
                    yield return 0.05f;
                    AimAtWorld(rig, cfg, NVec.Lerp(a, b, i / 10f));
                }
                yield return 0.3f;
                if (name == "around") SimShots.Request("02c_drag_box");
                yield return 0.3f;
                hands.Right.Trigger = false;
                yield return 0.6f;
                Log($"drag {name}: {a} -> {b}, mouse at end {NativeInput.MousePixel}, selected {selection.GetSelectedEntities().Count}");
            }
        }
        else Log("select test: no player unit on screen");

        // The laser at the table centre and around it: does the game's own raycast follow?
        var centre = rig.Anchor;
        foreach (var offset in new[] { NVec.Zero, new NVec(10, 0, 0), new NVec(0, 0, 10), new NVec(-10, 0, -5) })
        {
            AimAtWorld(rig, cfg, centre + offset);
            yield return 0.6f;
            Log($"aim at {centre + offset}: game hit {VrPointer.WorldHit}, onMenu {VrPointer.OnMenu}, mouse {NativeInput.MousePixel}, hovered {Hovered()}, ray overrides {NativeInput.RayOverrides}");
        }
        AimAtWorld(rig, cfg, centre);
        yield return 0.5f;
        SimShots.Request("03_aim_centre");
        yield return 1f;

        // Click a harmless HUD button through the panel: pause, then again to resume.
        var hud = FindHudButton("pause") ?? FindHudButton("");
        if (hud != null)
        {
            var pixel = ScreenPixel(hud.transform);
            AimAtTracking(cfg, VrMenuPanel.PixelToTracking(pixel));
            yield return 0.6f;
            Log($"hover HUD '{hud.name}' at {pixel}: onMenu {VrPointer.OnMenu}, hovered {Hovered()}");
            SimShots.Request("04_hover_hud");
            yield return 0.5f;
            if (hud.name.ToLowerInvariant().Contains("pause"))
            {
                float scaleBefore = Time.timeScale;
                yield return Click(cfg, pixel);
                Log($"clicked '{hud.name}': time scale {scaleBefore} -> {Time.timeScale}");
                yield return Click(cfg, pixel);
                Log($"clicked '{hud.name}' again: time scale {Time.timeScale}");
            }
        }

        // Left click on the world, then right click (cancel), at the table centre.
        AimAtWorld(rig, cfg, centre);
        yield return 0.5f;
        hands.Right.Trigger = true;
        yield return 0.15f;
        hands.Right.Trigger = false;
        yield return 1f;
        Log($"after world click: hovered {Hovered()}");
        SimShots.Request("05_world_click");
        yield return 1f;
        secondary = true;
        yield return 0.15f;
        secondary = false;
        yield return 1f;

        // Grip drag: move the left hand 15 cm while gripping; the table follows.
        var before = rig.Anchor;
        hands.Left.Grip = true;
        yield return 0.2f;
        hands.Left.Pose = new Pose(LeftHandRest.Position + new NVec(0.15f, 0, 0), LeftHandRest.Rotation);
        yield return 0.3f;
        hands.Left.Grip = false;
        hands.Left.Pose = LeftHandRest;
        yield return 0.3f;
        Log($"grip drag 15 cm: anchor {before} -> {rig.Anchor}, game pivot {Game.Gameplay?.GetPivotPosition()} (the drag moves the game's pivot)");

        // Pan with the pointer (right) stick, by the stick standard: the game's W, and its pivot should move.
        var pivot = Game.Gameplay?.GetPivotPosition();
        hands.Right.StickY = 1;
        yield return 1.5f;
        hands.Right.StickY = 0;
        yield return 1f;
        Log($"stick pan: pivot {pivot} -> {Game.Gameplay?.GetPivotPosition()}, anchor {rig.Anchor}, table scale {rig.Scale:F2}, game zoom {Game.ZoomDistance:F2}");

        // Rotate and zoom with the off (left) stick, by the stick standard, pushed as on a controller. They go
        // through the game's own input (Q / E, the wheel), and the table follows
        // the game camera: its turn and scale change must match the camera's, once.
        Game.TryGetView(out _, out var gameYawBefore);
        var yawBefore = rig.Yaw;
        hands.Left.Flick = 1;
        hands.Left.StickX = 1;
        yield return 0.2f;
        hands.Left.StickX = 0;
        yield return 1.5f;
        Game.TryGetView(out _, out var gameYawAfter);
        Log($"stick right: game yaw {gameYawBefore:F2} -> {gameYawAfter:F2} (turned {Mathf.DeltaAngle(gameYawBefore, gameYawAfter):F2}), " +
            $"table yaw {yawBefore:F2} -> {rig.Yaw:F2} (turned {Mathf.DeltaAngle(yawBefore, rig.Yaw):F2})");
        float zoomBefore = Game.ZoomDistance, tableScaleBefore = rig.Scale;
        hands.Left.StickY = 1;
        yield return 1f;
        hands.Left.StickY = 0;
        yield return 1.5f;
        Log($"stick up: game zoom {zoomBefore:F2} -> {Game.ZoomDistance:F2} (x{Game.ZoomDistance / zoomBefore:F3}), " +
            $"table scale {tableScaleBefore:F2} -> {rig.Scale:F2} (x{rig.Scale / tableScaleBefore:F3})");
        Log($"after stick: yaw {rig.Yaw}, scale {rig.Scale}");
        SimShots.Request("06_turned");
        yield return 1f;

        // Turntable: a circle of the world on the table (the cylinder mask from our shader bundle, CylinderCull for big meshes).
        var modeBeforeTurntable = VrUiKit.VrModes.Current?.Id;
        VrUiKit.VrModes.Select("Turntable");
        yield return 2f;
        Log($"turntable: mode {rig.ModeName}, cut radius {rig.Table.CutRadius:F1} world units, mask available {CylinderMask.Available} on {CylinderMask.On}, anchor {rig.Anchor}, scale {rig.Scale:F1}");
        SimShots.Request("06b_turntable");
        yield return 1f;
        if (modeBeforeTurntable != null) VrUiKit.VrModes.Select(modeBeforeTurntable);
        yield return 1f;

        // A closer, lower look across the table.
        vr.SimulateHead(new Pose(new NVec(0, 1.15f, 0.15f), Pose.Pitch(30)));
        yield return 0.5f;
        SimShots.Request("07_close");
        yield return 1f;
        vr.SimulateHead(StandingHead);

        // The panel must stay see-through where there's no UI.
        var canvases = new List<Canvas>(VrMenuPanel.Canvases);
        foreach (var c in canvases) c.enabled = false;
        yield return 0.3f;
        Log($"panel alpha with no canvases (0 = see-through): {Alpha()}");
        foreach (var c in canvases) c.enabled = true;
        yield return 0.3f;
        Log($"panel with UI: alpha {Alpha()}, colour {ColourSum()}");

        // The VR menu (UI kit): the chord opens it, without moving the table.
        var anchorBefore = rig.Anchor;
        yield return Chord();
        Log($"chord: VR menu open {VrUiKit.VrUi.MenuOpen}, anchor {anchorBefore} -> {rig.Anchor}");
        if (VrUiKit.VrUi.MenuOpen)
        {
            // Aim at a control by label: pixel positions move as the kit's tabs change.
            var target = VrUiKit.VrUi.MenuPointOf("Mode");
            Log($"VR menu 'Mode' tab found {target.HasValue}");
            AimAtTracking(cfg, target ?? VrUiKit.VrUi.MenuPointToTracking(320, 400));
            yield return 0.6f;
            Log($"laser on VR menu {VrPointer.OnVrMenu}, on game panel {VrPointer.OnMenu}, hovered {Hovered()}");
            SimShots.Request("08_vr_menu");
            yield return 1f;
            yield return Chord();
            Log($"chord again: VR menu open {VrUiKit.VrUi.MenuOpen}");
        }

        // A scripted camera move, as the game does to show you something: the
        // table should end up centred on what the desktop camera looks at.
        var gameplay = Game.Gameplay;
        if (gameplay != null)
        {
            LogView("before camera move", rig);
            var data = new DigitalSun.Castle.GameplayCameraPositionData();
            gameplay.FillGameplayCameraPositionData(data);
            Log($"camera data: {data.ToString()}");
            data.xzPosition += new Vector2(40, 25);
            data.yLocalRotation += 90;
            data.MoveCameraToPos();
            for (int i = 0; i < 8; i++)
            {
                yield return 0.5f;
                LogView($"camera move +{(i + 1) * 0.5f:F1}s", rig);
            }
            SimShots.Request("09_camera_move");
            yield return 1f;
        }

        // The hotkey bar, VR menu closed: its buttons press the game's own keys,
        // and the table follows the camera as with the stick.
        // Each set is its own bar now: the camera buttons are on the Tutorial bar.
        var tutorialBar = VrUiKit.VrHotkeys.Set("Tutorial");
        bool tutorialBarWasOn = VrUiKit.VrHotkeys.IsOn(tutorialBar);
        VrUiKit.VrHotkeys.SetOn(tutorialBar, true);
        yield return 0.5f;
        Log($"hotbar: shown {VrUiKit.VrHotbar.Visible}, Tutorial bar was on {tutorialBarWasOn}");
        foreach (var (label, hold) in new[] { ("Q", 0.15f), ("W", 0.6f), ("+", 0.15f), ("-", 0.6f) })
        {
            if (VrUiKit.VrUi.HotbarPointOf(label) is not NVec at)
            {
                Log($"hotbar '{label}' not found");
                continue;
            }
            AimAtTracking(cfg, at);
            yield return 0.4f;
            Game.TryGetView(out var pivotBefore, out var gameYawBefore2);
            float zoomBefore2 = Game.ZoomDistance, yawBefore2 = rig.Yaw, tableScaleBefore2 = rig.Scale;
            bool onBar = VrPointer.OnVrMenu;
            if (label == "Q") SimShots.Request("10_hotbar");
            hands.Right.Trigger = true;
            yield return hold;
            hands.Right.Trigger = false;
            yield return 1.5f;
            Game.TryGetView(out var pivotAfter, out var gameYawAfter2);
            Log($"hotbar '{label}' (laser on bar {onBar}): game yaw {gameYawBefore2:F2} -> {gameYawAfter2:F2}, table yaw {yawBefore2:F2} -> {rig.Yaw:F2}, " +
                $"pivot {pivotBefore} -> {pivotAfter}, game zoom {zoomBefore2:F2} -> {Game.ZoomDistance:F2} (x{Game.ZoomDistance / zoomBefore2:F3}), " +
                $"table scale {tableScaleBefore2:F2} -> {rig.Scale:F2} (x{rig.Scale / tableScaleBefore2:F3})");
        }
        VrUiKit.VrHotkeys.SetOn(tutorialBar, tutorialBarWasOn);

        // The HUD as windows (Arranged UI mode), then moving one (Free mode).
        // The player's UI mode and Free layout are put back afterwards.
        // Control: the same pause click through the full panel first.
        rig.Recenter();
        var panelPause = FindHudButton("pause");
        if (panelPause != null)
        {
            float scale0 = Time.timeScale;
            yield return Click(cfg, ScreenPixel(panelPause.transform));
            Log($"control: clicked '{panelPause.name}' on the full panel: time scale {scale0} -> {Time.timeScale}, hovered {Hovered()}");
            yield return Click(cfg, ScreenPixel(panelPause.transform));
        }
        var modeBefore = VrUiKit.VrUi.Mode;
        var freePath = Path.Combine(BepInEx.Paths.ConfigPath, "catavr-layouts", "free.txt");
        string freeBefore = File.Exists(freePath) ? File.ReadAllText(freePath) : null;
        VrUiKit.VrUi.SetMode(VrUiKit.VrUi.UiMode.Arranged);
        vr.SimulateHead(new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(15)));
        yield return 1.5f;
        foreach (var id in new[] { "resources", "time", "quests", "squads", "build", "selected", "focus" })
            Log($"window {id}: shown {VrUiKit.VrUi.WindowVisible(id)}, rect {VrUiKit.VrUi.WindowRect(id)}");
        SimShots.Request("11_arranged");
        yield return 1f;
        // Pause and resume through the time window.
        var pauseButton = FindHudButton("pause");
        if (pauseButton != null && VrUiKit.VrUi.PixelToTracking("time", ScreenPixel(pauseButton.transform), out var onWindow))
        {
            float scaleBeforeWindow = Time.timeScale;
            AimAtTracking(cfg, onWindow);
            yield return 0.6f;
            Log($"laser on '{pauseButton.name}' in the time window: on UI {VrPointer.OnMenu}, on VR menu {VrPointer.OnVrMenu}, mouse {NativeInput.MousePixel} (button at {ScreenPixel(pauseButton.transform)}), hovered {Hovered()}");
            hands.Right.Trigger = true;
            yield return 0.15f;
            hands.Right.Trigger = false;
            yield return 1f;
            Log($"clicked '{pauseButton.name}' through the time window: time scale {scaleBeforeWindow} -> {Time.timeScale}");
            hands.Right.Trigger = true;
            yield return 0.15f;
            hands.Right.Trigger = false;
            yield return 1f;
            Log($"clicked again: time scale {Time.timeScale}");
        }
        else Log("no pause button in the time window");
        // The laser on the world between windows still hits the level.
        AimAtWorld(rig, cfg, rig.Anchor);
        yield return 0.6f;
        Log($"arranged, laser on the table: game hit {VrPointer.WorldHit}, on UI {VrPointer.OnMenu}, on VR menu {VrPointer.OnVrMenu}");

        VrUiKit.VrUi.SetMode(VrUiKit.VrUi.UiMode.Free);
        VrUiKit.VrUi.ResetFree();
        yield return 1.5f;
        if (VrUiKit.VrUi.PixelToTracking("time", VrUiKit.VrUi.WindowRect("time").center, out var timeAt))
        {
            var windowBefore = VrUiKit.VrUi.WindowCentre("time");
            var tableBefore = rig.Anchor;
            float yawBefore3 = rig.Yaw;
            AimAtTracking(cfg, timeAt);
            yield return 0.4f;
            hands.Right.Grip = true;
            yield return 0.3f;
            Log($"free mode: grip on the time window, holding {VrUiKit.VrUi.GrabbingWindow}");
            for (int i = 1; i <= 10; i++)
            {
                AimAtTracking(cfg, timeAt + new NVec(0.03f * i, 0, 0));
                yield return 0.05f;
            }
            // The pointer stick makes it bigger, and must not turn the camera.
            hands.Right.StickX = 1;
            yield return 0.5f;
            hands.Right.StickX = 0;
            yield return 0.3f;
            hands.Right.Grip = false;
            yield return 0.4f;
            Log($"free mode: time window moved {(VrUiKit.VrUi.WindowCentre("time") - windowBefore).Length():F2} m, table moved {NVec.Distance(rig.Anchor, tableBefore):F2}, table yaw {yawBefore3:F1} -> {rig.Yaw:F1}");
            SimShots.Request("12_free_moved");
            yield return 1f;
        }
        else Log("free mode: time window not shown");
        VrUiKit.VrUi.SetMode(modeBefore);
        if (freeBefore != null) File.WriteAllText(freePath, freeBefore);
        else if (File.Exists(freePath)) File.Delete(freePath);
        yield return 0.5f;

        yield return runner.Nest(SimRunner.Finish(cfg.QuitAfterTest.Value));
    }

    // Alpha of the UI texture at a few spots (0 = transparent).
    static string Alpha()
    {
        var rt = VrMenuPanel.Texture;
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        var parts = new List<string>();
        foreach (var (x, y) in new[] { (0.5f, 0.5f), (0.3f, 0.3f), (0.7f, 0.7f) })
            parts.Add($"{tex.GetPixel((int)(x * rt.width), (int)(y * rt.height)).a:F2}");
        UnityEngine.Object.Destroy(tex);
        return string.Join(" ", parts);
    }

    // Total brightness of the UI texture, sampled on a grid: 0 = nothing drawn.
    static float ColourSum()
    {
        var rt = VrMenuPanel.Texture;
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        float sum = 0;
        for (int y = 0; y < rt.height; y += 16)
            for (int x = 0; x < rt.width; x += 16)
            {
                var c = tex.GetPixel(x, y);
                sum += c.r + c.g + c.b;
            }
        UnityEngine.Object.Destroy(tex);
        return sum;
    }

    static string Hovered()
    {
        var es = EventSystem.current;
        if (es == null) return "no event system";
        return es.IsPointerOverGameObject() ? "UI" : "world";
    }

    static Vector2 ScreenPixel(Transform t)
    {
        var cam = VrMenuPanel.UiCamera;
        var rect = t.TryCast<RectTransform>();
        var p = t.position;
        if (rect != null)
        {
            var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            rect.GetWorldCorners(corners);
            p = (corners[0] + corners[2]) * 0.5f;
        }
        var s = cam != null ? cam.WorldToScreenPoint(p) : p;
        return new Vector2(s.x, s.y);
    }

    static string Label(Selectable s)
    {
        var tmp = s.GetComponentInChildren<TMP_Text>();
        if (tmp != null) return tmp.text?.Trim() ?? "";
        var text = s.GetComponentInChildren<Text>();
        return text != null ? text.text?.Trim() ?? "" : "";
    }

    static Selectable FindButton(string[] labels, out string label, bool lowest = false)
    {
        label = null;
        var all = UnityEngine.Object.FindObjectsOfType<Selectable>();
        foreach (var want in labels)
        {
            Selectable found = null;
            foreach (var s in all)
            {
                if (!s.isActiveAndEnabled || !s.interactable) continue;
                var l = Label(s);
                if (!l.Equals(want, StringComparison.OrdinalIgnoreCase)) continue;
                if (found == null || (lowest && ScreenPixel(s.transform).y < ScreenPixel(found.transform).y))
                {
                    found = s;
                    label = l;
                }
                if (!lowest) break;
            }
            if (found != null) return found;
        }
        return null;
    }

    // An interactable button on screen whose name contains `name` (case-insensitive).
    static DigitalSun.Castle.WorldEntity NearestPlayerEntity(DigitalSun.Castle.SelectionManager selection)
    {
        if (selection == null) return null;
        var all = selection.GetPlayerWorldEntitiesInSquareSelection(Vector3.zero, new Vector3(Screen.width, Screen.height, 0));
        var cam = Game.MainCamera;
        DigitalSun.Castle.WorldEntity best = null;
        float bestDistance = float.MaxValue;
        var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        Log($"player entities on screen: {all?.Count ?? 0}");
        if (all == null || cam == null) return null;
        foreach (var e in all)
        {
            if (e == null) continue;
            var s = cam.WorldToScreenPoint(e.transform.position);
            float d = Vector2.Distance(new Vector2(s.x, s.y), centre);
            if (s.z > 0 && d < bestDistance) { best = e; bestDistance = d; }
        }
        return best;
    }

    static Selectable FindHudButton(string name)
    {
        foreach (var s in UnityEngine.Object.FindObjectsOfType<Selectable>())
        {
            if (!s.isActiveAndEnabled || !s.interactable || s.TryCast<Button>() == null) continue;
            if (!s.name.ToLowerInvariant().Contains(name)) continue;
            var p = ScreenPixel(s.transform);
            if (p.x > 5 && p.y > 5 && p.x < Screen.width - 5 && p.y < Screen.height - 5) return s;
        }
        return null;
    }

    static void LogButtons()
    {
        var names = new List<string>();
        foreach (var s in UnityEngine.Object.FindObjectsOfType<Selectable>())
        {
            if (!s.isActiveAndEnabled) continue;
            names.Add($"{s.name}='{Label(s)}'@{ScreenPixel(s.transform)}");
            if (names.Count >= 40) break;
        }
        Log($"buttons: {string.Join(" | ", names)}");
    }
}
