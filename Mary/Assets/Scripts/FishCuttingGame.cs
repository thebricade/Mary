using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// "Cooking Mama meets Before Your Eyes": a cutting-board mini game where your gaze places the
/// cut line and every blink drops the knife. Your left hand holds the fish, so careless cuts
/// cost fingers (or the whole hand).
///
/// Builds its own UI at runtime, so nothing needs to be set up in the scene. Input comes from
/// GazeInput.Source (mouse + Space by default, a webcam source later).
/// </summary>
public class FishCuttingGame : MonoBehaviour
{
    // ---------- access ----------

    static FishCuttingGame instance;
    /// <summary>True while the mini game is on screen.</summary>
    public static bool Active { get; private set; }

    public static FishCuttingGame Instance
    {
        get
        {
            if (instance == null)
                instance = new GameObject("Fish Cutting Game").AddComponent<FishCuttingGame>();
            return instance;
        }
    }

    public struct Result
    {
        public int slices;
        public float evenness;      // 0..1, how close the slices were to the target width
        public int fingersLost;
        public bool lostHand;
        public bool timeUp;
    }

    /// <summary>Raised when a round ends, so other systems can react to how it went.</summary>
    public event Action<Result> Finished;

    // ---------- tuning ----------

    const float PlayTime = 60f;
    const float CanvasW = 1920f;
    const float FishW = 1100f, FishH = 260f;
    const float HandEnd = 0.105f;                              // u from 0 to here: heel of the hand = hand lost
    static readonly float[] FingerStart = { 0.125f, 0.185f, 0.245f, 0.305f };
    const float FingerWidth = 0.032f;                          // fraction of the fish length
    const float TargetSlice = 0.055f;                          // ideal slice width (fraction of fish length)
    const float MinGap = 0.012f;                               // cuts closer than this to another cut do nothing
    const float BlinkCooldown = 0.25f;
    const float PieceGap = 6f;                                 // pixels between slices
    const float ResultLockTime = 1f;                           // so a blink-spam doesn't skip the result screen

    // ---------- state ----------

    enum Phase { Intro, Calibrate, Playing, Result }
    Phase phase;
    FirstPersonController player;
    IGazeSource gaze;

    float timeLeft, cooldown, gazeX, resultTime, knifeT = 1f, knifeX;
    int openedFrame;
    readonly List<float> cuts = new List<float>();             // u of every cut, ascending
    readonly bool[] fingerLost = new bool[FingerStart.Length];
    int FingersLost { get { int n = 0; foreach (var b in fingerLost) if (b) n++; return n; } }
    bool handLost;

    // ---------- ui ----------

    Texture2D fishTex, capsuleTex, roundTex, circleTex;
    GameObject root, handRoot, bloodFlash;
    RectTransform fishRoot, knife, guideLine;
    readonly List<RawImage> pieces = new List<RawImage>();
    readonly RawImage[] fingers = new RawImage[FingerStart.Length];
    readonly RectTransform[] fingerBlood = new RectTransform[FingerStart.Length];
    readonly RectTransform[] fingerTips = new RectTransform[FingerStart.Length];
    Text title, timerText, statsText, message, centerText, debugText;
    RectTransform calDot;
    int calStep;
    bool calWaiting, calRetry;

    float FishLeft => -FishW / 2f;
    float UToX(float u) => FishLeft + u * FishW;

    // ---------- lifecycle ----------

    void Awake()
    {
        instance = this;
        Build();
        root.SetActive(false);
    }

    public void Open(FirstPersonController controller)
    {
        player = controller;
        player.InputLocked = true;
        gaze = GazeInput.Source;
        gaze.Begin();
        Active = true;
        openedFrame = Time.frameCount;
        ResetRound();
        phase = Phase.Intro;
        ShowIntro();
        centerPanel.SetActive(true);
        root.SetActive(true);
    }

    void ShowIntro()
    {
        bool webcam = GazeInput.UseWebcam;
        centerText.text =
            "FISH CUTTING\n\n" +
            "Your eyes place the cut line.\nEvery blink drops the knife.\n\n" +
            "Aim for the dotted marks, and keep clear of the fingers\nholding the fish.\n\n" +
            (webcam ? "Input: WEBCAM  (keep your head still, face the camera)\n" + (gaze as IGazeCalibration)?.Status
                    : "Input: MOUSE  (move to aim, Space or click to blink)") +
            "\n\n[E]  Begin      [W]  Switch input      [Esc]  Leave";
    }

