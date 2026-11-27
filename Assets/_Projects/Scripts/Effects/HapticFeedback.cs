using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 玩家第一人称相机综合震动控制器（适配当前 0.5m 玩家 / 1.0m 走廊比例，低频柔和不眩晕）：
/// 1. 玩家奔跑：轻微流畅的脚步微晃（走路/停止自动平滑归零）；
/// 2. 附近猴子：
///    - 巡逻/静止：完全不影响玩家屏幕；
///    - 普通奔跑（声源/水晶追查）：范围内轻微压迫震动（按距离平滑衰减）；
///    - 狂奔（直视追击）：范围内较明显但克制的剧烈震动（平滑进出不突兀）；
/// 3. 处决/外部冲击：自动订阅 PlayerInteract.OnCaught，并保留 Shake(...) 与静态广播入口。
/// </summary>
[DefaultExecutionOrder(50)]
public class HapticFeedback : MonoBehaviour
{
    public static event Action<float, float> OnShakeRequested;

    /// <summary>
    /// 全局镜头晃动倍率（后续可直接由设置面板「镜头晃动」选项调节，0=关闭，1=标准）。
    /// </summary>
    public static float GlobalShakeScale = 1f;

    public static void RequestShake(float strength, float time)
    {
        OnShakeRequested?.Invoke(strength, time);
    }

    [Header("全局开关与兼容倍率")]
    [SerializeField, Range(0f, 2f)] private float effect = 1f;
    [SerializeField] private float duration = 1f;

    [Header("玩家奔跑轻微摇晃（走路/停止=无）")]
    [SerializeField] private float sprintBobAmpY = 0.0042f;
    [SerializeField] private float sprintSwayAmpX = 0.0020f;
    [SerializeField] private float sprintRollDeg = 0.28f;
    [SerializeField] private float sprintStrideFreq = 9.2f;
    [SerializeField] private float sprintBlendSpeed = 6.5f;

    [Header("猴子普通奔跑（范围内轻微震动；巡逻不影响屏幕）")]
    [SerializeField] private float monkeyRunRadius = 5.5f;
    [SerializeField] private float monkeyRunMaxPos = 0.0048f;
    [SerializeField] private float monkeyRunMaxRollDeg = 0.26f;
    [SerializeField] private float monkeyRunNoiseFreq = 10.5f;

    [Header("猴子狂奔（范围内剧烈震动，平滑过渡不突兀）")]
    [SerializeField] private float monkeySprintRadius = 7.5f;
    [SerializeField] private float monkeySprintMaxPos = 0.0135f;
    [SerializeField] private float monkeySprintMaxRollDeg = 0.85f;
    [SerializeField] private float monkeySprintNoiseFreq = 17.5f;

    [Header("猴子压迫震动平滑进出速度")]
    [SerializeField] private float threatRiseSpeed = 5.5f;
    [SerializeField] private float threatReleaseSpeed = 2.6f;

    [Header("处决瞬间冲击震动")]
    [SerializeField] private float executeShakeStrength = 0.012f;
    [SerializeField] private float executeShakeDuration = 0.22f;
    [SerializeField] private float executeShakeRollDeg = 1.00f;
    [SerializeField] private float impulseNoiseFreq = 22.0f;

    private PlayerInteract player;
    private Vector3 initialOriginPos;
    private Vector3 originPos;
    private Quaternion originRot = Quaternion.identity;
    private bool useCustomBaseRotation;
    private float noiseSeedX;
    private float noiseSeedY;
    private float noiseSeedR;

    private float sprintWeight;
    private float sprintPhase;

    private float curThreatPos;
    private float curThreatRoll;
    private float curThreatFreq = 10.5f;
    private float threatNoiseTime;

    private float impulseTimer;
    private float impulseTotalTime;
    private float impulsePosAmp;
    private float impulseRollAmp;
    private float impulseNoiseTime;

    private bool hasAppliedOffset;

