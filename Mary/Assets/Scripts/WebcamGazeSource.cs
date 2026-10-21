using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>Optional extras a gaze source can offer: a calibration step and a status line for tuning.</summary>
public interface IGazeCalibration
{
    bool IsReady { get; }
    bool IsSampling { get; }
    /// <summary>How many calibration points have a valid reading so far.</summary>
    int Points { get; }
    /// <summary>Record where the eyes are while the player looks at this screen position (0..1 across).</summary>
    void BeginSample(float screenX);
    string Status { get; }
    /// <summary>Why the last calibration attempt was rejected, or empty.</summary>
    string Problem { get; }
}

/// <summary>
/// Webcam gaze and blinks, using Apple's Vision framework through the WebcamGaze plugin (macOS only).
/// Blinks: both eyelids close below a fraction of this player's open-eye size.
/// Gaze: where the pupils sit inside the eyes, mapped to the screen with a two point calibration.
/// </summary>
public class WebcamGazeSource : IGazeSource, IGazeCalibration
{
    // ---- native plugin ----
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
    const string Lib = "WebcamGaze";
    [DllImport(Lib)] static extern int wg_start();
    [DllImport(Lib)] static extern void wg_stop();
    [DllImport(Lib)] static extern int wg_state();
    [DllImport(Lib)] static extern int wg_poll(float[] values);
    [DllImport(Lib)] static extern void wg_dump(string path);
    static bool Supported => true;
#else
    static int wg_start() => 4;
    static void wg_stop() { }
    static int wg_state() => 4;
    static int wg_poll(float[] values) => 0;
    static void wg_dump(string path) { }
    static bool Supported => false;
#endif

    const int FaceIdx = 0, LeftOpenIdx = 1, RightOpenIdx = 2, LeftPupilIdx = 3, RightPupilIdx = 4, YawIdx = 5, LeftIrisIdx = 8, RightIrisIdx = 9;

    // ---- tuning ----
    const float CloseFraction = 0.68f;     // eyelids count as closed below this fraction of the open size
    const float ReopenFraction = 0.82f;    // ...and as open again above this (stops flicker)
    const int ClosedFramesNeeded = 2;      // frames below the threshold before it counts as a blink
    const float SampleDelay = 0.6f;        // after the key press, so the eyes can get back from the keyboard to the dot
    const float SampleSeconds = 0.8f;
    const float MinCalibrationSpread = 0.03f;   // iris movement between the two dots, as a fraction of the eye width

    readonly float[] v = new float[16];
    int lastFrame = -1, frameStamp = -1;
    bool started;

    // blink
    float baseline;                        // open-eye size for this player
    bool closed;
    int closedFrames;
    bool blink;                            // set for the frame in which a blink is detected
    public int BlinkCount { get; private set; }

    // gaze
    float rawGaze = 0.5f, heldRaw = 0.5f;
    readonly float[] recent = new float[5];            // last few iris readings, averaged to steady the cut line
    int recentCount, recentNext;
    float calRawA, calXA, calRawB, calXB;
    int calPoints;

    // calibration sampling
    bool sampling;
    float sampleStart, sampleEnd, sampleXTarget, rawSum, openSum;
    int sampleCount;

    public bool IsReady => calPoints >= 2 && baseline > 0.01f;
    public bool IsSampling { get { Poll(); return sampling; } }
    public int Points => calPoints;
    public string Problem { get; private set; } = "";
    public float Responsiveness => 10f;    // webcam gaze is noisy, so follow it more slowly than the mouse

    // ---- IGazeSource ----

    public void Begin()
    {
        if (!Supported) return;
        if (!started) { wg_start(); started = true; }
    }

    public void End()
    {
        if (started) { wg_stop(); started = false; }
        closed = false; closedFrames = 0; sampling = false;
    }

    public Vector2 Gaze
    {
        get
        {
            Poll();
            float x;
            if (calPoints >= 2)
                x = calXA + (heldRaw - calRawA) * (calXB - calXA) / (calRawB - calRawA);
            else
                x = 0.5f;                                // not calibrated: sit in the middle rather than guess a direction
            return new Vector2(Mathf.Clamp01(x), 0.5f);
        }
    }

    public bool BlinkThisFrame
    {
        get { Poll(); return blink; }
    }

    /// <summary>Debug: saves the next camera frame, with the eye boxes and iris marks drawn on it, to this file.</summary>
    public void DumpFrame(string path)
    {
        if (started) wg_dump(path);
    }

    // ---- IGazeCalibration ----

