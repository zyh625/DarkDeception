using UnityEngine;

public class FirstView : MonoBehaviour
{
    [SerializeField] private float sensitivity = 0.1f;

    [Header("被抓处决时的镜头构图与防穿模")]
    [SerializeField] private float killerFocusHeight = 0.62f;
    [SerializeField] private float focusRotateSpeed = 14f;
    [SerializeField] private float executionNearClip = 0.01f;

    private PlayerInteract input;
    private Camera cam;
    private HapticFeedback haptic;
    private float defaultNearClip = 0.02f;
    private float pitch;

    private Transform focusTarget;
    private MonkeyAnimation killerAnim;
    private SkinnedMeshRenderer[] killerSkinnedRenderers;
    private bool[] killerOrigUpdateWhenOffscreen;
    private Transform killerHeadBone;
    private Transform killerJawBone;
    private Transform killerChestBone;
    private Transform killerHandLBone;
    private Transform killerHandRBone;
    private Transform killerForearmRBone;

    private int currentCatchType;
    private float executionElapsed;
    private int wallLayerMask;

    // 处决相机平滑状态（世界空间驱动，彻底支持举起、手部360°随动旋转与倒地仰视）
    private bool hasExecPose;
    private Vector3 execStartCamPos;
    private Vector3 execCamWorldPos;
    private Quaternion execCamWorldRot;
    private float currentArmRollDeg;

    // 第三种处决（ArmSlam）最后甩出阶段锁存参数
    private bool armSlamThrowLatched;
    private Vector3 throwStartPos;
    private Vector3 throwLandPos;
    private float throwStartRollDeg;
    private float throwRollSign = 1f;

    // 过场演出相机接管状态（用于首领电梯入场动画：拉远仰拍、平滑切回玩家视角）
    private bool isCutsceneCameraActive;
    private Vector3 cutsceneWorldPos;
    private Quaternion cutsceneWorldRot = Quaternion.identity;
    private float defaultFov = 60f;

    private void Awake()
    {
        input = GetComponentInParent<PlayerInteract>();
        cam = GetComponent<Camera>();
        haptic = GetComponent<HapticFeedback>();
        if (cam != null)
        {
            defaultNearClip = cam.nearClipPlane;
            defaultFov = cam.fieldOfView;
        }
        wallLayerMask = LayerMask.GetMask("Wall");
        if (wallLayerMask == 0) wallLayerMask = 1 << 7;
    }

    private void OnEnable()
    {
        if (input == null) return;
        input.turnAround += TurnAround;
        input.look += LookAround;
        input.OnCaughtWithType += FocusOnKillerExecution;
        input.OnRevived += HandleRevived;
    }

    private void OnDisable()
    {
        if (input == null) return;
        input.turnAround -= TurnAround;
        input.look -= LookAround;
        input.OnCaughtWithType -= FocusOnKillerExecution;
        input.OnRevived -= HandleRevived;
    }

    private void TurnAround() => input.transform.Rotate(Vector3.up, 180f);

