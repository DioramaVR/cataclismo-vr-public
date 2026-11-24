using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using VrUiKit;

namespace CataVr;

// Cataclismo's side of the shared VR UI kit (dvr-unity-core): the controller
// actions (rebindable on the VR menu's Controls tab), the settings rows, and
// the IVrUiHost the kit draws through. Both grips + both triggers (the chord)
// opens the VR menu. In a level the HUD can be split into windows (Arranged / Free modes).
sealed class CataUi : IVrUiHost
{
    public static readonly CataUi Instance = new();
    VrSession vr;
    CataRig rig;

    public const string Click = "click", RightMouse = "mouse.right", MiddleMouse = "mouse.middle",
        Pause = "pause", Back = "back",
        MoveWindow = "window.move", HandGrabWindow = "window.handgrab";

    // The HUD windows, for filtering Popout clicks (PopoutClicks).
    public static readonly string[] WindowIds = { "resources", "time", "quests", "squads", "build", "selected", "focus" };
    // The pieces drawn in the world or at the laser (cut out of the panel there): PopoutClicks.
    public static UiWindowDef PointersPiece, HoverPiece;

    public static void Init(Plugin cfg, ConfigFile config)
    {
        VrUi.Init(config, "catavr-layouts");
        // Arranged and Free windows can pop out: each drawn on its own (core VrPopout), the default there.
        VrUi.Offer(VrUi.UiMode.Arranged, VrUi.UiMode.Free);
        VrUi.OfferPopout();
        // The level HUD as windows (Arranged / Free UI modes), measured at
        // 1920x1080: resources top left, time controls top centre, quests and
        // the menu buttons top right, the squads down the right, the build
        // panel bottom left, the selected unit's commands bottom centre.
        // Pop-ups, notifications and the time-anchor screen go in the pop-ups window.
        VrUi.RegisterWindow(new UiWindowDef { Id = "resources", Title = "Resources", Roots = () => Find("ResourcesAndMainEntityStatusPanel") });
        VrUi.RegisterWindow(new UiWindowDef { Id = "time", Title = "Time", Roots = () => Find("TimePanel") });
        VrUi.RegisterWindow(new UiWindowDef { Id = "quests", Title = "Quests and menu", Roots = () => Find("QuestsPanel", "OptionsMenuButtonPanel") });
        VrUi.RegisterWindow(new UiWindowDef { Id = "squads", Title = "Squads", Roots = () => Find("QuickSquadSelectionPanel", "HelpPanel") });
        VrUi.RegisterWindow(new UiWindowDef { Id = "build", Title = "Build", Roots = () => Find("PieceSelectionPanel/Piece Selection Section", "PieceSelectionPanel/Demolish Section", "PieceSelectionPanel/ProsperityPanel") });
        VrUi.RegisterWindow(new UiWindowDef { Id = "selected", Title = "Selected", Roots = () => Find("SelectedEntityManagementPanel") });
        VrUi.RegisterWindow(new UiWindowDef { Id = "focus", Title = "Pop-ups", Remainder = true });
        // Things drawn where something in the world or the mouse is on the
        // game's screen: screen-sized sparse windows over the panel's spot,
        // showing only what they draw (pointers to incoming waves and other
        // targets; hover info and key hints by the mouse).
        // The game pins pointers to off-screen targets to the screen edge: in the
        // world each goes to its target instead of back through the camera.
        VrUi.RegisterWindow(PointersPiece = new UiWindowDef
        {
            Id = "pointers", Title = "Wave pointers", Kind = UiPieceKind.World, Roots = VrUi.Named("ScreenPointersPanel"),
            Anchor = PointerTarget,
        });
        VrUi.RegisterWindow(HoverPiece = new UiWindowDef { Id = "hover", Title = "Hover info", Kind = UiPieceKind.Mouse, Roots = VrUi.Named("PawnHoverInformationPanel", "PointerFollowingHintDisplay") });
        // The drag-select box (outlined on the table instead), the pause
        // border, the cinematic bars, the saving icon and the version label
        // don't belong in a window.
        VrUi.Exclude("RectangleSelectorPanel", "TimestateVisualsPanel", "NarrativeBarsPanel", "SavingIndicatorPanel", "Versioning");
        VrUi.RegisterLayout(DefaultLayout());
        VrPieces.GameCamera = () => Game.MainCamera;
        VrPieces.ScreenToWorld = WorldMouse.ScreenToGround;
        VrUiLog.MousePixel = () => NativeInput.Active ? NativeInput.MousePixel : null;
        VrMenu.Welcome("Cataclismo", Plugin.Version,
            "Aim the {laser side} laser at the floating panel and {press:click} to click the game's UI.",
            "Aim at the table to build: the game's mouse goes where the laser points.",
            "Terrain near the table can rise above it; drag the table (grip) or pan ({bind:pan.up,pan.down,pan.left,pan.right}) to clear the view.");
        RegisterHotkeys();
        VrInput.Init(config, "catavr-controls");
        VrInput.Register(Click, "Click (left mouse button)", "Laser", "Menus", "Pointer.Trigger");
        VrInput.Register(RightMouse, "Right mouse button (cancel)", "Laser", "Playing", "Pointer.Secondary");
        VrInput.Register(MiddleMouse, "Middle mouse button", "Laser", "Playing", "Pointer.Primary");
        VrInput.Register(Pause, "Pause (Space)", "Game", "Playing", "Off.Primary");
        VrInput.Register(Back, "Back / menu (Escape)", "Game", "Playing", "Off.Secondary");
        // The table's actions for a camera-pivot game: the off stick turns and zooms, the pointer
        // stick pans (Turntable: moves the circle); the game pans with its W A S D (CataHost.Pan).
        TableActions.RegisterTableModes("Playing");
        VrInput.Register(MoveWindow, "Grab a window", "Free mode windows", "Free", "Pointer.Grip");
        VrInput.Register(HandGrabWindow, "Grab a window by touching it", "Free mode windows", "Free", "Off.Grip");
        VrInput.Load();
        // The chord's grips start a table drag; undo it.
        VrInput.ChordFired += () => Instance.rig?.CancelGripDrag();

        // The new menu places these by label (core MenuModel): Controls' "you" section and
        // Game UI's "panel" section; the unit comes from the "(m)".
        VrSettings.Add(new ToggleSetting("Controls", "Left-handed", cfg.LeftHanded)
            .WithHelp("The laser on the left controller"));
        VrSettings.Add(new NumberSetting("Controls", "Laser angle", cfg.PointerPitch, 0, 90, 5, "0")
            .WithHelp("How far the laser tilts down from the controller, in degrees"));
        VrSettings.Add(new NumberSetting("Table", "Panel width (m)", cfg.MenuWidth, 0.8f, 2.4f, 0.1f, "0.0")
            .WithHelp("The game's menus and screens, on their panel"));
        VrSettings.Add(new NumberSetting("Table", "Panel height (m)", cfg.MenuHeight, 1.0f, 2.2f, 0.05f, "0.00")
            .WithHelp("The panel's centre above the floor"));
        VrSettings.Add(new NumberSetting("Table", "Panel distance (m)", cfg.MenuDistance, 0.6f, 2.5f, 0.1f, "0.0")
            .WithHelp("How far in front of you the panel floats"));
        VrSettings.PutOnTab("Table", "View");
        // Record tab: photos and video (the core's Recorder, from its spectator view), as in tpm-vr.
        Recorder.Init(config);
        MistProbe.Init();
        SpectatorCam.Init(config);
        AddHelp();
    }

