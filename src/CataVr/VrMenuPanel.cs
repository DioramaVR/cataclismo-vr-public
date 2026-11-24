using System;
using System.Collections.Generic;
using UnityEngine;
using NVec = System.Numerics.Vector3;

namespace CataVr;

// Shows the game's screen-space UI in VR through the core's ScreenUiPanel:
// every root screen-space canvas (overlay, or on the game's own GUI camera, as
// the in-level HUD is) is moved onto a hidden UI camera that renders into a
// screen-sized texture, so the game's layout, scaling and input code see
// exactly the same pixel space as on the monitor. This adds Cataclismo's
// placement, URP renderer and the laser / sim mapping between panel and pixels.
static class VrMenuPanel
{
    // Panel placement in tracking space (metres). In the diorama: from config,
    // above the table, tilted back slightly (the HUD sits at the edges and the
    // middle is transparent). In cinema mode: a big screen straight ahead, the
    // same size and place as the core's VrCinema game view, which it overlays.
    static NVec PanelCentre(CataRig rig) => !rig.InWorld
        ? CinemaCentre
        : new(0, Plugin.Instance.MenuHeight.Value, Plugin.Instance.MenuDistance.Value);
    static float PanelWidth(CataRig rig) => !rig.InWorld ? CinemaWidth : Plugin.Instance.MenuWidth.Value;
    static float PanelTilt(CataRig rig) => !rig.InWorld ? 0f : -10f;

    public static readonly NVec CinemaCentre = new(0, 1.45f, 2.2f);
    public const float CinemaWidth = 3.0f;

    // The UI camera must sit near the origin: the game's soft masks (scrolling
    // lists) compute clipping from world-space vertex positions, which falls
    // apart at large coordinates. Its layer (UI) is excluded from the eye cameras.
    static readonly Vector3 UiCameraPosition = new(0, -60, 0);

    static ScreenUiPanel panel;
    static CataRig lastRig;

    public static bool Visible => panel != null && panel.Visible;
    public static RenderTexture Texture => panel?.Texture;
    public static Camera UiCamera => panel?.UiCamera;
    public static IReadOnlyList<Canvas> Canvases => panel != null ? panel.Canvases : Array.Empty<Canvas>();
    // Set by the VR UI kit (CataUi): false while its own windows show the UI instead.
    public static bool ShowFull { set { if (panel != null) panel.ShowFull = value; } }

    public static void Update(CataRig rig)
    {
        lastRig = rig;
        if (panel == null)
        {
            var cfg = Plugin.Instance;
            panel = new ScreenUiPanel(cfg.MenuWidth, cfg.MenuHeight, cfg.MenuDistance)
            {
                // The HUD's canvas is on the game's GUI camera; menus are overlays.
                IsGameUiCamera = cam => !cam.name.StartsWith("VrUi") && !cam.name.StartsWith(VrCore.Name),
                PrepareUiCamera = PrepareUiCamera,
                Placement = () => new PanelPlacement(new Pose(PanelCentre(rig), Pose.Pitch(PanelTilt(rig))), PanelWidth(rig)),
                // The user can carry it in the diorama; the cinema screen stays put (the game view is drawn there).
                PlaceKey = () => !rig.InWorld ? null : "Panel",
                UiCameraPosition = UiCameraPosition,
                // Scanning every object for new canvases is slow on a scene this size.
                ScanInterval = 2f,
            };
        }
        // Re-rendering the full-screen UI every frame costs a lot on the single
        // render thread; menus look fine refreshed every few frames.
        panel.RefreshEveryFrames = Math.Max(1, Plugin.Instance.MenuRefreshEveryFrames.Value);
        panel.Update(rig, rig.Active);
        UseRendererForScene();
    }

    // Tracking-space point on the panel for a screen pixel (for scripted aiming).
    public static NVec PixelToTracking(Vector2 pixel)
    {
        var place = Current(lastRig);
        float width = place.Width, height = width * Texture.height / Texture.width;
        var local = new NVec((pixel.x / Screen.width - 0.5f) * width, (pixel.y / Screen.height - 0.5f) * height, 0);
        return place.Pose.Position + NVec.Transform(local, place.Pose.Rotation);
    }

