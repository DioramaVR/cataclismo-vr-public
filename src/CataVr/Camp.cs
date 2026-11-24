using System.Collections.Generic;
using DigitalSun.Castle;
using DigitalSun.Castle.Macro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CataVr;

// The camp between campaign levels (the game's "Macro" scene, the war table):
// no gameplay camera; its 3D objects (Hermes, the map, the book, the level
// nodes) are clicked through the EventSystem's physics raycaster on its own camera.
static class Camp
{
    static MacroStateManager states;
    static MacroInteractablesController interactables;
    static float nextSearch;

    static void Find()
    {
        if (states != null && states.isActiveAndEnabled) return;
        states = null;
        interactables = null;
        if (Time.unscaledTime < nextSearch) return;
        nextSearch = Time.unscaledTime + 1f;
        states = Object.FindObjectOfType<MacroStateManager>();
        interactables = states != null ? Object.FindObjectOfType<MacroInteractablesController>() : null;
    }

    public static bool InCamp
    {
        get { Find(); return states != null; }
    }

    public static string State
    {
        get
        {
            Find();
            if (states == null) return "-";
            var current = states.CurrentState;
            return $"{(current != null ? current.stateID.ToString() : "none")}{(states.ChangingState ? " (changing)" : "")}{(states._stateChangeBlocked ? " (blocked)" : "")}";
        }
    }

    public static Camera Camera
    {
        get
        {
            Find();
            var cams = states != null ? Object.FindObjectOfType<MacroCamerasManager>() : null;
            return cams != null ? cams.GetCamera() : null;
        }
    }

    public static MacroInteractableElement Map
    {
        get { Find(); return interactables != null ? interactables.macroMap : null; }
    }

    public static IEnumerable<(string, MacroInteractableElement)> Elements()
    {
        Find();
        if (interactables == null) yield break;
        yield return ("bird", interactables.macroBird);
        yield return ("map", interactables.macroMap);
        yield return ("book", interactables.macroKnowledgeBook);
        if (interactables.levelNodes != null)
            foreach (var node in interactables.levelNodes)
                if (node != null && node.gameObject.activeInHierarchy)
                    yield return ($"level {node.name}", node);
    }

    // Where the element's collider centre is on the monitor, or null if off screen.
    public static Vector2? ScreenPixel(MacroInteractableElement element)
    {
        var cam = Camera;
        if (element == null || cam == null) return null;
        var collider = element.myCollider;
        var centre = collider != null ? collider.bounds.center : element.transform.position;
        var p = cam.WorldToScreenPoint(centre);
        if (p.z <= 0 || p.x < 0 || p.y < 0 || p.x > Screen.width || p.y > Screen.height) return null;
        return new Vector2(p.x, p.y);
    }

    // What the EventSystem sees under the mouse (UI or the camp's 3D objects).
    public static string UnderPointer()
    {
        var es = EventSystem.current;
        if (es == null) return "no event system";
        var data = new PointerEventData(es) { position = Input.mousePosition };
        var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        es.RaycastAll(data, results);
        if (results.Count == 0) return "nothing";
        var names = new List<string>();
        for (int i = 0; i < results.Count && i < 4; i++)
        {
            var go = results[i].gameObject;
            var g = go != null ? go.GetComponent<UnityEngine.UI.Graphic>() : null;
            var canvas = g != null ? g.canvas : null;
            var root = canvas != null ? canvas.rootCanvas : null;
            names.Add($"{go?.name} ({results[i].module?.GetIl2CppType().Name}) path {Path(go?.transform)} colour {(g != null ? g.color.ToString() : "-")} alpha {(g != null ? g.canvasRenderer.GetInheritedAlpha().ToString("F2") : "-")} cull {(g != null ? g.canvasRenderer.cull.ToString() : "-")} layer {go?.layer} canvas '{canvas?.name}' root '{root?.name}' {root?.renderMode} cam '{root?.worldCamera?.name}' enabled {root?.enabled}");
        }
        return string.Join(" || ", names);
    }

    static string Path(Transform t)
    {
        var parts = new List<string>();
        for (; t != null && parts.Count < 8; t = t.parent) parts.Insert(0, t.name);
        return string.Join("/", parts);
    }
}
