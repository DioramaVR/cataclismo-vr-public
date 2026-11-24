using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CataVr;

// Where the game's mouse goes while the laser is on the world: the game
// camera's screen pixel of the laser's hit, so the game sees a click or a drag
// where the player clicks or drags. The ray through that pixel is still
// replaced by the laser (NativeInput), so hits don't change. A drag selects a
// box on the ground, square to the table, between where it began and the
// laser (WorldSelect swaps it in for the game's screen rectangle); it is
// outlined on the table while dragging.
static class WorldMouse
{
    // Inside the game's edge-scroll margin (1% of the screen), so a laser past
    // the edge of the game camera's view doesn't pan it.
    const float EdgeMargin = 0.03f;
    // A hand turns a little while pulling the trigger. Until the laser has
    // turned this far from where the press began, the mouse stays on the press
    // pixel, so a click stays a click instead of becoming a tiny box select
    // (which selects nothing and clears the selection).
    const float ClickSlopDegrees = 3f;

    static readonly GameObject[] edges = new GameObject[4];
    static bool dragging, parkedDrag, projected;
    static Vector2 dragStart, lastPixel;
    static Ray aim, pressAim;
    static bool inSlop;
    // The game reads the button release a frame after we set it: the mouse
    // stays where the press or drag ended for a few frames, so the game ends
    // the click or the box there (not where the laser moves next, or on the
    // parking pixel once the selection box is UI under the laser).
    const int ReleaseHoldFrames = 3;
    static int releaseHold;
    static Vector2 releasePixel;
    static bool releaseIsClick;

    // The game picks units with the laser's ray, not the pixel: while a click
    // is held still (above), the ray stays where the press began too.
    public static Ray? HeldRay { get; private set; }
    static float dragHeight;
    static Vector3 boxStart, boxEnd, boxRight, boxForward;
    // How far outside the box a unit's centre may be and still be selected.
    const float BoxMargin = 0.5f;

    // A drag past the click slop, until the game has read its release.
    public static bool WorldBoxActive => (dragging && !inSlop) || (releaseHold > 0 && !releaseIsClick);

    public static bool InWorldBox(Vector3 p)
    {
        var d = p - boxStart;
        var e = boxEnd - boxStart;
        return Between(Vector3.Dot(d, boxRight), Vector3.Dot(e, boxRight))
            && Between(Vector3.Dot(d, boxForward), Vector3.Dot(e, boxForward));
    }

    static bool Between(float x, float end) =>
        x >= Mathf.Min(0, end) - BoxMargin && x <= Mathf.Max(0, end) + BoxMargin;

    // The game camera's pixel for a world hit, or null (park the mouse) when
    // it's behind the camera or (when not dragging) over the game's UI, where
    // a click would land on the UI instead of the world. A drag that began
    // parked stays parked, so the game's rectangle doesn't jump.
    public static Vector2? PixelFor(Camera main, Vector3? hit, Ray laser)
    {
        aim = laser;
        HeldRay = null;
        if (releaseHold > 0)
        {
            releaseHold--;
            if (releaseIsClick) HeldRay = pressAim;
            return releasePixel;
        }
        if (dragging && inSlop)
        {
            if (Vector3.Angle(aim.direction, pressAim.direction) < ClickSlopDegrees)
            {
                HeldRay = pressAim;
                return dragStart;
            }
            inSlop = false;
        }
        var p = Project(main, hit);
        projected = p.HasValue;
        // A drag keeps its last pixel when the hit drops out for a moment.
        if (p == null && dragging) return lastPixel;
        if (p is Vector2 v) lastPixel = v;
        return p;
    }

