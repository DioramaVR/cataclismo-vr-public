using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.Rendering;

namespace CataVr;

// Cataclismo's GameAssembly.dll has no DRM, so Il2CppInterop's class injection
// works: VrDriver is a real MonoBehaviour and Update() is our per-frame entry.
[BepInPlugin(Guid, "Cataclismo VR", Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "dvr.cataclismo";
    // The repo's VERSION file, written into ModVersion.g.cs by the build (CataVr.csproj).
    public const string Version = ModVersion.Value;
    internal static ManualLogSource Logger;
    internal static Plugin Instance;

    internal ConfigEntry<bool> VrEnabled;
    internal ConfigEntry<bool> FlipVertical;
    internal ConfigEntry<bool> LeftHanded;
    internal ConfigEntry<float> PointerPitch;
    internal ConfigEntry<int> MenuRefreshEveryFrames;
    internal ConfigEntry<float> MenuWidth;
    internal ConfigEntry<float> MenuHeight;
    internal ConfigEntry<float> MenuDistance;
    internal ConfigEntry<bool> ReconLogging;
    internal ConfigEntry<bool> DevTools;
    internal ConfigEntry<bool> SimulateVr;
    internal ConfigEntry<bool> AutoLoad;
    internal ConfigEntry<string> AutoLoadButtons;
    internal ConfigEntry<bool> AutoLoadLowest;
    internal ConfigEntry<bool> RunTestScript;
    internal ConfigEntry<bool> QuitAfterTest;

    public override void Load()
    {
        Logger = Log;
        Instance = this;
        VrCore.Init(Log, "CataVr");
        CarryOverOldConfig();
        // Sim screenshots (core SimShots): eye, UI panel and desktop, as before.
        SimShots.Dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "catavr-shots");
        SimShots.UiTexture = () => VrMenuPanel.Texture;
        // Recorder: file names, and the photo's headset copy taken once the eye has rendered (SimShots).
        Recorder.FilePrefix = "Cataclismo-VR";
        Recorder.SaveHeadset = SimShots.SaveEye;
        // Turntable's per-pixel cylinder edge (core CylinderMask), from our shader bundle.
        CylinderMask.ShaderSource = () => ModShaders.Get("CylinderMask");
        CylinderMask.PropertyPrefix = "_CataVr";
        VrEnabled = Config.Bind("VR", "Enabled", true, "Start OpenVR and render to the headset.");
        // Scale, table height and distance, follow, eye resolution, shadows, post-processing and desktop view are the
        // core table's now ([Dvr.Table], [Dvr.Diorama], [Dvr.Turntable], [Dvr.Graphics]); the [VR] values move over once.
        FlipVertical = Config.Bind("VR", "FlipVertical", true, "Flip submitted eye images vertically (if the headset view is upside down).");
        LeftHanded = Config.Bind("Controls", "LeftHanded", false, "Laser pointer on the left controller instead of the right.");
        PointerPitch = Config.Bind("Controls", "PointerPitch", 45f, "Tilt the laser down (+) or up (-) relative to the controller, degrees.");
        MenuRefreshEveryFrames = Config.Bind("Menu", "RefreshEveryFrames", 2, "Redraw the VR menu panel every N frames. 1 = every frame (smoothest, slowest).");
        MenuWidth = Config.Bind("Menu", "Width", 1.6f, "Width of the floating menu panel, metres.");
        MenuHeight = Config.Bind("Menu", "Height", 1.55f, "Height of the menu panel's centre above the floor, metres.");
        MenuDistance = Config.Bind("Menu", "Distance", 1.3f, "Distance of the menu panel in front of the play-space origin, metres.");
        ReconLogging = Config.Bind("Debug", "ReconLogging", false, "Log cameras, canvases and the HUD's structure a few seconds after each change of scene or view.");
        DevTools = Config.Bind("Debug", "DevTools", false, "Investigation tools: a [perf] line every 5 s (frame rate, SteamVR reprojection) and the Mist test row on the VR menu (Graphics; F10) with its [mist] log in each level.");
        SimulateVr = Config.Bind("Debug", "SimulateVr", false, "Run without a headset: fixed head pose, scripted hands, eye images saved as screenshots.");
        AutoLoad = Config.Bind("Debug", "AutoLoad", false, "With SimulateVr: click through the main menu into a level with the laser.");
        AutoLoadButtons = Config.Bind("Debug", "AutoLoadButtons", "Continue;Resume;Load;Play;Campaign", "Button labels AutoLoad clicks, in order of preference (semicolon-separated).");
        AutoLoadLowest = Config.Bind("Debug", "AutoLoadLowest", false, "With AutoLoad: of several buttons with the same label, click the lowest on screen (e.g. the second campaign slot).");
        RunTestScript = Config.Bind("Debug", "RunTestScript", false, "With SimulateVr: run the scripted interaction test once in a level.");
        QuitAfterTest = Config.Bind("Debug", "QuitAfterTest", false, "Quit the game when the test script finishes.");
        CataUi.Init(this, Config);

        NativeInput.Install();
        var harmony = new HarmonyLib.Harmony(Guid);
        GameShots.Install(harmony);
        InputTrace.Install(harmony);
        NoEdgeScroll.Install(harmony);
        WorldSelect.Install(harmony);
        PopoutClicks.Install(harmony);
        ClassInjector.RegisterTypeInIl2Cpp<VrDriver>();
        var go = new GameObject("CataVr Driver");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<VrDriver>();
        Log.LogInfo("Cataclismo VR loaded");
    }

    // The plugin ID was scrire.catavr before the DVR rename, so its settings (scale, UI mode,
    // panel sizes) are in scrire.catavr.cfg: copy them over once, the first time the new file
    // is missing. The old file is left where it is.
    void CarryOverOldConfig()
    {
        try
        {
            var old = Path.Combine(Paths.ConfigPath, "scrire.catavr.cfg");
            if (File.Exists(Config.ConfigFilePath) || !File.Exists(old)) return;
            File.Copy(old, Config.ConfigFilePath);
            Config.Reload();
            Log.LogInfo($"Settings carried over from {Path.GetFileName(old)} (the plugin's old ID) to {Path.GetFileName(Config.ConfigFilePath)}");
        }
        catch (Exception e) { Log.LogWarning($"Couldn't carry over the old settings file: {e.Message}"); }
    }

    internal string PluginDir => Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
}

