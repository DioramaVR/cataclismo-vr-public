using System;
using System.Collections.Generic;
using System.Linq;
using DigitalSun.Castle;
using HarmonyLib;
using UnityEngine;

namespace CataVr;

// Logs the game's selection and camera-movement calls, with the frame and the
// mouse pixel, to see what a laser click turns into (a click, a tiny drag, an
// edge scroll, a camera jump). Each method logs a limited number of times.
static class InputTrace
{
    static readonly (Type type, string[] methods)[] Hooks =
    {
        (typeof(PlayerEntityInputHandler), new[]
        {
            nameof(PlayerEntityInputHandler.SelectUnitOrStructure),
            nameof(PlayerEntityInputHandler.UnselectUnitOrStructure),
            nameof(PlayerEntityInputHandler.SelectSimilarMultipleUnitsOrStructures),
            nameof(PlayerEntityInputHandler.StartMultipleUnitsOrStructuresSelection),
            nameof(PlayerEntityInputHandler.EndMultipleUnitsOrStructuresSelection),
            nameof(PlayerEntityInputHandler.CancelMultipleUnitsOrStructuresSelection),
            nameof(PlayerEntityInputHandler.RtsInteraction),
            nameof(PlayerEntityInputHandler.StartRMBDrag),
            nameof(PlayerEntityInputHandler.EndRMBDrag),
            nameof(PlayerEntityInputHandler.SquadSelectionOrRegister),
            nameof(PlayerEntityInputHandler.SelectCore),
            nameof(PlayerEntityInputHandler.SelectIris),
        }),
        (typeof(SelectionManager), new[]
        {
            nameof(SelectionManager.SelectEntity),
            nameof(SelectionManager.SelectEntities),
            nameof(SelectionManager.SelectSquad),
            nameof(SelectionManager.ClearCurrentSelectedEntities),
            nameof(SelectionManager.DeselectEntities),
            nameof(SelectionManager.Deselect),
            nameof(SelectionManager.SelectAllUnitOrStructuresOnDrawnScreenRectangle),
        }),
        (typeof(GameplayCamera), new[]
        {
            nameof(GameplayCamera.ProcessMouseMovementInput),
            nameof(GameplayCamera.ProcessRotateMouseInput),
        }),
    };

    const int MaxPerMethod = 40;
    static readonly Dictionary<string, int> counts = new();

    public static void Install(Harmony harmony)
    {
        var prefix = new HarmonyMethod(typeof(InputTrace), nameof(Called));
        foreach (var (type, names) in Hooks)
            foreach (var m in type.GetMethods().Where(m => names.Contains(m.Name) && !m.IsStatic))
            {
                try { harmony.Patch(m, prefix: prefix); }
                catch (Exception e) { Plugin.Logger.LogWarning($"[trace] can't hook {type.Name}.{m.Name}: {e.Message}"); }
            }
    }

    static void Called(System.Reflection.MethodBase __originalMethod, object[] __args)
    {
        var name = __originalMethod.Name;
        // Per-frame camera input: only when it actually moves the camera.
        if (name == nameof(GameplayCamera.ProcessMouseMovementInput) && __args[0] is Vector2 d && d == Vector2.zero) return;
        if (name == nameof(GameplayCamera.ProcessRotateMouseInput) && __args[0] is float x && __args[1] is float y && x == 0 && y == 0) return;
        counts.TryGetValue(name, out int n);
        counts[name] = n + 1;
        if (n >= MaxPerMethod) return;
        var args = string.Join(", ", __args.Select(a => a?.ToString() ?? "null"));
        Plugin.Logger.LogInfo($"[trace] f{Time.frameCount} {__originalMethod.DeclaringType?.Name}.{name}({args}) mouse {NativeInput.MousePixel}");
    }
}
