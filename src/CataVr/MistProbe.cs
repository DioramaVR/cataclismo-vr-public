using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CataVr;

// Dev build: why the mist flickers in the headset (2026-11-23: not SteamVR's
// reprojection, which was 0 all session). Two parts:
// - Log (once in a level, and with F9): the mist and fog renderers, their shaders,
//   keywords and dither / noise / depth settings, and the eyes' URP settings beside
//   the game camera's, so what the eyes do differently is on record.
// Both only with [Debug] DevTools on (F9 logs it any time).
// - "Mist test" (VR menu, Graphics; F10 on the keyboard): steps through one change
//   at a time, so the player can say which one stops the flicker. Each is undone
//   when the next is picked; "Off" is the normal game.
static class MistProbe
{
    static readonly string[] Modes =
    {
        "Off (normal)",
        "Eye anti-aliasing off",
        "Eye post-processing off",
        "Eye shadows off",
        "Mist hidden",
        "Mist dither and noise off",
        "Eye depth texture forced on",
    };
    static int mode;
    static bool logged;
    static float logAt = -1;
    static readonly Regex MistName = new("(?i)mist");
    static readonly Regex FogName = new("(?i)mist|fog|cloud|haze");
    static readonly Regex Interesting = new("(?i)dither|noise|soft|fade|depth|camera|time|speed|step|shell|thick|zwrite|surface|blend|jitter|frame|screen|silhouette|smooth|offset|width");

    public static void Init()
    {
        if (!Plugin.Instance.DevTools.Value) return;   // [Debug] DevTools
        VrUiKit.VrSettings.Add(new VrUiKit.ActionSetting("Graphics", "Mist test (dev)", () => $"{mode}: {Modes[mode]}  (press for next)", Next));
    }

    public static void Next()
    {
        Undo();
        mode = (mode + 1) % Modes.Length;
        Plugin.Logger.LogInfo($"[mist] test {mode}: {Modes[mode]}");
    }

    // Each frame, after the rig has set the eyes up for this frame.
    public static void Update(Camera[] eyes, bool inWorld)
    {
        if (!Plugin.Instance.DevTools.Value) return;   // [Debug] DevTools
        lastEyes = eyes;
        if (Input.GetKeyDown(KeyCode.F10)) Next();
        var game = Game.MainCamera;
        if (!inWorld || game == null) return;
        if (!logged && Game.LevelReady)
        {
            if (logAt < 0) logAt = Time.unscaledTime + 3f;
            else if (Time.unscaledTime >= logAt) { logged = true; Log(eyes); }
        }
        if (mode == 0) return;
        var gameData = game.GetComponent<UniversalAdditionalCameraData>();
        foreach (var eye in eyes)
        {
            if (eye == null) continue;
            var data = eye.GetComponent<UniversalAdditionalCameraData>();
            if (data == null) continue;
            switch (mode)
            {
                case 1: if (data.antialiasing != AntialiasingMode.None) data.antialiasing = AntialiasingMode.None; break;
                case 2: if (data.renderPostProcessing) data.renderPostProcessing = false; break;
                case 3: if (data.renderShadows) data.renderShadows = false; break;
                case 6: if (data.requiresDepthOption != CameraOverrideOption.On) data.requiresDepthOption = CameraOverrideOption.On; break;
            }
        }
        if (mode == 4 && hidden == null) HideMist();
        if (mode == 5 && changed == null) Undither();
        lastGameData = gameData;
    }

    static UniversalAdditionalCameraData lastGameData;
    static Camera[] lastEyes;
    static List<Renderer> hidden;
    static List<(Material m, string prop, float was)> changed;
    static List<(Material m, string keyword)> keywordsOff;

    // Put back what the current test changed (the rig sets shadows itself each frame).
    static void Undo()
    {
        var game = Game.MainCamera;
        var src = lastGameData != null ? lastGameData : game != null ? game.GetComponent<UniversalAdditionalCameraData>() : null;
        if (mode is 1 or 2 or 6 && src != null)
            foreach (var eye in lastEyes ?? Array.Empty<Camera>())
            {
                var data = eye != null ? eye.GetComponent<UniversalAdditionalCameraData>() : null;
                if (data == null) continue;
                if (mode == 1) data.antialiasing = src.antialiasing;
                if (mode == 2) data.renderPostProcessing = src.renderPostProcessing;
                if (mode == 6) data.requiresDepthOption = src.requiresDepthOption;
            }
        if (hidden != null) { foreach (var r in hidden) if (r != null) r.enabled = true; hidden = null; }
        if (changed != null) { foreach (var (m, p, was) in changed) if (m != null) m.SetFloat(p, was); changed = null; }
        if (keywordsOff != null) { foreach (var (m, k) in keywordsOff) if (m != null) m.EnableKeyword(k); keywordsOff = null; }
    }

