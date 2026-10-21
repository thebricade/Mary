using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// The Outspoken dialogue mechanic. Characters speak on their own and move on after a short pause.
/// Nothing is shown about what Christine could say. Pressing Space cuts the character off, and only then
/// are the lines that fit that exact moment shown: 1-4, Up/Down or the mouse wheel to choose, Space or Enter to say it.
///
/// Builds its own UI at runtime, so nothing has to be set up in the scene.
/// </summary>
public class DialogueUI : MonoBehaviour
{
    static DialogueUI instance;
    public static DialogueUI Instance
    {
        get
        {
            if (instance == null)
                instance = new GameObject("Dialogue UI").AddComponent<DialogueUI>();
            return instance;
        }
    }

    public const string PlayerName = "Christine";

    /// <summary>Everything about the moment Christine cut in. Hook this to react to how, and when, the player interrupts.</summary>
    public struct InterruptInfo
    {
        public NPC npc;
        public string nodeId;
        public int beat;            // which beat of the line she cut into (1 based); beatCount + 1 means after the line ended
        public int beatCount;
        public bool afterLine;      // she waited for the NPC to finish
        public float fraction;      // how far through the line she was, 0..1
        public string playerLine;
        public string target;
    }
    public static event Action<InterruptInfo> Interrupted;

    public bool IsOpen { get; private set; }

    // ---------- tuning ----------

    const float PlayerCharsPerSecond = 55f;     // Christine's own lines; each character's speed is set on the NPC
    const float DefaultPause = 3f;          // seconds the NPC waits after finishing a line (nodes can override with "~ seconds")
    const float PlayerHoldMin = 0.9f;       // how long Christine's own line stays up once spoken
    const float PlayerHoldPerChar = 0.02f;
    const int MaxOptions = 4;
    const float ChooseLockTime = 0.3f;

    // ---------- state ----------

    enum Mode { Npc, Choosing, Player }
    Mode mode;
    NPC npc;
    DialogueScript script;
    DialogueNode node;
    FirstPersonController player;
    int openedFrame;

    float revealed;                         // characters of the current line shown so far
    float pauseLeft, pauseTotal;
    List<Interjection> options = new List<Interjection>();   // what Christine can say, filled in when she interrupts
    int selected;
    int interruptShown, interruptBeat;
    bool interruptAfterLine;
    float chooseLock;                       // brief delay so a double tap on Space doesn't pick the first line by accident
    float noticeUntil;                      // when the "nothing to say" flash ends

    string playerLine, playerTarget;
    float playerHold;

    // ---------- ui ----------

    GameObject lookCard, dialoguePanel;
    Text lookName, lookTitle, lookPrompt;
    Text speakerText, ghostText, bodyText, choicesText, hintText;
    RectTransform barFill;

    void Awake()
    {
        instance = this;
        Build();
    }

    // ---------- public ----------

    public void ShowLookCard(IInteractable target)
    {
        bool show = target != null && !IsOpen;
        if (lookCard.activeSelf != show) lookCard.SetActive(show);
        if (!show) return;
        lookName.text = target.DisplayName;
        lookTitle.text = target.Subtitle;
        lookPrompt.text = target.Prompt;
    }

    public void Open(NPC target, FirstPersonController controller)
    {
        var s = target.GetScript();
        if (s == null || s.Start == null) return;

        npc = target;
        script = s;
        player = controller;
        player.InputLocked = true;
        IsOpen = true;
        openedFrame = Time.frameCount;
        lookCard.SetActive(false);
        dialoguePanel.SetActive(true);
        EnterNode(script.Start);
    }

    public void Close()
    {
        IsOpen = false;
        dialoguePanel.SetActive(false);
        if (player != null) player.InputLocked = false;
        npc = null;
        script = null;
        node = null;
    }

    // ---------- flow ----------

    // A line's own "@ speed" wins, then the character's talk speed.
    float SpeedOf(DialogueNode n) => n.speed > 0f ? n.speed : Mathf.Max(1f, npc.talkSpeed);

    void EnterNode(DialogueNode next)
    {
        if (next == null) { Close(); return; }
        node = next;
        mode = Mode.Npc;
        revealed = 0f;
        pauseTotal = pauseLeft = node.pause >= 0f ? node.pause : DefaultPause;
        options = new List<Interjection>();
        selected = 0;
        speakerText.text = node.speaker;
        ghostText.text = "";
        bodyText.text = "";
        choicesText.text = "";
        hintText.text = "[Space] interrupt";
    }

    void Update()
    {
        if (!IsOpen || Time.frameCount == openedFrame) return;

        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) { Close(); return; }

