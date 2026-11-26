using UnityEngine;
public class PlayerMovement : MonoBehaviour
{
    private PlayerInteract input;
    private CharacterController cc;
    [SerializeField] private float beginMoveSpeed, beginRunSpeed;
    private float speedRatio = 1f;
    bool run;

    private void Awake()
    {
        cc = GetComponent<CharacterController>();
        input = GetComponent<PlayerInteract>();
    }
    private void OnEnable()
    {
        input.move += Move;
        input.run += Run;
    }
    private void OnDisable()
    {
        input.move -= Move;
        input.run-= Run;
    }
    private void Move(Vector2 move)
    {
        Vector3 dir = transform.right * move.x + transform.forward * move.y;
        if (dir.sqrMagnitude > 1f) dir.Normalize();
        cc.Move(dir * (run ? beginRunSpeed : beginMoveSpeed)
            * speedRatio * Time.deltaTime);
    }
    private void Run(bool running) => run = running;
}