    static void HideMist()
    {
        hidden = new List<Renderer>();
        var names = new SortedSet<string>();
        foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
        {
            if (r == null || !r.enabled || r.gameObject.layer == CataRig.VisualLayer || !IsMist(r, MistName)) continue;
            r.enabled = false;
            hidden.Add(r);
            names.Add(r.name);
        }
        Plugin.Logger.LogInfo($"[mist] hid {hidden.Count} renderers: {string.Join(", ", names)}");
    }

    // Dither and noise amounts to 0 (a "dither fade disabled" switch to 1) and DITHER keywords off,
    // on the mist's materials only.
    static void Undither()
    {
        changed = new List<(Material, string, float)>();
        keywordsOff = new List<(Material, string)>();
        var seen = new HashSet<IntPtr>();
        var log = new StringBuilder();
        foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
        {
            if (r == null || !r.enabled || r.gameObject.layer == CataRig.VisualLayer || !IsMist(r, MistName)) continue;
            foreach (var m in r.sharedMaterials)
            {
                if (m == null || !seen.Add(m.Pointer)) continue;
                var sh = m.shader;
                if (sh == null) continue;
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                {
                    var t = sh.GetPropertyType(i);
                    if (t != ShaderPropertyType.Float && t != ShaderPropertyType.Range) continue;
                    var p = sh.GetPropertyName(i);
                    if (!Regex.IsMatch(p, "(?i)dither|noise")) continue;
                    float was = m.GetFloat(p), want = Regex.IsMatch(p, "(?i)disabled") ? 1f : 0f;
                    if (was == want) continue;
                    m.SetFloat(p, want);
                    changed.Add((m, p, was));
                    log.Append($" {m.name}.{p} {was:G3}->{want:G3};");
                }
                foreach (var k in m.shaderKeywords)
                    if (k.Contains("DITHER") && !k.Contains("OFF")) { m.DisableKeyword(k); keywordsOff.Add((m, k)); log.Append($" {m.name} -{k};"); }
            }
        }
        Plugin.Logger.LogInfo($"[mist] undithered {changed.Count} values, {keywordsOff.Count} keywords:{log}");
    }

    static bool IsMist(Renderer r, Regex re)
    {
        if (re.IsMatch(r.name)) return true;
        foreach (var m in r.sharedMaterials)
            if (m != null && (re.IsMatch(m.name) || (m.shader != null && re.IsMatch(m.shader.name)))) return true;
        return false;
    }

    // ---- the log ----

    public static void Log(Camera[] eyes)
    {
        var log = Plugin.Logger;
        try
        {
            var game = Game.MainCamera;
            log.LogInfo($"[mist] game camera: {Describe(game)}");
            foreach (var eye in eyes) log.LogInfo($"[mist] eye: {Describe(eye)}");
            foreach (var cam in UnityEngine.Object.FindObjectsOfType<Camera>())
                if (cam.enabled && cam != game && Array.IndexOf(eyes, cam) < 0)
                    log.LogInfo($"[mist] other camera on: '{cam.name}' mask 0x{cam.cullingMask:X8} depth {cam.depth} target {(cam.targetTexture != null ? cam.targetTexture.name : "-")}");
            var asset = UniversalRenderPipeline.asset;
            if (asset != null)
                log.LogInfo($"[mist] URP asset '{asset.name}': depth texture {asset.supportsCameraDepthTexture}, opaque texture {asset.supportsCameraOpaqueTexture}, " +
                    $"msaa {asset.msaaSampleCount}, render scale {asset.renderScale}, upscaling {asset.upscalingFilter}, hdr {asset.supportsHDR}, soft shadows {asset.supportsSoftShadows}, " +
                    $"shadow distance {asset.shadowDistance}, cascades {asset.shadowCascadeCount}");
            foreach (var data in Resources.FindObjectsOfTypeAll<UniversalRendererData>())
            {
                var feats = new List<string>();
                var list = data.rendererFeatures;
                if (list != null) for (int i = 0; i < list.Count; i++) { var f = list[i]; if (f != null) feats.Add($"{f.name}{(f.isActive ? "" : " (off)")}"); }
                log.LogInfo($"[mist] renderer '{data.name}': depth priming {data.depthPrimingMode}, copy depth {data.copyDepthMode}, features [{string.Join(", ", feats)}]");
            }
            foreach (var v in UnityEngine.Object.FindObjectsOfType<Volume>())
            {
                if (!v.isActiveAndEnabled || v.sharedProfile == null) continue;
                var parts = new List<string>();
                var comps = v.sharedProfile.components;
                for (int i = 0; i < comps.Count; i++) if (comps[i] != null && comps[i].active) parts.Add(comps[i].name);
                log.LogInfo($"[mist] volume '{v.name}' global {v.isGlobal} weight {v.weight}: {string.Join(", ", parts)}");
            }
            LogRenderers();
            var mist = UnityEngine.Object.FindObjectOfType<DigitalSun.Castle.MistManager>();
            if (mist != null && mist.mistShaderParameters != null)
                log.LogInfo($"[mist] MistManager: width {mist.mistShaderParameters.mistWidth}, offset {mist.mistShaderParameters.mistOffset}, on {mist.mistShaderParameters.turnOnMist}");
        }
        catch (Exception e) { log.LogWarning($"[mist] log failed: {e}"); }
        log.LogInfo("[mist] done");
    }

