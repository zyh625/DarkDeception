using UnityEngine;
using UnityEngine.InputSystem;

public class FirstView : MonoBehaviour
{
    private InputAction lookAction,pauseAction;
    [SerializeField] private Transform player, eye;
    bool stop = false;
    [SerializeField] private float sensitivity = 0.1f;
    private float pitch;
    private void Awake()
    {
        lookAction = InputSystem.actions.FindAction("Look");
        pauseAction = InputSystem.actions.FindAction("Pause");
        transform.position = eye.position;
    }
    private void LateUpdate()
    {
        if (pauseAction.WasPressedThisFrame()) stop = !stop;
        if (stop) return;
        Vector2 move = lookAction.ReadValue<Vector2>();
        player.Rotate(sensitivity * move.x * Vector3.up, Space.World);
        pitch -= move.y * sensitivity;
        pitch = Mathf.Clamp(pitch, -80f, 80f);
        transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }
}