    private void LookAround(Vector2 move)
    {
        if (focusTarget != null || isCutsceneCameraActive) return;

        float effectiveSens = sensitivity * GameSettings.MouseSensitivity;
        input.transform.Rotate(effectiveSens * move.x * Vector3.up, Space.World);
        pitch = Mathf.Clamp(pitch - move.y * effectiveSens, -80f, 80f);
        transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    private void FocusOnKillerExecution(Transform killer, int catchType)
    {
        focusTarget = killer;
        currentCatchType = catchType;
        executionElapsed = 0f;
        hasExecPose = false;
        armSlamThrowLatched = false;
        currentArmRollDeg = 0f;

        killerAnim = killer != null ? killer.GetComponentInChildren<MonkeyAnimation>(true) : null;
        killerHeadBone = killer != null ? FindChildRecursive(killer, "Head") : null;
        killerJawBone = killer != null ? FindChildRecursive(killer, "Jaw") : null;
        killerChestBone = killer != null ? FindChildRecursive(killer, "Chest") : null;
        killerHandLBone = killer != null ? FindChildRecursive(killer, "Hand.L") : null;
        killerHandRBone = killer != null ? FindChildRecursive(killer, "Hand.R") : null;
        killerForearmRBone = killer != null ? FindChildRecursive(killer, "Forearm.R") : null;

        // 强制处决期间关闭猴子的蒙皮视锥剔除（彻底解决举起玩家时双手时隐时现的闪烁问题）
        RestoreKillerOffscreenFlags();
        if (killer != null)
        {
            killerSkinnedRenderers = killer.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            killerOrigUpdateWhenOffscreen = new bool[killerSkinnedRenderers.Length];
            for (int i = 0; i < killerSkinnedRenderers.Length; i++)
            {
                if (killerSkinnedRenderers[i] == null) continue;
                killerOrigUpdateWhenOffscreen[i] = killerSkinnedRenderers[i].updateWhenOffscreen;
                killerSkinnedRenderers[i].updateWhenOffscreen = true;
            }
        }

        execStartCamPos = transform.position;
        execCamWorldPos = transform.position;
        execCamWorldRot = transform.rotation;

        if (cam != null)
        {
            cam.nearClipPlane = executionNearClip;
        }
    }

    public void ClearExecutionCamera()
    {
        RestoreKillerOffscreenFlags();
        focusTarget = null;
        killerAnim = null;
        killerHeadBone = null;
        killerJawBone = null;
        killerChestBone = null;
        killerHandLBone = null;
        killerHandRBone = null;
        killerForearmRBone = null;

        currentCatchType = 0;
        executionElapsed = 0f;
        hasExecPose = false;
        armSlamThrowLatched = false;
        currentArmRollDeg = 0f;
        pitch = 0f;

        if (cam != null)
        {
            cam.nearClipPlane = defaultNearClip;
        }
        if (haptic != null)
        {
            haptic.ClearImpulseAndResetBase();
        }
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;
    }

    /// <summary>
    /// 首领电梯入场演出接管相机：按世界坐标与仰拍构图驱动镜头，并保留 HapticFeedback 震动叠加。
    /// </summary>
    public void BeginCutsceneCamera(Vector3 worldPos, Quaternion worldRot, float fov = 58f)
    {
        isCutsceneCameraActive = true;
        cutsceneWorldPos = worldPos;
        cutsceneWorldRot = worldRot;
        if (cam != null)
        {
            cam.nearClipPlane = executionNearClip;
            cam.fieldOfView = fov;
        }
        ApplyCutscenePoseToTransform();
    }

    public void UpdateCutsceneCamera(Vector3 worldPos, Quaternion worldRot, float fov = 56f)
    {
        if (!isCutsceneCameraActive) return;
        cutsceneWorldPos = worldPos;
        cutsceneWorldRot = worldRot;
        if (cam != null)
        {
            cam.fieldOfView = fov;
        }
        ApplyCutscenePoseToTransform();
    }

    public void EndCutsceneCamera()
    {
        if (!isCutsceneCameraActive) return;
        isCutsceneCameraActive = false;
        if (cam != null)
        {
            cam.nearClipPlane = defaultNearClip;
            cam.fieldOfView = defaultFov;
        }
        if (haptic != null)
        {
            haptic.ClearImpulseAndResetBase();
        }
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    private void ApplyCutscenePoseToTransform()
    {
        Transform parent = transform.parent;
        Vector3 localPos = parent != null ? parent.InverseTransformPoint(cutsceneWorldPos) : cutsceneWorldPos;
        Quaternion localRot = parent != null ? Quaternion.Inverse(parent.rotation) * cutsceneWorldRot : cutsceneWorldRot;

        if (haptic != null)
        {
            haptic.SetExecutionBasePose(localPos, localRot);
        }
        else
        {
            transform.localPosition = localPos;
            transform.localRotation = localRot;
        }
    }

    private void HandleRevived()
    {
        EndCutsceneCamera();
        ClearExecutionCamera();
    }

    private void LateUpdate()
    {
        if (isCutsceneCameraActive)
        {
            ApplyCutscenePoseToTransform();
            return;
        }

        if (focusTarget == null || input == null) return;

        float dt = Time.deltaTime;
        if (dt <= 0.00001f) return;

        executionElapsed += dt;

        // 优先使用真实动画归一化进度，确保运镜与骨骼关键帧精确对齐
        float animNorm = GetExecutionNormalizedTime();

        Vector3 targetWorldPos;
        Quaternion targetWorldRot;

        switch (currentCatchType)
        {
            case 3:
                ComputeGrabBiteCamera(animNorm, dt, out targetWorldPos, out targetWorldRot);
                break;
            case 1:
                ComputeArmSlamCamera(animNorm, dt, out targetWorldPos, out targetWorldRot);
                break;
            default:
                ComputePunchStompCamera(animNorm, dt, out targetWorldPos, out targetWorldRot);
                break;
        }

        if (!hasExecPose)
        {
            execCamWorldPos = transform.position;
            execCamWorldRot = transform.rotation;
            hasExecPose = true;
        }

        float posSmooth = currentCatchType == 1 ? 22f : Mathf.Max(10f, focusRotateSpeed);
        float rotSmooth = currentCatchType == 1 ? 24f : Mathf.Max(12f, focusRotateSpeed);
        execCamWorldPos = Vector3.Lerp(execCamWorldPos, targetWorldPos, 1f - Mathf.Exp(-posSmooth * dt));
        execCamWorldRot = Quaternion.Slerp(execCamWorldRot, targetWorldRot, 1f - Mathf.Exp(-rotSmooth * dt));

        // 抓咬与拳击时做每帧硬边界防穿模（保证相机始终在猴子口鼻及双手前端外侧，不穿进头/牙/手指/天花板/墙壁）
        if (currentCatchType != 1)
        {
            execCamWorldPos = EnforceFaceAntiClip(execCamWorldPos, currentCatchType == 3 ? 0.28f : 0.26f, currentCatchType == 3);
        }
        execCamWorldPos = ClampInsideCorridor(GetMonkeyChestPos(), execCamWorldPos, 0.08f, 0.92f);

        // 转换回 Eye 局部坐标并同步交给 HapticFeedback 作为基准姿态（叠加震动时不抹除滚转与俯仰）
        Transform parent = transform.parent;
        Vector3 localPos = parent != null ? parent.InverseTransformPoint(execCamWorldPos) : execCamWorldPos;
        Quaternion localRot = parent != null ? Quaternion.Inverse(parent.rotation) * execCamWorldRot : execCamWorldRot;

        if (haptic != null)
        {
            haptic.SetExecutionBasePose(localPos, localRot);
        }
        else
        {
            transform.localPosition = localPos;
            transform.localRotation = localRot;
        }
    }

    /// <summary>
    /// 处决 1（GrabBite 抓咬，3.53s）：
    /// 1) 0.00~0.16：双手扑出抓住玩家；
    /// 2) 0.16~0.74：相机随猴子双手明显上移（相当于被高高举起至 0.85m 高度），视线略微朝下直视猴子正脸；
    /// 3) 0.74~1.00：猴子头部与下颌扑向镜头猛咬，相机保持正脸微俯视且严格避开口鼻与手部防穿模。
    /// </summary>
    private void ComputeGrabBiteCamera(float norm, float dt, out Vector3 camPos, out Quaternion camRot)
    {
        Vector3 rootPos = focusTarget.position;
        Vector3 fwd = GetFlatKillerForward();
        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;

        Vector3 headPos = GetMonkeyHeadPos();
        Vector3 jawPos = killerJawBone != null ? killerJawBone.position : headPos + fwd * 0.06f;

        // 实测 Ashjaw_Monkey_GrabBite：口鼻比 Head 前探 0.25m、比 Jaw 前探 0.19m；
        // 双手指尖在抓取与举起阶段（norm 0.15~0.70）前探至 Hand 骨骼前方约 +0.19m（总计最远 Z=0.726m）
        float faceFrontAlongFwd = Mathf.Max(
            Vector3.Dot(headPos - rootPos, fwd) + 0.25f,
            Vector3.Dot(jawPos - rootPos, fwd) + 0.19f);
        float handsFrontAlongFwd = GetHandsFrontAlongForward(rootPos, fwd, 0.56f) + 0.19f;

        float startY = Mathf.Clamp(execStartCamPos.y, 0.36f, 0.45f);
        float handMidY = GetHandsMidY(rootPos.y + 0.65f);

        float camY;
        float desiredDistFromRoot;
        float swayX = 0f;
        float rollDeg = 0f;

        if (norm < 0.16f)
        {
            // 抓取起始：镜头位于伸出的双爪正前方安全距离，完整看见双爪扑来抓住衣领，彻底杜绝手指切近裁剪面
            float u = Smooth01(norm / 0.16f);
            camY = Mathf.Lerp(startY, startY + 0.08f, u);
            float minSafe = Mathf.Max(handsFrontAlongFwd + 0.26f, faceFrontAlongFwd + 0.36f);
            desiredDistFromRoot = Mathf.Max(Mathf.Lerp(0.98f, 1.00f, u), minSafe);
        }
        else if (norm < 0.74f)
        {
            // 被举起阶段：随双手流畅上移至 0.88~0.91m（略高于猴头 0.72m 与手掌 0.83m），
            // 保持镜头在指尖外侧 >=0.23m，双爪稳稳托在画面下方两侧不闪烁、不穿模，视角微朝下直视猴子正脸
            float u = Smooth01((norm - 0.16f) / 0.58f);
            float liftedY = Mathf.Max(Mathf.Lerp(startY + 0.08f, 0.90f, u), handMidY + 0.07f);
            camY = Mathf.Clamp(liftedY, startY + 0.08f, 0.91f);
            float minSafe = Mathf.Max(handsFrontAlongFwd + 0.23f, faceFrontAlongFwd + 0.34f);
            desiredDistFromRoot = Mathf.Max(Mathf.Lerp(0.98f, 0.84f, u), minSafe);

            float struggle = Mathf.Sin((norm - 0.16f) * 24f) * Mathf.Sin(u * Mathf.PI);
            swayX = struggle * 0.012f;
            rollDeg = struggle * 2.2f;
        }
        else
        {
            // 猛咬阶段（0.74~1.00）：双手回收、猴头猛扑向镜头，镜头微沉迎向张开的血口同时守住口鼻前 0.28m 安全线
            float u = Smooth01((norm - 0.74f) / 0.20f);
            camY = Mathf.Lerp(0.90f, 0.77f, u);
            float minSafe = Mathf.Max(faceFrontAlongFwd + 0.28f, handsFrontAlongFwd + 0.20f);
            desiredDistFromRoot = Mathf.Max(Mathf.Lerp(0.84f, 0.90f, u), minSafe);
            rollDeg = Mathf.Sin(u * Mathf.PI) * 3.2f;
        }

        camPos = new Vector3(rootPos.x, 0f, rootPos.z)
                 + fwd * desiredDistFromRoot
                 + right * swayX
                 + Vector3.up * camY;

        // 视线聚焦猴子正脸（双眼与张开的口鼻之间），形成清晰的被高举+微俯视压迫感
        Vector3 faceAim = Vector3.Lerp(headPos, jawPos, 0.42f) + fwd * 0.07f + Vector3.up * 0.01f;
        Vector3 lookDir = (faceAim - camPos).normalized;
        if (lookDir.sqrMagnitude < 0.0001f) lookDir = -fwd;

        Quaternion baseLook = Quaternion.LookRotation(lookDir, Vector3.up);
        camRot = baseLook * Quaternion.Euler(0f, 0f, rollDeg);
    }

    /// <summary>
    /// 处决 3（ArmSlam 挥砸摔出，6.23s — 最后一条命）：
    /// 1) 0.00~0.09：右手抓起玩家；
    /// 2) 0.09~0.60：相机锚定跟随猴子右手（Hand.R）在三维空间划圆抡砸，始终朝向猴子正脸且随手臂方向持续滚转旋转；
    /// 3) 0.60~结束：按抛物线向后甩飞跌落在地（高度 0.085m），倒地视角带轻微歪斜、始终从低往高仰视猴子。
    /// </summary>
    private void ComputeArmSlamCamera(float norm, float dt, out Vector3 camPos, out Quaternion camRot)
    {
        Vector3 rootPos = focusTarget.position;
        Vector3 fwd = GetFlatKillerForward();
        Vector3 headPos = GetMonkeyHeadPos();
        Vector3 chestPos = GetMonkeyChestPos();
        Vector3 monkeyFace = headPos + fwd * 0.06f;

        const float grabEndNorm = 0.09f;
        const float throwReleaseNorm = 0.60f;
        const float landCompleteNorm = 0.74f;

        if (norm < throwReleaseNorm)
        {
            // 实时计算跟随 Hand.R 的三维位置与手臂旋转 Up 向量
            Vector3 handPos = killerHandRBone != null
                ? killerHandRBone.position
                : rootPos + fwd * 0.45f + Vector3.up * 0.55f;
            Vector3 forearmPos = killerForearmRBone != null
                ? killerForearmRBone.position
                : chestPos;

            // 从胸口指向手部的径向向量，确保镜头位于手掌外侧且略偏猴子正前方，避免被手臂/躯干挡住穿模
            Vector3 radialOut = handPos - chestPos;
            if (radialOut.sqrMagnitude < 0.0004f) radialOut = fwd;
            else radialOut.Normalize();

            Vector3 handAnchorPos = handPos + fwd * 0.34f + radialOut * 0.14f;
            handAnchorPos.y = Mathf.Clamp(handAnchorPos.y, 0.10f, 0.90f);

            // 防止镜头太贴近猴子脸或躯干造成穿模
            Vector3 fromFace = handAnchorPos - headPos;
            if (fromFace.magnitude < 0.44f)
            {
                Vector3 pushDir = fromFace.sqrMagnitude > 0.0004f ? fromFace.normalized : fwd;
                handAnchorPos = headPos + pushDir * 0.44f;
                handAnchorPos.y = Mathf.Clamp(handAnchorPos.y, 0.10f, 0.90f);
            }

            if (norm < grabEndNorm)
            {
                float u = Smooth01(norm / grabEndNorm);
                camPos = Vector3.Lerp(execStartCamPos, handAnchorPos, u);
            }
            else
            {
                camPos = handAnchorPos;
            }

            camPos = ClampInsideCorridor(chestPos, camPos, 0.09f, 0.90f);

            // 相机始终朝向猴子正脸
            Vector3 lookDir = (monkeyFace - camPos).normalized;
            if (lookDir.sqrMagnitude < 0.0001f) lookDir = -fwd;

            // 用前臂->手部与胸口->手部的实时摆动向量在视线平面内的极角，连续驱动 360° 手部随动滚转（无万向节翻转）
            Quaternion uprightLook = Quaternion.LookRotation(lookDir, Vector3.up);
            Vector3 rawArmAxis = handPos - forearmPos;
            if (rawArmAxis.sqrMagnitude < 0.0004f) rawArmAxis = radialOut;
            else rawArmAxis.Normalize();

            Vector3 blendedArmAxis = Vector3.Slerp(radialOut, rawArmAxis, 0.65f);
            float ax = Vector3.Dot(blendedArmAxis, uprightLook * Vector3.right);
            float ay = Vector3.Dot(blendedArmAxis, uprightLook * Vector3.up);
            float targetRollDeg = (ax * ax + ay * ay) > 0.0025f
                ? -Mathf.Atan2(ax, ay) * Mathf.Rad2Deg
                : currentArmRollDeg;

            if (norm < grabEndNorm)
            {
                float u = Smooth01(norm / grabEndNorm);
                targetRollDeg = Mathf.LerpAngle(0f, targetRollDeg, u);
            }

            currentArmRollDeg = Mathf.LerpAngle(currentArmRollDeg, targetRollDeg, 1f - Mathf.Exp(-18f * dt));
            camRot = uprightLook * Quaternion.Euler(0f, 0f, currentArmRollDeg);
        }
        else
        {
            // 进入最终甩出阶段：在 norm >= 0.60 的首帧锁存起飞点与安全的走廊落地点
            if (!armSlamThrowLatched)
            {
                armSlamThrowLatched = true;
                throwStartPos = execCamWorldPos;
                throwStartRollDeg = currentArmRollDeg;

                // 计算沿走廊前方（略带右手挥甩方向）的安全落地位置（不穿墙！）
                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                Vector3 throwDir = (fwd + right * 0.14f).normalized;
                float maxThrowDist = 1.38f;
                Vector3 castOrigin = new Vector3(rootPos.x, 0.24f, rootPos.z);
                float safeDist = maxThrowDist;
                if (Physics.SphereCast(castOrigin, 0.11f, throwDir, out RaycastHit hit, maxThrowDist + 0.14f, wallLayerMask, QueryTriggerInteraction.Ignore))
                {
                    safeDist = Mathf.Max(0.72f, hit.distance - 0.12f);
                }
                else if (Physics.SphereCast(castOrigin, 0.11f, fwd, out RaycastHit hitFwd, maxThrowDist + 0.14f, wallLayerMask, QueryTriggerInteraction.Ignore))
                {
                    throwDir = fwd;
                    safeDist = Mathf.Max(0.72f, hitFwd.distance - 0.12f);
                }

                throwLandPos = new Vector3(
                    rootPos.x + throwDir.x * safeDist,
                    rootPos.y + 0.085f, // 倒地低机位（约 8.5cm 贴近地毯）
                    rootPos.z + throwDir.z * safeDist);

                throwRollSign = throwStartRollDeg >= 0f ? 1f : -1f;
            }

            float flyT = Mathf.Clamp01((norm - throwReleaseNorm) / (landCompleteNorm - throwReleaseNorm));
            // 前快后缓的抛飞落地曲线 + 落地轻微弹跳
            float horizT = 1f - Mathf.Pow(1f - flyT, 2.2f);
            Vector3 pos = Vector3.Lerp(throwStartPos, throwLandPos, horizT);
            float arcBoost = Mathf.Sin(flyT * Mathf.PI) * 0.14f;
            float bounce = flyT > 0.78f
                ? Mathf.Sin(((flyT - 0.78f) / 0.22f) * Mathf.PI) * 0.025f
                : 0f;
            pos.y = Mathf.Lerp(throwStartPos.y, throwLandPos.y, flyT * flyT) + arcBoost + bounce;

            // 倒地后极轻微呼吸抖动，保持低机位仰视压迫感
            if (flyT >= 1f)
            {
                float groundTime = (norm - landCompleteNorm) * 6.23f;
                pos.y = throwLandPos.y + Mathf.Sin(groundTime * 4.5f) * 0.003f;
            }

            camPos = ClampInsideCorridor(chestPos, pos, 0.075f, 0.90f);

            // 甩飞与倒地全程始终朝向猴子！倒地后相机在 Y=0.085m 而猴子头部在 Y≈0.55m，自然形成清晰的从低往高仰视视角
            Vector3 lookTarget = headPos + Vector3.up * 0.03f;
            Vector3 lookDir = (lookTarget - camPos).normalized;
            if (lookDir.sqrMagnitude < 0.0001f) lookDir = -fwd;

            // 相机滚转从手部旋转惯性顺滑翻滚落地成轻微歪头倒地姿态（约 ±18°）
            float downedRoll = Mathf.LerpAngle(throwStartRollDeg, throwRollSign * 18f, Smooth01(flyT));
            currentArmRollDeg = downedRoll;
            camRot = Quaternion.LookRotation(lookDir, Vector3.up) * Quaternion.Euler(0f, 0f, downedRoll);
        }
    }

    /// <summary>
    /// 处决 2（PunchStomp 猛击踩踏，4.80s）：被打倒后镜头平滑下沉微退，低位仰视且全程防挥拳/头部穿模。
    /// </summary>
    private void ComputePunchStompCamera(float norm, float dt, out Vector3 camPos, out Quaternion camRot)
    {
        Vector3 rootPos = focusTarget.position;
        Vector3 fwd = GetFlatKillerForward();
        Vector3 headPos = GetMonkeyHeadPos();

        float startDist = Mathf.Max(1.22f, Vector3.Dot(execStartCamPos - rootPos, fwd));
        float startY = Mathf.Clamp(execStartCamPos.y, 0.34f, 0.45f);

        float dropT = Smooth01((norm - 0.14f) / 0.16f);
        float camY = Mathf.Lerp(startY, 0.19f, dropT);
        float camDist = Mathf.Lerp(startDist, Mathf.Max(startDist, 1.38f), dropT);

        camPos = new Vector3(rootPos.x, 0f, rootPos.z) + fwd * camDist + Vector3.up * camY;
        Vector3 lookDir = (headPos + Vector3.up * 0.02f - camPos).normalized;
        if (lookDir.sqrMagnitude < 0.0001f) lookDir = -fwd;

        float rollDeg = Mathf.Lerp(0f, -6f, dropT);
        camRot = Quaternion.LookRotation(lookDir, Vector3.up) * Quaternion.Euler(0f, 0f, rollDeg);
    }

    private float GetExecutionNormalizedTime()
    {
        if (killerAnim != null && killerAnim.CurrentNormalizedTime > 0.0001f)
        {
            return Mathf.Clamp01(killerAnim.CurrentNormalizedTime);
        }

        float estDuration = currentCatchType switch
        {
            3 => 3.53f,
            2 => 4.80f,
            _ => 6.23f
        };
        return Mathf.Clamp01(executionElapsed / estDuration);
    }

    private Vector3 GetFlatKillerForward()
    {
        Vector3 fwd = focusTarget != null ? focusTarget.forward : Vector3.forward;
        fwd.y = 0f;
        return fwd.sqrMagnitude > 0.0001f ? fwd.normalized : Vector3.forward;
    }

    private Vector3 GetMonkeyHeadPos()
    {
        if (killerHeadBone != null) return killerHeadBone.position;
        return focusTarget.position + Vector3.up * killerFocusHeight;
    }

    private Vector3 GetMonkeyChestPos()
    {
        if (killerChestBone != null) return killerChestBone.position;
        return focusTarget.position + Vector3.up * 0.48f;
    }

    private float GetHandsMidY(float fallback)
    {
        if (killerHandLBone != null && killerHandRBone != null)
        {
            return 0.5f * (killerHandLBone.position.y + killerHandRBone.position.y);
        }
        if (killerHandLBone != null) return killerHandLBone.position.y;
        if (killerHandRBone != null) return killerHandRBone.position.y;
        return fallback;
    }

    private float GetHandsFrontAlongForward(Vector3 rootPos, Vector3 fwd, float fallback)
    {
        float best = -999f;
        if (killerHandLBone != null) best = Mathf.Max(best, Vector3.Dot(killerHandLBone.position - rootPos, fwd));
        if (killerHandRBone != null) best = Mathf.Max(best, Vector3.Dot(killerHandRBone.position - rootPos, fwd));
        return best > -100f ? best : fallback;
    }

    private void RestoreKillerOffscreenFlags()
    {
        if (killerSkinnedRenderers != null && killerOrigUpdateWhenOffscreen != null)
        {
            int n = Mathf.Min(killerSkinnedRenderers.Length, killerOrigUpdateWhenOffscreen.Length);
            for (int i = 0; i < n; i++)
            {
                if (killerSkinnedRenderers[i] != null)
                {
                    killerSkinnedRenderers[i].updateWhenOffscreen = killerOrigUpdateWhenOffscreen[i];
                }
            }
        }
        killerSkinnedRenderers = null;
        killerOrigUpdateWhenOffscreen = null;
    }

    /// <summary>
    /// 硬边界防穿模：确保镜头始终位于猴子头/口鼻网格前端以及双手指尖外侧，杜绝切近裁剪面与面部穿模。
    /// </summary>
    private Vector3 EnforceFaceAntiClip(Vector3 camPos, float minFrontMargin, bool protectHands = false)
    {
        Vector3 fwd = GetFlatKillerForward();
        Vector3 headPos = GetMonkeyHeadPos();
        Vector3 jawPos = killerJawBone != null ? killerJawBone.position : headPos + fwd * 0.06f;

        Vector3 snoutAnchor = Vector3.Dot(jawPos - headPos, fwd) > 0f
            ? jawPos + fwd * 0.19f
            : headPos + fwd * 0.25f;

        float forwardClear = Vector3.Dot(camPos - snoutAnchor, fwd);
        if (forwardClear < minFrontMargin)
        {
            camPos += fwd * (minFrontMargin - forwardClear);
        }

        // 同时保证球面距离远离头骨中心（防止上下偏置过近擦到额头/下巴）
        Vector3 toCam = camPos - headPos;
        float minSphereDist = 0.26f + minFrontMargin * 0.85f;
        if (toCam.magnitude < minSphereDist)
        {
            Vector3 push = toCam.sqrMagnitude > 0.0004f ? toCam.normalized : fwd;
            camPos = headPos + push * minSphereDist;
        }

        // 抓咬举起时额外检查双手指尖前沿与球面净空，防止手指穿进相机近裁剪面导致一闪一闪
        if (protectHands && focusTarget != null)
        {
            Vector3 rootPos = focusTarget.position;
            float handsTipFwd = GetHandsFrontAlongForward(rootPos, fwd, 0.50f) + 0.19f;
            float camFwd = Vector3.Dot(camPos - rootPos, fwd);
            if (camFwd < handsTipFwd + 0.21f)
            {
                camPos += fwd * ((handsTipFwd + 0.21f) - camFwd);
            }

            if (killerHandLBone != null)
            {
                Vector3 dl = camPos - (killerHandLBone.position + fwd * 0.08f);
                if (dl.magnitude < 0.23f)
                {
                    camPos += fwd * (0.23f - dl.magnitude);
                }
            }
            if (killerHandRBone != null)
            {
                Vector3 dr = camPos - (killerHandRBone.position + fwd * 0.08f);
                if (dr.magnitude < 0.23f)
                {
                    camPos += fwd * (0.23f - dr.magnitude);
                }
            }
        }
        return camPos;
    }

    private Vector3 ClampInsideCorridor(Vector3 anchorPos, Vector3 desiredPos, float minY, float maxY)
    {
        desiredPos.y = Mathf.Clamp(desiredPos.y, minY, maxY);

        Vector3 delta = desiredPos - anchorPos;
        float dist = delta.magnitude;
        if (dist > 0.02f &&
            Physics.SphereCast(anchorPos, 0.07f, delta / dist, out RaycastHit hit, dist, wallLayerMask, QueryTriggerInteraction.Ignore))
        {
            float safeDist = Mathf.Max(0.15f, hit.distance - 0.05f);
            desiredPos = anchorPos + (delta / dist) * safeDist;
            desiredPos.y = Mathf.Clamp(desiredPos.y, minY, maxY);
        }

        return desiredPos;
    }

    private static float Smooth01(float x)
    {
        x = Mathf.Clamp01(x);
        return x * x * (3f - 2f * x);
    }

    private static Transform FindChildRecursive(Transform root, string name)
    {
        for (int i = 0; i < root.childCount; i++)
        {
            Transform ch = root.GetChild(i);
            if (ch.name == name) return ch;
            Transform found = FindChildRecursive(ch, name);
            if (found != null) return found;
        }
        return null;
    }
}
