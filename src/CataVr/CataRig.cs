using System;
using DigitalSun.Castle;
using UnityEngine;
using NVec = System.Numerics.Vector3;

namespace CataVr;

// Cataclismo on the core's shared table (TableRig, vr-urz.3): Diorama and
// Turntable with the standard controls; Cinema (menus, loading) is the core's.
// What stays Cataclismo's:
// - the host: the game camera's pivot and yaw, moving the pivot, and the pan
//   (the game's own W A S D, held by VrPointer);
// - turning and zooming go through the game (Q / E and the wheel, which its
//   tutorial waits for), and the table follows the game's yaw and zoom;
// - while the game frames a shot of its own (GameShots), the table re-centres
//   on it, height and yaw included;
// - the layer our visuals go on.
// The rest of the mod talks to this class in tracking-space System.Numerics
// vectors, as it did to its own rig.
sealed class CataRig : ITrackingSpace
{
    public readonly TableRig Table;
    readonly CataHost host = new();

    public CataRig(VrSession vr, Plugin cfg)
    {
        // The core's objects (the VR UI kit's menu, the hands) go on the same layer.
        VrCore.VisualLayer = VisualLayer = PickFreeLayer();
        Table = new TableRig(vr, cfg.Config, host)
            .Table(t => { t.TableHeight = 0.75f; t.TableDistance = 0.6f; t.FollowGameCamera = true; },
                e => { e.EyeResolution = 1f; e.EyeShadows = true; e.DesktopView = false; e.EyePostProcessing = true; },
                new OldKey("TableHeight", "VR", "TableHeight", 0.9f),
                new OldKey("TableDistance", "VR", "TableDistance", 0.6f),
                new OldKey("FollowGameCamera", "VR", "FollowFocalPoint", true),
                new OldKey("DesktopView", "VR", "DesktopView", false),
                new OldKey("EyeResolution", "VR", "EyeResolution", 1f),
                new OldKey("EyeShadows", "VR", "EyeShadows", true),
                new OldKey("EyePostProcessing", "VR", "EyePostProcessing", true))
            // Scale 80 world units a metre and the 2 - 2000 range, as before.
            .Offer(RigMode.Diorama, t => { t.Scale = 80f; t.MinScale = 2f; t.MaxScale = 2000f; },
                new[] { "Laser", "Game" },
                new OldKey("Scale", "VR", "Scale", 80f));
        // Turntable isn't offered for now (2026-11-06: no circle, dark views and broken
        // rendering in the headset; still being looked into). To bring it back:
        // .Offer(RigMode.Turntable, t => { t.Scale = 80f; t.MinScale = 2f; t.MaxScale = 2000f; }, new[] { "Laser", "Game" })
        LockToOfferedMode(cfg.Config);
        Plugin.Logger.LogInfo($"[rig] on the core's TableRig: modes {string.Join(", ", Table.Offered)}");
    }

    // A saved mode that isn't offered (Turntable from an earlier build, or anything
    // unknown) is replaced in the config by Diorama, once, so the choice is honest.
    static void LockToOfferedMode(BepInEx.Configuration.ConfigFile config)
    {
        if (!config.TryGetEntry<string>("VrUi", "ViewMode", out var entry)) return;
        var saved = entry.Value;
        if (saved == nameof(RigMode.Diorama)) return;
        entry.Value = nameof(RigMode.Diorama);
        Plugin.Logger.LogInfo($"[rig] saved view mode '{saved}' isn't offered in Cataclismo; set to Diorama");
    }

    // ---- each frame ----

    // Zoom input was sent to the game: follow its camera's zoom for a moment
    // (the game eases its zoom after the input stops).
    public void FollowGameZoom() => host.FollowZoomUntil = Time.unscaledTime + 1f;

    public void Update()
    {
        host.BeginFrame();
        bool inWorld = Table.InWorld;
        if (inWorld && Game.TryGetView(out var focal, out var gameYaw))
        {
            if (Table.Dragging) GameShots.Cancel();
            GameShots.Track(focal, gameYaw, Game.Gameplay.IsFollowingTarget());
            // The game is framing a shot: centre it on the table, at its height,
            // seen from the side the game camera looks from.
            if (GameShots.Active) Table.Recenter();
        }
        Table.Update();
    }

