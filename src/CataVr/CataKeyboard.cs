using System;
using System.Text;
using Valve.VR;
using VrUiKit;

namespace CataVr;

// Text from SteamVR's keyboard into the game's TextMesh Pro fields, built key
// by key (2026-11-23: "each key replaces the last letter"). The core's
// VrKeyboard copies SteamVR's whole keyboard text into the field on every key;
// if that text holds only the newest key (or doesn't start from the field's
// text), each key replaces what was there. Here each key event's own input
// (cNewInput: a character, or backspace) is applied to the field's current
// text, cursor at the end and nothing selected, whatever SteamVR's buffer
// says. Where the two disagree it is logged, so the log shows which it was.
// The rest (focus, submit, leave) is the core's TmpTextFields.
sealed class CataKeyboardFields : ITextFields
{
    readonly TmpTextFields tmp = new();
    readonly Func<VrSession> session;
    int frame = -1, nextEvent;
    object modeField;
    bool? newestOnly;
    static int logs;

    public CataKeyboardFields(Func<VrSession> session) => this.session = session;

    public object Focused() => tmp.Focused();
    public bool Same(object a, object b) => tmp.Same(a, b);
    public string Name(object f) => tmp.Name(f);
    public int MaxLength(object f) => tmp.MaxLength(f);
    public string Get(object f) => tmp.Get(f);
    public void Submit(object f) => tmp.Submit(f);
    public void Leave(object f) => tmp.Leave(f);

    // VrKeyboard calls this once per KeyboardCharInput event, in the order of this frame's
    // events, and once more on Done (no key of its own: SteamVR's final text).
    public void Set(object f, string keyboardText)
    {
        string before = tmp.Get(f) ?? "";
        var key = NextKey();
        string after;
        int max = tmp.MaxLength(f);
        if (!string.IsNullOrEmpty(key))
        {
            after = Apply(before, key, max);
            if (after != keyboardText) Log($"key {Show(key)}: SteamVR's text '{keyboardText}' differs from the field '{before}' + key; field set to '{after}'");
            else if (logs < 12) Log($"key {Show(key)}: '{before}' -> '{after}'");
        }
        else if (key != null)
        {
            // The event carried no character (2026-11-23: always so here), so SteamVR's text decides.
            // It is either the whole text (one key longer or shorter than the field) or, as seen here,
            // only the newest key: then that key is added, and an empty text is a backspace.
            keyboardText ??= "";
            // Which kind SteamVR's text is, once seen, holds for the field: a text of two or more
            // characters is the whole text; a key that can't be the whole text means newest-only.
            if (!tmp.Same(f, modeField)) { modeField = f; newestOnly = null; }
            if (keyboardText.Length >= 2) newestOnly = false;
            bool whole = newestOnly != true && ((keyboardText.Length == before.Length + 1 && keyboardText.StartsWith(before, StringComparison.Ordinal))
                || (keyboardText.Length + 1 == before.Length && before.StartsWith(keyboardText, StringComparison.Ordinal) && keyboardText.Length > 0)
                || newestOnly == false);
            if (!whole && keyboardText.Length > 0) newestOnly = true;
            if (whole) after = keyboardText;
            else if (keyboardText.Length == 0) after = Apply(before, "\b", max);
            else after = Apply(before, keyboardText, max);
            if (logs < 30) Log($"key (none in the event): SteamVR's text '{keyboardText}' read as {(whole ? "the whole text" : keyboardText.Length == 0 ? "a backspace" : "the newest key")}: '{before}' -> '{after}'");
        }
        else
        {
            // Done: SteamVR's final text, unless it disagrees with what the keys built.
            after = before;
            if (keyboardText != before) Log($"done: SteamVR's text '{keyboardText}', kept the field's '{before}'");
        }
        tmp.Set(f, after);   // cursor at the end, nothing selected
        Show(f);
    }

    // The field's text is right after each key (the log), but the box only showed it after
    // Done (2026-11-23): its label is refreshed here and its text mesh rebuilt, and the
    // field re-scrolled to the end, as typing on a keyboard would.
    static int shows;
    static void Show(object f)
    {
        var field = (TMPro.TMP_InputField)f;
        var label = field.textComponent;
        string state() => $"focused {field.isFocused}, label '{(label != null ? label.text : "-")}' {(label != null ? $"on {label.isActiveAndEnabled} alpha {label.color.a:F2}" : "")}, " +
            $"placeholder {(field.placeholder != null ? field.placeholder.isActiveAndEnabled.ToString() : "-")}, update due {field.m_IsTextComponentUpdateRequired}";
        string was = shows < 4 ? state() : null;
        try
        {
            field.ForceLabelUpdate();
            field.m_forceRectTransformAdjustment = true;
            if (label != null) label.ForceMeshUpdate(false, false);
        }
        catch (Exception e) { if (shows < 4) Plugin.Logger.LogWarning($"[keyboard] couldn't refresh the field's label: {e.Message}"); }
        if (was != null) Plugin.Logger.LogInfo($"[keyboard] field shown: before {was} | after {state()}");
        shows++;
    }

    // The next keyboard character event of this frame's events, as its UTF-8 text; null if none is left.
    string NextKey()
    {
        var vr = session();
        if (vr == null) return null;
        if (UnityEngine.Time.frameCount != frame) { frame = UnityEngine.Time.frameCount; nextEvent = 0; }
        var events = vr.Events;
        while (nextEvent < events.Count)
        {
            var ev = events[nextEvent++];
            if ((EVREventType)ev.eventType != EVREventType.VREvent_KeyboardCharInput) continue;
            LogRaw(ev);
            var k = ev.data.keyboard;
            var bytes = new[] { k.cNewInput0, k.cNewInput1, k.cNewInput2, k.cNewInput3, k.cNewInput4, k.cNewInput5, k.cNewInput6, k.cNewInput7 };
            int n = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(bytes, 0, n < 0 ? bytes.Length : n);
        }
        return null;
    }

    // The first few key events as raw bytes (the struct as marshalled: header, then the data
    // union at its offset), to see where SteamVR put the character, if anywhere.
    static int raws;
    static void LogRaw(VREvent_t ev)
    {
        if (raws >= 6) return;
        raws++;
        int size = System.Runtime.InteropServices.Marshal.SizeOf<VREvent_t>();
        var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(ev, ptr, false);
            var bytes = new byte[Math.Min(size, 48)];
            System.Runtime.InteropServices.Marshal.Copy(ptr, bytes, 0, bytes.Length);
            int offset = (int)System.Runtime.InteropServices.Marshal.OffsetOf<VREvent_t>(nameof(VREvent_t.data));
            Plugin.Logger.LogInfo($"[keyboard] raw event ({size} bytes, data at {offset}): {BitConverter.ToString(bytes)}");
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }
    }

    // Backspace removes the last character; other control characters (Enter, tab) add nothing.
    static string Apply(string text, string key, int max)
    {
        var sb = new StringBuilder(text);
        foreach (char c in key)
        {
            if (c == '\b') { if (sb.Length > 0) sb.Length--; }
            else if (c >= ' ' && (max <= 0 || sb.Length < max)) sb.Append(c);
        }
        return sb.ToString();
    }

    static string Show(string key) => key == "\b" ? "backspace" : key.Length == 1 && key[0] < ' ' ? $"0x{(int)key[0]:X2}" : $"'{key}'";

    static void Log(string s)
    {
        if (logs++ < 40) Plugin.Logger.LogInfo("[keyboard] " + s);
    }
}
