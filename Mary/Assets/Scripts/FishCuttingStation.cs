using UnityEngine;

/// <summary>A table with a fish on it. Look at it and press E to start the blink-to-slice mini game.</summary>
public class FishCuttingStation : MonoBehaviour, IInteractable
{
    [Header("Who")]
    public string displayName = "Fish Cutting Station";
    public string subtitle = "Blink to slice. Mind your fingers.";

    [Header("Look (applied at runtime to the child objects of the same name)")]
    public Color tableColor = new Color(0.35f, 0.22f, 0.12f);
    public Color boardColor = new Color(0.72f, 0.55f, 0.35f);
    public Color fishColor = new Color(0.62f, 0.72f, 0.8f);

    public string DisplayName => displayName;
    public string Subtitle => subtitle;
    public string Prompt => "[E]  Start cutting";

    static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

    void Awake()
    {
        Tint("Table", tableColor);
        Tint("Board", boardColor);
        Tint("Fish", fishColor);
    }

    void Tint(string childName, Color color)
    {
        var child = transform.Find(childName);
        if (child == null) return;
        var block = new MaterialPropertyBlock();
        block.SetColor(BaseColor, color);
        foreach (var r in child.GetComponentsInChildren<Renderer>())
            r.SetPropertyBlock(block);
    }

    public void Interact(FirstPersonController player)
    {
        FishCuttingGame.Instance.Open(player);
    }
}
