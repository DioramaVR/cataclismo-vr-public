using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using VrUiKit;

namespace CataVr;

// The game's own input module does the clicking (NativeInput puts its mouse on
// the pixel under the laser), so its EventSystem.RaycastAll is filtered:
// - Pieces shown in the world or at the laser (wave pointers, our wave-detail
//   copies, hover info) are cut out of the panel and the windows, yet still sit
//   on the game's screen canvas at their screen spot. While the laser is on the
//   panel or a window, their hits are dropped, so an unseen piece never takes a
//   click meant for what the player sees there (2026-11-23: build buttons blocked).
// - Popout windows (Arranged and Free): windows that overlap on the game's screen
//   are apart in VR, so while the laser is on a window, what belongs to another
//   window is dropped (VrUi.Owns). The pop-ups remainder owns nothing, so it keeps the rest.
static class PopoutClicks
{
    // The window the laser is on this frame (VrPointer), or null: off the windows, or on the full panel.
    public static string Window;
    static int logs, pieceLogs;

    public static void Install(Harmony harmony)
    {
        try
        {
            var target = AccessTools.Method(typeof(EventSystem), nameof(EventSystem.RaycastAll));
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(PopoutClicks), nameof(Filter)));
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[vrui] can't hook EventSystem.RaycastAll, so Popout clicks aren't filtered: {e.Message}"); }
    }

    public static bool Popout => VrUi.Windows == VrUi.WindowDraw.Popout && VrUi.Mode is VrUi.UiMode.Arranged or VrUi.UiMode.Free;

    static void Filter(Il2CppSystem.Collections.Generic.List<RaycastResult> raycastResults)
    {
        if (raycastResults == null || raycastResults.Count == 0) return;
        try { DropHiddenPieces(raycastResults); }
        catch (Exception e) { if (pieceLogs++ < 20) Plugin.Logger.LogWarning($"[vrui] piece click filter: {e.Message}"); }
        var window = Window;
        if (window == null || raycastResults.Count == 0 || !Popout) return;
        try
        {
            int before = raycastResults.Count;
            for (int i = raycastResults.Count - 1; i >= 0; i--)
                if (OwnedByOther(window, raycastResults[i].gameObject)) raycastResults.RemoveAt(i);
            if (raycastResults.Count != before && logs++ < 20)
                Plugin.Logger.LogInfo($"[vrui] popout click on '{window}': dropped {before - raycastResults.Count} of {before} hits from other windows");
        }
        catch (Exception e)
        {
            if (logs++ < 20) Plugin.Logger.LogWarning($"[vrui] popout click filter: {e.Message}");
        }
    }

    // On the panel or a window: hits on pieces that aren't shown there are not the player's target.
    static void DropHiddenPieces(Il2CppSystem.Collections.Generic.List<RaycastResult> results)
    {
        if (!VrPointer.OnMenu) return;
        bool pointersAway = AwayFromPanel(CataUi.PointersPiece), hoverAway = AwayFromPanel(CataUi.HoverPiece);
        if (!pointersAway && !hoverAway) return;
        for (int i = results.Count - 1; i >= 0; i--)
        {
            var go = results[i].gameObject;
            if (go == null) continue;
            string piece = pointersAway && Under(go, PointerRoots) ? "pointers" : hoverAway && Under(go, HoverRoots) ? "hover" : null;
            if (piece == null) continue;
            if (pieceLogs++ < 30) Plugin.Logger.LogInfo($"[vrui] click on the panel: dropped a hit on '{Path(go)}' (the {piece} piece, shown {VrPieces.PlaceOf(piece == "pointers" ? CataUi.PointersPiece : CataUi.HoverPiece)}, not on the panel)");
            results.RemoveAt(i);
        }
    }

    // The pieces' roots by name, as CataUi registers them (our wave-detail copies sit under ScreenPointersPanel).
    static readonly string[] PointerRoots = { "ScreenPointersPanel" };
    static readonly string[] HoverRoots = { "PawnHoverInformationPanel", "PointerFollowingHintDisplay" };

    static bool Under(GameObject go, string[] names)
    {
        for (var t = go.transform; t != null; t = t.parent)
            if (Array.IndexOf(names, t.name) >= 0) return true;
        return false;
    }

    static bool AwayFromPanel(UiWindowDef def) => def != null && VrPieces.PlaceOf(def) is VrPieces.Place.World or VrPieces.Place.AtLaser or VrPieces.Place.Hidden;

    static string Path(GameObject go)
    {
        var t = go.transform;
        string p = t.name;
        for (int i = 0; i < 3 && t.parent != null; i++) { t = t.parent; p = t.name + "/" + p; }
        return p;
    }

    static bool OwnedByOther(string window, GameObject go)
    {
        if (go == null) return true;
        foreach (var id in CataUi.WindowIds)
            if (id != window && VrUi.Owns(id, go)) return true;
        return false;
    }
}
