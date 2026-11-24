using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CataVr;

// The game reads the mouse and keyboard through Unity's legacy Input class
// (directly and through InControl and UGUI's StandaloneInputModule). IL2CPP
// code calls Unity's native icalls directly, so we detour those in
// UnityPlayer.dll: while VR is active the mouse position comes from the laser,
// the VR buttons are OR-ed into the real mouse buttons and keys, and the
// game's main-camera rays through the mouse become the laser ray.
static unsafe class NativeInput
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void RayFn(IntPtr self, IntPtr pos, int eye, IntPtr ret);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void OutFn(IntPtr ret);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    delegate bool IntBoolFn(int arg);

    // Detours, their targets and the originals must stay referenced.
    static readonly List<object> keepAlive = new();
    static RayFn rayOriginal;
    static OutFn mouseOriginal, scrollOriginal;
    static IntBoolFn buttonOriginal, buttonDownOriginal, buttonUpOriginal, keyOriginal, keyDownOriginal, keyUpOriginal;

    // State written by VrPointer once per frame.
    public static bool Active;            // VR owns the mouse position
    public static Vector2 MousePixel;     // y up, like Input.mousePosition
    public static bool RayActive;         // replace main-camera rays through MousePixel
    public static Ray LaserRay;
    public static IntPtr MainCameraPtr;
    public static float ScrollY;          // added to the wheel for the next frame

    public static int RayCalls, RayOverrides;

    public static void Install()
    {
        rayOriginal = Hook<RayFn>("UnityEngine.Camera::ScreenPointToRay_Injected", OnScreenPointToRay);
        mouseOriginal = Hook<OutFn>("UnityEngine.Input::get_mousePosition_Injected", OnMousePosition);
        scrollOriginal = Hook<OutFn>("UnityEngine.Input::get_mouseScrollDelta_Injected", OnScroll);
        buttonOriginal = Hook<IntBoolFn>("UnityEngine.Input::GetMouseButton", b => buttonOriginal(b) || Buttons(b).Held);
        buttonDownOriginal = Hook<IntBoolFn>("UnityEngine.Input::GetMouseButtonDown", b => buttonDownOriginal(b) || Buttons(b).Down);
        buttonUpOriginal = Hook<IntBoolFn>("UnityEngine.Input::GetMouseButtonUp", b => buttonUpOriginal(b) || Buttons(b).Up);
        keyOriginal = Hook<IntBoolFn>("UnityEngine.Input::GetKeyInt", k => keyOriginal(k) || Key(k).Held);
        keyDownOriginal = Hook<IntBoolFn>("UnityEngine.Input::GetKeyDownInt", k => keyDownOriginal(k) || Key(k).Down);
        keyUpOriginal = Hook<IntBoolFn>("UnityEngine.Input::GetKeyUpInt", k => keyUpOriginal(k) || Key(k).Up);
    }

    static T Hook<T>(string icall, T hook) where T : Delegate
    {
        var fn = IL2CPP.il2cpp_resolve_icall(icall);
        if (fn == IntPtr.Zero)
        {
            Plugin.Logger.LogError($"icall not found: {icall}");
            return null;
        }
        var detour = INativeDetour.CreateAndApply(fn, hook, out T original);
        keepAlive.Add(hook);
        keepAlive.Add(detour);
        Plugin.Logger.LogInfo($"Hooked {icall} at 0x{fn.ToInt64():X}");
        return original;
    }

    // A virtual button or key. Changes take effect from the next frame, so
    // every reader in a frame agrees, and Down/Up last exactly one frame.
    sealed class VButton
    {
        bool target;
        int changeFrame = -10;

        public void Set(bool held)
        {
            if (held == target) return;
            target = held;
            changeFrame = Time.frameCount + 1;
        }

        bool Changed => Time.frameCount == changeFrame;
        public bool Held => Active && (Time.frameCount >= changeFrame ? target : !target);
        public bool Down => Active && target && Changed;
        public bool Up => Active && !target && Changed;
    }

    static readonly VButton[] buttons = { new(), new(), new() };
    static readonly VButton none = new();
    static readonly Dictionary<int, VButton> keys = new();

    static VButton Buttons(int b) => b >= 0 && b < buttons.Length ? buttons[b] : none;
    static VButton Key(int k) => keys.TryGetValue(k, out var v) ? v : none;

    public static void SetButton(int button, bool held) => buttons[button].Set(held);

    public static void SetKey(KeyCode key, bool held)
    {
        if (!keys.TryGetValue((int)key, out var v)) keys[(int)key] = v = new VButton();
        v.Set(held);
    }

    public static void ReleaseAll()
    {
        foreach (var b in buttons) b.Set(false);
        foreach (var k in keys.Values) k.Set(false);
    }

    static void OnMousePosition(IntPtr ret)
    {
        mouseOriginal(ret);
        if (!Active) return;
        var f = (float*)ret;
        f[0] = MousePixel.x;
        f[1] = MousePixel.y;
        f[2] = 0;
    }

    static void OnScroll(IntPtr ret)
    {
        scrollOriginal(ret);
        if (!Active) return;
        ((float*)ret)[1] += ScrollY;
    }

    static void OnScreenPointToRay(IntPtr self, IntPtr pos, int eye, IntPtr ret)
    {
        rayOriginal(self, pos, eye, ret);
        RayCalls++;
        try
        {
            if (!RayActive || self != MainCameraPtr) return;
            // Only rays through the mouse: the camera also casts through fixed
            // points (e.g. the screen centre for its pivot), which must stay.
            var p = (float*)pos;
            if (MathF.Abs(p[0] - MousePixel.x) > 1.5f || MathF.Abs(p[1] - MousePixel.y) > 1.5f) return;
            var o = LaserRay.origin;
            var d = LaserRay.direction;
            var f = (float*)ret;
            f[0] = o.x; f[1] = o.y; f[2] = o.z;
            f[3] = d.x; f[4] = d.y; f[5] = d.z;
            RayOverrides++;
        }
        catch (Exception e)
        {
            // Never let an exception unwind into native Unity code.
            Plugin.Logger.LogError($"Ray hook: {e.Message}");
        }
    }
}
