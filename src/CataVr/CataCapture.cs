using System;
using System.Collections.Generic;
using DigitalSun.Castle;
using UnityEngine;

namespace CataVr;

// Cataclismo's hooks for the core's diorama capture (Record > Diorama > "Capture
// diorama"; core docs/DIORAMA-CAPTURE.md). The core renders 208 views of the
// table's region over about 52 frames; here the game holds still, its markers
// hide, and the region sits on the terrain.
// - Freeze: the game's own pause (TimeManager), Unity's time scale and DOTween's
//   global one (tweens marked independent of Unity's time still obey DOTween's),
//   all put back as they were. The game eases its time scale between states, so
//   Tick holds Time.timeScale at 0 every frame while the capture runs.
// - Hide: the core's default (the game's world-space canvases), the game's own gizmo switch
//   (GizmoVisibilityManager, restored as it was) and cursor gizmos, plus the Shapes
//   drawers (range circles, the build grid, selection), which aren't canvases.
//   Our own pieces (wave pointers and their details, the drag-select outline,
//   hands, laser, panels) are on CataRig.VisualLayer, which the capture camera
//   never draws.
// - Frame: the table's centre (the game camera's focal point) on the ground
//   there, with the table's radius; the core's own when the ground lookup fails.
static class CataCapture
{
    static TimeManager timeManager;
    static TimeState? savedState;
    static float savedTimeScale = 1f, savedTweenScale = 1f;
    static bool frozen;

    public static void Offer(Plugin cfg, CataRig rig)
    {
        DioramaCapture.Freeze = Freeze;
        DioramaCapture.HideForCapture = Hide;
        DioramaCapture.Frame = () => Frame(rig);
        DioramaCapture.Offer(cfg.Config, "Cataclismo", "cataclismo");
    }

    // Each frame, from Vr.Frame: keep the game still while the capture runs.
    public static void Tick()
    {
        if (frozen && DioramaCapture.Capturing && Time.timeScale != 0f) Time.timeScale = 0f;
    }

    static void Freeze(bool on)
    {
        if (on)
        {
            savedTimeScale = Time.timeScale;
            savedTweenScale = DG.Tweening.DOTween.timeScale;
            savedState = null;
            try
            {
                timeManager = UnityEngine.Object.FindObjectOfType<TimeManager>();
                if (timeManager != null)
                {
                    var state = timeManager.GetCurrentTimeState();
                    if (state != TimeState.Pause)
                    {
                        // Not the player's input, forced past any confirmation, silent, and not
                        // remembered as the player's chosen speed.
                        timeManager.ChangeTimeState(TimeState.Pause, false, true, true, false);
                        savedState = state;
                    }
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[diorama] couldn't pause through the game, holding the time scale only: {e.Message}"); }
            Time.timeScale = 0f;
            DG.Tweening.DOTween.timeScale = 0f;
            frozen = true;
            Plugin.Logger.LogInfo($"[diorama] frozen (game state {(savedState?.ToString() ?? "already paused or unknown")}, time scale {savedTimeScale}, tween scale {savedTweenScale})");
        }
        else
        {
            frozen = false;
            DG.Tweening.DOTween.timeScale = savedTweenScale;
            Time.timeScale = savedTimeScale;
            if (savedState.HasValue && timeManager != null)
            {
                try { timeManager.ChangeTimeState(savedState.Value, false, true, true, false); }
                catch (Exception e) { Plugin.Logger.LogWarning($"[diorama] couldn't resume the game's speed ({savedState.Value}): {e.Message}"); }
            }
            savedState = null;
            timeManager = null;
            Plugin.Logger.LogInfo("[diorama] unfrozen");
        }
    }

    static Action Hide()
    {
        var canvases = DioramaCapture.HideWorldCanvases();
        // The game's own gizmo switch (selection, range, area and path gizmos, however they're
        // drawn), put back as it was; and the cursor's disc and info gizmos, which the game
        // shows again on the next hover.
        GizmoVisibilityManager gizmos = null;
        bool gizmosWereVisible = false;
        try
        {
            gizmos = UnityEngine.Object.FindObjectOfType<GizmoVisibilityManager>();
            if (gizmos != null && (gizmosWereVisible = gizmos.AreGizmosVisible())) gizmos.HideGizmos();
            foreach (var c in UnityEngine.Object.FindObjectsOfType<CursorGizmosController>()) c.HideAllGizmos();
            Plugin.Logger.LogInfo($"[diorama] gizmos {(gizmos == null ? "manager not found" : gizmosWereVisible ? "hidden" : "already hidden")}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[diorama] couldn't hide the game's gizmos: {e.Message}"); }
        var off = new List<Behaviour>();
        var names = new SortedDictionary<string, int>();
        void Disable(Behaviour b)
        {
            if (b == null || !b.enabled || b.gameObject.layer == CataRig.VisualLayer) return;
            b.enabled = false;
            off.Add(b);
            names.TryGetValue(b.gameObject.name, out int n);
            names[b.gameObject.name] = n + 1;
        }
        try
        {
            foreach (var d in UnityEngine.Object.FindObjectsOfType<Shapes.ImmediateModeShapeDrawer>()) Disable(d);
            foreach (var s in UnityEngine.Object.FindObjectsOfType<Shapes.ShapeRenderer>()) Disable(s);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[diorama] couldn't hide the Shapes markers: {e.Message}"); }
        // Named so the first capture shows what went: anything that should have stayed is listed here.
        var list = new List<string>();
        foreach (var kv in names) list.Add(kv.Value > 1 ? $"{kv.Key} x{kv.Value}" : kv.Key);
        Plugin.Logger.LogInfo($"[diorama] hid {off.Count} Shapes drawers: {string.Join(", ", list)}");
        return () =>
        {
            foreach (var b in off) if (b != null) b.enabled = true;
            canvases?.Invoke();
            try { if (gizmos != null && gizmosWereVisible) gizmos.ShowGizmos(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[diorama] couldn't show the game's gizmos again: {e.Message}"); }
        };
    }

    static (Vector3 centre, float radius)? Frame(CataRig rig)
    {
        var t = rig.Table;
        if (t == null || Game.Gameplay == null) return null;
        var c = t.Anchor;
        if (!Game.Gameplay.GetGroundHeightAt(c.x, c.z, out float ground)) return null;
        float r = t.CutRadius > 0 ? t.CutRadius : t.Tuning.Radius * t.WorldScale;
        return (new Vector3(c.x, ground, c.z), Mathf.Max(r, 0.01f));
    }
}