    static Vector2? Project(Camera main, Vector3? hit)
    {
        if (parkedDrag || main == null || hit is not Vector3 w) return null;
        var s = main.WorldToScreenPoint(w);
        if (s.z <= 0) return null;
        float x = Mathf.Clamp(s.x, Screen.width * EdgeMargin, Screen.width * (1 - EdgeMargin));
        float y = Mathf.Clamp(s.y, Screen.height * EdgeMargin, Screen.height * (1 - EdgeMargin));
        // The game camera casts its own rays through the screen centre (for its
        // pivot); the mouse must not sit there, or those rays become the laser.
        if (Mathf.Abs(x - Screen.width * 0.5f) < 3f && Mathf.Abs(y - Screen.height * 0.5f) < 3f) x += 4f;
        var p = new Vector2(x, y);
        if (!dragging && OverUi(p)) return null;
        return p;
    }

    // The ground under a game-camera screen pixel (y up), for UI pieces placed
    // in the world. Viewport rays: ScreenPointToRay is replaced by the laser.
    public static Vector3? ScreenToGround(Vector2 pixel)
    {
        var main = Game.MainCamera;
        if (main == null) return null;
        var ray = main.ViewportPointToRay(new Vector3(pixel.x / Screen.width, pixel.y / Screen.height, 0));
        const int mask = ~((1 << 2) | (1 << 5));   // not Ignore Raycast or UI
        return Physics.Raycast(ray, out var hit, 100000f, mask) ? hit.point : null;
    }

    static PointerEventData pointerData;
    static Il2CppSystem.Collections.Generic.List<RaycastResult> results;

    static bool OverUi(Vector2 pixel)
    {
        var es = EventSystem.current;
        if (es == null) return false;
        pointerData ??= new PointerEventData(es);
        results ??= new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        pointerData.position = pixel;
        results.Clear();
        es.RaycastAll(pointerData, results);
        return results.Count > 0;
    }

    // Once per frame after the mouse pixel is set: track a left-button drag that
    // started on the world and outline the game's selection on the ground.
    public static void Update(CataRig rig, Camera main, bool held, bool onWorld, Vector2 pixel, Vector3? hit)
    {
        if (held && !dragging && !parkedDrag && onWorld)
        {
            if (projected && hit is Vector3 start)
            {
                dragging = inSlop = true;
                dragStart = lastPixel = pixel;
                pressAim = aim;
                dragHeight = start.y;
                boxStart = boxEnd = start;
                // The table's own axes, as the game world sees them.
                var f = rig.TrackingToWorld(rig.TableAnchor + System.Numerics.Vector3.UnitZ) - rig.TrackingToWorld(rig.TableAnchor);
                var forward = new Vector3(f.X, 0, f.Z);
                boxForward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
                boxRight = Vector3.Cross(Vector3.up, boxForward);
            }
            else parkedDrag = true;
        }
        else if (!held)
        {
            if (dragging)
            {
                releaseHold = ReleaseHoldFrames;
                releasePixel = inSlop ? dragStart : lastPixel;
                releaseIsClick = inSlop;
            }
            dragging = parkedDrag = false;
        }
        if (dragging && hit is Vector3 end) boxEnd = end;
        bool show = dragging && !inSlop;
        if (edges[0] == null)
        {
            if (!show) return;
            for (int i = 0; i < 4; i++)
                edges[i] = VrVisuals.Primitive(PrimitiveType.Cube, "CataVr Select Edge", new Color(0.4f, 1f, 0.5f), CataRig.VisualLayer);
        }
        for (int i = 0; i < 4; i++) edges[i].SetActive(show);
        if (!show) return;

        // The box on the ground, at the height where the drag began.
        var span = boxEnd - boxStart;
        var right = boxRight * Vector3.Dot(span, boxRight);
        var ahead = boxForward * Vector3.Dot(span, boxForward);
        var origin = new Vector3(boxStart.x, dragHeight, boxStart.z);
        var corners = new[] { origin, origin + right, origin + right + ahead, origin + ahead };
        float thickness = 0.004f * rig.WorldScale;
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            var t = edges[i].transform;
            t.SetPositionAndRotation((a + b) * 0.5f, Quaternion.LookRotation(b - a == Vector3.zero ? Vector3.forward : b - a));
            t.localScale = new Vector3(thickness, thickness, (b - a).magnitude);
        }
    }
}