    void SwitchInput()
    {
        gaze?.End();
        GazeInput.UseWebcam = !GazeInput.UseWebcam;
        gaze = GazeInput.Source;
        gaze.Begin();
        ShowIntro();
    }

    void Close()
    {
        gaze?.End();
        Active = false;
        root.SetActive(false);
        if (player != null)
        {
            player.InputLocked = false;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    void ResetRound()
    {
        cuts.Clear();
        for (int i = 0; i < fingerLost.Length; i++) fingerLost[i] = false;
        handLost = false;
        timeLeft = PlayTime;
        cooldown = 0f;
        knifeT = 1f;
        gazeX = Mathf.Lerp(UToX(0.2f), UToX(1f), 0.5f);
        message.text = "";
        bloodFlash.SetActive(false);
        handRoot.SetActive(true);
        for (int i = 0; i < fingers.Length; i++) RestoreFinger(i);
        RebuildPieces();
        RefreshStats();
    }

    // ---------- frame loop ----------

    void Update()
    {
        if (!Active || Time.frameCount == openedFrame) return;

        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) { Close(); return; }
        if (kb != null && kb.f8Key.wasPressedThisFrame && gaze is WebcamGazeSource cam)
        {
            const string path = "/tmp/webcam_debug.png";
            cam.DumpFrame(path);
            message.text = "Saved a camera snapshot to " + path;
        }

        switch (phase)
        {
            case Phase.Intro:
                if (Keyboard.current != null && Keyboard.current.wKey.wasPressedThisFrame) SwitchInput();
                else if (GazeInput.UseWebcam) ShowIntro();   // keeps the camera status current
                if (AdvancePressed()) BeginFromIntro();
                break;
            case Phase.Calibrate:
                Calibrate();
                break;
            case Phase.Playing:
                Tick();
                break;
            case Phase.Result:
                if (Time.unscaledTime - resultTime > ResultLockTime && AdvancePressed()) Close();
                break;
        }

        AnimateKnife();
    }

    static bool AdvancePressed()
    {
        var k = Keyboard.current;
        var m = Mouse.current;
        return (k != null && (k.eKey.wasPressedThisFrame || k.enterKey.wasPressedThisFrame || k.spaceKey.wasPressedThisFrame))
            || (m != null && m.leftButton.wasPressedThisFrame);
    }

    void BeginFromIntro()
    {
        if (gaze is IGazeCalibration cal && !cal.IsReady)
        {
            phase = Phase.Calibrate;
            calStep = 0;
            calWaiting = false;
            calRetry = false;
            calDot.gameObject.SetActive(true);
            return;
        }
        StartPlaying();
    }

    // Look at a dot on the left, then one on the right, so the camera's idea of "where the eyes point"
    // can be matched to the screen. Press E (not a blink) once you are looking at the dot.
    void Calibrate()
    {
        var cal = (IGazeCalibration)gaze;
        float[] spots = { 0.2f, 0.8f };
        string[] names = { "LEFT", "RIGHT" };

        if (calWaiting)
        {
            if (cal.IsSampling) { centerText.text = "Hold still, keep looking at the dot..."; return; }
            calWaiting = false;
            if (cal.Points > calStep) calStep++;
            else { calRetry = true; if (cal.Points == 0) calStep = 0; }   // no clear reading, or the two dots didn't differ enough: start over
        }
        if (calStep >= spots.Length)
        {
            calDot.gameObject.SetActive(false);
            StartPlaying();
            return;
        }

        float x = (spots[calStep] - 0.5f) * CanvasW;
        calDot.anchoredPosition = new Vector2(x, 0f);
        centerText.text = $"CALIBRATION  {calStep + 1}/2\n\nLook at the {names[calStep]} red dot\nwith your eyes open and head still,\nthen press  [E] and keep looking at the dot\n\n" +
                          (calRetry ? (string.IsNullOrEmpty(cal.Problem) ? "That didn't get a clear reading. Face the camera and try again." : cal.Problem) + "\n" : "") + cal.Status;
        if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame)
        {
            cal.BeginSample(spots[calStep]);
            calWaiting = true;
        }
    }

