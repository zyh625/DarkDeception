using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 走廊老旧黄铜吊灯控制器（1D Blend Tree 平滑过渡 + 狂奔猴子触发低概率闪烁 + 灯光随摆动微移）：
/// - 玩家静止/走路：轻微摇晃（Intensity = 0.0，Light）
/// - 玩家奔跑（范围内）：中等摇晃（Intensity = 0.5，Medium）
/// - 猴子巡逻（范围内）：中等摇晃（Intensity = 0.5，Medium）
/// - 猴子普通奔跑（范围内）：剧烈摇晃（Intensity = 1.0，Heavy）
/// - 猴子狂奔（范围内）：剧烈摇晃（Intensity = 1.0，Heavy）+ 小概率随机闪烁
/// 所有档位升降均以平滑速率连续经过 Medium（0.5），上升较快、回落缓慢带余震感。
/// </summary>
public class ChandelierController : MonoBehaviour
{
    private static readonly int IntensityHash = Animator.StringToHash("Intensity");
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    [Header("动画组件与过渡速度（必经 Medium 平缓过渡）")]
    [SerializeField] private Animator chandelierAnimator;
    [SerializeField] private RuntimeAnimatorController fallbackController;
    [SerializeField] private float riseSpeed = 0.85f;   // 轻->重 约 1.18 秒，清晰经过 Medium
    [SerializeField] private float decaySpeed = 0.30f;  // 重->轻 约 3.3 秒，缓慢余震收敛经过 Medium
    [SerializeField] private bool randomizePhaseOnStart = true;

    [Header("触发影响半径（适配当前 1 格 = 1m 走廊）")]
    [SerializeField] private float playerRunRadius = 4.2f;
    [SerializeField] private float monkeyPatrolRadius = 5.0f;
    [SerializeField] private float monkeyRunRadius = 5.8f;
    [SerializeField] private float monkeySprintRadius = 6.8f;

    [Header("吊灯光源与灯罩随摆动联动")]
    [SerializeField] private Transform lightRig;
    [SerializeField] private Transform lampBodyBone;
    [SerializeField, Range(0f, 1.2f)] private float lightFollowFactor = 0.85f;
    [SerializeField] private Light mainLight;
    [SerializeField] private Light coreGlowLight;
    [SerializeField] private Renderer amberGlassRenderer;

    [Header("常态微暖呼吸与狂奔小概率闪烁")]
    [SerializeField] private float baseMainIntensity = 1.65f;
    [SerializeField] private float baseCoreIntensity = 0.45f;
    [SerializeField, Range(0f, 0.1f)] private float subtleWarmthBreath = 0.025f;
    [SerializeField] private float flickerCheckMinInterval = 0.38f;
    [SerializeField] private float flickerCheckMaxInterval = 0.85f;
    [SerializeField, Range(0f, 1f)] private float sprintFlickerChance = 0.24f;
    [SerializeField] private float flickerMinDuration = 0.10f;
    [SerializeField] private float flickerMaxDuration = 0.24f;
    [SerializeField, Range(0.05f, 0.6f)] private float flickerMinBrightness = 0.15f;

    [Header("大规模迷宫灯光与阴影平滑距离 LOD（防止 URP 阴影图集溢出）")]
    [SerializeField] private bool enableDistanceLOD = true;
    [SerializeField] private float shadowFadeStart = 7.0f;
    [SerializeField] private float shadowFadeEnd = 9.2f;
    [SerializeField] private float coreGlowFadeStart = 6.2f;
    [SerializeField] private float coreGlowFadeEnd = 8.5f;
    [SerializeField] private float lightFadeStart = 13.5f;
    [SerializeField] private float lightCullDistance = 16.5f;
    [SerializeField] private float baseShadowStrength = 0.78f;

    private float currentIntensity;
    private float targetIntensity;
    private bool sprintMonkeyNearby;
    private float lodMainMul = 1f;
    private float lodCoreMul = 1f;
    private bool isCulledByDistance;

    private float breathSeed;
    private float nextFlickerCheckTimer;
    private float activeFlickerTimer;
    private float activeFlickerDuration;
    private float activeFlickerFloor;
    private bool activeDoubleDip;

    private Vector3 initialBoneLocalToRoot;
    private Vector3 initialLightRigLocalPos;
    private bool canFollowBone;