    // World pose of the panel (rotation only, unscaled).
    public static Pose PanelPose(CataRig rig) => rig.TrackingToWorld(Current(rig).Pose);

    // Where the panel is in tracking space: where the user carried it, else the default.
    static PanelPlacement Current(CataRig rig) => panel != null && panel.Current.Width > 0
        ? panel.Current
        : new PanelPlacement(new Pose(PanelCentre(rig), Pose.Pitch(PanelTilt(rig))), PanelWidth(rig));

    // Where a world ray hits the panel, as a screen pixel (y up) plus distance along the ray.
    public static bool Raycast(CataRig rig, NVec origin, NVec dir, out Vector2 pixel, out float distance)
    {
        pixel = default;
        distance = 0;
        if (!Visible) return false;
        var pose = PanelPose(rig);
        var normal = NVec.Transform(NVec.UnitZ, pose.Rotation);
        float denom = NVec.Dot(dir, normal);
        if (MathF.Abs(denom) < 1e-5f) return false;
        distance = NVec.Dot(pose.Position - origin, normal) / denom;
        if (distance <= 0) return false;
        var hit = origin + dir * distance;
        var local = NVec.Transform(hit - pose.Position, System.Numerics.Quaternion.Inverse(pose.Rotation)) / rig.WorldScale;
        float width = Current(rig).Width, height = width * Texture.height / Texture.width;
        float u = local.X / width + 0.5f, v = local.Y / height + 0.5f;
        if (u < 0 || u > 1 || v < 0 || v > 1) return false;
        pixel = new Vector2(u * Screen.width, v * Screen.height);
        return true;
    }

    // HDR, MSAA and post-processing go through targets without alpha, which
    // would make the panel opaque black; so does any of the game's 3D renderers.
    static void PrepareUiCamera(Camera cam)
    {
        cam.allowHDR = false;
        cam.allowMSAA = false;
        var data = cam.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>()
            ?? cam.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
        data.renderPostProcessing = false;
        data.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.None;
        uiCameraData = data;
        pickedFor = IntPtr.Zero;
        UseRendererForScene();
    }

    // The renderer PickUiRenderer finds draws nothing at all in the camp between
    // levels (its Macro scene: the tutorial card blocks every click, invisibly).
    // There the game's WarmapUI renderer draws its UI.
    static int uiRenderer = -1, campRenderer = -1;
    static int? usedRenderer;
    static UnityEngine.Rendering.Universal.UniversalAdditionalCameraData uiCameraData;

    // A renderer index belongs to one URP asset, and the game switches assets with
    // the scene (URP_Asset_MainMenu, URP_Asset, URP_Asset_MacroMenu, ...; since its
    // 2026-11-12 update the main menu's has no UI renderer). So pick again whenever
    // the active asset changes, not once at start.
    static IntPtr pickedFor;

    static void UseRendererForScene()
    {
        if (uiCameraData == null) return;
        var asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
        var ptr = asset != null ? asset.Pointer : IntPtr.Zero;
        if (ptr != pickedFor)
        {
            pickedFor = ptr;
            Plugin.Logger.LogInfo($"[menu] URP asset now '{(asset != null ? asset.name : "-")}'");
            uiRenderer = PickUiRenderer();
            campRenderer = FindRenderer("WarmapUI");
            usedRenderer = null;
        }
        int want = Camp.InCamp && campRenderer >= 0 ? campRenderer : uiRenderer;
        if (want < 0 || usedRenderer == want) return;
        usedRenderer = want;
        uiCameraData.SetRenderer(want);
        Plugin.Logger.LogInfo($"[menu] UI camera uses renderer {want}{(want == campRenderer ? " (camp)" : "")}");
    }

    static int FindRenderer(string suffix)
    {
        var asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.TryCast<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>();
        if (asset == null) return -1;
        var list = asset.m_RendererDataList;
        for (int i = 0; i < list.Length; i++)
            if (list[i] != null && list[i].name.EndsWith(suffix)) return i;
        return -1;
    }

