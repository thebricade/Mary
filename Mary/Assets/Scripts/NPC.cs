using UnityEngine;

[System.Serializable]
public class DialogueLine
{
    [TextArea(2, 6)] public string text;
}

/// <summary>
/// A character the player can walk up to and talk to. Put it on the root of an NPC prefab,
/// with a collider somewhere in its children so the player's look-ray can hit it.
/// </summary>
public class NPC : MonoBehaviour, IInteractable
{
    [Header("Who")]
    public string displayName = "Unnamed";
    [Tooltip("Short description shown under the name when the player looks at them.")]
    public string title = "";

    [Header("Look")]
    [Tooltip("Body colour, applied at runtime so every NPC can share one material.")]
    public Color tint = Color.white;

    [Header("Dialogue")]
    [Tooltip("How fast this character speaks, in characters per second. A line in the script can override it with '@ speed'.")]
    [Min(1f)] public float talkSpeed = 42f;
    [Tooltip("Branching script with interjections (see Assets/Dialogue/_Format.txt). If empty, the simple Lines below are used.")]
    public TextAsset dialogue;
    [Tooltip("Plain lines, spoken one after another. Used only when no Dialogue script is assigned.")]
    public DialogueLine[] lines;

    /// <summary>Parsed each time a conversation starts, so edits to the script file show up straight away.</summary>
    public DialogueScript GetScript()
    {
        if (dialogue != null)
        {
            var script = DialogueScript.Parse(dialogue.text);
            foreach (var warning in script.warnings)
                Debug.LogWarning($"[{dialogue.name}] {warning}", dialogue);
            return script;
        }
        if (lines == null || lines.Length == 0) return null;
        var plain = new System.Collections.Generic.List<string>();
        foreach (var l in lines) plain.Add(l.text);
        return DialogueScript.FromLines(displayName, plain);
    }

    public string DisplayName => displayName;
    public string Subtitle => title;
    public string Prompt => "[E]  Talk";

    public void Interact(FirstPersonController player)
    {
        FaceTowards(player.transform.position);
        DialogueUI.Instance.Open(this, player);
    }

    static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

    void Awake()
    {
        var block = new MaterialPropertyBlock();
        block.SetColor(BaseColor, tint);
        foreach (var r in GetComponentsInChildren<Renderer>())
            r.SetPropertyBlock(block);
    }

    /// <summary>Turn to face the player while talking (yaw only).</summary>
    public void FaceTowards(Vector3 worldPoint)
    {
        Vector3 flat = worldPoint - transform.position;
        flat.y = 0f;
        if (flat.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.LookRotation(flat);
    }
}