    // This frame's table sticks (turn, zoom), which VrPointer turns into the game's Q / E and wheel.
    public TableSticks Sticks => host.LastSticks;

    // Where the core asked to pan this frame (world units, sideways), for the game's W A S D; zero if not.
    public Vector3 PanWanted => host.PanWanted;

    // Threaded rendering: hand the previous frame to the render thread.
    public void QueuePreviousFrame() => Table.QueuePreviousFrame();

    // ---- read back, in the mod's tracking-space terms ----

    public bool Active => Table.Active;
    public bool InWorld => Table.InWorld;
    public string ModeName => InWorld ? Table.Mode.ToString() : "Cinema";
    public bool Turntable => Table.InWorld && Table.Mode == RigMode.Turntable;
    public float Scale { get => Table.Scale; set => Table.Scale = value; }
    public float WorldScale => Table.WorldScale;
    public float Yaw => Table.Yaw;
    public NVec Anchor => ToN(Table.Anchor);
    public NVec TableAnchor => ToN(Table.TableCentre);
    public Camera[] Eyes => new[] { Table.Eye(0), Table.Eye(1) };
    public RenderTexture LeftTarget => Table.Left;

    public NVec TrackingToWorld(NVec t) => ToN(Table.TrackingToWorld(new Vector3(t.X, t.Y, t.Z)));
    public NVec WorldToTracking(NVec w) => ToN(Table.WorldToTracking(new Vector3(w.X, w.Y, w.Z)));
    public Pose TrackingToWorld(Pose p) => Table.TrackingToWorld(p);

    public void Recenter() => Table.Recenter();
    public void CancelGripDrag() => Table.CancelGripDrag();

    static NVec ToN(Vector3 v) => new(v.x, v.y, v.z);

    // ---- our visuals' layer ----

    // Everything we draw (hands, laser, panels) lives on an unused layer, which
    // the eye cameras always render and the game's cameras never do.
    public static int VisualLayer { get; private set; } = 31;

    // The game names almost every layer, and its URP renderers only draw the
    // layers in their opaque/transparent masks (its unnamed layers 3 and 6
    // aren't drawn). Prefer an unnamed layer every renderer draws, else Default.
    static int PickFreeLayer()
    {
        int drawn = ~0;
        foreach (var data in Resources.FindObjectsOfTypeAll<UnityEngine.Rendering.Universal.UniversalRendererData>())
        {
            Plugin.Logger.LogInfo($"Renderer '{data.name}': opaque layers 0x{data.opaqueLayerMask.value:X8}, transparent 0x{data.transparentLayerMask.value:X8}");
            drawn &= data.opaqueLayerMask.value & data.transparentLayerMask.value;
        }
        for (int i = 31; i > 0; i--)
            if (string.IsNullOrEmpty(LayerMask.LayerToName(i)) && (drawn & (1 << i)) != 0)
            {
                Plugin.Logger.LogInfo($"VR visuals on unused layer {i}");
                return i;
            }
        Plugin.Logger.LogInfo("No unnamed layer is drawn by the game's renderers; VR visuals on Default");
        return 0;
    }
}

// The game's side of the table: its camera's pivot and yaw.
sealed class CataHost : PivotHost
{
    public override Camera GameCamera => Game.MainCamera;
    // A level (or the main menu's backdrop) with a live gameplay camera; otherwise Cinema.
    public override bool InWorld => Game.MainCamera != null && Game.Gameplay != null && Game.Gameplay.isActiveAndEnabled;

    public override bool TryGetPivot(out Vector3 focal, out float yaw)
    {
        bool ok = Game.TryGetView(out var f, out yaw);
        focal = new Vector3(f.X, f.Y, f.Z);
        return ok;
    }

    // A grip drag moves the game camera's pivot sideways (its xz rig), so the
    // game's view, culling and rays stay with the table.
    public override Vector3 SetPivot(Vector3 focal, float yaw)
    {
        var gameplay = Game.Gameplay;
        var xz = gameplay != null ? gameplay.xzMovementTransform : null;
        if (xz == null) return focal;
        var pivot = gameplay.GetPivotPosition();
        var delta = new Vector3(focal.x - pivot.x, 0, focal.z - pivot.z);
        if (delta.sqrMagnitude > 1e-8f) xz.position += delta;
        return new Vector3(focal.x, pivot.y, focal.z);
    }

