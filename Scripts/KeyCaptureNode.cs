using Godot;

namespace ModConfig;

/// <summary>
/// Temporary node added to the scene tree to capture a single key press.
/// Used by the KeyBind config UI. Self-destructs after capture.
/// </summary>
internal partial class KeyCaptureNode : Node
{
    internal System.Action<long>? OnKeyCaptured;

    public override void _Ready()
    {
        SetProcessUnhandledKeyInput(true);
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true } keyEvent)
        {
            // Ignore standalone modifier keys (Ctrl/Shift/Alt/Meta pressed alone)
            var key = keyEvent.Keycode;
            if (key is Key.Ctrl or Key.Shift or Key.Alt or Key.Meta) return;

            GetViewport().SetInputAsHandled();
            // GetKeycodeWithModifiers() includes Ctrl/Shift/Alt/Meta bit flags,
            // so Ctrl+M becomes a single long value that OS.GetKeycodeString() can decode.
            OnKeyCaptured?.Invoke((long)keyEvent.GetKeycodeWithModifiers());
        }
    }
}