    // Cataclismo's questions on the Help page (shown first, under the game's name).
    static void AddHelp()
    {
        MenuFaq.Add("How do I move, turn and zoom the map?",
            "{bind:pan.up,pan.down,pan.left,pan.right} moves across the map. {bind:turn.left,turn.right} turns the view, and {bind:zoom.in,zoom.out} zooms. The table follows the game's camera.");
        MenuFaq.Add("Which buttons do what?",
            "{bind:click} clicks. {bind:mouse.right} cancels or deselects, like the right mouse button, and {bind:mouse.middle} is the middle mouse button. {bind:pause} pauses the game and {bind:back} goes back or opens the game menu. You can change them on the Controls page.");
        MenuFaq.Add("The tutorial wants me to press keys. How?",
            "The sticks press the same keys. You can also turn on the Tutorial button bar under Hotkey bars on the Game UI page: it has W A S D, Q and E, and zoom. Point at a button and pull the trigger. The Play bar has pause, fast, build and camera reset.");
        MenuFaq.Add("Why did the table move by itself?",
            "When the game shows you something, like an event, a 'go to' or the map view, the table centres on it. Grab the table to stop that and take over.");
        MenuFaq.Add("Can I see what's in the next wave?",
            "Yes. Each incoming wave's details show above its marker all the time, so you don't need to hover over it.");
        MenuFaq.Add("Why is there no Turntable mode?",
            "Turntable is switched off in Cataclismo for now: it didn't draw the world properly here. Diorama is the only table mode.");
        MenuFaq.Add("Cliffs or dark areas block my view. What can I do?",
            "The whole world is drawn, so high ground near the table can stand above it. Turn the view, move across the map, or make the table bigger or lower.");
        MenuFaq.Add("How do I type a name?",
            "Point at the text box and pull the trigger: SteamVR's keyboard opens.");
    }

