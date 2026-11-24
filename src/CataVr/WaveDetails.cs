using System.Collections.Generic;
using DigitalSun.Castle.UI;
using UnityEngine;
using VrUiKit;
using NVec = System.Numerics.Vector3;

namespace CataVr;

// The game shows what's coming in a wave (enemy portraits and an amount) on one
// shared WaveDetailPanel while the mouse hovers that wave's pointer. In VR the
// pointers are drawn in the world, where the laser can't hover the screen copy,
// so every wave pointer gets its own copy of the panel, set up for its wave
// and always shown: a sibling element of the "pointers" piece, anchored just
// above its pointer (WaveDetails.Anchor, called from CataUi.PointerTarget).
static class WaveDetails
{
    sealed class Detail
    {
        public ScreenPointer pointer;
        public GameObject go;
        public WaveDetailPanelController panel;
        public long wave;
        public bool seen;
        public CanvasGroup group;
    }

    // By pointer (the game pools pointers, so a pointer can move to another wave).
    static readonly Dictionary<long, Detail> byPointer = new();
    // By the copy's transform, for the anchor hook.
    static readonly Dictionary<long, Detail> byCopy = new();
    static ScreenPointersController controller;
    static float nextFind;
    static int made;
    // A gap between the pointer and the panel above it, UI pixels.
    const float GapPixels = 8f;

    public static void Update(bool active)
    {
        if (!active) { HideAll(); return; }
        if ((controller == null || !controller.gameObject.activeInHierarchy) && Time.unscaledTime >= nextFind)
        {
            nextFind = Time.unscaledTime + 1f;
            controller = Object.FindObjectOfType<ScreenPointersController>();
        }
        if (controller == null || controller._waveDetailPanel == null || controller._usedPointers == null) { DestroyAll(); return; }

        foreach (var d in byPointer.Values) d.seen = false;
        var used = controller._usedPointers;
        for (int i = 0; i < used.Count; i++)
        {
            var p = used[i];
            if (p == null) continue;
            long wave = WaveOf(p);
            if (wave == 0) continue;
            long key = Dvr.VrCore.Key(p);
            if (!byPointer.TryGetValue(key, out var d) || d.go == null)
            {
                d = Make(p);
                if (d == null) continue;
                byPointer[key] = d;
            }
            d.seen = true;
            bool show = p.gameObject.activeInHierarchy && (p._canvasGroup == null || p._canvasGroup.alpha > 0.01f);
            // Active first: the copy's Awake runs on its first activation, and SetUp needs it.
            if (d.go.activeSelf != show) d.go.SetActive(show);
            if (show && d.wave != wave)
            {
                d.wave = wave;
                SetUp(d);
            }
            if (show) ClickThrough(d);
        }
        List<long> gone = null;
        foreach (var kv in byPointer)
            if (!kv.Value.seen) (gone ??= new()).Add(kv.Key);
        if (gone != null)
            foreach (var k in gone) Remove(k);
    }

    // Which wave a pointer shows, as a key that changes when the pooled pointer moves on; 0 if it isn't a wave pointer.
    static long WaveOf(ScreenPointer p)
    {
        var type = p._pointerType;
        if (type == ScreenPointer.ScreenPointerType.Wave && p._enemyWaveInfo != null)
            return Dvr.VrCore.Key(p._enemyWaveInfo) ^ (p._waveGroupInfo != null ? (long)p._waveGroupInfo.Pointer << 1 : 0);
        if (type == ScreenPointer.ScreenPointerType.GiantWave && p._giantWave != null)
            return Dvr.VrCore.Key(p._giantWave);
        return 0;
    }

    static Detail Make(ScreenPointer p)
    {
        var original = controller._waveDetailPanel;
        var parent = ElementRoot(p);
        if (parent == null) return null;
        var go = Object.Instantiate(original.gameObject, parent, false);
        go.name = $"CataVr WaveDetail {++made}";
        var d = new Detail { pointer = p, go = go, panel = go.GetComponent<WaveDetailPanelController>() };
        if (d.panel == null) { Object.Destroy(go); return null; }
        byCopy[Dvr.VrCore.Key(go.transform)] = d;
        return d;
    }

    // The copy goes beside the pointer's element, under the root of the "pointers" piece (ScreenPointersPanel).
    static Transform ElementRoot(ScreenPointer p)
    {
        for (var t = p.transform; t != null; t = t.parent)
            if (t.name == "ScreenPointersPanel") return t;
        return p.transform.parent;
    }