// Injected MonoBehaviour: our per-frame entry point.
public class VrDriver : MonoBehaviour
{
    public VrDriver(IntPtr ptr) : base(ptr) { }

    void Update()
    {
        try
        {
            Vr.Frame();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"VR frame: {e}");
        }
    }
}

static class Vr
{
    static float nextLog;
    static VrSession session;
    static CataRig rig;

    public static void Frame()
    {
        var cfg = Plugin.Instance;
        Game.Refresh();
        if (cfg.ReconLogging.Value)
            VrRecon.Tick(() => rig != null ? rig.ModeName : "no VR", new[] { "GameplayUI(Clone)" }, sb =>
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                sb.AppendLine($"[recon]   mouse {Input.mousePosition} main {(Camera.main != null ? Camera.main.name : "-")} over UI {(es != null && es.IsPointerOverGameObject())}");
            });

        // Retry every 5 s until the headset is connected (VrBoot), on Direct3D 11 only.
        if (session == null && cfg.VrEnabled.Value && VrBoot.StartDue() && VrBoot.GraphicsOk()) Start(cfg);
        if (session == null) return;

        VrBoot.UncapFrameRate(session);
        VrBoot.KeepFocus();
        HandleKeys(cfg);
        // The render thread submits last frame's eyes and paces the next.
        rig.QueuePreviousFrame();
        SimShots.CaptureIfRequested(rig.LeftTarget);
        session.FlipVertical = cfg.FlipVertical.Value;
        session.BeginFrame();
        // SteamVR's keyboard for the game's text fields (TextMesh Pro: save names, the level editor).
        VrUiKit.VrKeyboard.Update(session);
        if (session.Simulated && cfg.RunTestScript.Value) SimScript.Update(session, rig, cfg);
        CataUi.UpdateInput(session, rig);
        rig.Update();
        CataCapture.Tick();
        MistProbe.Update(rig.Eyes, rig.InWorld);
        SpectatorCam.Update(rig, session.Head, rig.Eyes[0], rig.InWorld);
        VrPointer.Update(session, rig, cfg);
        Recorder.Update();
        session.Left.EndFrame();
        session.Right.EndFrame();

