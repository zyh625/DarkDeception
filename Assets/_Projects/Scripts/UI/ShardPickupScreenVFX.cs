using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 水晶拾取屏幕边缘粒子爆发效果：
/// 共用 shardEffect.png 切出的 5 张 Sprite（3 种碎晶 + 四角闪光 + 柔光点），
/// 按拾取类型分别染紫（普通碎片）、染红（红水晶）、染金（黄水晶），
/// 严格在屏幕四周边缘短促散开并留出中央视野。
/// </summary>
public class ShardPickupScreenVFX : MonoBehaviour
{
    [Header("图集切片（3 种碎晶 + 四角闪光 + 柔光点）")]
    [SerializeField] private Sprite[] shardSprites;
    [SerializeField] private Sprite sparkleSprite;
    [SerializeField] private Sprite softGlowSprite;

    [Header("三种水晶配色（共用同一套白灰图集）")]
    [SerializeField] private Color purpleTint = new Color(0.76f, 0.34f, 1.00f, 1f);
    [SerializeField] private Color purpleSparkleTint = new Color(0.95f, 0.76f, 1.00f, 1f);
    [SerializeField] private Color redTint = new Color(1.00f, 0.20f, 0.24f, 1f);
    [SerializeField] private Color redSparkleTint = new Color(1.00f, 0.72f, 0.72f, 1f);
    [SerializeField] private Color goldTint = new Color(1.00f, 0.80f, 0.20f, 1f);
    [SerializeField] private Color goldSparkleTint = new Color(1.00f, 0.95f, 0.65f, 1f);

    [Header("屏幕边缘散开参数（留出中央视野）")]
    [SerializeField, Range(0.06f, 0.22f)] private float edgeBandWidthRatio = 0.13f;
    [SerializeField, Range(0.08f, 0.24f)] private float edgeBandHeightRatio = 0.15f;
    [SerializeField] private int shardsPerBurst = 10;
    [SerializeField] private int sparklesPerBurst = 5;
    [SerializeField] private int glowDotsPerBurst = 6;
    [SerializeField] private int poolSize = 48;

    [Header("红/黄水晶副层缩放（让三维光波成为主效果，普通紫碎片保持原样）")]
    [SerializeField, Range(0f, 1f)] private float specialCrystalCountScale = 0.30f;
    [SerializeField, Range(0f, 1f)] private float specialCrystalAlphaScale = 0.42f;

    private struct ParticleItem
    {
        public RectTransform Rect;
        public Image Img;
        public Vector2 StartPos;
        public Vector2 Velocity;
        public float StartRot;
        public float AngularSpeed;
        public float BaseScale;
        public float PeakScale;
        public float Elapsed;
        public float Duration;
        public Color BaseColor;
        public float MaxAlpha;
        public bool Active;
    }

    private RectTransform container;
    private ParticleItem[] pool;
    private int poolCursor;

    private void Awake()
    {
        container = transform as RectTransform;
        InitPool();
    }

