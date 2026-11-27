using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 主菜单紫晶裂隙氛围粒子（纯 UI 层，使用 crystalParticleAtlas_2x2 切出的 4 张子图）：
/// - 0/1：缓慢上升并轻微旋摆的暗紫碎晶；
/// - 2：柔和脉动的紫光光斑；
/// - 3：闪烁渐显的四角星芒。
/// 全部关闭 raycastTarget 且使用 unscaledDeltaTime，不影响任何按钮交互。
/// </summary>
public class MainMenuCrystalParticles : MonoBehaviour
{
    [Header("2x2 水晶粒子子图（0/1=碎晶, 2=光斑, 3=星芒）")]
    [SerializeField] private Sprite[] particleSprites = new Sprite[4];

    [Header("数量与分布")]
    [SerializeField] private int particleCount = 24;
    [SerializeField] private Vector2 spawnAreaX = new Vector2(-180f, 940f);
    [SerializeField] private Vector2 spawnAreaY = new Vector2(-560f, 560f);

    private struct ParticleItem
    {
        public RectTransform rect;
        public Image image;
        public int spriteKind;
        public float baseX;
        public float posY;
        public float speedY;
        public float swayAmp;
        public float swayFreq;
        public float phase;
        public float rotSpeed;
        public float baseScale;
        public float baseAlpha;
        public float pulseSpeed;
    }

    private ParticleItem[] items;
    private bool initialized;

    private void Start()
    {
        InitPool();
    }

    private void InitPool()
    {
        if (initialized) return;
        initialized = true;

        if (particleSprites == null || particleSprites.Length == 0) return;

        bool hasAny = false;
        for (int i = 0; i < particleSprites.Length; i++)
        {
            if (particleSprites[i] != null) { hasAny = true; break; }
        }
        if (!hasAny) return;

        int count = Mathf.Clamp(particleCount, 6, 48);
        items = new ParticleItem[count];

        for (int i = 0; i < count; i++)
        {
            GameObject go = new GameObject($"CrystalMote_{i:00}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(transform, false);

            Image img = go.GetComponent<Image>();
            img.raycastTarget = false;
            img.preserveAspect = true;

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);

            items[i] = new ParticleItem { rect = rt, image = img };
            RespawnParticle(ref items[i], true);
        }
    }

    private void Update()
    {
        if (items == null) return;
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        float now = Time.unscaledTime;

        for (int i = 0; i < items.Length; i++)
        {
            ref ParticleItem p = ref items[i];
            if (p.rect == null) continue;

            p.posY += p.speedY * dt;
            if (p.posY > spawnAreaY.y + 60f)
            {
                RespawnParticle(ref p, false);
                continue;
            }

            float x = p.baseX + Mathf.Sin(now * p.swayFreq + p.phase) * p.swayAmp;
            p.rect.anchoredPosition = new Vector2(x, p.posY);

            if (Mathf.Abs(p.rotSpeed) > 0.01f)
            {
                p.rect.Rotate(0f, 0f, p.rotSpeed * dt);
            }

            // 上下边缘柔和淡入淡出 + 呼吸闪烁
            float normY = Mathf.InverseLerp(spawnAreaY.x, spawnAreaY.y, p.posY);
            float edgeFade = Mathf.Clamp01(normY * 5f) * Mathf.Clamp01((1f - normY) * 4f);
            float pulse = 0.65f + 0.35f * Mathf.Sin(now * p.pulseSpeed + p.phase);

            if (p.spriteKind == 3)
            {
                float s = p.baseScale * (0.78f + 0.34f * pulse);
                p.rect.localScale = new Vector3(s, s, 1f);
            }

            Color c = p.image.color;
            c.a = p.baseAlpha * edgeFade * pulse;
            p.image.color = c;
        }
    }

    private void RespawnParticle(ref ParticleItem p, bool randomY)
    {
        // 偏向右侧裂隙生成，同时允许少量向左漂散
        float bias = Mathf.Pow(Random.value, 0.62f);
        p.baseX = Mathf.Lerp(spawnAreaX.x, spawnAreaX.y, bias);
        p.posY = randomY ? Random.Range(spawnAreaY.x, spawnAreaY.y) : (spawnAreaY.x - Random.Range(20f, 90f));
        p.phase = Random.Range(0f, Mathf.PI * 2f);

        int kind = PickSpriteKind();
        p.spriteKind = kind;
        Sprite sp = particleSprites[Mathf.Clamp(kind, 0, particleSprites.Length - 1)];
        if (sp == null)
        {
            for (int k = 0; k < particleSprites.Length; k++)
            {
                if (particleSprites[k] != null) { sp = particleSprites[k]; break; }
            }
        }
        p.image.sprite = sp;

        if (kind <= 1)
        {
            // 碎晶：中等尺寸、缓慢旋摆上浮
            float sz = Random.Range(20f, 40f);
            p.rect.sizeDelta = new Vector2(sz, sz);
            p.rect.localRotation = Quaternion.Euler(0f, 0f, Random.Range(-35f, 35f));
            p.rect.localScale = Vector3.one;
            p.baseScale = 1f;
            p.speedY = Random.Range(14f, 32f);
            p.swayAmp = Random.Range(12f, 34f);
            p.swayFreq = Random.Range(0.45f, 0.95f);
            p.rotSpeed = Random.Range(-18f, 18f);
            p.baseAlpha = Random.Range(0.38f, 0.72f);
            p.pulseSpeed = Random.Range(0.9f, 1.8f);
            p.image.color = new Color(0.92f, 0.78f, 1f, p.baseAlpha);
        }
        else if (kind == 2)
        {
            // 紫光光斑：更大、更柔和
            float sz = Random.Range(34f, 74f);
            p.rect.sizeDelta = new Vector2(sz, sz);
            p.rect.localRotation = Quaternion.identity;
            p.rect.localScale = Vector3.one;
            p.baseScale = 1f;
            p.speedY = Random.Range(8f, 20f);
            p.swayAmp = Random.Range(18f, 44f);
            p.swayFreq = Random.Range(0.3f, 0.65f);
            p.rotSpeed = 0f;
            p.baseAlpha = Random.Range(0.18f, 0.42f);
            p.pulseSpeed = Random.Range(0.7f, 1.4f);
            p.image.color = new Color(0.84f, 0.56f, 1f, p.baseAlpha);
        }
        else
        {
            // 四角星芒：细锐闪烁
            float sz = Random.Range(18f, 36f);
            p.rect.sizeDelta = new Vector2(sz, sz);
            p.rect.localRotation = Quaternion.Euler(0f, 0f, Random.Range(-12f, 12f));
            p.baseScale = 1f;
            p.rect.localScale = Vector3.one;
            p.speedY = Random.Range(16f, 36f);
            p.swayAmp = Random.Range(8f, 22f);
            p.swayFreq = Random.Range(0.6f, 1.2f);
            p.rotSpeed = 0f;
            p.baseAlpha = Random.Range(0.45f, 0.88f);
            p.pulseSpeed = Random.Range(2.2f, 4.2f);
            p.image.color = new Color(1f, 0.88f, 1f, p.baseAlpha);
        }

        p.rect.anchoredPosition = new Vector2(p.baseX, p.posY);
    }

    private static int PickSpriteKind()
    {
        float r = Random.value;
        if (r < 0.28f) return 0;
        if (r < 0.54f) return 1;
        if (r < 0.78f) return 2;
        return 3;
    }
}
