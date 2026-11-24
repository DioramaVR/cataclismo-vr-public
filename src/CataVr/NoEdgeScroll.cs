using System;
using DigitalSun.Castle;
using HarmonyLib;
using UnityEngine;

namespace CataVr;

// The game pans its camera when the mouse sits at the screen's edge. In VR the
// mouse is the laser: on a game window at the edge of the screen (the right-hand
// panels, the build bar) it lands on an edge pixel and the map slides away,
// which in Turntable shows as the circle drifting under you. Panning is the
// stick's (the game's W A S D), so the edge pan is off while VR drives the mouse.
static class NoEdgeScroll
{
    static int skipped;

    public static void Install(Harmony harmony)
    {
        var m = AccessTools.Method(typeof(GameplayCamera), nameof(GameplayCamera.ProcessMouseMovementInput));
        if (m == null) { Plugin.Logger.LogWarning("[edge] GameplayCamera.ProcessMouseMovementInput not found; the game's edge pan stays on"); return; }
        try { harmony.Patch(m, prefix: new HarmonyMethod(typeof(NoEdgeScroll), nameof(Prefix))); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[edge] can't hook the edge pan: {e.Message}"); }
    }

    // The method still runs every frame (whatever else it updates), just with no edge-pan direction.
    static void Prefix(ref Vector2 dir)
    {
        if (!NativeInput.Active || dir == Vector2.zero) return;
        if (skipped++ < 3)
            Plugin.Logger.LogInfo($"[edge] game edge pan {dir} zeroed (mouse {NativeInput.MousePixel}; the laser drives the mouse)");
        dir = Vector2.zero;
    }
}
