using System;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerInteract : MonoBehaviour
{
    [Header("发声半径（按玩家移速 1.0 / 1.7 的场景比例设定）")]
    [SerializeField] private float idleSoundRadius = 0f;
    [SerializeField] private float walkSoundRadius = 2.5f;
    [SerializeField] private float runSoundRadius = 5.0f;

    private InputAction pausePressed;
    private InputAction turnAroundAction;
    private InputAction lookAction;
    private InputAction moveAction;
    private InputAction runAction;

    public event Action<bool> pauseAction;
    public event Action<Vector3> pickUp;
    public event Action turnAround;
    public event Action<Vector2> look;
    public event Action<Vector2> move;
    public event Action<bool> run;
    public event Action<Transform> OnCaught;

    private bool isPaused;
    private bool isCaught;
    private float listenRadius;

    public bool IsAlive => !isCaught;

    public void PickUp(Vector3 shardPos)
    {
        if (isCaught) return;
        pickUp?.Invoke(shardPos);
    }

    /// <summary>
    /// 玩家被抓时由关卡管理器调用：防重复触发、锁死输入，并通知相机看向抓捕者。
    /// </summary>
    public void CatchBy(Transform killer)
    {
        if (isCaught) return;
        isCaught = true;
        isPaused = false;
        listenRadius = 0f;
        OnCaught?.Invoke(killer);
    }

    /// <summary>
    /// 供外部 UI（如暂停界面的“返回游戏”按钮）同步暂停状态。
    /// </summary>
    public void SetPaused(bool pause)
    {
        if (isCaught) return;
        isPaused = pause;
        if (isPaused)
        {
            listenRadius = 0f;
        }
    }

    public bool Listen(Vector3 listenerPos)
    {
        if (isCaught || isPaused || listenRadius <= 0f) return false;

        Vector3 delta = listenerPos - transform.position;
        delta.y = 0f;
        return delta.sqrMagnitude <= listenRadius * listenRadius;
    }

    private void Awake()
    {
        pausePressed = InputSystem.actions.FindAction("Pause");
        turnAroundAction = InputSystem.actions.FindAction("TurnAround");
        lookAction = InputSystem.actions.FindAction("Look");
        moveAction = InputSystem.actions.FindAction("Move");
        runAction = InputSystem.actions.FindAction("Sprint");
    }

    private void Update()
    {
        if (isCaught) return;

        if (pausePressed != null && pausePressed.WasPressedThisFrame())
        {
            isPaused = !isPaused;
            if (isPaused) listenRadius = 0f;
            pauseAction?.Invoke(isPaused);
        }

        if (isPaused) return;

        if (turnAroundAction != null && turnAroundAction.WasPressedThisFrame())
        {
            turnAround?.Invoke();
        }

        Vector2 faceMove = lookAction != null ? lookAction.ReadValue<Vector2>() : Vector2.zero;
        look?.Invoke(faceMove);

        Vector2 placeMove = moveAction != null ? moveAction.ReadValue<Vector2>() : Vector2.zero;
        bool isMoving = placeMove.sqrMagnitude > 0.001f;
        bool isRunning = isMoving && runAction != null && runAction.IsPressed();

        run?.Invoke(isRunning);
        move?.Invoke(placeMove);

        listenRadius = isRunning
            ? runSoundRadius
            : (isMoving ? walkSoundRadius : idleSoundRadius);
    }
}
