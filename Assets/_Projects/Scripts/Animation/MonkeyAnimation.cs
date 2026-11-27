using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Animator))]
public class MonkeyAnimation : MonoBehaviour
{
    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int LocomotionSpeedMulHash = Animator.StringToHash("LocomotionSpeedMul");
    private static readonly int CatchHash = Animator.StringToHash("Catch");
    private static readonly int IsSleepingHash = Animator.StringToHash("IsSleeping");
    private static readonly int WakeUpHash = Animator.StringToHash("WakeUp");
    private static readonly int IsFrozenHash = Animator.StringToHash("IsFrozen");
    private static readonly int EntranceHash = Animator.StringToHash("Entrance");

    private struct EyelidBlendTarget
    {
        public SkinnedMeshRenderer renderer;
        public int blendShapeIndex;
    }

    private struct ImpactCue
    {
        public float normTime;
        public float shakeStrength;
        public float shakeDuration;
        public float bloodIntensity;
        public int bloodDirHint; // -1=左, 0=中, 1=右, 2=全屏重击
    }

    // 与 Blender 实测的 3 段 FBX 处决动作关键帧精确对齐的震动 + 屏幕血溅节奏：
    // Catch == 3 -> ExecuteOne (Ashjaw_Monkey_GrabBite, 3.53s)
    private static readonly ImpactCue[] GrabBiteCues =
    {
        new ImpactCue { normTime = 0.14f, shakeStrength = 0.017f, shakeDuration = 0.28f, bloodIntensity = 0.45f, bloodDirHint = 1 }, // 双手猛抓
        new ImpactCue { normTime = 0.50f, shakeStrength = 0.008f, shakeDuration = 0.35f, bloodIntensity = 0.00f, bloodDirHint = 0 }, // 拎起挣扎微震
        new ImpactCue { normTime = 0.83f, shakeStrength = 0.032f, shakeDuration = 0.52f, bloodIntensity = 1.40f, bloodDirHint = 2 }, // 张嘴猛咬爆血重击
    };

    // Catch == 2 -> ExecuteTwo (Ashjaw_Monkey_PunchStomp, 4.80s)
    private static readonly ImpactCue[] PunchStompCues =
    {
        new ImpactCue { normTime = 0.13f, shakeStrength = 0.020f, shakeDuration = 0.28f, bloodIntensity = 0.65f, bloodDirHint = -1 }, // 第一记重拳
        new ImpactCue { normTime = 0.19f, shakeStrength = 0.018f, shakeDuration = 0.25f, bloodIntensity = 0.55f, bloodDirHint = 1 },  // 第二记连击
        new ImpactCue { normTime = 0.32f, shakeStrength = 0.024f, shakeDuration = 0.32f, bloodIntensity = 0.80f, bloodDirHint = 0 },  // 扑压砸击
        new ImpactCue { normTime = 0.46f, shakeStrength = 0.022f, shakeDuration = 0.28f, bloodIntensity = 0.68f, bloodDirHint = -1 }, // 连续捶打
        new ImpactCue { normTime = 0.55f, shakeStrength = 0.028f, shakeDuration = 0.36f, bloodIntensity = 0.95f, bloodDirHint = 0 },  // 举臂猛砸
        new ImpactCue { normTime = 0.72f, shakeStrength = 0.032f, shakeDuration = 0.42f, bloodIntensity = 1.15f, bloodDirHint = 1 },  // 重击压碎
        new ImpactCue { normTime = 0.84f, shakeStrength = 0.034f, shakeDuration = 0.48f, bloodIntensity = 1.40f, bloodDirHint = 2 },  // 终结践踏爆血
    };

    // Catch == 1 -> ExecuteThree (Ashjaw_Monkey_ArmSlam, 6.23s — 最后一条命：三次摔砸 + 猛力扔出 + 倒地仰视)
    private static readonly ImpactCue[] ArmSlamThrowCues =
    {
        new ImpactCue { normTime = 0.09f, shakeStrength = 0.018f, shakeDuration = 0.28f, bloodIntensity = 0.45f, bloodDirHint = 0 },  // 抓住玩家
        new ImpactCue { normTime = 0.23f, shakeStrength = 0.028f, shakeDuration = 0.36f, bloodIntensity = 0.85f, bloodDirHint = -1 }, // 第一次左侧摔砸
        new ImpactCue { normTime = 0.36f, shakeStrength = 0.028f, shakeDuration = 0.36f, bloodIntensity = 0.90f, bloodDirHint = 1 },  // 第二次右侧摔砸
        new ImpactCue { normTime = 0.49f, shakeStrength = 0.032f, shakeDuration = 0.40f, bloodIntensity = 1.10f, bloodDirHint = -1 }, // 第三次重力猛砸
        new ImpactCue { normTime = 0.60f, shakeStrength = 0.036f, shakeDuration = 0.48f, bloodIntensity = 1.30f, bloodDirHint = 2 },  // 猛力甩飞出手
        new ImpactCue { normTime = 0.73f, shakeStrength = 0.028f, shakeDuration = 0.45f, bloodIntensity = 0.95f, bloodDirHint = 0 },  // 重重跌落地面
    };