    private MaterialPropertyBlock glassBlock;
    private Color baseGlassColor = new Color(1f, 0.72f, 0.32f, 1f);
    private Color baseGlassEmission = new Color(0.95f, 0.52f, 0.14f, 1f);
    private bool hasGlassEmission;
    private bool hasGlassBaseColor;

    public float CurrentIntensity => currentIntensity;

    private void Awake()
    {
        AutoBindReferences();

        if (mainLight != null && baseMainIntensity <= 0.001f)
        {
            baseMainIntensity = mainLight.intensity;
        }
        if (coreGlowLight != null && baseCoreIntensity <= 0.001f)
        {
            baseCoreIntensity = coreGlowLight.intensity;
        }

        breathSeed = Random.Range(0f, 100f);
        nextFlickerCheckTimer = Random.Range(flickerCheckMinInterval, flickerCheckMaxInterval);

        if (lampBodyBone != null && lightRig != null)
        {
            initialBoneLocalToRoot = transform.InverseTransformPoint(lampBodyBone.position);
            initialLightRigLocalPos = lightRig.localPosition;
            canFollowBone = true;
        }

        InitGlassMaterialCache();
    }

    private void Start()
    {
        if (chandelierAnimator != null)
        {
            if (chandelierAnimator.runtimeAnimatorController == null && fallbackController != null)
            {
                chandelierAnimator.runtimeAnimatorController = fallbackController;
            }
            chandelierAnimator.SetFloat(IntensityHash, 0f);

            if (randomizePhaseOnStart && chandelierAnimator.runtimeAnimatorController != null)
            {
                chandelierAnimator.speed = Random.Range(0.94f, 1.06f);
                chandelierAnimator.Play(0, 0, Random.value);
            }
        }
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0.00001f) return;

        UpdateDistanceLOD();
        if (isCulledByDistance)
        {
            return;
        }

        EvaluateNearbyInfluences();

        // 无论目标从 Light(0) 直跳 Heavy(1) 还是 Heavy(1) 降到 Light(0)，都平滑连续经过 Medium(0.5)
        float speed = targetIntensity > currentIntensity ? riseSpeed : decaySpeed;
        currentIntensity = Mathf.MoveTowards(currentIntensity, targetIntensity, speed * dt);

        if (chandelierAnimator != null && chandelierAnimator.runtimeAnimatorController != null)
        {
            chandelierAnimator.SetFloat(IntensityHash, currentIntensity);
        }