    private void InitPool()
    {
        if (pool != null) return;
        int count = Mathf.Max(24, poolSize);
        pool = new ParticleItem[count];

        for (int i = 0; i < count; i++)
        {
            GameObject go = new GameObject($"ShardParticle_{i:00}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(transform, false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);

            Image img = go.GetComponent<Image>();
            img.raycastTarget = false;
            img.preserveAspect = true;
            go.SetActive(false);

            pool[i] = new ParticleItem
            {
                Rect = rt,
                Img = img,
                Active = false
            };
        }
    }

    public void PlayBurst(ShardColorType colorType)
    {
        if (pool == null) InitPool();
        if (container == null) container = transform as RectTransform;

        Vector2 canvasSize = container != null && container.rect.width > 10f
            ? container.rect.size
            : new Vector2(1920f, 1080f);

        GetPalette(colorType, out Color mainCol, out Color brightCol);

        // 普通紫碎片维持原亮度与数量；红/黄水晶作为三维光波的副层，降低屏幕边缘碎晶的数量与亮度
        bool isSpecial = colorType == ShardColorType.Red || colorType == ShardColorType.Yellow;
        float countScale = isSpecial ? Mathf.Clamp01(specialCrystalCountScale) : 1f;
        float alphaScale = isSpecial ? Mathf.Clamp01(specialCrystalAlphaScale) : 1f;

        int glowCount = isSpecial ? Mathf.Max(1, Mathf.RoundToInt(glowDotsPerBurst * countScale)) : glowDotsPerBurst;
        int shardCount = isSpecial ? Mathf.Max(2, Mathf.RoundToInt(shardsPerBurst * countScale)) : shardsPerBurst;
        int sparkleCount = isSpecial ? Mathf.Max(1, Mathf.RoundToInt(sparklesPerBurst * countScale)) : sparklesPerBurst;

        // 1. 柔光点（底层边缘微光）
        if (softGlowSprite != null)
        {
            for (int i = 0; i < glowCount; i++)
            {
                SpawnEdgeParticle(
                    softGlowSprite,
                    canvasSize,
                    mainCol,
                    Random.Range(110f, 180f),
                    Random.Range(0.85f, 1.25f),
                    Random.Range(0.32f, 0.50f),
                    Random.Range(0.28f, 0.45f) * alphaScale,
                    Random.Range(60f, 130f),
                    0f);
            }
        }

        // 2. 3 种碎晶（沿屏幕边缘短促翻飞散开）
        if (shardSprites != null && shardSprites.Length > 0)
        {
            for (int i = 0; i < shardCount; i++)
            {
                Sprite sp = shardSprites[Random.Range(0, shardSprites.Length)];
                if (sp == null) continue;
                Color tint = Color.Lerp(mainCol, brightCol, Random.Range(0f, 0.35f));
                SpawnEdgeParticle(
                    sp,
                    canvasSize,
                    tint,
                    Random.Range(42f, 76f),
                    Random.Range(0.95f, 1.30f),
                    Random.Range(0.36f, 0.58f),
                    Random.Range(0.82f, 0.98f) * alphaScale,
                    Random.Range(140f, 320f),
                    Random.Range(-320f, 320f));
            }
        }

        // 3. 四角闪光（短促高亮星芒）
        if (sparkleSprite != null)
        {
            for (int i = 0; i < sparkleCount; i++)
            {
                SpawnEdgeParticle(
                    sparkleSprite,
                    canvasSize,
                    brightCol,
                    Random.Range(56f, 98f),
                    Random.Range(1.05f, 1.45f),
                    Random.Range(0.28f, 0.46f),
                    Random.Range(0.88f, 1.00f) * alphaScale,
                    Random.Range(70f, 170f),
                    Random.Range(-90f, 90f));
            }
        }
    }

    private void SpawnEdgeParticle(
        Sprite sprite,
        Vector2 canvasSize,
        Color color,
        float baseSize,
        float peakScale,
        float duration,
        float maxAlpha,
        float speed,
        float angularSpeed)
    {
        int idx = poolCursor;
        poolCursor = (poolCursor + 1) % pool.Length;

        float halfW = canvasSize.x * 0.5f;
        float halfH = canvasSize.y * 0.5f;
        float bandW = canvasSize.x * edgeBandWidthRatio;
        float bandH = canvasSize.y * edgeBandHeightRatio;

        // 从四条屏幕外缘带采样出生点，完全避开中央视野区
        int side = Random.Range(0, 4);
        Vector2 pos;
        Vector2 outwardDir;
        switch (side)
        {
            case 0: // 左边缘
                pos = new Vector2(Random.Range(-halfW + 12f, -halfW + bandW), Random.Range(-halfH + 24f, halfH - 24f));
                outwardDir = new Vector2(Random.Range(-0.25f, 0.35f), Random.Range(-0.85f, 0.85f));
                break;
            case 1: // 右边缘
                pos = new Vector2(Random.Range(halfW - bandW, halfW - 12f), Random.Range(-halfH + 24f, halfH - 24f));
                outwardDir = new Vector2(Random.Range(-0.35f, 0.25f), Random.Range(-0.85f, 0.85f));
                break;
            case 2: // 上边缘
                pos = new Vector2(Random.Range(-halfW + 24f, halfW - 24f), Random.Range(halfH - bandH, halfH - 12f));
                outwardDir = new Vector2(Random.Range(-0.85f, 0.85f), Random.Range(-0.30f, 0.25f));
                break;
            default: // 下边缘
                pos = new Vector2(Random.Range(-halfW + 24f, halfW - 24f), Random.Range(-halfH + 12f, -halfH + bandH));
                outwardDir = new Vector2(Random.Range(-0.85f, 0.85f), Random.Range(-0.25f, 0.30f));
                break;
        }

        // 优先沿屏幕外缘切向与微向外扩散，不闯入屏幕正中央
        Vector2 dir = (outwardDir + pos.normalized * 0.45f).normalized;

        ParticleItem item = pool[idx];
        item.Img.sprite = sprite;
        item.Rect.sizeDelta = new Vector2(baseSize, baseSize);
        item.Rect.anchoredPosition = pos;
        item.StartPos = pos;
        item.Velocity = dir * speed;
        item.StartRot = Random.Range(0f, 360f);
        item.AngularSpeed = angularSpeed;
        item.BaseScale = 0.35f;
        item.PeakScale = peakScale;
        item.Elapsed = 0f;
        item.Duration = duration;
        item.BaseColor = color;
        item.MaxAlpha = maxAlpha;
        item.Active = true;

        item.Rect.localRotation = Quaternion.Euler(0f, 0f, item.StartRot);
        item.Rect.localScale = Vector3.one * item.BaseScale;
        item.Img.color = new Color(color.r, color.g, color.b, 0f);
        item.Img.gameObject.SetActive(true);
        pool[idx] = item;
    }

    private void Update()
    {
        if (pool == null) return;
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        for (int i = 0; i < pool.Length; i++)
        {
            if (!pool[i].Active) continue;

            ParticleItem item = pool[i];
            item.Elapsed += dt;
            float t = Mathf.Clamp01(item.Elapsed / item.Duration);

            if (t >= 1f)
            {
                item.Active = false;
                item.Img.gameObject.SetActive(false);
                pool[i] = item;
                continue;
            }

            // 缓出位移：初速短促，随后迅速减速消散
            float easeOut = 1f - (1f - t) * (1f - t);
            item.Rect.anchoredPosition = item.StartPos + item.Velocity * (easeOut * item.Duration);
            item.Rect.localRotation = Quaternion.Euler(0f, 0f, item.StartRot + item.AngularSpeed * item.Elapsed);

            // 前 22% 迅速弹出到峰值，后段收缩淡出
            float scale = t < 0.22f
                ? Mathf.Lerp(item.BaseScale, item.PeakScale, t / 0.22f)
                : Mathf.Lerp(item.PeakScale, item.BaseScale * 0.6f, (t - 0.22f) / 0.78f);
            item.Rect.localScale = Vector3.one * scale;

            float alpha = t < 0.16f
                ? Mathf.Lerp(0f, item.MaxAlpha, t / 0.16f)
                : Mathf.Lerp(item.MaxAlpha, 0f, (t - 0.16f) / 0.84f);
            item.Img.color = new Color(item.BaseColor.r, item.BaseColor.g, item.BaseColor.b, alpha);

            pool[i] = item;
        }
    }

    private void GetPalette(ShardColorType type, out Color mainCol, out Color brightCol)
    {
        switch (type)
        {
            case ShardColorType.Red:
                mainCol = redTint;
                brightCol = redSparkleTint;
                break;
            case ShardColorType.Yellow:
                mainCol = goldTint;
                brightCol = goldSparkleTint;
                break;
            default:
                mainCol = purpleTint;
                brightCol = purpleSparkleTint;
                break;
        }
    }
}