    // The UI camera, the kit's menu, bars and labels all draw layer 5 (UI) only, with
    // one renderer, which must draw layer 5 and no 3D layers: the game's 3D renderers
    // run full-screen passes that leave the texture opaque black, and a renderer with
    // no layers (the DearImgui cheat ones) draws nothing at all. The game's GameUI
    // renderer is the one (layers 0x20). Since the 2026-11-12 update only URP_Asset
    // (the levels) lists it: URP_Asset_MainMenu has just its 3D renderer and an empty
    // slot, URP_Asset_MacroMenu its 3D one and a cheat one. There it's put into the
    // asset's list (the empty slot, else one more), for this session only; URP makes
    // a renderer for a new entry the first time a camera asks for it.
    static bool IsUiOnly(UnityEngine.Rendering.Universal.ScriptableRendererData d)
    {
        var urd = d?.TryCast<UnityEngine.Rendering.Universal.UniversalRendererData>();
        if (urd == null) return false;
        // The core's Popout adds its spare layers (in Cataclismo the unnamed 3 and 6) to the
        // UI camera's renderer: GameUI is 0x68 then. Those don't make it a 3D renderer.
        int all = urd.opaqueLayerMask.value | urd.transparentLayerMask.value;
        if ((all & ~VrUiKit.VrUi.PopoutLayers) == 1 << 5) return true;
        // While the core looks for its layers again (the asset changed), its list can be empty
        // for a moment: the game's own GameUI still counts (UI, and not the 3D Default layer).
        return urd.name.EndsWith("GameUI") && (all & (1 << 5)) != 0 && (all & 1) == 0;
    }

    static UnityEngine.Rendering.Universal.UniversalRendererData gameUi;

    static UnityEngine.Rendering.Universal.UniversalRendererData FindGameUi()
    {
        if (gameUi != null) return gameUi;
        UnityEngine.Rendering.Universal.UniversalRendererData any = null;
        foreach (var d in Resources.FindObjectsOfTypeAll<UnityEngine.Rendering.Universal.UniversalRendererData>())
        {
            if (!IsUiOnly(d)) continue;
            if (d.name.EndsWith("GameUI")) return gameUi = d;
            any ??= d;
        }
        return gameUi = any;
    }

    static int PickUiRenderer()
    {
        var asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.TryCast<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>();
        if (asset == null) return -1;
        var list = asset.m_RendererDataList;
        int pick = -1, empty = -1;
        for (int i = 0; i < list.Length; i++)
        {
            var urd = list[i]?.TryCast<UnityEngine.Rendering.Universal.UniversalRendererData>();
            Plugin.Logger.LogInfo($"[menu] URP renderer {i}: {(list[i] != null ? list[i].name : "-")}{(i == asset.m_DefaultRendererIndex ? " (default)" : "")}" +
                (urd != null ? $", layers opaque 0x{urd.opaqueLayerMask.value:X8} transparent 0x{urd.transparentLayerMask.value:X8}" : ""));
            if (pick < 0 && IsUiOnly(list[i])) pick = i;
            if (empty < 0 && list[i] == null) empty = i;
        }
        if (pick >= 0)
        {
            Plugin.Logger.LogInfo($"[menu] UI camera uses renderer {pick} ({list[pick].name})");
            return pick;
        }
        var ui = FindGameUi();
        if (ui == null)
        {
            Plugin.Logger.LogWarning("[menu] no UI-only renderer in this URP asset or loaded: the UI camera keeps the default, which can leave the panel black");
            return -1;
        }
        try
        {
            if (empty >= 0) list[empty] = ui;
            else
            {
                var grown = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Rendering.Universal.ScriptableRendererData>(list.Length + 1);
                for (int i = 0; i < list.Length; i++) grown[i] = list[i];
                grown[list.Length] = ui;
                empty = list.Length;
                asset.m_RendererDataList = grown;
            }
            Plugin.Logger.LogInfo($"[menu] '{asset.name}' has no UI-only renderer: added {ui.name} as renderer {empty}; the UI camera uses it");
            return empty;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[menu] couldn't add {ui.name} to '{asset.name}': {e.Message}; the UI camera keeps the default");
            return -1;
        }
    }
}
