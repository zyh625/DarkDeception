using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : MonoBehaviour
{
    private InputAction moveAction, runAction;
    private CharacterController cc;
    [SerializeField] private float beginMoveSpeed, beginRunSpeed;
    private float speedRatio = 1f;

    private void Awake()
    {
        cc = GetComponent<CharacterController>();
        moveAction = InputSystem.actions.FindAction("Move");
        runAction = InputSystem.actions.FindAction("Sprint");
    }
    private void Update()
    {
        Vector2 move = moveAction.ReadValue<Vector2>();
        Vector3 dir = transform.right * move.x + transform.forward * move.y;
        bool run = runAction.IsPressed();
        cc.Move(dir * (run ? beginRunSpeed : beginMoveSpeed)
            * speedRatio * Time.deltaTime);

    }
}