    void StartPlaying()
    {
        phase = Phase.Playing;
        centerPanel.SetActive(false);
        message.text = "Look where you want to cut, then blink.";
    }

    void Tick()
    {
        timeLeft -= Time.deltaTime;
        if (timeLeft <= 0f) { timeLeft = 0f; EndRound(true); return; }

        // Smoothed gaze -> position on the board.
        float targetX = (gaze.Gaze.x - 0.5f) * CanvasW;
        gazeX = Mathf.Lerp(gazeX, targetX, 1f - Mathf.Exp(-gaze.Responsiveness * Time.deltaTime));
        float u = Mathf.Clamp01((gazeX - FishLeft) / FishW);
        guideLine.anchoredPosition = new Vector2(UToX(u), 0f);

        cooldown -= Time.deltaTime;
        if (gaze.BlinkThisFrame && cooldown <= 0f)
        {
            cooldown = BlinkCooldown;
            Cut(u);
        }

        timerText.text = Mathf.CeilToInt(timeLeft).ToString();
        debugText.text = (gaze as IGazeCalibration)?.Status ?? "";
    }

    // ---------- cutting ----------

    void Cut(float u)
    {
        knifeT = 0f;
        knifeX = UToX(u);

        if (u < HandEnd)
        {
            handLost = true;
            Flash();
            message.text = "YOUR HAND!";
            EndRound(false);
            return;
        }

        for (int k = 0; k < FingerStart.Length; k++)
        {
            if (!fingerLost[k] && u >= FingerStart[k] && u <= FingerStart[k] + FingerWidth)
            {
                fingerLost[k] = true;
                LoseFinger(k);
                Flash();
                message.text = FingersLost >= FingerStart.Length ? "That was every finger..." : "OUCH! There goes a finger.";
                RefreshStats();
                return;
            }
        }

        foreach (float c in cuts)
            if (Mathf.Abs(c - u) < MinGap) { message.text = "Too close to the last cut."; return; }

        cuts.Add(u);
        cuts.Sort();
        RebuildPieces();
        message.text = "Slice!";
        RefreshStats();
    }

    void EndRound(bool timeUp)
    {
        phase = Phase.Result;
        resultTime = Time.unscaledTime;

        var result = new Result
        {
            slices = cuts.Count,
            evenness = Evenness(),
            fingersLost = FingersLost,
            lostHand = handLost,
            timeUp = timeUp
        };

        string headline = handLost ? "Ouch! No more cooking for you."
            : result.evenness > 0.8f && result.fingersLost == 0 ? "A clean job. Beautiful."
            : result.fingersLost > 0 ? "It will do, if you ignore the blood."
            : "Edible, at least.";

        centerText.text =
            headline + "\n\n" +
            $"Slices: {result.slices}\n" +
            $"Evenness: {Mathf.RoundToInt(result.evenness * 100f)}%\n" +
            $"Fingers lost: {result.fingersLost}\n\n" +
            "[E]  Continue";
        centerPanel.SetActive(true);

        Finished?.Invoke(result);
    }

    float Evenness()
    {
        if (cuts.Count == 0) return 0f;
        float total = 0f, previous = 1f;
        for (int i = cuts.Count - 1; i >= 0; i--)
        {
            total += Mathf.Abs((previous - cuts[i]) - TargetSlice) / TargetSlice;
            previous = cuts[i];
        }
        return Mathf.Clamp01(1f - total / cuts.Count);
    }

    // ---------- visuals ----------

    void RebuildPieces()
    {
        var bounds = new List<float> { 0f };
        bounds.AddRange(cuts);
        bounds.Add(1f);

        for (int i = 0; i < bounds.Count - 1; i++)
        {
            if (i >= pieces.Count)
            {
                var img = Raw(fishRoot, "Piece", fishTex, Color.white, Vector2.zero, Vector2.zero);
                pieces.Add(img);
            }
            var piece = pieces[i];
            piece.gameObject.SetActive(true);
            float u0 = bounds[i], u1 = bounds[i + 1];
            piece.uvRect = new Rect(u0, 0f, u1 - u0, 1f);
            var rt = piece.rectTransform;
            rt.sizeDelta = new Vector2((u1 - u0) * FishW, FishH);
            rt.anchoredPosition = new Vector2(UToX((u0 + u1) / 2f) + PieceGap * i, 0f);
        }
        for (int i = bounds.Count - 1; i < pieces.Count; i++)
            pieces[i].gameObject.SetActive(false);
    }

