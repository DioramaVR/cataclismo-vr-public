using System;
using UnityEngine;
using VrUiKit;
using NVec = System.Numerics.Vector3;

namespace CataVr;

// Laser pointer on one controller. Pointing at the menu panel moves the
// game's mouse to that pixel; pointing at the diorama parks the mouse on a
// pixel with no UI and replaces the game's mouse rays with the laser
// (NativeInput). Controller buttons become mouse buttons and the game's own
// keys, so everything goes through the game's normal input code.
static class VrPointer
{
    static VrHandVisuals[] hands;
    static GameObject laser, hitMarker;
    static bool built;

    public static bool OnMenu { get; private set; }     // the game's UI panel
    public static bool OnVrMenu { get; private set; }   // the UI kit's VR menu (or a window frame, or a window being moved)
    public static bool HoldingWindow { get; private set; }   // Free UI mode: a window is on the laser
    public static Ray CurrentRay { get; private set; }
    // Where the game's own raycast through the laser landed (from the last frame).
    public static Vector3? WorldHit { get; private set; }

    static Vector2 lastMenuPixel = new(Screen.width * 0.5f, Screen.height * 0.5f);

    public static void Update(VrSession vr, CataRig rig, Plugin cfg)
    {
        PopoutClicks.Window = null;
        if (!rig.Active)
        {
            NativeInput.Active = NativeInput.RayActive = false;
            NativeInput.ReleaseAll();
            SetVisible(false);
            return;
        }
        if (!built) Build();
        var main = Game.MainCamera;
        // The ray icall receives Unity's native object, not the interop wrapper.
        NativeInput.MainCameraPtr = main != null ? main.m_CachedPtr : IntPtr.Zero;
        VrMenuPanel.Update(rig);
        CataUi.Update();
        // Cinema: the game's flat view on a big screen behind the panel, at the panel's place and Cinema width.
        var cinemaPose = VrMenuPanel.PanelPose(rig);
        Dvr.VrCinema.Update(rig.Active && !rig.InWorld, () => Game.MainCamera,
            cinemaPose.UnityPosition, cinemaPose.UnityRotation, VrMenuPanel.CinemaWidth);
        bool diorama = rig.InWorld;

        hands[0].Place(vr.Left, rig);
        hands[1].Place(vr.Right, rig);

        var hand = cfg.LeftHanded.Value ? vr.Left : vr.Right;
        if (!hand.Valid)
        {
            // No laser: give the mouse back to the desk.
            NativeInput.Active = NativeInput.RayActive = false;
            NativeInput.ReleaseAll();
            laser.SetActive(false);
            hitMarker.SetActive(false);
            return;
        }

        // Aim: controller pose tilted down by a configurable pitch (Touch poses point up along the ring).
        var aimTracking = hand.Pose.Compose(new Pose(NVec.Zero, Pose.Pitch(cfg.PointerPitch.Value)));
        var aim = rig.TrackingToWorld(aimTracking);
        var origin = aim.Position;
        var dir = NVec.Normalize(aim.Forward);

        float scale = rig.WorldScale;
        float length = 3f * scale;
        bool hit = false;
        Vector2 mousePixel;
        // Free UI mode: the pointer grip moves the window under the laser; the
        // holding hand's own stick pushes and sizes it (the core reads it).
        if (VrUi.GrabbingWithLaser)
        {
            if (VrInput.Held(CataUi.MoveWindow))
                VrUi.UpdateGrab(aimTracking, 0, 0, Time.unscaledDeltaTime);
            else VrUi.EndGrab();
        }
        // The VR menu and the HUD windows (Arranged / Free UI modes) take the
        // laser first; then the game's full UI panel; then the world.
        var uiHit = VrUi.Raycast(origin, dir);
        if (!VrUi.GrabbingWindow && VrInput.Pressed(CataUi.MoveWindow) && uiHit.Kind is UiHitKind.GameUi or UiHitKind.Frame
            && VrUi.BeginGrab(aimTracking))
            rig.CancelGripDrag();   // the same grip started a table drag this frame
        HoldingWindow = VrUi.GrabbingWithLaser;
        if (HoldingWindow) uiHit = new UiHit { Kind = UiHitKind.Frame, Distance = VrUi.GrabDistanceWorld };
        // On our menu, an empty window frame or a window being moved: the game gets no clicks.
        OnVrMenu = uiHit.Kind is UiHitKind.Menu or UiHitKind.Frame;
        Vector2 panelPixel = default;
        float panelDistance = 0;
        PopoutClicks.Window = uiHit.Kind == UiHitKind.GameUi ? uiHit.Window : null;
        if (uiHit.Kind == UiHitKind.GameUi)
        {
            OnMenu = true;
            panelPixel = uiHit.Pixel;
            panelDistance = uiHit.Distance;
        }
        else OnMenu = !OnVrMenu && VrMenuPanel.Raycast(rig, origin, dir, out panelPixel, out panelDistance);
        if (!OnMenu) pinned = false;   // off the panel: a held press is the world's (or the menu's)
        CurrentRay = new Ray(new Vector3(origin.X, origin.Y, origin.Z), new Vector3(dir.X, dir.Y, dir.Z));
        if (OnVrMenu)
        {
            mousePixel = diorama ? ParkingPixel() : lastMenuPixel;
            length = uiHit.Distance;
            hit = true;
            if (VrInput.Pressed(CataUi.Click) && uiHit.Kind == UiHitKind.Menu) VrUi.Click();
        }
        else if (OnMenu)
        {
            mousePixel = lastMenuPixel = StickyPress(panelPixel);
            length = panelDistance;
            hit = true;
        }
        else
        {
            // On the world the mouse follows the laser's hit as the game camera
            // sees it (for drag-select); off the cinema screen it stays where
            // the laser last left it.
            mousePixel = !diorama ? lastMenuPixel : WorldMouse.PixelFor(main, WorldHit, CurrentRay) ?? ParkingPixel();
        }

        // The laser ends where the game's own raycast landed last frame.
        WorldHit = null;
        if (diorama && !OnMenu && !OnVrMenu && main != null && Game.Gameplay.GetRaycastHitMousePosition(out var w))
        {
            WorldHit = w;
            var toHit = new NVec(w.x, w.y, w.z) - origin;
            float along = NVec.Dot(toHit, dir);
            if (along > 0 && along < 50f * scale && (toHit - dir * along).Length() < 0.05f * scale)
            {
                length = along;
                hit = true;
            }
        }

        NativeInput.Active = true;
        NativeInput.MousePixel = mousePixel;
        // Over the menu the world must not react; rays then go nowhere useful (straight up).
        NativeInput.RayActive = diorama;
        NativeInput.LaserRay = OnMenu || OnVrMenu ? new Ray(CurrentRay.origin, Vector3.up) : WorldMouse.HeldRay ?? CurrentRay;

        float thickness = 0.003f * scale;
        var mid = origin + dir * (length * 0.5f);
        laser.SetActive(true);
        laser.transform.SetPositionAndRotation(new Vector3(mid.X, mid.Y, mid.Z), aim.UnityRotation);
        laser.transform.localScale = new Vector3(thickness, thickness, length);

        // Mouse UI pieces (hover info, key hints) can sit beside the laser's end.
        if (!OnMenu && !OnVrMenu)
        {
            var tip = origin + dir * length;
            VrPieces.LaserEnd(new Vector3(tip.X, tip.Y, tip.Z));
        }

        hitMarker.SetActive(hit);
        if (hit)
        {
            var end = origin + dir * length;
            hitMarker.transform.position = new Vector3(end.X, end.Y, end.Z);
            hitMarker.transform.localScale = Vector3.one * (0.012f * scale);
        }

        UpdateButtons(vr, rig, diorama);
        WorldMouse.Update(rig, main, !OnVrMenu && VrInput.Held(CataUi.Click), diorama && !OnMenu && !OnVrMenu, mousePixel, WorldHit);
        wasOnMenu = OnMenu;
    }

