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
    public event Action<Vector3, ShardColorType> onPickUpTyped;
    public event Action turnAround;
    public event Action<Vector2> look;
    public event Action<Vector2> move;
    public event Action<bool> run;
    public event Action<Transform> OnCaught;
    public event Action<Transform, int> OnCaughtWithType;
    public event Action OnRevived;

    public static PlayerInteract ActivePlayer { get; private set; }

    private bool isPaused;
    private bool isCaught;
    private bool isCutsceneFrozen;
    private bool isMoving;
    private bool isRunning;
    private float listenRadius;

    public bool IsAlive => !isCaught;
    public bool IsPaused => isPaused;
    public bool IsCutsceneFrozen => isCutsceneFrozen;
    public bool IsMoving => !isCaught && !isPaused && !isCutsceneFrozen && isMoving;
    public bool IsRunning => !isCaught && !isPaused && !isCutsceneFrozen && isMoving && isRunning;

    private void OnEnable()
    {
        ActivePlayer = this;
    }

    private void OnDisable()
    {
        if (ActivePlayer == this)
        {
            ActivePlayer = null;
        }
    }

    public void PickUp(Vector3 shardPos, ShardColorType shardType = ShardColorType.NormalPurple)
    {
        if (isCaught) return;
        onPickUpTyped?.Invoke(shardPos, shardType);
        pickUp?.Invoke(shardPos);
    }

    /// <summary>
    /// 玩家被抓时由关卡管理器调用：防重复触发、锁死输入，并通知相机按对应处决类型进行防穿模构图。
    /// </summary>
    public void CatchBy(Transform killer, int catchType = 3)
    {
        if (isCaught) return;
        isCaught = true;
        isPaused = false;
        isMoving = false;
        isRunning = false;
        listenRadius = 0f;
        OnCaught?.Invoke(killer);
        OnCaughtWithType?.Invoke(killer, catchType);
    }

    public void TeleportSafe(Vector3 worldPos)
    {
        CharacterController cc = GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        transform.position = worldPos;
        if (cc != null) cc.enabled = true;
    }

    public void TeleportSafe(Vector3 worldPos, Quaternion worldRot)
    {
        CharacterController cc = GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        transform.SetPositionAndRotation(worldPos, worldRot);
        if (cc != null) cc.enabled = true;
    }

    /// <summary>
    /// 复活过场暗幕期间调用：安全瞬移玩家回安全出生点、重置移动状态并通知镜头解除锁定。
    /// </summary>
    public void Revive(Vector3 respawnPos, Quaternion respawnRot)
    {
        CharacterController cc = GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        transform.SetPositionAndRotation(respawnPos, respawnRot);
        if (cc != null) cc.enabled = true;

        isCaught = false;
        isPaused = false;
        isMoving = false;
        isRunning = false;
        listenRadius = 0f;
        OnRevived?.Invoke();
    }

    /// <summary>
    /// 胜利结算等终局画面调用：锁死玩家操作但不触发被抓镜头晃动。
    /// </summary>
    public void FreezeForEndScreen()
    {
        isCaught = true;
        isPaused = false;
        isCutsceneFrozen = false;
        isMoving = false;
        isRunning = false;
        listenRadius = 0f;
    }

    /// <summary>
    /// 过场演出期间临时冻结玩家移动与视角输入（不改变存活状态），结束后无缝恢复。
    /// </summary>
    public void SetCutsceneFrozen(bool frozen)
    {
        isCutsceneFrozen = frozen;
        if (frozen)
        {
            isMoving = false;
            isRunning = false;
            listenRadius = 0f;
        }
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
        if (isCaught || isCutsceneFrozen) return;

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
        isMoving = placeMove.sqrMagnitude > 0.001f;
        isRunning = isMoving && runAction != null && runAction.IsPressed();

        run?.Invoke(isRunning);
        move?.Invoke(placeMove);

        listenRadius = isRunning
            ? runSoundRadius
            : (isMoving ? walkSoundRadius : idleSoundRadius);
    }
}