    [Header("移动动画步频随移速联动（消除滑步平移感）")]
    [SerializeField] private float speedDampTime = 0.1f;
    [SerializeField] private float runAnimReferenceSpeed = 0.88f; // 实测 Ashjaw_Monkey_FastRun 原速步幅约 0.87 m/s
    [SerializeField] private float minLocomotionAnimSpeed = 0.95f;
    [SerializeField] private float maxLocomotionAnimSpeed = 4.5f;

    [Header("处决动画收尾节点")]
    [SerializeField, Range(0.5f, 1f)] private float grabBiteFinishNorm = 0.94f;
    [SerializeField, Range(0.5f, 1f)] private float punchStompFinishNorm = 0.94f;
    [SerializeField, Range(0.5f, 1f)] private float armSlamFinishNorm = 0.90f;

    private MonkeyMovement movement;
    private Animator animator;
    private Action onExecuteFinished;
    private Coroutine executeWaitRoutine;
    private Coroutine wakeWaitRoutine;
    private bool isExecuting;
    private bool isSleepingOrWaking;
    private bool isEyesClosedSleep;
    private bool hasManualMotionOverride;
    private bool isTimeFrozen;
    private bool isPlayingCutscene;
    private int currentCatchType;
    private float currentNormalizedTime;

    private bool hasSpeedParam;
    private bool hasLocomotionMulParam;
    private bool hasCatchParam;
    private bool hasIsSleepingParam;
    private bool hasWakeUpParam;
    private bool hasIsFrozenParam;
    private bool hasEntranceParam;

    private readonly List<EyelidBlendTarget> eyelidTargets = new();
    private float currentEyesClosedWeight;

    public bool IsExecuting => isExecuting;
    public int CurrentCatchType => currentCatchType;
    public float CurrentNormalizedTime => currentNormalizedTime;

    private float currentLocomotionMul = 1f;

    private void Awake()
    {
        movement = GetComponentInParent<MonkeyMovement>();
        animator = GetComponent<Animator>();
        CacheEyelidBlendShapes();
        if (animator != null)
        {
            CacheAnimatorParameters();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (hasLocomotionMulParam)
            {
                animator.SetFloat(LocomotionSpeedMulHash, 1f);
            }
        }
    }