    // The hotkey bar: buttons that press the game's own keys, for the camera
    // tutorial (which waits for the keys themselves) and actions with no
    // controller button. Keys from the game's default bindings.
    static void RegisterHotkeys()
    {
        VrHotkeys.UseKeys((key, down) =>
        {
            if (key == "ScrollUp" || key == "ScrollDown")
            {
                if (down) HotkeyScroll += key == "ScrollUp" ? 1 : -1;
                return;
            }
            if (!System.Enum.TryParse<KeyCode>(key, out var k)) return;
            hotkeysUsed.Add(k);
            if (down) hotkeysHeld.Add(k);
            else hotkeysHeld.Remove(k);
        });
        VrHotkeys.Set("Tutorial")
            .Group("Pan", 0, 0).Button("W", "W", 1, 0).Button("A", "A", 0, 1).Button("D", "D", 2, 1).Button("S", "S", 1, 2).Set
            .Group("Rotate", 3.5f, 0).Button("Q", "Q", 0, 0).Button("E", "E", 1, 0).Set
            .Group("Zoom", 6, 0).Button("+", "ScrollUp", 0, 0).Button("-", "ScrollDown", 0, 1);
        VrHotkeys.Set("Play")
            .Group("Time", 0, 0).Button("Pause", "Space", 0, 0).Button("Fast", "F", 1, 0).Set
            .Group("Build", 2.5f, 0).Button("Build", "B", 0, 0).Button("Rebuild", "I", 1, 0).Set
            .Group("Camera", 5, 0).Button("Reset", "C", 0, 0).Button("Back", "Escape", 1, 0);
    }

    // What the bar holds down this frame and the wheel notches it sent, merged
    // by VrPointer with the controllers' own keys.
    static readonly HashSet<KeyCode> hotkeysHeld = new(), hotkeysUsed = new();
    public static float HotkeyScroll;
    public static bool HotkeyHeld(KeyCode key) => hotkeysHeld.Contains(key);
    public static IEnumerable<KeyCode> HotkeysUsed => hotkeysUsed;

    // Before the rig and the pointer read any actions.
    public static void UpdateInput(VrSession vr, CataRig rig)
    {
        Instance.vr = vr;
        Instance.rig = rig;
        VrInput.Update(Instance, vr.Left.State, vr.Right.State);
        // Before the rig reads the grips, so a grip that takes a window doesn't also move the table.
        var off = Instance.LeftHanded ? vr.Right : vr.Left;
        if (off.Valid) VrUi.HandGrab(off.Pose, VrInput.Pressed(HandGrabWindow), VrInput.Held(HandGrabWindow));
    }

    // A HUD panel under the level's GameplayUI, by its path below GameplayPanels.
    const string Panels = "GameplayUI(Clone)/GameplayPanels/";
    static IEnumerable<Transform> Find(params string[] paths)
    {
        foreach (var path in paths)
        {
            var go = GameObject.Find(Panels + path);
            if (go != null) yield return go.transform;
        }
    }

    // One pointer (a child of ScreenPointersPanel): where its target is in the
    // world. Some targets (quest "go here" points, spawners) sit at or under
    // the terrain, which hid the icon until zoomed far out: the icon floats
    // this far above the ground there (world units; a unit is about 1 tall).
    const float PointerLift = 3f;
    static Vector3? PointerTarget(Transform element)
    {
        // Our copies of the wave detail panel go above their pointers.
        if (WaveDetails.Anchor(element, PointerAnchor, out var detail)) return detail;
        return PointerAnchor(element);
    }

    static Vector3? PointerAnchor(Transform element)
    {
        var p = element.GetComponentInChildren<DigitalSun.Castle.UI.ScreenPointer>();
        if (p == null || p._targetTransform == null) return null;
        var target = p._targetTransform.position + p._positionOffset;
        float y = target.y;
        if (Game.Gameplay != null && Game.Gameplay.GetGroundHeightAt(target.x, target.z, out float ground)) y = Mathf.Max(y, ground);
        return new Vector3(target.x, y + PointerLift, target.z);
    }