    private void Awake()
    {
        initialOriginPos = transform.localPosition;
        originPos = initialOriginPos;
        player = GetComponentInParent<PlayerInteract>();

        noiseSeedX = UnityEngine.Random.Range(1f, 97f);
        noiseSeedY = UnityEngine.Random.Range(101f, 197f);
        noiseSeedR = UnityEngine.Random.Range(201f, 297f);
    }

    /// <summary>
    /// 供 FirstView 在处决演出期间微调相机基准本地位置（避免穿模/演出运镜），震动会叠加在该基准之上。
    /// </summary>
    public void SetBaseLocalPosition(Vector3 localPos)
    {
        originPos = localPos;
        useCustomBaseRotation = false;
        if (!hasAppliedOffset)
        {
            transform.localPosition = originPos;
        }
    }

    /// <summary>
    /// 供 FirstView 在处决演出期间同步传入相机基准本地位置与完整三维旋转（保留滚转与任意视角），震动叠加在其上而不被覆盖。
    /// </summary>
    public void SetExecutionBasePose(Vector3 localPos, Quaternion localRot)
    {
        originPos = localPos;
        originRot = localRot;
        useCustomBaseRotation = true;
        transform.localPosition = originPos;
        transform.localRotation = originRot;
    }

    public void ClearImpulseAndResetBase()
    {
        impulseTimer = 0f;
        impulseTotalTime = 0f;
        impulsePosAmp = 0f;
        impulseRollAmp = 0f;
        curThreatPos = 0f;
        curThreatRoll = 0f;
        originPos = initialOriginPos;
        originRot = Quaternion.identity;
        useCustomBaseRotation = false;
        ResetCameraOffset();
    }

    private void OnEnable()
    {
        if (player == null)
        {
            player = GetComponentInParent<PlayerInteract>();
        }
        if (player != null)
        {
            player.OnCaught += HandlePlayerCaught;
        }
        OnShakeRequested += HandleExternalShakeRequest;
    }

    private void OnDisable()
    {
        if (player != null)
        {
            player.OnCaught -= HandlePlayerCaught;
        }
        OnShakeRequested -= HandleExternalShakeRequest;
        ResetCameraOffset();
    }

    /// <summary>
    /// 外部或动画事件调用的一次性冲击震动（带防除零保护，弱震不会打断正在播放的强震）。
    /// </summary>
    public void Shake(float strength, float time)
    {
        float safeEffect = effect > 0.0001f ? effect : 1f;
        float safeDurMul = duration > 0.0001f ? duration : 1f;
        float finalPos = Mathf.Max(0f, strength) * safeEffect;
        float finalDur = Mathf.Max(0.01f, time * safeDurMul);
        float derivedRoll = Mathf.Clamp(finalPos * 75f, 0f, 2.2f);
        TriggerImpulse(finalPos, finalDur, derivedRoll);
    }

    public void TriggerImpulse(float posAmp, float dur, float rollDeg)
    {
        if (posAmp <= 0.00001f || dur <= 0.001f) return;

        // 计算当前残留脉冲强度，避免弱震把正在播的强震覆盖掉
        float currentRemainingPos = impulseTotalTime > 0.001f && impulseTimer > 0f
            ? impulsePosAmp * (impulseTimer / impulseTotalTime)
            : 0f;

        if (posAmp >= currentRemainingPos * 0.85f)
        {
            impulsePosAmp = posAmp;
            impulseRollAmp = Mathf.Max(0f, rollDeg);
            impulseTotalTime = dur;
            impulseTimer = dur;
        }
    }

    private void HandlePlayerCaught(Transform killer)
    {
        TriggerImpulse(executeShakeStrength, executeShakeDuration, executeShakeRollDeg);
    }

