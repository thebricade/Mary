using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Where the player is looking on screen, and when they blink. Swap the implementation to change the input device.</summary>
public interface IGazeSource
{
    /// <summary>Screen position of the gaze, 0..1 on both axes (0,0 = bottom left).</summary>
    Vector2 Gaze { get; }
    /// <summary>True for exactly one frame when a blink is detected.</summary>
    bool BlinkThisFrame { get; }
    /// <summary>How quickly the on-screen cut line follows the gaze (higher = snappier). Webcam gaze is noisy and wants a lower value.</summary>
    float Responsiveness { get; }
    void Begin();
    void End();
}

/// <summary>Stand-in for the webcam: the mouse is your gaze, Space or left click is a blink.</summary>
public class MouseKeyboardGaze : IGazeSource
{
    public Vector2 Gaze
    {
        get
        {
            var m = Mouse.current;
            if (m == null) return new Vector2(0.5f, 0.5f);
            Vector2 p = m.position.ReadValue();
            return new Vector2(p.x / Screen.width, p.y / Screen.height);
        }
    }

    public bool BlinkThisFrame
    {
        get
        {
            var k = Keyboard.current;
            var m = Mouse.current;
            return (k != null && k.spaceKey.wasPressedThisFrame) || (m != null && m.leftButton.wasPressedThisFrame);
        }
    }

    public float Responsiveness => 30f;

    public void Begin()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public void End() { }
}

public static class GazeInput
{
    static readonly IGazeSource mouse = new MouseKeyboardGaze();
    static WebcamGazeSource webcam;

    /// <summary>True to read eyes from the webcam instead of the mouse. The player toggles this on the game's intro screen.</summary>
    public static bool UseWebcam;

    public static IGazeSource Source => UseWebcam ? (webcam ??= new WebcamGazeSource()) : mouse;
}