    // Our copies only show information, and they're always there: on the game's screen canvas
    // they'd take clicks meant for what is under them (the build buttons, 2026-11-23).
    // Every frame, as the game's panel controller may set its own group when it shows.
    static void ClickThrough(Detail d)
    {
        var group = d.group ??= d.go.GetComponent<CanvasGroup>() ?? d.go.AddComponent<CanvasGroup>();
        if (group.blocksRaycasts) group.blocksRaycasts = false;
        if (group.interactable) group.interactable = false;
    }

    static void SetUp(Detail d)
    {
        var p = d.pointer;
        var target = p._pointTransform != null ? p._pointTransform : p._rectTransform;
        try
        {
            if (p._pointerType == ScreenPointer.ScreenPointerType.GiantWave)
                d.panel.SetUpForGiantWave(p._giantWave, target);
            else if (p._isSurvivalWave)
                d.panel.SetUpForSurvivalWave(p._enemyWaveInfo, p._entitiesToShow, target, p._survivalWaveLevel);
            else
                d.panel.SetUpForWave(p._enemyWaveInfo, p._entitiesToShow, target);
            Plugin.Logger.LogInfo($"[waves] detail '{d.go.name}' for pointer '{p.name}' ({p._pointerType}, full info {p._showFullInfo}, {(p._entitiesToShow != null ? p._entitiesToShow.Count : 0)} kinds, under '{d.go.transform.parent?.name}')");
        }
        catch (System.Exception e)
        {
            Plugin.Logger.LogWarning($"[waves] detail for pointer '{p.name}' not set up, so it isn't shown: {e.Message}");
            d.go.SetActive(false);
        }
    }

    // The anchor of one element of the "pointers" piece, if it is one of our copies:
    // above its pointer's anchor by half of each one's height on the screen plus
    // a gap, at the size the core draws pieces in the world.
    public static bool Anchor(Transform element, System.Func<Transform, Vector3?> pointerAnchor, out Vector3? anchor)
    {
        anchor = null;
        if (!byCopy.TryGetValue(Dvr.VrCore.Key(element), out var d)) return false;
        var host = CataUi.Instance;
        if (!host.Active || d.pointer == null) return true;
        var below = pointerAnchor(d.pointer.transform);
        if (below == null) return true;
        var cam = host.UiCamera;
        var rp = UiBounds.Of(cam, new[] { d.pointer.transform });
        var rd = UiBounds.Of(cam, new[] { element });
        if (rd.height < 1) return true;

        var origin = host.TrackingToWorld(Dvr.Pose.Identity);
        float scale = Mathf.Max(1e-6f, host.WorldScale);
        var w = below.Value;
        var at = NVec.Transform(new NVec(w.x, w.y, w.z) - origin.Position, System.Numerics.Quaternion.Inverse(origin.Rotation)) / scale;
        // The core's metres per UI pixel for pieces in the world (VrPieces.MetresPerPixel).
        float dist = Mathf.Max(0.1f, NVec.Distance(host.Head.Position, at));
        float mpp = 2f * dist * Mathf.Tan(35f * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height) * PieceSize();
        var up = NVec.Transform(NVec.UnitY, HandDrag.FacingHead(host, at).Rotation);
        var above = at + up * ((rp.height * 0.5f + rd.height * 0.5f + GapPixels) * mpp);
        var world = origin.Position + NVec.Transform(above * scale, origin.Rotation);
        anchor = new Vector3(world.X, world.Y, world.Z);
        return true;
    }

    static BepInEx.Configuration.ConfigEntry<float> pieceSize;
    static float PieceSize()
    {
        pieceSize ??= Plugin.Instance.Config.Bind("VrUi", "PieceSize", 1f, "Size of UI pieces shown in the world or at the laser (1 = a screen of UI spans 35 degrees).");
        return pieceSize.Value;
    }

    static void HideAll()
    {
        foreach (var d in byPointer.Values)
            if (d.go != null && d.go.activeSelf) d.go.SetActive(false);
    }

    static void Remove(long key)
    {
        var d = byPointer[key];
        byPointer.Remove(key);
        if (d.go != null)
        {
            byCopy.Remove(Dvr.VrCore.Key(d.go.transform));
            Object.Destroy(d.go);
        }
    }

    static void DestroyAll()
    {
        foreach (var k in new List<long>(byPointer.Keys)) Remove(k);
        byCopy.Clear();
    }
}
