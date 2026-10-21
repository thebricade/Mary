/// <summary>Anything the player can look at and press E on: NPCs, stations, doors...</summary>
public interface IInteractable
{
    string DisplayName { get; }
    string Subtitle { get; }
    string Prompt { get; }
    void Interact(FirstPersonController player);
}
