using System;
using DigitalSun.Castle;
using HarmonyLib;
using UnityEngine;
using Il2CppSystem.Collections.Generic;

namespace CataVr;

// The game's drag select takes what's inside a rectangle on its camera's
// screen: on the table that's a skewed trapezoid, and nothing off the hidden
// camera's view. During a laser drag, its two square-selection lookups instead
// return what's inside the box drawn on the table (WorldMouse). The game's
// own lookup still decides what can be selected (asked with a screen
// rectangle far bigger than the screen), and its own code still orders and
// selects the result.
static class WorldSelect
{
    static readonly Vector3 Everywhere0 = new(-1e6f, -1e6f, 0), Everywhere1 = new(1e6f, 1e6f, 0);
    static bool busy;
    static int logs;

    public static void Install(Harmony harmony)
    {
        Patch(harmony, nameof(SelectionManager.GetPlayerSelectablesInSquareSelection), nameof(Selectables));
        Patch(harmony, nameof(SelectionManager.GetPlayerWorldEntitiesInSquareSelection), nameof(WorldEntities));
    }

    static void Patch(Harmony harmony, string method, string prefix)
    {
        try { harmony.Patch(AccessTools.Method(typeof(SelectionManager), method), prefix: new HarmonyMethod(typeof(WorldSelect), prefix)); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[select] can't hook {method}: {e.Message}"); }
    }

    static bool Selectables(SelectionManager __instance, ref List<ISelectable> __result)
    {
        if (busy || !WorldMouse.WorldBoxActive) return true;
        busy = true;
        try
        {
            var all = __instance.GetPlayerSelectablesInSquareSelection(Everywhere0, Everywhere1);
            var inside = new List<ISelectable>();
            for (int i = 0; i < all.Count; i++)
            {
                var go = all[i]?.GetSelectableGameObject();
                if (go != null && WorldMouse.InWorldBox(go.transform.position)) inside.Add(all[i]);
            }
            Log("selectables", inside.Count, all.Count);
            __result = inside;
            return false;
        }
        finally { busy = false; }
    }

    static bool WorldEntities(SelectionManager __instance, ref List<WorldEntity> __result)
    {
        if (busy || !WorldMouse.WorldBoxActive) return true;
        busy = true;
        try
        {
            var all = __instance.GetPlayerWorldEntitiesInSquareSelection(Everywhere0, Everywhere1);
            var inside = new List<WorldEntity>();
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && WorldMouse.InWorldBox(all[i].transform.position)) inside.Add(all[i]);
            Log("entities", inside.Count, all.Count);
            __result = inside;
            return false;
        }
        finally { busy = false; }
    }

    static void Log(string what, int inside, int all)
    {
        if (logs++ < 40) Plugin.Logger.LogInfo($"[select] f{Time.frameCount} world box: {inside} of {all} {what}");
    }
}
