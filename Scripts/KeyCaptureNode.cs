using Godot;

namespace ModConfig;

/// <summary>
/// Temporary node added to the scene tree to capture a single key press.
/// Used by the KeyBind config UI. Self-destructs after capture.
/// </summary>
internal partial class KeyCaptureNode : Node
{
    internal System.Action<long>? OnKeyCaptured;
    private bool _mouseCancelArmed;

    public override void _Ready()
    {
        SetProcessUnhandledKeyInput(true);
        SetProcessInput(true);
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null)
            tree.ProcessFrame += ArmMouseCancel;
        else
            _mouseCancelArmed = true;
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true } keyEvent)
        {
            if (keyEvent.Keycode == Key.Escape)
            {
                GetViewport().SetInputAsHandled();
                OnKeyCaptured?.Invoke(0);
                return;
            }

            // Ignore standalone modifier keys (Ctrl/Shift/Alt/Meta pressed alone)
            var key = keyEvent.Keycode;
            if (key is Key.Ctrl or Key.Shift or Key.Alt or Key.Meta) return;

            GetViewport().SetInputAsHandled();
            // GetKeycodeWithModifiers() includes Ctrl/Shift/Alt/Meta bit flags,
            // so Ctrl+M becomes a single long value that OS.GetKeycodeString() can decode.
            OnKeyCaptured?.Invoke((long)keyEvent.GetKeycodeWithModifiers());
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (!_mouseCancelArmed)
            return;

        if (@event is InputEventMouseButton { Pressed: true } mouseEvent)
        {
            if (mouseEvent.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown or MouseButton.WheelLeft or MouseButton.WheelRight)
                return;

            GetViewport().SetInputAsHandled();
            OnKeyCaptured?.Invoke(0);
        }
    }

    private void ArmMouseCancel()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null)
            tree.ProcessFrame -= ArmMouseCancel;
        _mouseCancelArmed = true;
    }
}
