using System;
using System.Collections.Generic;
using System.Linq;
using DigitalSun.Castle;
using HarmonyLib;
using UnityEngine;
using NVec = System.Numerics.Vector3;

namespace CataVr;

// The game moves its own camera to show you something (events, "go to"
// buttons, following a unit, restoring the view after a cutscene). The table
// normally keeps the player's drag offset, height and turn, which puts those
// shots off-centre and seen from the wrong side; while one runs, CataRig frames
// the table the way the game frames its camera instead.
static class GameShots
{
    static readonly string[] Methods =
    {
        nameof(GameplayCamera.MoveTo),
        nameof(GameplayCamera.MoveToRespectingBoundaries),
        nameof(GameplayCamera.FollowTransform),
        nameof(GameplayCamera.RestoreCameraPositionAndOrientation),
        nameof(GameplayCamera.ResetCameraToDefault),
        // The map view (zooming far out, or its key) swings the camera up and
        // back over the whole level; the table follows it out and back in.
        nameof(GameplayCamera.TriggerMapViewChanged),
        nameof(GameplayCamera.MapViewGoto),
    };

    // A shot lasts until the camera has stopped moving for this long.
    const float SettleTime = 0.5f, MaxTime = 20f;

    static float startTime = -1, stillSince;
    static NVec lastPivot;
    static float lastYaw;
    static readonly Dictionary<string, int> logged = new();

    public static bool Active { get; private set; }

    public static void Install(Harmony harmony)
    {
        var prefix = new HarmonyMethod(typeof(GameShots), nameof(Started));
        foreach (var m in typeof(GameplayCamera).GetMethods().Where(m => Methods.Contains(m.Name) && !m.IsStatic))
        {
            try
            {
                harmony.Patch(m, prefix: prefix);
                Plugin.Logger.LogInfo($"[shots] hooked {m}");
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[shots] can't hook {m}: {e.Message}"); }
        }
    }

    static void Started(System.Reflection.MethodBase __originalMethod, GameplayCamera __instance)
    {
        var name = __originalMethod.Name;
        // The game calls this on every left click; it only moves the camera in the map view.
        if (name == nameof(GameplayCamera.MapViewGoto) && !__instance.MapViewEnabled) return;
        logged.TryGetValue(name, out int n);
        if (n < 20) Plugin.Logger.LogInfo($"[shots] game camera {name}");
        logged[name] = n + 1;
        Active = true;
        startTime = stillSince = Time.unscaledTime;
    }

    // The player took over (grip drag): stop framing the shot.
    public static void Cancel() => Active = false;

    // Once per frame with the game camera's pivot and yaw: ends the shot once
    // the camera holds still (or stops following a target).
    public static void Track(NVec pivot, float yaw, bool following)
    {
        if (!Active) return;
        float now = Time.unscaledTime;
        if (following || NVec.DistanceSquared(pivot, lastPivot) > 1e-4f || Mathf.Abs(Mathf.DeltaAngle(yaw, lastYaw)) > 0.05f)
            stillSince = now;
        lastPivot = pivot;
        lastYaw = yaw;
        if (now - stillSince > SettleTime || now - startTime > MaxTime) Active = false;
    }
}