    void LoseFinger(int k)
    {
        var f = fingers[k].rectTransform;
        f.sizeDelta = new Vector2(f.sizeDelta.x, 120f);            // a short stump
        fingerBlood[k].gameObject.SetActive(true);
        fingerTips[k].gameObject.SetActive(true);
    }

    void RestoreFinger(int k)
    {
        var f = fingers[k].rectTransform;
        f.sizeDelta = new Vector2(f.sizeDelta.x, FishH + 130f);
        fingerBlood[k].gameObject.SetActive(false);
        fingerTips[k].gameObject.SetActive(false);
    }

    void Flash()
    {
        bloodFlash.SetActive(true);
        flashUntil = Time.unscaledTime + 0.25f;
        if (handLost) handRoot.SetActive(false);
    }

    float flashUntil;

    void AnimateKnife()
    {
        if (bloodFlash.activeSelf && Time.unscaledTime > flashUntil && phase != Phase.Result)
            bloodFlash.SetActive(false);

        const float down = 0.07f, up = 0.18f;
        const float top = 420f, bottom = -120f;                    // y of the blade tip
        knifeT += Time.deltaTime;
        float y;
        if (knifeT < down) y = Mathf.Lerp(top, bottom, knifeT / down);
        else if (knifeT < down + up) y = Mathf.Lerp(bottom, top, (knifeT - down) / up);
        else y = 1000f;                                            // parked off screen
        knife.anchoredPosition = new Vector2(knifeX, y + 260f);    // the tip sits 260 below the knife's centre
    }

    void RefreshStats()
    {
        statsText.text = $"Slices {cuts.Count}      Fingers {FingerStart.Length - FingersLost}/{FingerStart.Length}";
    }

    // ---------- construction ----------