    private void HandleExternalShakeRequest(float strength, float time)
    {
        Shake(strength, time);
    }

    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0.00001f || Time.timeScale <= 0.001f)
        {
            return;
        }

        float masterScale = Mathf.Clamp(GlobalShakeScale, 0f, 2f) * Mathf.Clamp(effect > 0.0001f ? effect : 1f, 0f, 2f);
        if (masterScale <= 0.0001f)
        {
            if (hasAppliedOffset) ResetCameraOffset();
            return;
        }

        Vector3 totalPosOffset = Vector3.zero;
        float totalRollDeg = 0f;

        // 1. 玩家奔跑轻微摇晃（走路/静止平滑归零）
        bool isSprinting = player != null && player.IsRunning;
        float targetSprint = isSprinting ? 1f : 0f;
        sprintWeight = Mathf.Lerp(sprintWeight, targetSprint, 1f - Mathf.Exp(-sprintBlendSpeed * dt));
        if (sprintWeight > 0.002f)
        {
            sprintPhase += dt * sprintStrideFreq;
            float swayX = Mathf.Sin(sprintPhase) * sprintSwayAmpX * sprintWeight;
            float bobY = Mathf.Cos(sprintPhase * 2f) * sprintBobAmpY * sprintWeight;
            float roll = -Mathf.Sin(sprintPhase) * sprintRollDeg * sprintWeight;

            totalPosOffset.x += swayX;
            totalPosOffset.y += bobY;
            totalRollDeg += roll;
        }
        else
        {
            sprintWeight = 0f;
        }

        // 2. 附近猴子压迫震动（巡逻=0；普通奔跑=轻微；狂奔=剧烈；按距离平滑衰减+平滑进出）
        EvaluateNearestMonkeyThreat(out float targetThreatPos, out float targetThreatRoll, out float targetThreatFreq);

        float blendRate = targetThreatPos > curThreatPos ? threatRiseSpeed : threatReleaseSpeed;
        float tFactor = 1f - Mathf.Exp(-blendRate * dt);
        curThreatPos = Mathf.Lerp(curThreatPos, targetThreatPos, tFactor);
        curThreatRoll = Mathf.Lerp(curThreatRoll, targetThreatRoll, tFactor);
        curThreatFreq = Mathf.Lerp(curThreatFreq, targetThreatFreq, 1f - Mathf.Exp(-5f * dt));

        if (curThreatPos > 0.00015f)
        {
            threatNoiseTime += dt * curThreatFreq;
            Vector3 n = SampleSmoothNoise(threatNoiseTime);
            totalPosOffset.x += n.x * curThreatPos;
            totalPosOffset.y += n.y * curThreatPos * 0.85f;
            totalRollDeg += n.z * curThreatRoll;
        }
        else
        {
            curThreatPos = 0f;
            curThreatRoll = 0f;
        }

        // 3. 处决/外部一次性脉冲震动（平滑二次衰减）
        if (impulseTimer > 0f && impulseTotalTime > 0.001f)
        {
            impulseTimer = Mathf.Max(0f, impulseTimer - dt);
            float normalized = impulseTimer / impulseTotalTime;
            float decay = normalized * normalized;

            impulseNoiseTime += dt * impulseNoiseFreq;
            Vector3 n = SampleSmoothNoise(impulseNoiseTime + 37.3f);
            totalPosOffset.x += n.x * impulsePosAmp * decay;
            totalPosOffset.y += n.y * impulsePosAmp * decay * 0.9f;
            totalRollDeg += n.z * impulseRollAmp * decay;
        }

        // 应用最终偏移（位置直接叠加到 originPos，滚转仅写入 Z 轴以保留 FirstView 的俯仰角 pitch）
        totalPosOffset *= masterScale;
        totalRollDeg *= masterScale;

        if (totalPosOffset.sqrMagnitude > 1e-10f || Mathf.Abs(totalRollDeg) > 0.001f)
        {
            if (useCustomBaseRotation)
            {
                // 处决运镜期间按相机自身局部轴叠加震动，避免覆盖手部跟随滚转与俯仰
                transform.localPosition = originPos + originRot * totalPosOffset;
                transform.localRotation = originRot * Quaternion.Euler(0f, 0f, totalRollDeg);
            }
            else
            {
                transform.localPosition = originPos + totalPosOffset;
                ApplyRollPreservingPitch(totalRollDeg);
            }
            hasAppliedOffset = true;
        }
        else if (hasAppliedOffset || useCustomBaseRotation)
        {
            ResetCameraOffset();
        }
    }

    private void EvaluateNearestMonkeyThreat(out float bestPos, out float bestRoll, out float bestFreq)
    {
        bestPos = 0f;
        bestRoll = 0f;
        bestFreq = monkeyRunNoiseFreq;

        if (player != null && !player.IsAlive) return;

        IReadOnlyList<MonkeyAI> monkeys = MonkeyAI.ActiveMonkeys;
        if (monkeys == null || monkeys.Count == 0) return;

        Vector3 refPos = player != null ? player.transform.position : transform.position;

        for (int i = 0; i < monkeys.Count; i++)
        {
            MonkeyAI mk = monkeys[i];
            if (mk == null || !mk.gameObject.activeInHierarchy) continue;

            MonkeyAI.MonkeyMotionTier tier = mk.MotionTier;
            // 巡逻与静止状态完全不影响玩家屏幕
            if (tier != MonkeyAI.MonkeyMotionTier.NormalRun && tier != MonkeyAI.MonkeyMotionTier.SprintRun)
            {
                continue;
            }

            Vector3 delta = mk.transform.position - refPos;
            delta.y = 0f;
            float dist = delta.magnitude;

            float radius = tier == MonkeyAI.MonkeyMotionTier.SprintRun ? monkeySprintRadius : monkeyRunRadius;
            if (dist >= radius || radius <= 0.01f) continue;

            // SmoothStep 距离衰减：进入边缘柔和过渡，越靠近越明显
            float linear = Mathf.Clamp01(1f - dist / radius);
            float weight = linear * linear * (3f - 2f * linear);

            float pos = (tier == MonkeyAI.MonkeyMotionTier.SprintRun ? monkeySprintMaxPos : monkeyRunMaxPos) * weight;
            float roll = (tier == MonkeyAI.MonkeyMotionTier.SprintRun ? monkeySprintMaxRollDeg : monkeyRunMaxRollDeg) * weight;
            float freq = tier == MonkeyAI.MonkeyMotionTier.SprintRun ? monkeySprintNoiseFreq : monkeyRunNoiseFreq;

            if (pos > bestPos)
            {
                bestPos = pos;
                bestRoll = roll;
                bestFreq = freq;
            }
        }
    }

    private Vector3 SampleSmoothNoise(float t)
    {
        // 主低频 + 少量次高频叠加，营造沉重厚实的脚步/压迫晃动感而不刺眼
        float nx = (Mathf.PerlinNoise(noiseSeedX, t) * 2f - 1f) * 0.78f
                 + (Mathf.PerlinNoise(noiseSeedX + 13.7f, t * 2.1f) * 2f - 1f) * 0.22f;
        float ny = (Mathf.PerlinNoise(noiseSeedY, t) * 2f - 1f) * 0.78f
                 + (Mathf.PerlinNoise(noiseSeedY + 19.3f, t * 2.1f) * 2f - 1f) * 0.22f;
        float nr = (Mathf.PerlinNoise(noiseSeedR, t * 0.85f) * 2f - 1f);
        return new Vector3(nx, ny, nr);
    }

    private void ApplyRollPreservingPitch(float rollDeg)
    {
        Vector3 e = transform.localEulerAngles;
        transform.localRotation = Quaternion.Euler(e.x, 0f, rollDeg);
    }

    private void ResetCameraOffset()
    {
        transform.localPosition = originPos;
        if (useCustomBaseRotation)
        {
            transform.localRotation = originRot;
        }
        else
        {
            ApplyRollPreservingPitch(0f);
        }
        hasAppliedOffset = false;
    }
}
