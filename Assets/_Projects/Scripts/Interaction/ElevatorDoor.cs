using System;
using System.Collections;
using UnityEngine;
using UnityEngine.AI;

public class ElevatorDoor : MonoBehaviour
{
    [Header("门扇引用（留空时自动按 Lift.fbx 子物体名查找）")]
    [SerializeField] private Transform leftLeaf;
    [SerializeField] private Transform rightLeaf;
    [SerializeField] private string leftLeafName = "Elevator_Left_Leaf";
    [SerializeField] private string rightLeafName = "Elevator_Right_Leaf";

    [Header("开关门参数（Lift.fbx 局部空间，门宽 0.76）")]
    [SerializeField] private float openOffset = 0.72f;
    [SerializeField] private float openDuration = 1.2f;
    [SerializeField] private float closeDuration = 0.9f;
    [SerializeField, Range(0.5f, 1f)] private float blockerReleaseProgress = 0.75f;

    [Header("开局模拟电梯运行")]
    [SerializeField] private bool playIntroOnStart = true;
    [SerializeField] private float travelDuration = 2.5f;
    [SerializeField] private Transform shakeTarget;
    [SerializeField] private float travelShakeAmplitude = 0.002f;

    [Header("碰撞隔离与出门检测")]
    [SerializeField] private BoxCollider doorBlocker;
    [SerializeField] private NavMeshObstacle doorObstacle;
    [SerializeField] private ElevatorExitTrigger exitTrigger;
    [SerializeField] private float playerClearanceMargin = 0.14f;

    public event Action OnOpened;
    public event Action OnClosed;
    public event Action OnPlayerExitedAndLocked;

    private Vector3 leftClosedPos, leftOpenPos;
    private Vector3 rightClosedPos, rightOpenPos;
    private Vector3 shakeOriginPos;

    private float progress;       // 0 = 全关，1 = 全开
    private float targetProgress;
    private bool isMoving;
    private bool hasStartedOpening;
    private bool isPermanentlyLocked;
    private bool hasLeavesInitialized;

    public bool IsOpen => !isPermanentlyLocked && targetProgress > 0.5f;
    public bool IsLocked => isPermanentlyLocked;

    private void Awake()
    {
        if (leftLeaf == null) leftLeaf = FindChildRecursive(transform, leftLeafName);
        if (rightLeaf == null) rightLeaf = FindChildRecursive(transform, rightLeafName);
        if (exitTrigger == null) exitTrigger = GetComponentInChildren<ElevatorExitTrigger>(true);

        if (leftLeaf != null && rightLeaf != null)
        {
            leftClosedPos = leftLeaf.localPosition;
            rightClosedPos = rightLeaf.localPosition;

            // 按两扇门初始 X 正负自动向外侧滑入墙内，不依赖正负方向
            float leftDir = Mathf.Abs(leftClosedPos.x) > 0.0001f ? Mathf.Sign(leftClosedPos.x) : -1f;
            float rightDir = Mathf.Abs(rightClosedPos.x) > 0.0001f ? Mathf.Sign(rightClosedPos.x) : 1f;

            leftOpenPos = leftClosedPos + Vector3.right * (leftDir * openOffset);
            rightOpenPos = rightClosedPos + Vector3.right * (rightDir * openOffset);
            hasLeavesInitialized = true;
        }

        if (shakeTarget != null)
        {
            shakeOriginPos = shakeTarget.localPosition;
        }

        // 若门口阻挡体尚未手动挂 NavMeshObstacle，则自动补上带 Carving 的盒子障碍物，防止猴子顶着关着的电梯门寻路
        if (doorObstacle == null && doorBlocker != null)
        {
            doorObstacle = doorBlocker.GetComponent<NavMeshObstacle>();
            if (doorObstacle == null)
            {
                doorObstacle = doorBlocker.gameObject.AddComponent<NavMeshObstacle>();
                doorObstacle.shape = NavMeshObstacleShape.Box;
                doorObstacle.center = doorBlocker.center;
                doorObstacle.size = doorBlocker.size;
                doorObstacle.carving = true;
            }
        }

        // 开局门关闭，碰撞隔离与寻路阻挡全部启用
        progress = 0f;
        targetProgress = 0f;
        SetBlockerActive(true);
    }

    private void OnEnable()
    {
        if (exitTrigger != null)
        {
            exitTrigger.OnPlayerExited += HandlePlayerExited;
        }
    }

