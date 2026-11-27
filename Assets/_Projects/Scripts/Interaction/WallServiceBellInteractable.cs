using System;
using UnityEngine;

/// <summary>
/// 墙壁服务铃交互控制器（挂载于 Hotel_WallServiceBell 预制体，自动覆盖场景中全部 10 个墙壁铃铛）：
/// - 当玩家视角中心对准铃铛时，由 SecretRoomManager 在屏幕中心显示 UI_ServiceBell_InteractHand.png；
/// - 点击 / 按下交互键后，铃铛响铃（默认 3 秒），期间所有活跃猴子的目标立即更新并持续指向此铃铛，
///   玩家可趁机逃离；但一旦某只猴子亲眼看到玩家，该猴子的目标会立刻改回玩家；
/// - 响铃期间伴随铃铛高频抖动、柔和铜金微光与内置合成复古服务铃清脆音效（亦可在 Inspector 指定自定义 AudioClip）。
/// </summary>
public class WallServiceBellInteractable : MonoBehaviour
{
    private static AudioClip synthesizedBellClip;

    public static WallServiceBellInteractable ActiveRingingBell { get; private set; }
    public static int ActiveRingSequenceId { get; private set; }

    public static bool HasActiveRingingBell => ActiveRingingBell != null && ActiveRingingBell.IsRinging;
    public static Vector3 ActiveBellTargetPos => ActiveRingingBell != null ? ActiveRingingBell.CorridorTargetPos : Vector3.zero;

    [Header("响铃与吸引参数")]
    [SerializeField] private float ringDuration = 3.0f;
    [SerializeField] private float corridorTargetOffset = 0.48f;

    [Header("响铃视觉与音效反馈")]
    [SerializeField] private float ringShakeAngleDeg = 12f;
    [SerializeField] private float ringShakeSpeed = 54f;
    [SerializeField] private AudioClip customRingClip;
    [SerializeField, Range(0f, 1f)] private float bellSfxVolume = 0.85f;
    [SerializeField] private bool showRingGlowLight = true;

    private Transform visualTransform;
    private Quaternion visualBaseLocalRot = Quaternion.identity;
    private AudioSource audioSource;
    private Light ringGlowLight;
    private float ringRemainingTime;
    private float ringElapsed;

    public bool IsRinging => ringRemainingTime > 0f;
    public bool CanInteract => !IsRinging;
    public float RingDuration => ringDuration;

    /// <summary>
    /// 铃铛正前方走廊地面坐标（供 NavMeshAgent 与 MonkeyRoutePlanner 寻路到铃铛位置）。
    /// </summary>
    public Vector3 CorridorTargetPos
    {
        get
        {
            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
            else fwd.Normalize();

            Vector3 pos = transform.position + fwd * corridorTargetOffset;
            pos.y = 0f;
            return pos;
        }
    }

    public static void StopAllRingingBells()
    {
        if (ActiveRingingBell != null)
        {
            ActiveRingingBell.StopRingingImmediate();
            ActiveRingingBell = null;
        }
    }

    private void Awake()
    {
        visualTransform = transform.Find("Visual");
        if (visualTransform != null)
        {
            visualBaseLocalRot = visualTransform.localRotation;
        }

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }
        audioSource.playOnAwake = false;
        audioSource.loop = true;
        audioSource.spatialBlend = 0.72f;
        audioSource.dopplerLevel = 0f;
        audioSource.minDistance = 500f;
        audioSource.maxDistance = 1000f;
        audioSource.rolloffMode = AudioRolloffMode.Linear;