    // Positions are chosen in the room (metres, origin at the play-space centre,
    // the default table centre at (0, 0.9, 0.6)) and stored relative to the
    // table, so they follow the table. Each window faces a standing player's
    // eyes. Size is metres per 1000 UI pixels.
    static UiLayout DefaultLayout()
    {
        var table = new System.Numerics.Vector3(0, 0.9f, 0.6f);
        var eye = new System.Numerics.Vector3(0, 1.5f, 0);
        var layout = new UiLayout { Name = "Default" };
        void Add(string id, System.Numerics.Vector3 room, float pivotX, float pivotY, float size)
        {
            var (yaw, pitch) = UiGeometry.Facing(room, eye);
            layout.Placements.Add(new UiPlacement
            {
                Window = id, Anchor = UiAnchor.Table, Position = room - table,
                Yaw = yaw, Pitch = pitch, Size = size, PivotX = pivotX, PivotY = pivotY,
            });
        }
        // Across the top, above the pop-ups: resources, time, quests and menu.
        Add("resources", new(-0.6f, 1.75f, 0.85f), 0.5f, 0, 0.9f);
        Add("time", new(0, 1.75f, 0.95f), 0.5f, 0, 0.9f);
        Add("quests", new(0.6f, 1.75f, 0.85f), 0.5f, 0, 0.9f);
        // The squads down the right side, growing towards the middle.
        Add("squads", new(0.9f, 1.35f, 0.5f), 1, 0.5f, 0.9f);
        // Low at the sides, like trays beside the table: build left, the selected unit's commands right.
        Add("build", new(-0.85f, 1.0f, 0.45f), 0, 0, 0.8f);
        Add("selected", new(0.8f, 1.0f, 0.4f), 1, 0, 0.7f);
        // Pop-ups in the middle, under the top row.
        Add("focus", new(0, 1.25f, 0.9f), 0.5f, 0.5f, 0.85f);
        return layout;
    }

    public static void Update()
    {
        WaveDetails.Update(Instance.Active && Instance.CanArrange);
        VrUi.Update(Instance);
    }

    public bool Active => rig != null && rig.Active;
    // The HUD windows are the level's; menus and the camp stay on the full panel.
    public bool CanArrange => rig != null && rig.Active && rig.InWorld;
    public Camera UiCamera => VrMenuPanel.UiCamera;
    public RenderTexture UiTexture => VrMenuPanel.Texture;
    public IReadOnlyList<Canvas> UiCanvases => new List<Canvas>(VrMenuPanel.Canvases);
    public Pose Head => vr.Head;
    public Pose LeftHand => vr.Left.Pose;
    public Pose RightHand => vr.Right.Pose;
    public bool LeftHanded
    {
        get => Plugin.Instance.LeftHanded.Value;
        set => Plugin.Instance.LeftHanded.Value = value;
    }
    public Vector3 TableCentre => new(rig.TableAnchor.X, rig.TableAnchor.Y, rig.TableAnchor.Z);
    public Pose TrackingToWorld(Pose p) => rig.TrackingToWorld(p);
    public float WorldScale => rig.WorldScale;
    public int VisualLayer => CataRig.VisualLayer;
    public Material MakeTexturedMaterial(Texture texture) => Primitives.MakeTextured(texture, transparent: true);

    // The kit's menu camera: the same URP renderer as our UI camera (no 3D
    // layers, keeps alpha), no HDR or MSAA.
    public void PrepareCamera(Camera cam)
    {
        if (cam.allowHDR) cam.allowHDR = false;
        if (cam.allowMSAA) cam.allowMSAA = false;
        var ui = VrMenuPanel.UiCamera;
        var source = ui != null ? ui.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>() : null;
        if (source == null) return;
        var data = cam.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>()
            ?? cam.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
        if (data.m_RendererIndex != source.m_RendererIndex) data.SetRenderer(source.m_RendererIndex);
        if (data.renderPostProcessing) data.renderPostProcessing = false;
    }

    public void ShowFullPanel(bool show) => VrMenuPanel.ShowFull = show;
    public string ConfigDirectory => BepInEx.Paths.ConfigPath;
    public void Log(string message) => Plugin.Logger.LogInfo(message);
}
