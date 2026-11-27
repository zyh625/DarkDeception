using UnityEngine;

/// <summary>
/// 走廊拐角/暗处地面补光控制器：
/// 提供柔和低位暖色地面光与地板微光光晕，带距离平滑淡出与远距裁剪以兼顾画质与性能。
/// </summary>
public class CornerFloorLightController : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private Light floorPointLight;
    [SerializeField] private Renderer floorGlowRenderer;

    [Header("Lighting")]
    [SerializeField] private float baseIntensity = 0.72f;
    [SerializeField] private float subtleBreathAmplitude = 0.04f;
    [SerializeField] private float subtleBreathSpeed = 1.15f;
    [SerializeField] private Color glowBaseColor = new Color(1f, 0.72f, 0.38f, 0.34f);

    [Header("Distance LOD")]
    [SerializeField] private bool enableDistanceLOD = true;
    [SerializeField] private float lightFadeStart = 8.5f;
    [SerializeField] private float lightCullDistance = 12.0f;
    [SerializeField] private float glowFadeStart = 12.0f;
    [SerializeField] private float glowCullDistance = 16.5f;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private MaterialPropertyBlock mpb;
    private float breathPhase;
    private float lodLightMul = 1f;
    private float lodGlowMul = 1f;
    private bool isFullyCulled;

    private void Awake()
    {
        if (floorPointLight == null)
        {
            floorPointLight = GetComponentInChildren<Light>(true);
        }
        if (floorGlowRenderer == null)
        {
            floorGlowRenderer = GetComponentInChildren<Renderer>(true);
        }
        if (floorPointLight != null)
        {
            floorPointLight.shadows = LightShadows.None;
            if (baseIntensity <= 0f)
            {
                baseIntensity = floorPointLight.intensity;
            }
        }

        // 基于世界坐标生成确定性相位偏移，避免所有地面光同步呼吸
        Vector3 p = transform.position;
        breathPhase = Mathf.Abs(p.x * 1.37f + p.z * 2.13f) % 6.28318f;
        mpb = new MaterialPropertyBlock();
    }

    private void OnEnable()
    {
        UpdateDistanceAndVisuals(forceApply: true);
    }

    private void Update()
    {
        UpdateDistanceAndVisuals(forceApply: false);
    }

    private void UpdateDistanceAndVisuals(bool forceApply)
    {
        float dist = 0f;
        if (enableDistanceLOD)
        {
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
                viewerPos = transform.position;
            }

            Vector3 d = transform.position - viewerPos;
            d.y = 0f;
            dist = d.magnitude;

            if (dist >= glowCullDistance)
            {
                if (!isFullyCulled || forceApply)
                {
                    isFullyCulled = true;
                    lodLightMul = 0f;
                    lodGlowMul = 0f;
                    if (floorPointLight != null && floorPointLight.enabled)
                    {
                        floorPointLight.enabled = false;
                    }
                    if (floorGlowRenderer != null && floorGlowRenderer.enabled)
                    {
                        floorGlowRenderer.enabled = false;
                    }
                }
                return;
            }

            isFullyCulled = false;
            lodLightMul = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(lightFadeStart, lightCullDistance, dist));
            lodGlowMul = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(glowFadeStart, glowCullDistance, dist));
        }
        else
        {
            isFullyCulled = false;
            lodLightMul = 1f;
            lodGlowMul = 1f;
        }

        float breath = 1f + Mathf.Sin(Time.time * subtleBreathSpeed + breathPhase) * subtleBreathAmplitude;

        if (floorPointLight != null)
        {
            bool lightOn = lodLightMul > 0.01f;
            if (floorPointLight.enabled != lightOn)
            {
                floorPointLight.enabled = lightOn;
            }
            if (lightOn)
            {
                floorPointLight.intensity = baseIntensity * lodLightMul * breath;
            }
        }

        if (floorGlowRenderer != null)
        {
            bool glowOn = lodGlowMul > 0.01f;
            if (floorGlowRenderer.enabled != glowOn)
            {
                floorGlowRenderer.enabled = glowOn;
            }
            if (glowOn)
            {
                Color c = glowBaseColor;
                c.a = glowBaseColor.a * lodGlowMul * (0.95f + 0.05f * breath);
                floorGlowRenderer.GetPropertyBlock(mpb);
                mpb.SetColor(BaseColorId, c);
                mpb.SetColor(ColorId, c);
                floorGlowRenderer.SetPropertyBlock(mpb);
            }
        }
    }
}