        if (showRingGlowLight)
        {
            GameObject lightGo = new GameObject("BellRingGlow");
            lightGo.transform.SetParent(transform, false);
            lightGo.transform.localPosition = new Vector3(0f, 0f, 0.14f);
            ringGlowLight = lightGo.AddComponent<Light>();
            ringGlowLight.type = LightType.Point;
            ringGlowLight.color = new Color(1.00f, 0.82f, 0.36f, 1f);
            ringGlowLight.intensity = 0f;
            ringGlowLight.range = 2.6f;
            ringGlowLight.shadows = LightShadows.None;
            ringGlowLight.enabled = false;
        }
    }

    private void OnDisable()
    {
        if (ActiveRingingBell == this)
        {
            ActiveRingingBell = null;
        }
        StopRingingImmediate();
    }

    /// <summary>
    /// 点击敲响铃铛：开始持续响铃（默认 3 秒），并立刻把全部活跃猴子的目标更新到此铃铛位置。
    /// </summary>
    public void Ring()
    {
        if (IsRinging) return;

        // 若此前另有铃铛正在响，切换最新响铃为当前全局吸引源
        if (ActiveRingingBell != null && ActiveRingingBell != this)
        {
            ActiveRingingBell.StopRingingImmediate();
        }

        ActiveRingSequenceId++;
        ActiveRingingBell = this;
        ringRemainingTime = Mathf.Max(0.5f, ringDuration);
        ringElapsed = 0f;

        if (ringGlowLight != null)
        {
            ringGlowLight.enabled = true;
            ringGlowLight.intensity = 1.35f;
        }

        PlayRingAudio();
        HapticFeedback.RequestShake(0.0045f, 0.14f);

        // 立刻将全部活跃猴子的目标更新为本铃铛前方走廊位置
        MonkeyAI.NotifyAllMonkeysBellRung(CorridorTargetPos, ActiveRingSequenceId);
    }

    private void Update()
    {
        if (ringRemainingTime <= 0f) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        ringRemainingTime -= dt;
        ringElapsed += dt;

        float normRemain = Mathf.Clamp01(ringRemainingTime / Mathf.Max(0.1f, ringDuration));

        // 铃铛响铃期间做快速微幅高频振动，直观体现正在持续响铃
        if (visualTransform != null)
        {
            float amp = ringShakeAngleDeg * Mathf.Lerp(0.35f, 1.0f, normRemain);
            float zRoll = Mathf.Sin(ringElapsed * ringShakeSpeed) * amp;
            float xPitch = Mathf.Cos(ringElapsed * ringShakeSpeed * 0.77f) * (amp * 0.35f);
            visualTransform.localRotation = visualBaseLocalRot * Quaternion.Euler(xPitch, 0f, zRoll);
        }

        if (ringGlowLight != null)
        {
            float pulse = 0.72f + 0.28f * Mathf.Sin(ringElapsed * 18f);
            ringGlowLight.intensity = 1.35f * normRemain * pulse;
        }

        // 响铃期间按与玩家的直线距离 + 实时音效音量动态更新音量（玩家按快捷键调音效时立刻生效）
        if (audioSource != null && audioSource.isPlaying)
        {
            float distAtten = AudioManager.ComputeStraightLineDistanceAttenuation(transform.position, 1.5f, 22.0f);
            float tailFade = ringRemainingTime < 0.25f ? Mathf.Clamp01(ringRemainingTime / 0.25f) : 1f;
            audioSource.volume = Mathf.Clamp01(bellSfxVolume * distAtten * tailFade * GameSettings.SfxVolume);
        }

        if (ringRemainingTime <= 0f)
        {
            StopRingingImmediate();
            if (ActiveRingingBell == this)
            {
                ActiveRingingBell = null;
            }
        }
    }

    private void StopRingingImmediate()
    {
        ringRemainingTime = 0f;
        ringElapsed = 0f;

        if (visualTransform != null)
        {
            visualTransform.localRotation = visualBaseLocalRot;
        }
        if (ringGlowLight != null)
        {
            ringGlowLight.intensity = 0f;
            ringGlowLight.enabled = false;
        }
        if (audioSource != null && audioSource.isPlaying)
        {
            audioSource.Stop();
        }
    }

    private void PlayRingAudio()
    {
        if (audioSource == null) return;
        AudioClip clip = customRingClip != null
            ? customRingClip
            : (AudioManager.Instance != null && AudioManager.Instance.BellRingLoopClip != null
                ? AudioManager.Instance.BellRingLoopClip
                : GetOrCreateSynthesizedBellClip());
        if (clip == null) return;

        audioSource.Stop();
        audioSource.clip = clip;
        audioSource.loop = true;
        float distAtten = AudioManager.ComputeStraightLineDistanceAttenuation(transform.position, 1.5f, 22.0f);
        audioSource.volume = Mathf.Clamp01(bellSfxVolume * distAtten * GameSettings.SfxVolume);
        audioSource.pitch = UnityEngine.Random.Range(0.97f, 1.03f);
        audioSource.Play();
    }

    /// <summary>
    /// 实时合成一段 3 秒复古酒店黄铜服务铃声（含多次清脆叮铃撞击与金属泛音余韵），无需外部音频依赖。
    /// </summary>
    private static AudioClip GetOrCreateSynthesizedBellClip()
    {
        if (synthesizedBellClip != null) return synthesizedBellClip;

        const int sampleRate = 22050;
        const float duration = 3.0f;
        int totalSamples = Mathf.CeilToInt(sampleRate * duration);
        float[] samples = new float[totalSamples];

        // 模拟老式酒店墙面机械电铃/按铃：连续清脆高频击打 + 黄铜钟体泛音衰减
        float[] strikeTimes = { 0.00f, 0.14f, 0.28f, 0.42f, 0.56f, 0.70f, 0.84f, 0.98f, 1.15f, 1.35f, 1.58f, 1.85f };
        for (int i = 0; i < totalSamples; i++)
        {
            float t = (float)i / sampleRate;
            float acc = 0f;

            for (int s = 0; s < strikeTimes.Length; s++)
            {
                float dt = t - strikeTimes[s];
                if (dt < 0f || dt > 1.6f) continue;

                float hitGain = s < 8 ? 0.55f : 0.42f;
                float env = Mathf.Exp(-dt * 4.2f);
                float strikeClick = Mathf.Exp(-dt * 65f) * 0.45f;

                // 黄铜钟体非谐波泛音列（1380Hz 基频 + 2765Hz + 4120Hz + 5580Hz）
                float tone =
                    Mathf.Sin(2f * Mathf.PI * 1380f * dt) * 0.50f +
                    Mathf.Sin(2f * Mathf.PI * 2765f * dt) * 0.30f +
                    Mathf.Sin(2f * Mathf.PI * 4120f * dt) * 0.18f +
                    Mathf.Sin(2f * Mathf.PI * 5580f * dt) * 0.10f;

                acc += (tone * env + strikeClick * Mathf.Sin(2f * Mathf.PI * 3200f * dt)) * hitGain;
            }

            // 尾段柔和淡出避免截断爆音
            float tailFade = t > 2.65f ? Mathf.Clamp01((duration - t) / 0.35f) : 1f;
            samples[i] = Mathf.Clamp(acc * 0.42f * tailFade, -0.95f, 0.95f);
        }

        synthesizedBellClip = AudioClip.Create("SynthesizedHotelServiceBell", totalSamples, 1, sampleRate, false);
        synthesizedBellClip.SetData(samples, 0);
        return synthesizedBellClip;
    }
}
