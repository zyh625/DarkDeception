using UnityEngine;

public class PlayerMovement : MonoBehaviour
{
    private PlayerInteract input;
    private CharacterController cc;
    [SerializeField] private float beginMoveSpeed, beginRunSpeed;
    [SerializeField] private float walkStepInterval = 0.48f;
    [SerializeField] private float runStepInterval = 0.30f;

    private float speedRatio = 1f;
    private float footstepTimer;
    private bool run;

    private void Awake()
    {
        cc = GetComponent<CharacterController>();
        input = GetComponent<PlayerInteract>();
        footstepTimer = 0.14f;
    }

    private void OnEnable()
    {
        input.move += Move;
        input.run += Run;
    }

    private void OnDisable()
    {
        input.move -= Move;
        input.run -= Run;
    }

    private void Move(Vector2 move)
    {
        Vector3 dir = transform.right * move.x + transform.forward * move.y;
        float magSq = dir.sqrMagnitude;
        if (magSq > 1f) dir.Normalize();

        float dt = Time.deltaTime;
        cc.Move(dir * (run ? beginRunSpeed : beginMoveSpeed) * speedRatio * dt);

        // 同步播放玩家行走/奔跑脚步音效
        if (magSq > 0.01f && input != null && input.IsAlive && !input.IsPaused)
        {
            float interval = run ? Mathf.Max(0.16f, runStepInterval) : Mathf.Max(0.24f, walkStepInterval);
            footstepTimer -= dt;
            if (footstepTimer <= 0f)
            {
                AudioManager.PlayPlayerFootstep(run);
                footstepTimer = interval;
            }
        }
        else
        {
            // 停步后重置较短延迟，保证下次起步迅速发出第一步脚步声
            footstepTimer = Mathf.Min(footstepTimer, 0.12f);
        }
    }

    private void Run(bool running) => run = running;
}