        UpdateLightingAndFlicker(dt);
    }

    private void LateUpdate()
    {
        if (isCulledByDistance) return;

        // 让吊灯主光源轻轻跟随 LampBody 骨骼摆动，墙壁和地板光影会自然晃动
        if (canFollowBone && lampBodyBone != null && lightRig != null)
        {
            Vector3 curBoneLocal = transform.InverseTransformPoint(lampBodyBone.position);
            Vector3 delta = curBoneLocal - initialBoneLocalToRoot;
            lightRig.localPosition = initialLightRigLocalPos + delta * lightFollowFactor;
        }
    }

    private void UpdateDistanceLOD()
    {
        if (!enableDistanceLOD)
        {
            lodMainMul = 1f;
            lodCoreMul = 1f;
            isCulledByDistance = false;
            return;
        }

        Vector3 viewerPos;
        PlayerInteract player = PlayerInteract.ActivePlayer;
        if (player != null)
        {
            viewerPos = player.transform.position;
        }
        else if (Camera.main != null)
        {
            viewerPos = Camera.main.transform.position;
        }
        else
        {
            lodMainMul = 1f;
            lodCoreMul = 1f;
            isCulledByDistance = false;
            return;
        }

        Vector3 d = transform.position - viewerPos;
        d.y = 0f;
        float dist = d.magnitude;

        if (dist >= lightCullDistance)
        {
            isCulledByDistance = true;
            lodMainMul = 0f;
            lodCoreMul = 0f;
            if (mainLight != null && mainLight.enabled)
            {
                mainLight.enabled = false;
                mainLight.shadows = LightShadows.None;
            }
            if (coreGlowLight != null && coreGlowLight.enabled)
            {
                coreGlowLight.enabled = false;
            }
            if (chandelierAnimator != null && chandelierAnimator.enabled)
            {
                chandelierAnimator.enabled = false;
            }
            return;
        }

        isCulledByDistance = false;
        if (chandelierAnimator != null && !chandelierAnimator.enabled)
        {
            chandelierAnimator.enabled = true;
        }

        // 主光柔和淡出（13.5m ~ 16.5m）
        lodMainMul = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(lightFadeStart, lightCullDistance, dist));
        if (mainLight != null)
        {
            if (!mainLight.enabled) mainLight.enabled = true;

            // 阴影柔和淡出（7.0m ~ 9.2m），保证同时投射阴影的吊灯数稳定落在 URP 阴影图集预算内
            if (dist < shadowFadeEnd)
            {
                if (mainLight.shadows != LightShadows.Soft) mainLight.shadows = LightShadows.Soft;
                float shMul = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(shadowFadeStart, shadowFadeEnd, dist));
                mainLight.shadowStrength = baseShadowStrength * shMul;
            }
            else if (mainLight.shadows != LightShadows.None)
            {
                mainLight.shadows = LightShadows.None;
            }
        }

        // 内核补光仅近距离开启（6.2m ~ 8.5m 柔和淡出）
        lodCoreMul = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(coreGlowFadeStart, coreGlowFadeEnd, dist));
        if (coreGlowLight != null)
        {
            bool coreOn = lodCoreMul > 0.01f;
            if (coreGlowLight.enabled != coreOn) coreGlowLight.enabled = coreOn;
        }
    }

    private void EvaluateNearbyInfluences()
    {
        // 默认常态：轻微摇晃 Light (0.0)
        float desired = 0f;
        bool sprintingThreat = false;
        Vector3 selfPos = transform.position;

        // 1. 检测玩家：跑步 -> 中等摇晃 (0.5)；走路/停止 -> 保持轻微摇晃 (0.0)
        PlayerInteract player = PlayerInteract.ActivePlayer;
        if (player != null && player.IsRunning)
        {
            Vector3 dp = player.transform.position - selfPos;
            dp.y = 0f;
            if (dp.sqrMagnitude <= playerRunRadius * playerRunRadius)
            {
                desired = Mathf.Max(desired, 0.5f);
            }
        }

        // 2. 检测附近活跃猴子：巡逻 -> 中等(0.5)；普通奔跑 -> 剧烈(1.0)；狂奔 -> 剧烈(1.0) + 小概率闪烁
        IReadOnlyList<MonkeyAI> monkeys = MonkeyAI.ActiveMonkeys;
        if (monkeys != null)
        {
            for (int i = 0; i < monkeys.Count; i++)
            {
                MonkeyAI mk = monkeys[i];
                if (mk == null || !mk.gameObject.activeInHierarchy) continue;

                MonkeyAI.MonkeyMotionTier tier = mk.MotionTier;
                if (tier == MonkeyAI.MonkeyMotionTier.Idle) continue;

                Vector3 dm = mk.transform.position - selfPos;
                dm.y = 0f;
                float sqrDist = dm.sqrMagnitude;

                switch (tier)
                {
                    case MonkeyAI.MonkeyMotionTier.Patrol:
                        if (sqrDist <= monkeyPatrolRadius * monkeyPatrolRadius)
                        {
                            desired = Mathf.Max(desired, 0.5f);
                        }
                        break;

                    case MonkeyAI.MonkeyMotionTier.NormalRun:
                        if (sqrDist <= monkeyRunRadius * monkeyRunRadius)
                        {
                            desired = Mathf.Max(desired, 1.0f);
                        }
                        break;

                    case MonkeyAI.MonkeyMotionTier.SprintRun:
                        if (sqrDist <= monkeySprintRadius * monkeySprintRadius)
                        {
                            desired = 1.0f;
                            sprintingThreat = true;
                        }
                        break;
                }
            }
        }

        targetIntensity = desired;
        sprintMonkeyNearby = sprintingThreat;
    }

    private void UpdateLightingAndFlicker(float dt)
    {
        // 极柔和的老式钨丝灯呼吸，平时稳定不刺眼
        float breath = 1f + (Mathf.PerlinNoise(breathSeed, Time.time * 1.35f) * 2f - 1f) * subtleWarmthBreath;
        float flickerFactor = 1f;

        if (activeFlickerTimer > 0f)
        {
            activeFlickerTimer = Mathf.Max(0f, activeFlickerTimer - dt);
            float p = 1f - (activeFlickerTimer / Mathf.Max(0.001f, activeFlickerDuration));

            // 单次或双次电压骤降曲线（瞬间变暗再弹亮，模拟老酒店吊灯接触不良）
            float dip = activeDoubleDip
                ? Mathf.Sin(p * Mathf.PI * 2f)
                : Mathf.Sin(p * Mathf.PI);
            dip = Mathf.Abs(dip);
            flickerFactor = Mathf.Lerp(1f, activeFlickerFloor, dip);
        }
        else if (sprintMonkeyNearby)
        {
            nextFlickerCheckTimer -= dt;
            if (nextFlickerCheckTimer <= 0f)
            {
                nextFlickerCheckTimer = Random.Range(flickerCheckMinInterval, flickerCheckMaxInterval);
                if (Random.value < sprintFlickerChance)
                {
                    activeFlickerDuration = Random.Range(flickerMinDuration, flickerMaxDuration);
                    activeFlickerTimer = activeFlickerDuration;
                    activeFlickerFloor = Random.Range(flickerMinBrightness, Mathf.Min(0.45f, flickerMinBrightness + 0.22f));
                    activeDoubleDip = Random.value < 0.45f;
                }
            }
        }
        else
        {
            // 没有狂奔猴子时重置检查倒计时，避免刚靠近就立刻必闪
            nextFlickerCheckTimer = Mathf.Max(nextFlickerCheckTimer, 0.25f);
        }

        float brightness = breath * flickerFactor;
        if (mainLight != null)
        {
            mainLight.intensity = baseMainIntensity * brightness * lodMainMul;
        }
        if (coreGlowLight != null && coreGlowLight.enabled)
        {
            coreGlowLight.intensity = baseCoreIntensity * brightness * lodCoreMul;
        }

        if (amberGlassRenderer != null && (hasGlassEmission || hasGlassBaseColor))
        {
            glassBlock ??= new MaterialPropertyBlock();
            amberGlassRenderer.GetPropertyBlock(glassBlock);
            if (hasGlassEmission)
            {
                glassBlock.SetColor(EmissionColorId, baseGlassEmission * brightness);
            }
            if (hasGlassBaseColor)
            {
                float tintMul = Mathf.Lerp(0.45f, 1f, flickerFactor);
                glassBlock.SetColor(BaseColorId, new Color(
                    baseGlassColor.r * tintMul,
                    baseGlassColor.g * tintMul,
                    baseGlassColor.b * tintMul,
                    baseGlassColor.a));
            }
            amberGlassRenderer.SetPropertyBlock(glassBlock);
        }
    }

    private void AutoBindReferences()
    {
        if (chandelierAnimator == null)
        {
            chandelierAnimator = GetComponentInChildren<Animator>(true);
        }

        if (lightRig == null)
        {
            Transform lr = transform.Find("LightRig");
            if (lr != null) lightRig = lr;
        }

        if (mainLight == null || coreGlowLight == null)
        {
            Light[] lights = GetComponentsInChildren<Light>(true);
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i].name.Contains("Main") && mainLight == null)
                {
                    mainLight = lights[i];
                }
                else if (lights[i].name.Contains("Core") && coreGlowLight == null)
                {
                    coreGlowLight = lights[i];
                }
            }
            if (mainLight == null && lights.Length > 0) mainLight = lights[0];
        }

        if (lampBodyBone == null)
        {
            Transform[] all = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name == "LampBody")
                {
                    lampBodyBone = all[i];
                    break;
                }
            }
        }

        if (amberGlassRenderer == null)
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].name.Contains("amber"))
                {
                    amberGlassRenderer = renderers[i];
                    break;
                }
            }
        }
    }

    private void InitGlassMaterialCache()
    {
        if (amberGlassRenderer == null) return;
        Material mat = amberGlassRenderer.sharedMaterial;
        if (mat == null) return;

        if (mat.HasProperty(EmissionColorId))
        {
            baseGlassEmission = mat.GetColor(EmissionColorId);
            hasGlassEmission = baseGlassEmission.maxColorComponent > 0.01f;
        }
        if (mat.HasProperty(BaseColorId))
        {
            baseGlassColor = mat.GetColor(BaseColorId);
            hasGlassBaseColor = true;
        }
    }
}