    static string Describe(Camera c)
    {
        if (c == null) return "-";
        var d = c.GetComponent<UniversalAdditionalCameraData>();
        var s = $"'{c.name}' enabled {c.enabled} near {c.nearClipPlane:G4} far {c.farClipPlane:G5} fov {c.fieldOfView:F0} hdr {c.allowHDR} msaa {c.allowMSAA} depthMode {c.depthTextureMode} " +
            $"dynres {c.allowDynamicResolution} mask 0x{c.cullingMask:X8} target {(c.targetTexture != null ? $"{c.targetTexture.name} {c.targetTexture.width}x{c.targetTexture.height} depth {c.targetTexture.depth} fmt {c.targetTexture.depthStencilFormat} aa {c.targetTexture.antiAliasing}" : "-")}";
        if (d != null)
            s += $" | URP renderer {d.m_RendererIndex} AA {d.antialiasing} ({d.antialiasingQuality}) post {d.renderPostProcessing} depth {d.requiresDepthOption}/{d.requiresDepthTexture} " +
                $"opaque {d.requiresColorOption}/{d.requiresColorTexture} shadows {d.renderShadows} stopNaN {d.stopNaN} dither {d.dithering} volMask 0x{d.volumeLayerMask.value:X} type {d.renderType}";
        return s;
    }

    // Grouped by material: how many renderers, which layers, a few names, then the shader's settings.
    static void LogRenderers()
    {
        var groups = new Dictionary<IntPtr, (Material m, int count, HashSet<int> layers, List<string> names, string type, bool off)>();
        foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
        {
            if (r == null || r.gameObject.layer == CataRig.VisualLayer || !IsMist(r, FogName)) continue;
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                if (!groups.TryGetValue(m.Pointer, out var g)) g = (m, 0, new HashSet<int>(), new List<string>(), r.GetIl2CppType().Name, true);
                g.count++;
                g.layers.Add(r.gameObject.layer);
                if (g.names.Count < 4) g.names.Add($"{r.name}{(r.enabled && r.gameObject.activeInHierarchy ? "" : " (off)")} shadows {r.shadowCastingMode}/{r.receiveShadows}");
                if (r.enabled && r.gameObject.activeInHierarchy) g.off = false;
                groups[m.Pointer] = g;
            }
        }
        Plugin.Logger.LogInfo($"[mist] {groups.Count} mist/fog materials in use");
        int n = 0;
        foreach (var g in groups.Values)
        {
            if (++n > 40) { Plugin.Logger.LogInfo("[mist] ... (more)"); break; }
            var m = g.m;
            var sh = m.shader;
            var sb = new StringBuilder($"[mist] material '{m.name}' shader '{(sh != null ? sh.name : "-")}' queue {m.renderQueue} {g.type} x{g.count}{(g.off ? " (all off)" : "")} layers {string.Join(",", g.layers)} [{string.Join("; ", g.names)}]");
            sb.Append($" keywords [{string.Join(" ", m.shaderKeywords)}]");
            if (sh != null)
            {
                sb.Append($" passes {m.passCount}:");
                for (int p = 0; p < m.passCount; p++) sb.Append($" {m.GetPassName(p)}");
                sb.Append(" |");
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                {
                    var name = sh.GetPropertyName(i);
                    var t = sh.GetPropertyType(i);
                    if (t == ShaderPropertyType.Texture)
                    {
                        if (Regex.IsMatch(name, "(?i)noise|dither|blue|depth|camera|ray|mist")) { var tex = m.GetTexture(name); sb.Append($" {name}={(tex != null ? $"{tex.name} {tex.width}x{tex.height}" : "-")}"); }
                    }
                    else if ((t == ShaderPropertyType.Float || t == ShaderPropertyType.Range) && Interesting.IsMatch(name)) sb.Append($" {name}={m.GetFloat(name):G4}");
                    else if (t == ShaderPropertyType.Vector && Interesting.IsMatch(name)) sb.Append($" {name}={m.GetVector(name)}");
                }
            }
            Plugin.Logger.LogInfo(sb.ToString());
        }
    }
}