    private void OnDisable()
    {
        if (exitTrigger != null)
        {
            exitTrigger.OnPlayerExited -= HandlePlayerExited;
        }
    }

    private void Start()
    {
        if (playIntroOnStart)
        {
            StartCoroutine(IntroTravelRoutine());
        }
    }

    private IEnumerator IntroTravelRoutine()
    {
        float elapsed = 0f;
        while (elapsed < travelDuration)
        {
            elapsed += Time.deltaTime;

            if (shakeTarget != null && travelShakeAmplitude > 0f)
            {
                // 最后 0.5 秒模拟电梯减速停稳，震动渐弱
                float fadeOut = Mathf.Clamp01((travelDuration - elapsed) / 0.5f);
                float amp = travelShakeAmplitude * fadeOut;
                float offsetY = Mathf.Sin(elapsed * 42f) * amp;
                float offsetX = Mathf.Cos(elapsed * 29f) * amp * 0.4f;
                shakeTarget.localPosition = shakeOriginPos + new Vector3(offsetX, offsetY, 0f);
            }

            yield return null;
        }

        if (shakeTarget != null)
        {
            shakeTarget.localPosition = shakeOriginPos;
        }

        Open();
    }

    /// <summary>
    /// 打开电梯门；玩家离开并锁死后将永久忽略再次开门请求。
    /// </summary>
    public void Open()
    {
        if (isPermanentlyLocked) return;

        hasStartedOpening = true;
        targetProgress = 1f;
        isMoving = true;
    }

    /// <summary>
    /// 立即启用门口实体碰撞隔离并永久锁死，同时播放关门动画，防止门关一半时玩家挤回电梯。
    /// </summary>
    public void CloseAndLock()
    {
        isPermanentlyLocked = true;
        SetBlockerActive(true);
        if (exitTrigger != null)
        {
            exitTrigger.SetActive(false);
        }

        targetProgress = 0f;
        isMoving = true;
    }

    private void HandlePlayerExited(PlayerInteract player)
    {
        if (isPermanentlyLocked || !hasStartedOpening || player == null) return;

        // 二次安全校验：确保玩家胶囊体已经完全离开门框隔离盒范围再锁死，避免卡在门里
        if (!IsPlayerClearOfBlocker(player.transform.position)) return;

        CloseAndLock();
        OnPlayerExitedAndLocked?.Invoke();
    }

    private bool IsPlayerClearOfBlocker(Vector3 playerWorldPos)
    {
        if (doorBlocker == null) return true;

        Vector3 localPos = doorBlocker.transform.InverseTransformPoint(playerWorldPos);
        float halfThickness = doorBlocker.size.z * 0.5f;
        float distFromPlane = Mathf.Abs(localPos.z - doorBlocker.center.z);
        return distFromPlane >= halfThickness + playerClearanceMargin;
    }

    private void Update()
    {
        if (!isMoving || !hasLeavesInitialized) return;

        float dur = targetProgress >= 0.5f ? openDuration : closeDuration;
        float step = dur > 0.001f ? Time.deltaTime / dur : 1f;
        progress = Mathf.MoveTowards(progress, targetProgress, step);

        float t = Mathf.SmoothStep(0f, 1f, progress);
        leftLeaf.localPosition = Vector3.Lerp(leftClosedPos, leftOpenPos, t);
        rightLeaf.localPosition = Vector3.Lerp(rightClosedPos, rightOpenPos, t);

        // 仅在未锁死且开门进度达到阈值后才放开门口碰撞隔离
        if (!isPermanentlyLocked && targetProgress >= 1f && progress >= blockerReleaseProgress)
        {
            SetBlockerActive(false);
        }

        if (Mathf.Approximately(progress, targetProgress))
        {
            isMoving = false;
            if (targetProgress >= 1f)
            {
                OnOpened?.Invoke();
            }
            else
            {
                OnClosed?.Invoke();
            }
        }
    }

    private void SetBlockerActive(bool active)
    {
        if (doorBlocker != null)
        {
            doorBlocker.enabled = active;
        }
        if (doorObstacle != null)
        {
            doorObstacle.enabled = active;
        }
    }

    private static Transform FindChildRecursive(Transform root, string targetName)
    {
        if (root == null || string.IsNullOrEmpty(targetName)) return null;

        foreach (Transform child in root)
        {
            if (child.name == targetName) return child;
            Transform found = FindChildRecursive(child, targetName);
            if (found != null) return found;
        }
        return null;
    }
}