    static bool wasOnMenu;

    // ---- sticky press on the game's UI ----
    //
    // A hand is never still: with the trigger held, the laser drifts a few
    // pixels a second, which the game reads as the mouse moving. Its hold
    // buttons (the time anchors' "hold to load", hold to skip) then see the
    // pointer leave the button or start a scroll, and the hold starts over.
    // So a press that starts on the panel (or a window) keeps the game's mouse
    // where it started while the laser stays within a dead zone; past it, the
    // mouse follows the laser again for the rest of the press (scroll bars,
    // sliders and drags still work, with a short pull first).
    static bool pinned;
    static Vector2 pinPixel;
    static float pinDrift;
    static BepInEx.Configuration.ConfigEntry<float> deadZone;

    static Vector2 StickyPress(Vector2 pixel)
    {
        if (!VrInput.Held(CataUi.Click))
        {
            if (pinned) Plugin.Logger.LogInfo($"[pointer] press released while held still (drifted up to {pinDrift:F0} px)");
            pinned = false;
            return pixel;
        }
        if (VrInput.Pressed(CataUi.Click))
        {
            pinned = true;
            pinPixel = pixel;
            pinDrift = 0;
            return pixel;
        }
        if (!pinned) return pixel;
        deadZone ??= Plugin.Instance.Config.Bind("Controls", "PressDeadZone", 60f,
            "While the trigger is held on the game's UI, the mouse stays where the press started until the laser moves this far (pixels of a 1080-line screen); then it follows. Keeps hold buttons held. 0: off.");
        float drift = Vector2.Distance(pixel, pinPixel);
        pinDrift = Mathf.Max(pinDrift, drift);
        if (drift <= deadZone.Value * Screen.height / 1080f) return pinPixel;
        pinned = false;
        Plugin.Logger.LogInfo($"[pointer] press moved {drift:F0} px: the mouse follows the laser (a drag)");
        return pixel;
    }

