using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace CataVr;

// F9 (and the sim, once in a level): log what draws the scene, to find things
// the eyes miss: cameras and their stacks and renderers, world-space canvases,
// the game's screen pointers, and enabled renderers on layers the eyes skip.
static class SceneDump
{
    public static void Log(Camera[] eyes)
    {
        var log = Plugin.Logger;
        var main = Game.MainCamera;
        int eyeMask = eyes.Length > 0 && eyes[0] != null ? eyes[0].cullingMask : 0;
        log.LogInfo($"[dump] eye mask 0x{eyeMask:X8}, game camera '{main?.name}', gizmo layer mask 0x{(Game.Gameplay != null ? Game.Gameplay.worldGizmosLayer.value : 0):X8}");

        foreach (var cam in Object.FindObjectsOfType<Camera>())
        {
            var data = cam.GetComponent<UniversalAdditionalCameraData>();
            var sb = new StringBuilder($"[dump] camera '{cam.name}' enabled {cam.enabled} depth {cam.depth} mask 0x{cam.cullingMask:X8} target {(cam.targetTexture != null ? cam.targetTexture.name : "-")}");
            if (data != null)
            {
                sb.Append($" type {data.renderType} renderer {data.m_RendererIndex} ({data.scriptableRenderer?.GetType().Name})");
                var stack = data.cameraStack;
                if (stack != null && stack.Count > 0)
                {
                    sb.Append(" stack:");
                    foreach (var s in stack) sb.Append($" '{s?.name}' mask 0x{(s != null ? s.cullingMask : 0):X8}");
                }
            }
            log.LogInfo(sb.ToString());
        }

        foreach (var canvas in Object.FindObjectsOfType<Canvas>())
            if (canvas.isRootCanvas && canvas.renderMode == RenderMode.WorldSpace && canvas.isActiveAndEnabled)
                log.LogInfo($"[dump] world canvas '{canvas.name}' layer {canvas.gameObject.layer} ({LayerMask.LayerToName(canvas.gameObject.layer)}) at {canvas.transform.position} {(((eyeMask >> canvas.gameObject.layer) & 1) != 0 ? "drawn" : "NOT drawn")} by the eyes");
            else if (canvas.isRootCanvas && canvas.isActiveAndEnabled)
            {
                // Which layers and shaders its visible graphics use (the UI camera draws layer 5 only).
                var layers = new Dictionary<int, int>();
                var shaders = new HashSet<string>();
                var sample = new List<string>();
                foreach (var g in canvas.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
                {
                    if (!g.isActiveAndEnabled) continue;
                    int l = g.gameObject.layer;
                    layers[l] = layers.TryGetValue(l, out var n) ? n + 1 : 1;
                    shaders.Add(g.materialForRendering != null ? g.materialForRendering.shader.name : "-");
                    if (sample.Count < 6) sample.Add($"{g.name} a{g.color.a:F2} cr{g.canvasRenderer.GetAlpha():F2}");
                }
                var group = canvas.GetComponent<CanvasGroup>();
                log.LogInfo($"[dump] screen canvas '{canvas.name}' layer {canvas.gameObject.layer} mode {canvas.renderMode} camera '{canvas.worldCamera?.name}' order {canvas.sortingOrder} group alpha {(group != null ? group.alpha.ToString("F2") : "-")} | graphic layers {string.Join(",", System.Linq.Enumerable.Select(layers, kv => $"{kv.Key}x{kv.Value}"))} | shaders {string.Join(", ", shaders)} | {string.Join("; ", sample)}");
            }

        foreach (var p in Object.FindObjectsOfType<DigitalSun.Castle.UI.ScreenPointer>())
            if (p.isActiveAndEnabled)
                log.LogInfo($"[dump] screen pointer '{p.name}' type {p._pointerType} target {(p._targetTransform != null ? p._targetTransform.position.ToString() : "-")} inside screen {p._insideScreen}");

        // Renderers the eyes skip, grouped by layer (a few names each).
        var skipped = new Dictionary<int, List<string>>();
        foreach (var r in Object.FindObjectsOfType<Renderer>())
        {
            int layer = r.gameObject.layer;
            if (!r.enabled || !r.gameObject.activeInHierarchy || ((eyeMask >> layer) & 1) != 0) continue;
            if (!skipped.TryGetValue(layer, out var names)) skipped[layer] = names = new List<string>();
            if (names.Count < 12) names.Add($"{r.name} ({r.GetType().Name})");
            else if (names.Count == 12) names.Add("...");
        }
        foreach (var kv in skipped)
            log.LogInfo($"[dump] layer {kv.Key} ({LayerMask.LayerToName(kv.Key)}) not drawn by the eyes: {string.Join(", ", kv.Value)}");
        log.LogInfo("[dump] done");
    }

    // The HUD's object tree (for choosing VR UI windows): each active UI object
    // a few levels down from the panel's canvases, with its screen rect.
    public static void LogHud(int depth = 4)
    {
        var cam = VrMenuPanel.UiCamera;
        foreach (var canvas in VrMenuPanel.Canvases)
            if (canvas != null && canvas.isActiveAndEnabled)
                LogTree(canvas.transform, canvas.name, depth, cam);
        Plugin.Logger.LogInfo("[hud-tree] done");
    }

    static void LogTree(Transform t, string path, int depth, Camera cam)
    {
        var rect = t.TryCast<RectTransform>();
        string r = "-";
        if (rect != null && cam != null)
        {
            var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            rect.GetWorldCorners(corners);
            var a = cam.WorldToScreenPoint(corners[0]);
            var b = cam.WorldToScreenPoint(corners[2]);
            r = $"x {a.x:F0}..{b.x:F0} y {a.y:F0}..{b.y:F0}";
        }
        int graphics = t.GetComponentsInChildren<UnityEngine.UI.Graphic>(false).Length;
        Plugin.Logger.LogInfo($"[hud-tree] {path} | {r} | graphics {graphics}");
        if (depth <= 0) return;
        for (int i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            if (c.gameObject.activeInHierarchy) LogTree(c, path + "/" + c.name, depth - 1, cam);
        }
    }
}