    public void BeginSample(float screenX)
    {
        sampling = true;
        sampleStart = Time.unscaledTime + SampleDelay;
        sampleEnd = sampleStart + SampleSeconds;
        sampleXTarget = screenX;
        rawSum = openSum = 0f;
        sampleCount = 0;
    }

    public string Status
    {
        get
        {
            Poll();
            switch (wg_state())
            {
                case 0: return "Webcam: not started";
                case 1: return "Webcam: waiting for camera permission...";
                case 3: return "Webcam: camera access denied. Allow Unity in System Settings > Privacy > Camera, then restart Unity.";
                case 4: return Supported ? "Webcam: no camera found" : "Webcam: only supported on macOS for now";
            }
            if (v[FaceIdx] < 0.5f) return "Webcam: no face found. Face the camera in good light.";
            return $"Webcam OK   eyes {EyeOpen:0.00} / open {baseline:0.00}   iris {rawGaze:0.00} (L {v[LeftIrisIdx]:0.00} R {v[RightIrisIdx]:0.00})   cal {calRawA:0.00}->{calXA:0.0} {calRawB:0.00}->{calXB:0.0}   head {v[YawIdx]:+0.00;-0.00}   blinks {BlinkCount}";
        }
    }

    public float EyeOpen => (v[LeftOpenIdx] + v[RightOpenIdx]) * 0.5f;

    // ---- per frame ----

    /// <summary>Reads the newest camera data once per Unity frame.</summary>
    void Poll()
    {
        if (frameStamp == Time.frameCount) return;
        frameStamp = Time.frameCount;
        blink = false;
        if (!started) { sampling = false; return; }
        FinishSampleIfDue();

        int frame = wg_poll(v);
        if (frame == lastFrame) return;                 // no new camera frame yet
        lastFrame = frame;
        if (v[FaceIdx] < 0.5f) { closedFrames = 0; return; }

        float open = EyeOpen;
        UpdateBlink(open);
        if (!closed && TryIris(out float iris))
        {
            recent[recentNext] = iris;
            recentNext = (recentNext + 1) % recent.Length;
            if (recentCount < recent.Length) recentCount++;
            float sum = 0f;
            for (int i = 0; i < recentCount; i++) sum += recent[i];
            rawGaze = sum / recentCount;
            heldRaw = rawGaze;                           // mid-blink or a bad frame: keep the last good value
        }
        AccumulateSample(open);
    }

    /// <summary>Iris position inside the eyes, averaged over whichever eyes gave a reading.</summary>
    bool TryIris(out float iris)
    {
        float l = v[LeftIrisIdx], r = v[RightIrisIdx];
        bool hasL = l >= 0f, hasR = r >= 0f;
        iris = hasL && hasR ? (l + r) * 0.5f : hasL ? l : r;
        return hasL || hasR;
    }

    void UpdateBlink(float open)
    {
        if (baseline < 0.01f) baseline = open;
        if (!closed)
        {
            if (open > baseline * ReopenFraction)       // follow slow changes (tired eyes, lighting) while open
                baseline = Mathf.Lerp(baseline, open, 0.02f);

            closedFrames = open < baseline * CloseFraction ? closedFrames + 1 : 0;
            if (closedFrames >= ClosedFramesNeeded)
            {
                closed = true;
                blink = true;
                BlinkCount++;
            }
        }
        else if (open > baseline * ReopenFraction)
        {
            closed = false;
            closedFrames = 0;
        }
    }

    void AccumulateSample(float open)
    {
        if (!sampling || closed || Time.unscaledTime < sampleStart) return;
        rawSum += rawGaze;
        openSum += open;
        sampleCount++;
    }

    void FinishSampleIfDue()
    {
        if (!sampling || Time.unscaledTime < sampleEnd) return;
        sampling = false;
        if (sampleCount < 5) return;                    // no clear reading (face lost, eyes closed): the caller can retry

        float raw = rawSum / sampleCount;
        if (calPoints == 0) { calRawA = raw; calXA = sampleXTarget; baseline = openSum / sampleCount; calPoints = 1; }
        else
        {
            calRawB = raw; calXB = sampleXTarget; calPoints = 2;
            if (Mathf.Abs(calRawB - calRawA) < MinCalibrationSpread)
            {
                Problem = $"Your eyes barely moved between the dots ({calRawA:0.00} vs {calRawB:0.00}). Sit closer, look at the dots without turning your head, and try again.";
                calPoints = 0;
            }
            else Problem = "";
        }
    }
}