    void Build()
    {
        fishTex = MakeFishTexture();
        capsuleTex = MakeCapsuleTexture();
        roundTex = MakeRoundRectTexture();
        circleTex = MakeCircleTexture();

        var canvasGo = new GameObject("Canvas");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0f;                            // always 1920 wide, so gaze maps predictably

        var rootRt = R(canvasGo.transform, "Game", new Vector2(0, 0), Vector2.zero);
        rootRt.anchorMin = Vector2.zero;
        rootRt.anchorMax = Vector2.one;
        rootRt.offsetMin = rootRt.offsetMax = Vector2.zero;
        root = rootRt.gameObject;

        // Backdrop and board
        var dim = Raw(root.transform, "Dim", Texture2D.whiteTexture, new Color(0.05f, 0.04f, 0.04f, 0.92f), Vector2.zero, Vector2.zero);
        dim.rectTransform.anchorMin = Vector2.zero;
        dim.rectTransform.anchorMax = Vector2.one;
        dim.rectTransform.offsetMin = dim.rectTransform.offsetMax = Vector2.zero;

        var board = R(root.transform, "Board", new Vector2(1500, 560), new Vector2(0, -40));
        Raw(board, "Wood", roundTex, new Color(0.62f, 0.45f, 0.28f), new Vector2(1500, 560), Vector2.zero);
        Raw(board, "Grain", roundTex, new Color(0.55f, 0.39f, 0.24f), new Vector2(1440, 500), Vector2.zero);

        // Fish and target marks
        fishRoot = R(board, "Fish", new Vector2(FishW, FishH), Vector2.zero);
        for (int k = 1; k <= Mathf.FloorToInt((1f - 0.345f) / TargetSlice); k++)
        {
            float x = UToX(1f - k * TargetSlice);
            Raw(board, "Mark" + k, Texture2D.whiteTexture, new Color(1, 1, 1, 0.45f),
                new Vector2(3f, FishH + 36f), new Vector2(x, 0f));
        }

        BuildHand(board);

        // Cut line and knife
        guideLine = Raw(board, "Gaze Line", Texture2D.whiteTexture, new Color(1f, 0.25f, 0.25f, 0.9f),
                        new Vector2(4f, FishH + 120f), Vector2.zero).rectTransform;
        Raw(guideLine, "Eye", circleTex, new Color(1f, 0.25f, 0.25f, 1f), new Vector2(26, 26), new Vector2(0, (FishH + 120f) / 2f + 18f));

        knife = R(board, "Knife", new Vector2(70, 520), new Vector2(0, 1200));
        Raw(knife, "Blade", roundTex, new Color(0.85f, 0.87f, 0.9f), new Vector2(18, 330), new Vector2(0, -95f));
        Raw(knife, "Handle", roundTex, new Color(0.25f, 0.15f, 0.1f), new Vector2(34, 160), new Vector2(0, 170f));

        // Text
        title = Label(root.transform, "Title", 44, FontStyle.Bold, TextAnchor.MiddleCenter, new Vector2(0, 460), new Vector2(1000, 70));
        title.text = "FISH CUTTING";
        timerText = Label(root.transform, "Timer", 70, FontStyle.Bold, TextAnchor.MiddleRight, new Vector2(760, 440), new Vector2(260, 100));
        statsText = Label(root.transform, "Stats", 30, FontStyle.Normal, TextAnchor.MiddleLeft, new Vector2(-560, 440), new Vector2(760, 60));
        message = Label(root.transform, "Message", 34, FontStyle.Italic, TextAnchor.MiddleCenter, new Vector2(0, -400), new Vector2(1400, 60));
        message.color = new Color(1f, 0.9f, 0.6f);

        debugText = Label(root.transform, "Debug", 22, FontStyle.Normal, TextAnchor.MiddleLeft, new Vector2(-500, -480), new Vector2(940, 40));
        debugText.color = new Color(1, 1, 1, 0.6f);
        calDot = Raw(root.transform, "Calibration Dot", circleTex, new Color(1f, 0.15f, 0.15f), new Vector2(60, 60), Vector2.zero).rectTransform;
        calDot.gameObject.SetActive(false);

        // Intro / result text sits in a panel over the board.
        var panel = Raw(root.transform, "Panel", roundTex, new Color(0, 0, 0, 0.8f), new Vector2(1000, 560), new Vector2(0, -20));
        centerText = Label(panel.transform, "Center Text", 38, FontStyle.Normal, TextAnchor.MiddleCenter, Vector2.zero, new Vector2(920, 500));
        centerPanel = panel.gameObject;

        bloodFlash = Raw(root.transform, "Blood Flash", Texture2D.whiteTexture, new Color(0.8f, 0f, 0f, 0.35f), Vector2.zero, Vector2.zero).gameObject;
        var fr = (RectTransform)bloodFlash.transform;
        fr.anchorMin = Vector2.zero; fr.anchorMax = Vector2.one; fr.offsetMin = fr.offsetMax = Vector2.zero;
        bloodFlash.SetActive(false);
    }

    GameObject centerPanel;

    void BuildHand(RectTransform board)
    {
        var skin = new Color(0.96f, 0.76f, 0.62f);
        var skinShade = new Color(0.9f, 0.67f, 0.53f);
        handRoot = R(board, "Left Hand", Vector2.zero, Vector2.zero).gameObject;
        var hr = (RectTransform)handRoot.transform;

        float barY = -FishH / 2f - 85f;
        Raw(hr, "Sleeve", roundTex, new Color(0.25f, 0.35f, 0.6f), new Vector2(520, 170), new Vector2(FishLeft - 400f, barY - 20f));
        Raw(hr, "Forearm", roundTex, skinShade, new Vector2(420, 140), new Vector2(FishLeft - 130f, barY - 8f));
        Raw(hr, "Knuckles", roundTex, skin, new Vector2(0.37f * FishW, 120f), new Vector2(FishLeft + 0.172f * FishW, barY));
        Raw(hr, "Heel", roundTex, skin, new Vector2(HandEnd * FishW + 14f, FishH + 70f), new Vector2(FishLeft + HandEnd * FishW / 2f, 8f));

        for (int k = 0; k < FingerStart.Length; k++)
        {
            float cx = UToX(FingerStart[k] + FingerWidth / 2f);
            float bottom = -FishH / 2f - 70f;

            var f = Raw(hr, "Finger" + k, capsuleTex, skin, new Vector2(FingerWidth * FishW + 4f, FishH + 130f),
                        new Vector2(cx, bottom), new Vector2(0.5f, 0f));
            fingers[k] = f;

            var blood = Raw(hr, "Blood" + k, circleTex, new Color(0.75f, 0.05f, 0.05f), new Vector2(46, 36), new Vector2(cx, bottom + 118f));
            fingerBlood[k] = blood.rectTransform;
            blood.gameObject.SetActive(false);

            var tip = Raw(hr, "Tip" + k, capsuleTex, skin, new Vector2(FingerWidth * FishW, 90f), new Vector2(cx + 70f, bottom + 230f - 20f * k));
            tip.rectTransform.localRotation = Quaternion.Euler(0, 0, 38f - 20f * k);
            fingerTips[k] = tip.rectTransform;
            tip.gameObject.SetActive(false);
        }
    }