    // The actions are CataUi's (rebindable on the VR menu's Controls tab).
    // Defaults, in Quest Touch names (the pointer hand is the right unless LeftHanded):
    //   pointer trigger   left mouse button (click, hold, drag); clicks the VR menu
    //   pointer B         right mouse button
    //   pointer A         middle mouse button
    //   either grip       move the table; both grips scale and turn it (the core's TableRig)
    //   turn / zoom       (the off stick, by the stick standard) left / right = rotate the game
    //                     camera (Q / E), up / down = zoom it (mouse wheel); the table follows
    //                     the camera (CataRig), so the game sees real input
    //   other X           pause (Space)
    //   other Y           back / close menu (Escape)
    //   pan               (the pointer stick) the game camera's W A S D, relative to where you're looking
    //   both grips + both triggers   the VR menu
    const float StickHoldThreshold = 0.5f;
    const float ZoomNotchesPerSecond = 2f;   // wheel notches per second at full stick (6 crossed the whole range in a second)

    static void UpdateButtons(VrSession vr, CataRig rig, bool diorama)
    {
        // Clicks on the VR menu are the kit's, not the game's.
        bool game = !OnVrMenu;
        NativeInput.SetButton(0, game && VrInput.Held(CataUi.Click));
        NativeInput.SetButton(1, game && VrInput.Held(CataUi.RightMouse));
        NativeInput.SetButton(2, game && VrInput.Held(CataUi.MiddleMouse));

        // The game's own rotate keys and wheel zoom, so its tutorial and camera
        // logic see them. Q / E rotate about the pivot (in fixed steps by default).
        // While a UI window is held, the pointer stick pushes and sizes it instead.
        bool camera = diorama && !HoldingWindow;
        // The sticks come from the core's table (CataHost.Sticks), which leaves turning and zooming to the game.
        var sticks = rig.Sticks;
        HoldKey(KeyCode.E, camera && sticks.Turn > StickHoldThreshold);
        HoldKey(KeyCode.Q, camera && sticks.Turn < -StickHoldThreshold);
        float zoom = sticks.Zoom;
        NativeInput.ScrollY = camera && !OnVrMenu && MathF.Abs(zoom) > StickHoldThreshold
            ? zoom * ZoomNotchesPerSecond * Time.unscaledDeltaTime
            : 0f;
        // Wheel notches from the hotkey bar (its laser is on the bar, not the world).
        NativeInput.ScrollY += CataUi.HotkeyScroll;
        CataUi.HotkeyScroll = 0;
        if (NativeInput.ScrollY != 0) rig.FollowGameZoom();

        HoldKey(KeyCode.Space, VrInput.Held(CataUi.Pause));
        HoldKey(KeyCode.Escape, VrInput.Held(CataUi.Back));
        HoldPanKeys(vr, rig, diorama);

        // The bar's other keys, which no controller action holds.
        foreach (var key in CataUi.HotkeysUsed)
            if (!keysHeldThisFrame.Contains(key)) NativeInput.SetKey(key, CataUi.HotkeyHeld(key));
        keysHeldThisFrame.Clear();
    }

    // A key held by a controller action or by the hotkey bar.
    static readonly System.Collections.Generic.HashSet<KeyCode> keysHeldThisFrame = new();
    static void HoldKey(KeyCode key, bool held)
    {
        keysHeldThisFrame.Add(key);
        NativeInput.SetKey(key, held || CataUi.HotkeyHeld(key));
    }

