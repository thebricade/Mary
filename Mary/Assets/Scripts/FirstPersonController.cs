using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class FirstPersonController : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] float walkSpeed = 4f;
    [SerializeField] float sprintSpeed = 7f;
    [SerializeField] float jumpHeight = 1.1f;
    [SerializeField] float gravity = -20f;

    [Header("Look")]
    [SerializeField] Transform cameraHolder;
    [SerializeField] float lookSensitivity = 0.1f;
    [SerializeField] float maxPitch = 85f;

    /// <summary>Set while talking to an NPC (or any menu) to freeze movement and looking.</summary>
    public bool InputLocked { get; set; }

    CharacterController controller;
    InputAction move, look, jump, sprint;
    float pitch;
    float verticalVelocity;

    void Awake()
    {
        controller = GetComponent<CharacterController>();

        move = new InputAction("Move", InputActionType.Value);
        move.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
        move.AddBinding("<Gamepad>/leftStick");

        look = new InputAction("Look", InputActionType.Value, "<Pointer>/delta");
        look.AddBinding("<Gamepad>/rightStick");

        jump = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
        jump.AddBinding("<Gamepad>/buttonSouth");

        sprint = new InputAction("Sprint", InputActionType.Button, "<Keyboard>/leftShift");
        sprint.AddBinding("<Gamepad>/leftStickPress");
    }

    void OnEnable()
    {
        move.Enable(); look.Enable(); jump.Enable(); sprint.Enable();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void OnDisable()
    {
        move.Disable(); look.Disable(); jump.Disable(); sprint.Disable();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void Update()
    {
        // Esc frees the cursor, clicking the Game view locks it again.
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        if (InputLocked) return;

        Look();
        Move();
    }

    void Look()
    {
        if (Cursor.lockState != CursorLockMode.Locked) return;

        Vector2 delta = look.ReadValue<Vector2>() * lookSensitivity;
        transform.Rotate(Vector3.up, delta.x);
        pitch = Mathf.Clamp(pitch - delta.y, -maxPitch, maxPitch);
        if (cameraHolder != null)
            cameraHolder.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    void Move()
    {
        Vector2 input = move.ReadValue<Vector2>();
        float speed = sprint.IsPressed() ? sprintSpeed : walkSpeed;
        Vector3 horizontal = (transform.right * input.x + transform.forward * input.y) * speed;

        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;
        if (controller.isGrounded && jump.WasPressedThisFrame())
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
        verticalVelocity += gravity * Time.deltaTime;

        controller.Move((horizontal + Vector3.up * verticalVelocity) * Time.deltaTime);
    }
}
