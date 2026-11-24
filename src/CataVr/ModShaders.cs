using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CataVr;

// Our own shaders (tools/ShaderBundle, built into lib/catavr-shaders with Unity
// 6000.0.63f1, the game's 6000.0 line, and copied beside the plugin), loaded the
// first time one is asked for. Today that's the Turntable's cylinder mask (the
// core's CylinderMask). Get returns null when the bundle is missing, won't load
// or the shader can't run here; the core then keeps the rough edge.
// (tpm-vr's ModShaders, the same loader.)
static class ModShaders
{
    const string BundleFile = "catavr-shaders";
    static bool tried;
    static AssetBundle bundle;
    static readonly Dictionary<string, Shader> shaders = new();

    public static Shader Get(string name)
    {
        if (shaders.TryGetValue(name, out var cached)) return cached;
        Shader shader = null;
        if (Open())
        {
            try
            {
                shader = LoadAsset(bundle, name, Il2CppInterop.Runtime.Il2CppType.Of<Shader>())?.TryCast<Shader>();
                if (shader == null || !shader.isSupported)
                {
                    Plugin.Logger.LogWarning($"[shaders] {name} {(shader == null ? "missing from the bundle" : "not supported here")}");
                    shader = null;
                }
                else Plugin.Logger.LogInfo($"[shaders] loaded {shader.name}");
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[shaders] loading {name} failed: {e.Message}"); shader = null; }
        }
        shaders[name] = shader;
        return shader;
    }

    static bool Open()
    {
        if (tried) return bundle != null;
        tried = true;
        var path = Path.Combine(Plugin.Instance.PluginDir, BundleFile);
        if (!File.Exists(path))
        {
            Plugin.Logger.LogWarning($"[shaders] {BundleFile} not found beside the plugin; the Turntable edge stays rough");
            return false;
        }
        try { bundle = LoadBundle(path); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[shaders] loading {BundleFile} failed: {e.Message}"); }
        if (bundle == null) Plugin.Logger.LogWarning($"[shaders] couldn't load {BundleFile} (built with a different Unity version?)");
        else Plugin.Logger.LogInfo($"[shaders] {BundleFile} loaded");
        return bundle != null;
    }

    // Unity 6 passes strings to native code as a span over their characters;
    // the interop wrappers that build that span fail under IL2CPP (tpm-vr found
    // this), so build it ourselves and call the native ("_Injected") functions
    // directly. They return GC handles.
    static unsafe AssetBundle LoadBundle(string path)
    {
        IntPtr handle;
        fixed (char* chars = path)
        {
            var span = new UnityEngine.Bindings.ManagedSpanWrapper(chars, path.Length);
            handle = AssetBundle.LoadFromFile_Internal_Injected(ref span, 0, 0);
        }
        return handle == IntPtr.Zero ? null : UnityEngine.Bindings.Unmarshal.UnmarshalUnityObject<AssetBundle>(handle);
    }

    static unsafe Object LoadAsset(AssetBundle bundle, string name, Il2CppSystem.Type type)
    {
        IntPtr handle;
        fixed (char* chars = name)
        {
            var span = new UnityEngine.Bindings.ManagedSpanWrapper(chars, name.Length);
            handle = AssetBundle.LoadAsset_Internal_Injected(bundle.m_CachedPtr, ref span, type);
        }
        return handle == IntPtr.Zero ? null : UnityEngine.Bindings.Unmarshal.UnmarshalUnityObject<Object>(handle);
    }
}