    // The table's pan (the core turns the pan stick into a world direction,
    // "away from you across the table") becomes the game's W A S D: the
    // direction is matched against the game camera's own forward / right.
    static void HoldPanKeys(VrSession vr, CataRig rig, bool diorama)
    {
        bool up = false, down = false, left = false, right = false;
        var want = rig.PanWanted;
        var cam = Game.MainCamera;
        if (diorama && cam != null && want.x * want.x + want.z * want.z > 1e-12f)
        {
            var dir = NVec.Normalize(new NVec(want.x, 0, want.z));
            var cf = cam.transform.forward;
            var camForward = NVec.Normalize(new NVec(cf.x, 0, cf.z));
            var camRight = new NVec(camForward.Z, 0, -camForward.X);
            float forward = NVec.Dot(dir, camForward);
            float side = NVec.Dot(dir, camRight);
            // Within ~67 degrees of an axis counts, so diagonals hold two keys.
            const float axis = 0.38f;
            up = forward > axis;
            down = forward < -axis;
            right = side > axis;
            left = side < -axis;
        }
        HoldKey(KeyCode.W, up);
        HoldKey(KeyCode.S, down);
        HoldKey(KeyCode.D, right);
        HoldKey(KeyCode.A, left);
    }

    // In the world the mouse position only matters for the game's "is the mouse
    // over UI" test (the ray itself is replaced), so park it on a pixel with no
    // UI under it, and off the exact centre, which the game camera casts
    // through for its own pivot. Walk through candidates while UI is reported there.
    static readonly Vector2[] ParkingCandidates =
    {
        new(0.47f, 0.46f), new(0.5f, 0.6f), new(0.5f, 0.4f), new(0.4f, 0.5f), new(0.6f, 0.5f),
        new(0.35f, 0.65f), new(0.65f, 0.65f), new(0.35f, 0.35f), new(0.65f, 0.35f),
    };
    static int parkingIndex;
    static int parkedFrames;

    static Vector2 ParkingPixel()
    {
        if (wasOnMenu) parkedFrames = 0;
        // The event system updates its hover state a frame after the mouse moves.
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (++parkedFrames > 3 && es != null && es.IsPointerOverGameObject())
        {
            parkingIndex = (parkingIndex + 1) % ParkingCandidates.Length;
            parkedFrames = 0;
        }
        var c = ParkingCandidates[parkingIndex];
        return new Vector2(c.x * Screen.width, c.y * Screen.height);
    }

    static void Build()
    {
        built = true;
        int layer = CataRig.VisualLayer;
        hands = new[] { new VrHandVisuals("CataVr Hand L", layer), new VrHandVisuals("CataVr Hand R", layer) };
        laser = VrVisuals.Primitive(PrimitiveType.Cube, "CataVr Laser", new Color(0.3f, 0.9f, 1f), layer);
        hitMarker = VrVisuals.Primitive(PrimitiveType.Sphere, "CataVr Hit", new Color(1f, 0.85f, 0.2f), layer);
    }

    static void SetVisible(bool on)
    {
        if (!built) return;
        laser.SetActive(on);
        hitMarker.SetActive(on);
        foreach (var h in hands) h.Root.SetActive(on);
    }
}

sealed class VrHandVisuals
{
    public readonly GameObject Root;

    public VrHandVisuals(string name, int layer)
    {
        Root = VrVisuals.Primitive(PrimitiveType.Sphere, name, new Color(0.9f, 0.9f, 0.95f), layer);
    }

    public void Place(VrHand hand, CataRig rig)
    {
        Root.SetActive(hand.Valid);
        if (!hand.Valid) return;
        var p = rig.TrackingToWorld(hand.Pose);
        Root.transform.SetPositionAndRotation(p.UnityPosition, p.UnityRotation);
        float size = (hand.GripHeld ? 0.05f : 0.035f) * rig.WorldScale;
        Root.transform.localScale = Vector3.one * size;
    }
}

static class Primitives
{
    static Shader shader;

    static Shader Unlit()
    {
        if (shader != null) return shader;
        shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Sprites/Default");
        Plugin.Logger.LogInfo($"VR visuals shader: {(shader != null ? shader.name : "none")}");
        return shader;
    }

    // Unlit, textured, always drawn on top (the diorama's surrounding terrain
    // would otherwise hide parts of it). Transparent: alpha-blended, for the
    // menu panel. Opaque: for the cinema screen, drawn just before the panel.
    // Uses URP Unlit's surface settings (the values its material editor writes).
    public static Material MakeTextured(Texture texture, bool transparent)
    {
        var mat = new Material(Unlit()) { mainTexture = texture, color = Color.white };
        mat.SetTexture("_BaseMap", texture);
        mat.SetColor("_BaseColor", Color.white);
        mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        mat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
        mat.SetFloat("_ZWrite", 0);
        if (transparent)
        {
            mat.SetFloat("_Surface", 1);
            mat.SetFloat("_Blend", 0);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            mat.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay;
        }
        else
        {
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay - 1;
        }
        return mat;
    }
}