    private void CacheEyelidBlendShapes()
    {
        eyelidTargets.Clear();
        SkinnedMeshRenderer[] smrs = GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < smrs.Length; i++)
        {
            SkinnedMeshRenderer smr = smrs[i];
            if (smr == null || smr.sharedMesh == null) continue;
            int idx = smr.sharedMesh.GetBlendShapeIndex("Eyes Closed");
            if (idx >= 0)
            {
                eyelidTargets.Add(new EyelidBlendTarget { renderer = smr, blendShapeIndex = idx });
            }
        }
    }

    private void CacheAnimatorParameters()
    {
        hasSpeedParam = false;
        hasLocomotionMulParam = false;
        hasCatchParam = false;
        hasIsSleepingParam = false;
        hasWakeUpParam = false;
        hasIsFrozenParam = false;
        hasEntranceParam = false;
        if (animator == null) return;

        AnimatorControllerParameter[] pars = animator.parameters;
        for (int i = 0; i < pars.Length; i++)
        {
            int h = pars[i].nameHash;
            if (h == SpeedHash) hasSpeedParam = true;
            else if (h == LocomotionSpeedMulHash) hasLocomotionMulParam = true;
            else if (h == CatchHash) hasCatchParam = true;
            else if (h == IsSleepingHash) hasIsSleepingParam = true;
            else if (h == WakeUpHash) hasWakeUpParam = true;
            else if (h == IsFrozenHash) hasIsFrozenParam = true;
            else if (h == EntranceHash) hasEntranceParam = true;
        }
    }

    /// <summary>
    /// 播放首领电梯入场演出动画（生气站立 -> 仰天怒吼 -> 走出电梯 -> 向四周张望）。
    /// </summary>
    public void PlayEntranceCutscene()
    {
        isPlayingCutscene = true;
        isTimeFrozen = false;
        isEyesClosedSleep = false;
        currentEyesClosedWeight = 0f;
        if (animator == null) animator = GetComponent<Animator>();
        if (animator == null) return;

        animator.speed = 1f;
        if (hasIsFrozenParam) animator.SetBool(IsFrozenHash, false);
        if (hasEntranceParam) animator.SetTrigger(EntranceHash);
        animator.Play("Entrance", 0, 0f);
        animator.Update(0f);
    }

    /// <summary>
    /// 黄水晶定身状态切换：
    /// 播放专属「定身」循环动画（闭眼、直立站姿、轻微身体摆动），解除定身时平滑回到追击/巡逻动画并睁眼。
    /// </summary>
    public void SetTimeFrozen(bool frozen)
    {
        isTimeFrozen = frozen;
        if (animator == null) animator = GetComponent<Animator>();
        if (animator == null) return;

        // 保持正常播放速率，以播放闭眼站立轻微摆动的定身动画
        animator.speed = 1f;

        if (hasIsFrozenParam)
        {
            animator.SetBool(IsFrozenHash, frozen);
        }

        if (frozen)
        {
            if (hasSpeedParam) animator.SetFloat(SpeedHash, 0f);
            if (hasLocomotionMulParam) animator.SetFloat(LocomotionSpeedMulHash, 1f);
            if (hasIsFrozenParam)
            {
                animator.CrossFadeInFixedTime("Frozen", 0.16f, 0, 0f);
            }
            else
            {
                animator.speed = 0f;
            }
        }
        else
        {
            if (!isExecuting && !isSleepingOrWaking)
            {
                animator.CrossFadeInFixedTime("Locomotion", 0.16f, 0, 0f);
            }
        }
    }

    private void LateUpdate()
    {
        if (eyelidTargets.Count == 0) return;

        // 在 Animator 采样后统一驱动闭眼形态键，确保定身与沉睡全程眼睑紧闭且开关过渡丝滑
        float targetWeight = (isTimeFrozen || isEyesClosedSleep) ? 100f : 0f;
        currentEyesClosedWeight = Mathf.MoveTowards(currentEyesClosedWeight, targetWeight, Time.deltaTime * 520f);
        for (int i = 0; i < eyelidTargets.Count; i++)
        {
            EyelidBlendTarget t = eyelidTargets[i];
            if (t.renderer != null)
            {
                t.renderer.SetBlendShapeWeight(t.blendShapeIndex, currentEyesClosedWeight);
            }
        }
    }

    private void Update()
    {
        if (isPlayingCutscene || isTimeFrozen || isExecuting || isSleepingOrWaking || hasManualMotionOverride || movement == null || animator == null) return;

        float actualSpeed = movement.Speed;
        float normSpeed = movement.NormalizedSpeed;
        ApplyLocomotionParams(actualSpeed, normSpeed, Time.deltaTime);
    }

    private void ApplyLocomotionParams(float actualSpeed, float normSpeed, float dt)
    {
        if (animator == null) return;
        if (hasSpeedParam)
        {
            animator.SetFloat(SpeedHash, normSpeed, speedDampTime, dt);
        }

        // 按实际移动速度动态缩放 Locomotion 动画播放倍率（静止保持 1.0 呼吸，移速越快步频越快，消除平移滑步感）
        float targetMul = 1f;
        if (actualSpeed > 0.05f)
        {
            float moveWeight = Mathf.Clamp01(actualSpeed / 0.45f);
            float refSpeed = Mathf.Max(0.25f, runAnimReferenceSpeed);
            float strideMul = Mathf.Clamp(actualSpeed / refSpeed, minLocomotionAnimSpeed, maxLocomotionAnimSpeed);
            targetMul = Mathf.Lerp(1f, strideMul, moveWeight);
        }

        currentLocomotionMul = Mathf.Lerp(currentLocomotionMul, targetMul, 1f - Mathf.Exp(-10f * dt));
        if (hasLocomotionMulParam)
        {
            animator.SetFloat(LocomotionSpeedMulHash, currentLocomotionMul);
        }
    }

    /// <summary>
    /// 进入暗门后的睡觉循环动画（Ashjaw_Monkey_Sleep）。
    /// </summary>
    public void EnterSleep()
    {
        if (wakeWaitRoutine != null)
        {
            StopCoroutine(wakeWaitRoutine);
            wakeWaitRoutine = null;
        }
        isSleepingOrWaking = true;
        isEyesClosedSleep = true;
        isTimeFrozen = false;
        hasManualMotionOverride = false;
        currentEyesClosedWeight = 100f;

        if (animator == null) animator = GetComponent<Animator>();
        if (animator == null) return;

        if (hasIsFrozenParam) animator.SetBool(IsFrozenHash, false);
        if (hasCatchParam) animator.SetInteger(CatchHash, 0);
        if (hasSpeedParam) animator.SetFloat(SpeedHash, 0f);
        if (hasLocomotionMulParam) animator.SetFloat(LocomotionSpeedMulHash, 1f);
        if (hasWakeUpParam) animator.ResetTrigger(WakeUpHash);
        if (hasIsSleepingParam) animator.SetBool(IsSleepingHash, true);
        animator.Play("Sleep", 0, UnityEngine.Random.Range(0f, 0.85f));
        animator.Update(0f);
    }

    /// <summary>
    /// 开门惊醒后播放苏醒起身动画（Ashjaw_Monkey_WakeUp，约 2.47s），结束后触发回调跨出门追击。
    /// </summary>
    public void PlayWakeUp(Action onWakeFinished)
    {
        if (wakeWaitRoutine != null)
        {
            StopCoroutine(wakeWaitRoutine);
            wakeWaitRoutine = null;
        }

        isSleepingOrWaking = true;
        isEyesClosedSleep = false;
        hasManualMotionOverride = false;

        if (animator == null) animator = GetComponent<Animator>();
        if (animator == null)
        {
            isSleepingOrWaking = false;
            onWakeFinished?.Invoke();
            return;
        }

        animator.SetBool(IsSleepingHash, false);
        animator.SetTrigger(WakeUpHash);
        animator.CrossFadeInFixedTime("WakeUp", 0.08f, 0, 0f);
        wakeWaitRoutine = StartCoroutine(WaitWakeUpRoutine(onWakeFinished));
    }

    /// <summary>
    /// 苏醒后跨出门槛期间手动驱动奔跑步频（此时 NavMeshAgent 尚未启用）。
    /// </summary>
    public void SetManualStepOutMotion(float actualSpeed, float maxRunSpeed)
    {
        isSleepingOrWaking = false;
        hasManualMotionOverride = true;
        if (animator == null) return;

        float norm = maxRunSpeed > 0.01f ? Mathf.Clamp01(actualSpeed / maxRunSpeed) : 0.8f;
        ApplyLocomotionParams(actualSpeed, norm, Time.deltaTime);
    }

    public void ClearSleepAndManualState()
    {
        if (wakeWaitRoutine != null)
        {
            StopCoroutine(wakeWaitRoutine);
            wakeWaitRoutine = null;
        }
        isSleepingOrWaking = false;
        hasManualMotionOverride = false;
        if (animator != null)
        {
            animator.SetBool(IsSleepingHash, false);
            animator.ResetTrigger(WakeUpHash);
        }
    }

    private IEnumerator WaitWakeUpRoutine(Action onWakeFinished)
    {
        float enterTimer = 0f;
        while (enterTimer < 0.45f)
        {
            AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
            if (!animator.IsInTransition(0) && info.IsName("WakeUp"))
            {
                break;
            }
            enterTimer += Time.deltaTime;
            yield return null;
        }

        float playTimer = 0f;
        while (playTimer < 2.85f)
        {
            AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
            if (!animator.IsInTransition(0))
            {
                if (info.IsName("Locomotion") || (info.IsName("WakeUp") && info.normalizedTime >= 0.88f))
                {
                    break;
                }
            }
            playTimer += Time.deltaTime;
            yield return null;
        }

        wakeWaitRoutine = null;
        isSleepingOrWaking = false;
        if (animator != null)
        {
            animator.CrossFadeInFixedTime("Locomotion", 0.12f, 0, 0f);
        }
        onWakeFinished?.Invoke();
    }

    /// <summary>
    /// 播放处决动画（3=ExecuteOne 抓咬，2=ExecuteTwo 猛击踩踏，1=ExecuteThree 摔砸扔出），
    /// 直接 CrossFade 切入目标状态并按关键帧同步触发屏幕震动与血液泼溅。
    /// </summary>
    public void Catch(int catchType, Action onFinished = null)
    {
        isExecuting = true;
        currentCatchType = Mathf.Clamp(catchType, 1, 3);
        currentNormalizedTime = 0f;
        onExecuteFinished = onFinished;

        if (animator == null)
        {
            NotifyExecuteFinished();
            return;
        }

        string targetState = currentCatchType switch
        {
            3 => "ExecuteOne",
            2 => "ExecuteTwo",
            _ => "ExecuteThree"
        };

        animator.SetFloat(SpeedHash, 0f);
        animator.SetInteger(CatchHash, currentCatchType);
        // 显式 CrossFade 确保即便状态机正处于过渡中也能在首帧立刻进入处决动画
        animator.CrossFadeInFixedTime(targetState, 0.06f, 0, 0f);

        if (executeWaitRoutine != null)
        {
            StopCoroutine(executeWaitRoutine);
        }
        executeWaitRoutine = StartCoroutine(WaitExecuteRoutine(currentCatchType, targetState));
    }

    public void SkipExecution()
    {
        if (!isExecuting) return;
        NotifyExecuteFinished();
    }

    public void OnExecuteAnimEnd()
    {
        NotifyExecuteFinished();
    }

    public void ResetAnimation()
    {
        if (executeWaitRoutine != null)
        {
            StopCoroutine(executeWaitRoutine);
            executeWaitRoutine = null;
        }
        if (wakeWaitRoutine != null)
        {
            StopCoroutine(wakeWaitRoutine);
            wakeWaitRoutine = null;
        }
        onExecuteFinished = null;
        isExecuting = false;
        isSleepingOrWaking = false;
        isEyesClosedSleep = false;
        hasManualMotionOverride = false;
        isTimeFrozen = false;
        currentEyesClosedWeight = 0f;
        currentCatchType = 0;
        currentNormalizedTime = 0f;

        if (animator != null)
        {
            animator.speed = 1f;
            if (hasIsFrozenParam) animator.SetBool(IsFrozenHash, false);
            if (hasIsSleepingParam) animator.SetBool(IsSleepingHash, false);
            if (hasWakeUpParam) animator.ResetTrigger(WakeUpHash);
            if (hasCatchParam) animator.SetInteger(CatchHash, 0);
            if (hasSpeedParam) animator.SetFloat(SpeedHash, 0f);
            currentLocomotionMul = 1f;
            if (hasLocomotionMulParam) animator.SetFloat(LocomotionSpeedMulHash, 1f);
            animator.Play("Locomotion", 0, 0f);
            animator.Update(0f);
        }
    }

    private IEnumerator WaitExecuteRoutine(int catchType, string targetState)
    {
        ImpactCue[] cues = catchType switch
        {
            3 => GrabBiteCues,
            2 => PunchStompCues,
            _ => ArmSlamThrowCues
        };
        float targetFinishNorm = catchType switch
        {
            3 => grabBiteFinishNorm,
            2 => punchStompFinishNorm,
            _ => armSlamFinishNorm
        };

        // 1. 等待进入目标 Execute 状态
        float enterTimer = 0f;
        while (enterTimer < 0.5f)
        {
            AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
            if (!animator.IsInTransition(0) && info.IsName(targetState))
            {
                break;
            }
            enterTimer += Time.deltaTime;
            yield return null;
        }

        // 2. 监听播放进度：同步触发屏幕震动 + 血液泼溅冲击
        int nextCueIdx = 0;
        float playTimer = 0f;
        while (playTimer < 9.0f)
        {
            AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
            bool inExecState = !animator.IsInTransition(0) && !info.IsName("Locomotion");
            if (inExecState)
            {
                currentNormalizedTime = info.normalizedTime;

                while (nextCueIdx < cues.Length && info.normalizedTime >= cues[nextCueIdx].normTime)
                {
                    ImpactCue cue = cues[nextCueIdx];
                    if (cue.shakeStrength > 0.0001f)
                    {
                        HapticFeedback.RequestShake(cue.shakeStrength, cue.shakeDuration);
                    }
                    if (cue.bloodIntensity > 0.01f)
                    {
                        ExecutionBloodScreenVFX.RequestImpact(cue.bloodIntensity, cue.bloodDirHint);
                    }
                    nextCueIdx++;
                }

                if (info.normalizedTime >= targetFinishNorm)
                {
                    break;
                }
            }

            playTimer += Time.deltaTime;
            yield return null;
        }

        executeWaitRoutine = null;
        NotifyExecuteFinished();
    }

    private void NotifyExecuteFinished()
    {
        if (executeWaitRoutine != null)
        {
            StopCoroutine(executeWaitRoutine);
            executeWaitRoutine = null;
        }

        Action callback = onExecuteFinished;
        onExecuteFinished = null;
        callback?.Invoke();
    }
}