    // The game turns with its own Q / E and zooms with its wheel (VrPointer presses them from these
    // sticks), so the rig skips its own turn and zoom; the table follows the game's yaw and zoom.
    public override bool GameTurns => true;
    public override TableInputs Sticks(TableSticks sticks)
    {
        LastSticks = sticks;
        return TableInputs.Turn | TableInputs.Zoom;
    }
    public TableSticks LastSticks { get; private set; }

    // The table scales with the game camera's zoom while the game zooms (the wheel, a shot,
    // the map view); null in between, so the game pulling its camera back over high
    // terrain while panning doesn't rescale it.
    public float FollowZoomUntil;
    public override float? GameZoomDistance =>
        Time.unscaledTime < FollowZoomUntil || GameShots.Active ? Game.ZoomDistance : null;

    // The game pans with its own W A S D: VrPointer holds the keys nearest this direction.
    public override bool Pan(Vector3 worldDelta)
    {
        PanWanted = worldDelta;
        return true;
    }

    public Vector3 PanWanted { get; private set; }
    public void BeginFrame()
    {
        PanWanted = Vector3.zero;
        LastSticks = default;
    }
}

// The game objects VR cares about, looked up rarely and cached.
static class Game
{
    static GameplayCamera gameplay;
    static LevelManager level;
    static float nextLookup;

    public static void Refresh()
    {
        if (Time.unscaledTime < nextLookup && gameplay != null) return;
        nextLookup = Time.unscaledTime + 1f;
        if (gameplay == null)
        {
            gameplay = UnityEngine.Object.FindObjectOfType<GameplayCamera>();
            if (gameplay == null && Camera.main != null) gameplay = Camera.main.GetComponentInParent<GameplayCamera>();
            if (gameplay != null) Plugin.Logger.LogInfo($"Gameplay camera found: '{gameplay.name}', camera '{(gameplay.myCamera != null ? gameplay.myCamera.name : "-")}'");
        }
        if (level == null) level = UnityEngine.Object.FindObjectOfType<LevelManager>();
        if (minimap == null && level != null)
        {
            // Diagnostic: our visuals (on VisualLayer) may show up in the minimap's own camera.
            minimap = UnityEngine.Object.FindObjectOfType<DigitalSun.Castle.UI.MinimapPanelController>();
            var cam = minimap != null ? minimap.minimapCamera : null;
            if (cam != null)
                Plugin.Logger.LogInfo($"[map] minimap camera '{cam.name}': mask 0x{cam.cullingMask:X8} (VR layer {CataRig.VisualLayer} {((cam.cullingMask & (1 << CataRig.VisualLayer)) != 0 ? "drawn" : "not drawn")}), " +
                    $"ortho {cam.orthographic} size {cam.orthographicSize}, target {(cam.targetTexture != null ? cam.targetTexture.name : "-")}, enabled {cam.enabled}");
        }
    }

    static DigitalSun.Castle.UI.MinimapPanelController minimap;

    public static GameplayCamera Gameplay => gameplay;
    public static Camera MainCamera => gameplay != null ? gameplay.myCamera : null;
    public static LevelManager Level => level;
    public static bool LevelReady => level != null && level.isLevelReadyToBePlayed;

    // How far the game camera sits from its pivot (its zoom), 0 if unknown.
    public static float ZoomDistance
    {
        get
        {
            var cam = MainCamera;
            return gameplay != null && cam != null ? Vector3.Distance(cam.transform.position, gameplay.GetPivotPosition()) : 0f;
        }
    }

    // The game camera's pivot and the direction it faces.
    public static bool TryGetView(out NVec focal, out float yaw)
    {
        focal = default;
        yaw = 0;
        var cam = MainCamera;
        if (gameplay == null || cam == null) return false;
        var f = gameplay.GetPivotPosition();
        focal = new NVec(f.x, f.y, f.z);
        yaw = cam.transform.eulerAngles.y;
        return true;
    }
}
