using UnityEngine;

/// <summary>
/// 特殊红/黄水晶的悬浮上下律动、缓慢自转与配套光晕脉动脚本。
/// </summary>
public class CrystalFloatSpin : MonoBehaviour
{
    [SerializeField] private float floatAmplitude = 0.045f;
    [SerializeField] private float floatFrequency = 2.1f;
    [SerializeField] private float spinDegreesPerSec = 52f;
    [SerializeField] private Light accentLight;
    [SerializeField] private float lightPulseAmplitude = 0.18f;

    private Vector3 baseLocalPos;
    private float baseLightIntensity;
    private float phaseOffset;

    private void Awake()
    {
        baseLocalPos = transform.localPosition;
        phaseOffset = Random.Range(0f, Mathf.PI * 2f);
        if (accentLight == null)
        {
            accentLight = GetComponentInChildren<Light>(true);
        }
        if (accentLight != null)
        {
            baseLightIntensity = accentLight.intensity;
        }
    }

    private void Update()
    {
        float t = Time.time * floatFrequency + phaseOffset;
        transform.localPosition = baseLocalPos + Vector3.up * (Mathf.Sin(t) * floatAmplitude);
        transform.Rotate(Vector3.up, spinDegreesPerSec * Time.deltaTime, Space.World);

        if (accentLight != null)
        {
            accentLight.intensity = baseLightIntensity * (1f + Mathf.Sin(t * 1.4f) * lightPulseAmplitude);
        }
    }
}
