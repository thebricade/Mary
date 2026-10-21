using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// On the player. Looks straight ahead from the camera for an NPC, shows who they are,
/// and starts a conversation on E.
/// </summary>
[RequireComponent(typeof(FirstPersonController))]
public class PlayerInteractor : MonoBehaviour
{
    [SerializeField] float range = 3.5f;

    FirstPersonController controller;
    Camera cam;
    IInteractable target;

    void Awake()
    {
        controller = GetComponent<FirstPersonController>();
        cam = GetComponentInChildren<Camera>();
    }

    void Update()
    {
        var ui = DialogueUI.Instance;
        if (ui.IsOpen || FishCuttingGame.Active) return;

        target = FindTarget();
        ui.ShowLookCard(target);

        if (target != null && Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame)
            target.Interact(controller);
    }

    IInteractable FindTarget()
    {
        if (cam == null) return null;
        Ray ray = new Ray(cam.transform.position, cam.transform.forward);
        if (Physics.Raycast(ray, out RaycastHit hit, range, ~0, QueryTriggerInteraction.Ignore))
            return hit.collider.GetComponentInParent<IInteractable>();
        return null;
    }
}