        if (Time.unscaledTime >= nextLog)
        {
            nextLog = Time.unscaledTime + 5f;
            Plugin.Logger.LogInfo($"[pointer] onMenu {VrPointer.OnMenu} mouse {NativeInput.MousePixel} | ray calls {NativeInput.RayCalls}, overridden {NativeInput.RayOverrides} | game hit {VrPointer.WorldHit}");
            NativeInput.RayCalls = NativeInput.RayOverrides = 0;
            if (cfg.DevTools.Value) LogPerf();
        }
    }

    // Every 5 s: the game's frame rate and SteamVR's own count of what it showed (presented,
    // reprojected, dropped). Reprojected frames smear moving see-through things like the mist.
    static int perfFrames;
    static float perfAt;
    static Valve.VR.Compositor_CumulativeStats perfLast;

    static void LogPerf()
    {
        float now = Time.unscaledTime;
        int frames = Time.frameCount;
        float fps = perfAt > 0 ? (frames - perfFrames) / (now - perfAt) : 0;
        perfFrames = frames;
        perfAt = now;
        var line = $"[perf] game {fps:F0} fps";
        try
        {
            if (session.Compositor != null && !session.Simulated && !session.PacingPaused && !VrQuit.Quitting)
            {
                var s = new Valve.VR.Compositor_CumulativeStats();
                session.Compositor.GetCumulativeStats(ref s, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Valve.VR.Compositor_CumulativeStats>());
                if (perfLast.m_nNumFramePresents > 0 && s.m_nPid == perfLast.m_nPid)
                    line += $" | SteamVR: {s.m_nNumFramePresents - perfLast.m_nNumFramePresents} presented, " +
                        $"{s.m_nNumReprojectedFrames - perfLast.m_nNumReprojectedFrames} reprojected, " +
                        $"{s.m_nNumDroppedFrames - perfLast.m_nNumDroppedFrames} dropped, {s.m_nNumFrameSubmits - perfLast.m_nNumFrameSubmits} submits";
                perfLast = s;
            }
        }
        catch (Exception e) { line += $" | SteamVR stats unavailable: {e.Message}"; }
        Plugin.Logger.LogInfo(line);
    }

    static void Start(Plugin cfg)
    {
        // The core checks Direct3D 11; this game also needs Unity's render thread (the eyes are submitted from it).
        if (SystemInfo.renderingThreadingMode == RenderingThreadingMode.Direct)
        {
            Plugin.Logger.LogError("VR needs Unity's render thread: launch without -force-gfx-direct. VR disabled.");
            VrBoot.GiveUp();
            return;
        }
        session = cfg.SimulateVr.Value ? VrSession.StartSimulated(1832, 1920) : VrSession.TryStart(cfg.PluginDir, allowThreaded: true);
        if (session == null) return;
        rig = new CataRig(session, cfg);
        // SteamVR's keyboard into the game's fields key by key (CataKeyboard).
        VrUiKit.VrKeyboard.Fields = new CataKeyboardFields(() => session);
        // Sims don't write zoom, pinch or table moves to the config.
        rig.Table.SaveChanges = !session.Simulated;
        // Record > Diorama: the capture for the game's page, with Cataclismo's freeze, hide and framing.
        CataCapture.Offer(cfg, rig);
    }

    static void HandleKeys(Plugin cfg)
    {
        if (Input.GetKeyDown(KeyCode.PageUp)) SetScale(cfg, rig.Scale / 1.25f);
        if (Input.GetKeyDown(KeyCode.PageDown)) SetScale(cfg, rig.Scale * 1.25f);
        if (Input.GetKeyDown(KeyCode.F8)) rig.Recenter();
        if (Input.GetKeyDown(KeyCode.F9)) { SceneDump.Log(rig.Eyes); MistProbe.Log(rig.Eyes); }
    }

    static void SetScale(Plugin cfg, float scale)
    {
        var t = rig.Table.Tuning;
        rig.Scale = Mathf.Clamp(scale, t.MinScale, t.MaxScale);
        rig.Table.SaveScale();
        Plugin.Logger.LogInfo($"Scale {rig.Scale:F1} (100 world units = {100f / rig.Scale:F2} m)");
    }
}