        if (noticeUntil > 0f && Time.unscaledTime > noticeUntil) { noticeUntil = 0f; ghostText.text = ""; }

        if (mode == Mode.Npc) UpdateNpc(kb);
        else if (mode == Mode.Choosing) UpdateChoosing(kb);
        else UpdatePlayer();
    }

    void UpdateNpc(Keyboard kb)
    {
        int length = node.text.Length;
        bool typing = revealed < length;
        if (typing) revealed = Mathf.Min(length, revealed + SpeedOf(node) * Time.deltaTime);
        else pauseLeft -= Time.deltaTime;

        int shown = Mathf.FloorToInt(revealed);
        bodyText.text = node.text.Substring(0, shown);
        SetBar(typing ? 1f : Mathf.Clamp01(pauseLeft / Mathf.Max(0.01f, pauseTotal)));

        if (kb != null && kb.spaceKey.wasPressedThisFrame)
        {
            // Only now do we work out what Christine could say at this exact moment.
            int beat = node.BeatAt(shown);
            var fits = new List<Interjection>();
            foreach (var i in node.interjections)
                if (i.Matches(beat, !typing) && fits.Count < MaxOptions) fits.Add(i);

            if (fits.Count > 0) { BeginChoosing(fits, shown, beat, !typing); return; }

            ghostText.text = "(nothing to say)";            // she has no line for this moment
            noticeUntil = Time.unscaledTime + 0.8f;
        }

        if (!typing && pauseLeft <= 0f) EnterNode(script.Get(node.next));
    }

    // The character is cut off mid-sentence and the options for that moment appear.
    void BeginChoosing(List<Interjection> fits, int shown, int beat, bool afterLine)
    {
        options = fits;
        selected = 0;
        interruptShown = shown;
        interruptBeat = beat;
        interruptAfterLine = afterLine;
        chooseLock = ChooseLockTime;
        mode = Mode.Choosing;

        bodyText.text = afterLine ? node.text : node.text.Substring(0, shown).TrimEnd() + "\u2014";
        ghostText.text = "";
        SetBar(0f);
        RenderChoices();
    }

    void UpdateChoosing(Keyboard kb)
    {
        chooseLock -= Time.deltaTime;

        int step = 0;
        if (kb != null)
        {
            if (kb.downArrowKey.wasPressedThisFrame) step = 1;
            else if (kb.upArrowKey.wasPressedThisFrame) step = -1;

            Key[] digits = { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4 };
            for (int i = 0; i < options.Count && i < digits.Length; i++)
                if (kb[digits[i]].wasPressedThisFrame) { Say(options[i]); return; }
        }
        var mouse = Mouse.current;
        if (mouse != null)
        {
            float wheel = mouse.scroll.ReadValue().y;
            if (wheel < -0.1f) step = 1;
            else if (wheel > 0.1f) step = -1;
        }
        if (step != 0) { selected = (selected + step + options.Count) % options.Count; RenderChoices(); }

        bool confirm = kb != null && (kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame);
        if (confirm && chooseLock <= 0f) Say(options[selected]);
    }

    void Say(Interjection chosen)
    {
        string said = interruptAfterLine ? node.text : node.text.Substring(0, interruptShown).TrimEnd() + "\u2014";
        ghostText.text = $"{node.speaker}: \u201C{said}\u201D";

        playerLine = chosen.text;
        playerTarget = chosen.target ?? node.next;
        mode = Mode.Player;
        revealed = 0f;
        playerHold = PlayerHoldMin + playerLine.Length * PlayerHoldPerChar;
        speakerText.text = PlayerName;
        bodyText.text = "";
        choicesText.text = "";
        hintText.text = "";
        SetBar(0f);

        Interrupted?.Invoke(new InterruptInfo
        {
            npc = npc,
            nodeId = node.id,
            beat = interruptAfterLine ? node.BeatCount + 1 : interruptBeat,
            beatCount = node.BeatCount,
            afterLine = interruptAfterLine,
            fraction = node.text.Length == 0 ? 1f : Mathf.Clamp01(interruptShown / (float)node.text.Length),
            playerLine = playerLine,
            target = playerTarget
        });
    }

    void UpdatePlayer()
    {
        int length = playerLine.Length;
        if (revealed < length)
        {
            revealed = Mathf.Min(length, revealed + PlayerCharsPerSecond * Time.deltaTime);
            bodyText.text = playerLine.Substring(0, Mathf.FloorToInt(revealed));
            return;
        }
        playerHold -= Time.deltaTime;
        if (playerHold <= 0f) EnterNode(script.Get(playerTarget));
    }

    // ---------- display ----------

    void RenderChoices()
    {
        if (options.Count == 0)
        {
            choicesText.text = "";
            hintText.text = "";
            return;
        }
        var sb = new StringBuilder();
        for (int i = 0; i < options.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(i == selected
                ? $"<color=#FFD966>▶  {options[i].text}</color>"
                : $"<color=#B8B8B8>     {options[i].text}</color>");
        }
        choicesText.text = sb.ToString();
        hintText.text = options.Count > 1 ? "[↑/↓] choose      [Space] interject" : "[Space] interject";
    }

    void SetBar(float fraction)
    {
        barFill.anchorMax = new Vector2(0.03f + 0.94f * Mathf.Clamp01(fraction), barFill.anchorMax.y);
    }

    // ---------- UI construction ----------

    void Build()
    {
        var canvasGo = new GameObject("Canvas");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        // "Who is this" card, just below the crosshair.
        lookCard = Panel(canvasGo.transform, "Look Card", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                         new Vector2(0, -90), new Vector2(520, 130), new Color(0, 0, 0, 0.55f));
        lookName = Label(lookCard.transform, "Name", 34, FontStyle.Bold, TextAnchor.MiddleCenter,
                         new Vector2(0, 0.52f), new Vector2(1, 1));
        lookTitle = Label(lookCard.transform, "Title", 22, FontStyle.Italic, TextAnchor.MiddleCenter,
                          new Vector2(0, 0.27f), new Vector2(1, 0.55f));
        lookTitle.color = new Color(1, 1, 1, 0.75f);
        lookPrompt = Label(lookCard.transform, "Prompt", 22, FontStyle.Normal, TextAnchor.MiddleCenter,
                           new Vector2(0, 0), new Vector2(1, 0.3f));
        lookPrompt.text = "[E]  Talk";
        lookPrompt.color = new Color(1f, 0.85f, 0.4f);
        lookCard.SetActive(false);

        // Dialogue box along the bottom.
        dialoguePanel = Panel(canvasGo.transform, "Dialogue Panel", new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                              new Vector2(0, 50), new Vector2(1400, 360), new Color(0, 0, 0, 0.82f));
        var p = dialoguePanel.transform;

        speakerText = Label(p, "Speaker", 36, FontStyle.Bold, TextAnchor.UpperLeft, new Vector2(0.03f, 0.86f), new Vector2(0.97f, 0.98f));
        speakerText.color = new Color(1f, 0.85f, 0.4f);
        ghostText = Label(p, "Interrupted", 22, FontStyle.Italic, TextAnchor.UpperLeft, new Vector2(0.03f, 0.74f), new Vector2(0.97f, 0.86f));
        ghostText.color = new Color(1, 1, 1, 0.5f);
        bodyText = Label(p, "Text", 30, FontStyle.Normal, TextAnchor.UpperLeft, new Vector2(0.03f, 0.46f), new Vector2(0.97f, 0.74f));

        // Time left before the character moves on (the window to interject).
        var barBack = Panel(p, "Bar Back", new Vector2(0.5f, 0), new Vector2(0.5f, 0), Vector2.zero, Vector2.zero, new Color(1, 1, 1, 0.12f));
        StretchTo((RectTransform)barBack.transform, new Vector2(0.03f, 0.43f), new Vector2(0.97f, 0.455f));
        var fill = Panel(p, "Bar Fill", new Vector2(0.5f, 0), new Vector2(0.5f, 0), Vector2.zero, Vector2.zero, new Color(1f, 0.85f, 0.4f, 0.8f));
        barFill = (RectTransform)fill.transform;
        StretchTo(barFill, new Vector2(0.03f, 0.43f), new Vector2(0.97f, 0.455f));

        choicesText = Label(p, "Choices", 26, FontStyle.Normal, TextAnchor.UpperLeft, new Vector2(0.03f, 0.13f), new Vector2(0.97f, 0.41f));
        choicesText.supportRichText = true;
        hintText = Label(p, "Hint", 20, FontStyle.Normal, TextAnchor.LowerRight, new Vector2(0.03f, 0.02f), new Vector2(0.97f, 0.13f));
        hintText.color = new Color(1, 1, 1, 0.55f);
        dialoguePanel.SetActive(false);
    }

    static void StretchTo(RectTransform rt, Vector2 min, Vector2 max)
    {
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    static GameObject Panel(Transform parent, string name, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = position;
        rt.sizeDelta = size;
        go.GetComponent<Image>().color = color;
        return go;
    }

    static Text Label(Transform parent, string name, int size, FontStyle style, TextAnchor anchor, Vector2 min, Vector2 max)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var t = go.GetComponent<Text>();
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
}