    // ---------- ui helpers ----------

    static RectTransform R(Transform parent, string name, Vector2 size, Vector2 pos, Vector2? pivot = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = pivot ?? new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;
        return rt;
    }

    static RawImage Raw(Transform parent, string name, Texture tex, Color color, Vector2 size, Vector2 pos, Vector2? pivot = null)
    {
        var rt = R(parent, name, size, pos, pivot);
        var img = rt.gameObject.AddComponent<RawImage>();
        img.texture = tex;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    static Text Label(Transform parent, string name, int size, FontStyle style, TextAnchor anchor, Vector2 pos, Vector2 rectSize)
    {
        var rt = R(parent, name, rectSize, pos);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = anchor;
        t.color = Color.white;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    // ---------- procedural art ----------

    static Texture2D NewTex(int w, int h, Func<float, float, Color> shade)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = shade((x + 0.5f) / w, (y + 0.5f) / h);
        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    static Texture2D MakeFishTexture()
    {
        const int w = 512, h = 160;
        var back = new Color(0.30f, 0.45f, 0.58f);
        var belly = new Color(0.88f, 0.90f, 0.92f);
        return NewTex(w, h, (fx, fy) =>
        {
            float ex = (fx - 0.45f) / 0.45f, ey = (fy - 0.5f) / 0.42f;
            bool body = ex * ex + ey * ey <= 1f;
            bool tail = fx > 0.8f && Mathf.Abs(fy - 0.5f) <= (fx - 0.78f) / 0.22f * 0.45f;
            if (!body && !tail) return Color.clear;

            Color c = Color.Lerp(belly, back, Mathf.SmoothStep(0.3f, 0.75f, fy));
            if (Mathf.Abs(fy - 0.52f) < 0.012f) c = Color.Lerp(c, new Color(0.2f, 0.3f, 0.4f), 0.6f);   // lateral line
            if (tail) c = Color.Lerp(c, new Color(0.25f, 0.4f, 0.5f), 0.5f);

            float dx = (fx - 0.10f) * w, dy = (fy - 0.58f) * h;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            if (d < 7f) c = new Color(0.05f, 0.05f, 0.08f);
            else if (d < 13f) c = Color.white;
            return c;
        });
    }

    static Texture2D MakeCapsuleTexture()
    {
        const int w = 64, h = 128;
        return NewTex(w, h, (fx, fy) =>
        {
            float x = fx * w, y = fy * h, r = 30f;
            float cy = Mathf.Clamp(y, 32f, 96f);
            float d = Mathf.Sqrt((x - 32f) * (x - 32f) + (y - cy) * (y - cy));
            return new Color(1, 1, 1, Mathf.Clamp01(r + 0.5f - d));
        });
    }

    static Texture2D MakeRoundRectTexture()
    {
        const int s = 128;
        return NewTex(s, s, (fx, fy) =>
        {
            float x = fx * s, y = fy * s, r = 26f;
            float qx = Mathf.Max(Mathf.Abs(x - s / 2f) - (s / 2f - r), 0f);
            float qy = Mathf.Max(Mathf.Abs(y - s / 2f) - (s / 2f - r), 0f);
            float d = Mathf.Sqrt(qx * qx + qy * qy);
            return new Color(1, 1, 1, Mathf.Clamp01(r + 0.5f - d));
        });
    }

    static Texture2D MakeCircleTexture()
    {
        const int s = 64;
        return NewTex(s, s, (fx, fy) =>
        {
            float d = Mathf.Sqrt((fx - 0.5f) * (fx - 0.5f) + (fy - 0.5f) * (fy - 0.5f)) * s;
            return new Color(1, 1, 1, Mathf.Clamp01(s / 2f + 0.5f - d));
        });
    }
}
