using System;
using System.Collections;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

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

    [Header("开局模拟电梯上行与遮光演出")]
    [SerializeField] private bool playIntroOnStart = true;
    [SerializeField] private float travelDuration = 1.75f;          // 井道内全暗上行阶段时长
    [SerializeField] private float shaftRevealDuration = 1.65f;     // 遮光挡板缓慢下移、光线自上而下透入时长
    [SerializeField] private float stopJoltPauseDuration = 0.36f;   // 停稳中度抖动后稍作停顿再开门
    [SerializeField] private Transform shakeTarget;
    [SerializeField] private float travelShakeAmplitude = 0.0024f;
    [SerializeField] private float travelCameraShakeStrength = 0.0020f;
    [SerializeField] private float arrivalStopJoltStrength = 0.015f;
    [SerializeField] private float arrivalStopJoltDuration = 0.34f;
    [SerializeField] private Transform shaftOccluder;               // 留空时自动在门外生成井道遮光板

    [Header("碰撞隔离与出门检测")]
    [SerializeField] private BoxCollider doorBlocker;
    [SerializeField] private NavMeshObstacle doorObstacle;
    [SerializeField] private ElevatorExitTrigger exitTrigger;
    [SerializeField] private float playerClearanceMargin = 0.14f;

    public event Action OnOpened;
    public event Action OnClosed;
    public event Action OnPlayerExitedAndLocked;
    public event Action<PlayerInteract> OnPlayerEnteredForEscape;

    private Vector3 leftClosedPos, leftOpenPos;
    private Vector3 rightClosedPos, rightOpenPos;
    private Vector3 shakeOriginPos;
    private Vector3 occluderClosedLocalPos;
    private Vector3 occluderLoweredLocalPos;
    private Material runtimeOccluderMat;

    private float progress;       // 0 = 全关，1 = 全开
    private float targetProgress;
    private bool isMoving;
    private bool hasStartedOpening;
    private bool isPermanentlyLocked;
    private bool hasLeavesInitialized;
    private bool isEscapeMode;
    private bool hasTriggeredEscape;

    public bool IsOpen => !isPermanentlyLocked && targetProgress > 0.5f;
    public bool IsLocked => isPermanentlyLocked;
    public bool IsEscapeOpen => isEscapeMode && !hasTriggeredEscape;
    public float CloseDuration => closeDuration;

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

        // 开局门外放置井道遮光挡板，彻底隔绝大厅灯光，模拟电梯还在下方井道中上行
        if (playIntroOnStart)
        {
            EnsureShaftOccluder();
        }

        // 开局门关闭，碰撞隔离与寻路阻挡全部启用
        progress = 0f;
        targetProgress = 0f;
        SetBlockerActive(true);
    }

    private void EnsureShaftOccluder()
    {
        if (shaftOccluder == null)
        {
            GameObject occ = GameObject.CreatePrimitive(PrimitiveType.Cube);
            occ.name = "ElevatorShaftOccluder";
            occ.transform.SetParent(transform, false);

            // 移除碰撞体并标记忽略 NavMesh，仅作视觉与双面投阴影遮光用
            Collider col = occ.GetComponent<Collider>();
            if (col != null) Destroy(col);

            NavMeshModifier navMod = occ.AddComponent<NavMeshModifier>();
            navMod.ignoreFromBuild = true;

            // 紧贴电梯门外侧（门在 z=0.05，向大厅 -Z 侧偏移至 z=-0.025），宽度与高度完全盖住门框缝隙
            occ.transform.localPosition = new Vector3(0f, 0.50f, -0.025f);
            occ.transform.localRotation = Quaternion.identity;
            occ.transform.localScale = new Vector3(1.16f, 1.08f, 0.055f);

            MeshRenderer mr = occ.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                // 复用轿厢墙壁已有材质 Shader，确保打包后绝对不会丢失 Shader
                Material srcMat = FindCabinWallMaterial();
                if (srcMat != null)
                {
                    runtimeOccluderMat = new Material(srcMat);
                }
                else
                {
                    Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
                    runtimeOccluderMat = urpLit != null ? new Material(urpLit) : mr.sharedMaterial;
                }

                if (runtimeOccluderMat != null)
                {
                    runtimeOccluderMat.name = "ElevatorShaftOccluder_RuntimeMat";
                    if (runtimeOccluderMat.HasProperty("_BaseMap")) runtimeOccluderMat.SetTexture("_BaseMap", null);
                    if (runtimeOccluderMat.HasProperty("_MainTex")) runtimeOccluderMat.SetTexture("_MainTex", null);
                    Color darkShaft = new Color(0.045f, 0.040f, 0.042f, 1f);
                    if (runtimeOccluderMat.HasProperty("_BaseColor")) runtimeOccluderMat.SetColor("_BaseColor", darkShaft);
                    if (runtimeOccluderMat.HasProperty("_Color")) runtimeOccluderMat.SetColor("_Color", darkShaft);
                    if (runtimeOccluderMat.HasProperty("_Smoothness")) runtimeOccluderMat.SetFloat("_Smoothness", 0.08f);
                    if (runtimeOccluderMat.HasProperty("_Metallic")) runtimeOccluderMat.SetFloat("_Metallic", 0.2f);
                    mr.sharedMaterial = runtimeOccluderMat;
                }

                // 双面投射阴影，彻底隔绝门外走廊吊灯与地面光透射
                mr.shadowCastingMode = ShadowCastingMode.TwoSided;
                mr.receiveShadows = true;
            }

            shaftOccluder = occ.transform;
        }

        occluderClosedLocalPos = shaftOccluder.localPosition;
        occluderLoweredLocalPos = occluderClosedLocalPos + Vector3.down * 1.12f;
        shaftOccluder.gameObject.SetActive(true);
    }

    private Material FindCabinWallMaterial()
    {
        Transform backWall = transform.Find("Cabin_BackWall");
        if (backWall != null)
        {
            Renderer r = backWall.GetComponent<Renderer>();
            if (r != null && r.sharedMaterial != null) return r.sharedMaterial;
        }
        Renderer anyR = GetComponentInChildren<Renderer>(true);
        return anyR != null ? anyR.sharedMaterial : null;
    }

    private void OnDestroy()
    {
        if (runtimeOccluderMat != null)
        {
            Destroy(runtimeOccluderMat);
            runtimeOccluderMat = null;
        }
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
        float camPulseTimer = 0f;

        // 阶段 1：井道内上行（门外遮光挡板完全闭合挡住大厅灯光 + 持续轻微机械震动）
        float elapsed = 0f;
        float holdDur = Mathf.Max(0.4f, travelDuration);
        while (elapsed < holdDur)
        {
            float dt = Time.deltaTime;
            elapsed += dt;
            camPulseTimer += dt;

            ApplyElevatorVibration(elapsed, 1f);
            if (camPulseTimer >= 0.14f)
            {
                camPulseTimer = 0f;
                HapticFeedback.RequestShake(travelCameraShakeStrength, 0.18f);
            }

            yield return null;
        }

        // 阶段 2：逐渐抵达目标楼层——将门外遮光挡板缓慢向下拉低，模拟电梯从下层上升穿出楼板、大厅光线自上而下渐显
        float revealElapsed = 0f;
        float revealDur = Mathf.Max(0.4f, shaftRevealDuration);
        while (revealElapsed < revealDur)
        {
            float dt = Time.deltaTime;
            revealElapsed += dt;
            elapsed += dt;
            camPulseTimer += dt;

            float rawT = Mathf.Clamp01(revealElapsed / revealDur);
            // 柔和减速曲线：起步平稳，接近楼层停靠时速度渐缓
            float slideT = 1f - Mathf.Pow(1f - rawT, 1.85f);
            if (shaftOccluder != null)
            {
                shaftOccluder.localPosition = Vector3.Lerp(occluderClosedLocalPos, occluderLoweredLocalPos, slideT);
            }

            // 临近停稳时持续震动略微减弱，准备迎接刹车抖动
            float vibFade = Mathf.Lerp(1f, 0.35f, rawT);
            ApplyElevatorVibration(elapsed, vibFade);
            if (camPulseTimer >= 0.16f)
            {
                camPulseTimer = 0f;
                HapticFeedback.RequestShake(travelCameraShakeStrength * vibFade, 0.18f);
            }

            yield return null;
        }

        if (shaftOccluder != null)
        {
            shaftOccluder.localPosition = occluderLoweredLocalPos;
            shaftOccluder.gameObject.SetActive(false);
        }

        // 阶段 3：到达停稳瞬间触发中度机械刹车抖动（视角中度震动 + 轿厢阻尼回弹）
        HapticFeedback.RequestShake(arrivalStopJoltStrength, arrivalStopJoltDuration);
        float stopElapsed = 0f;
        float stopDur = Mathf.Max(0.25f, stopJoltPauseDuration);
        while (stopElapsed < stopDur)
        {
            stopElapsed += Time.deltaTime;
            float u = Mathf.Clamp01(stopElapsed / stopDur);
            if (shakeTarget != null)
            {
                // 先下沉一顿再阻尼回弹归位，营造重型老式电梯刹停质感
                float damping = Mathf.Exp(-7.5f * u);
                float joltY = -Mathf.Sin(u * Mathf.PI * 3.0f) * 0.0085f * damping;
                float joltX = Mathf.Sin(u * Mathf.PI * 5.0f) * 0.0022f * damping;
                shakeTarget.localPosition = shakeOriginPos + new Vector3(joltX, joltY, 0f);
            }
            yield return null;
        }

        if (shakeTarget != null)
        {
            shakeTarget.localPosition = shakeOriginPos;
        }

        // 阶段 4：停稳后开启电梯门
        Open();
    }

    private void ApplyElevatorVibration(float time, float weight)
    {
        if (shakeTarget == null || travelShakeAmplitude <= 0f) return;

        float amp = travelShakeAmplitude * weight;
        float offsetY = (Mathf.Sin(time * 44f) * 0.7f + Mathf.Sin(time * 89f) * 0.3f) * amp;
        float offsetX = Mathf.Cos(time * 31f) * amp * 0.42f;
        shakeTarget.localPosition = shakeOriginPos + new Vector3(offsetX, offsetY, 0f);
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

    /// <summary>
    /// 启动雕像解锁出口后重新解锁并打开电梯门，允许玩家逃离本关。
    /// </summary>
    public void UnlockAndOpenForEscape()
    {
        isEscapeMode = true;
        hasTriggeredEscape = false;
        isPermanentlyLocked = false;
        hasStartedOpening = true;
        targetProgress = 1f;
        isMoving = true;
    }

    public void CloseForVictory()
    {
        isPermanentlyLocked = true;
        SetBlockerActive(true);
        targetProgress = 0f;
        isMoving = true;
    }

    /// <summary>
    /// 首领入场过场开始前瞬时归零关门（隐藏井道遮光板并严密合上门扇，等待入场运镜开门）。
    /// </summary>
    public void SnapClosedForBossCutscene()
    {
        StopAllCoroutines();
        if (shaftOccluder != null)
        {
            shaftOccluder.gameObject.SetActive(false);
        }
        if (shakeTarget != null)
        {
            shakeTarget.localPosition = shakeOriginPos;
        }
        isPermanentlyLocked = true;
        isMoving = false;
        progress = 0f;
        targetProgress = 0f;
        if (hasLeavesInitialized)
        {
            leftLeaf.localPosition = leftClosedPos;
            rightLeaf.localPosition = rightClosedPos;
        }
        SetBlockerActive(true);
    }

    /// <summary>
    /// 首领入场过场专用开门：平滑拉开双扇电梯门展示轿厢内愤怒的首领。
    /// </summary>
    public void OpenForBossCutscene()
    {
        isPermanentlyLocked = false;
        hasStartedOpening = true;
        targetProgress = 1f;
        isMoving = true;
    }

    private void HandlePlayerExited(PlayerInteract player)
    {
        if (isPermanentlyLocked || !hasStartedOpening || player == null) return;

        // 二次安全校验：确保玩家完全离开门框隔离盒范围再锁死，避免卡在门里
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
        if (isEscapeMode && !hasTriggeredEscape && progress >= blockerReleaseProgress)
        {
            CheckEscapeCabinEntry();
        }

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

    private void CheckEscapeCabinEntry()
    {
        PlayerInteract player = PlayerInteract.ActivePlayer;
        if (player == null || !player.IsAlive) return;

        // 轿厢本地坐标：门在 z≈0.05，后壁在 z≈1.01，左右壁在 x≈±0.50
        Vector3 local = transform.InverseTransformPoint(player.transform.position);
        if (local.z >= 0.28f && local.z <= 1.05f && Mathf.Abs(local.x) <= 0.52f)
        {
            hasTriggeredEscape = true;
            CloseForVictory();
            OnPlayerEnteredForEscape?.Invoke(player);
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
