using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 处决动画全屏血液冲击特效控制器（挂于 LevelManager 或任意常驻节点）：
/// - 响应 MonkeyAnimation 关键帧发出的血溅冲击请求（抓取、连拳、猛砸、咬合、最终抛飞）；
/// - 叠加 4 层镜头血效：
///   1) 瞬间猩红冲击闪屏暗角；
///   2) 多角度角落泼血（Execution_ImpactSplatter，按冲击方向逐次叠加与弹性放大）；
///   3) 镜头挂血缓慢下滑残痕（Execution_LensResidue，双侧垂流）；
///   4) 2x2 高光湿血滴与飞溅血丝粒子池（Execution_RedDropletAtlas_2x2，飞散后粘附屏幕缓滑）；
/// - 进入黑屏转场时由 RequestClear() 瞬间清空，不影响复活/失败界面。
/// </summary>
public class ExecutionBloodScreenVFX : MonoBehaviour
{
    public static event Action<float, int> OnBloodImpactRequested;
    public static event Action OnBloodClearRequested;

    /// <summary>
    /// 触发一次屏幕血溅冲击：
    /// intensity: 0.35(轻擦) ~ 1.45(咬合/抛飞重击)
    /// directionHint: -1=偏左击打, 0=居中爆发, 1=偏右击打, 2=全屏重击(咬合/终结)
    /// </summary>
    public static void RequestImpact(float intensity, int directionHint = 0)
    {
        OnBloodImpactRequested?.Invoke(intensity, directionHint);
    }

    public static void RequestClear()
    {
        OnBloodClearRequested?.Invoke();
    }

    [Header("血液特效贴图（Assets/_Projects/Images/Effects）")]
    [SerializeField] private Sprite impactSplatterSprite;
    [SerializeField] private Sprite lensResidueSprite;
    [SerializeField] private Sprite[] dropletSprites = new Sprite[4];

    [Header("层级与强度配置")]
    [SerializeField] private int canvasSortingOrder = 430;
    [SerializeField] private int maxDropletPool = 28;
    [SerializeField] private float flashFadeSpeed = 4.6f;
    [SerializeField] private float residueDripSpeed = 16f;
    [SerializeField] private float splatterDecayDelay = 2.6f;

    private struct SplatterLayer
    {
        public RectTransform rect;
        public Image image;
        public float alpha;
        public float punchTimer;
        public Vector3 baseScale;
    }

    private struct ResidueLayer
    {
        public RectTransform rect;
        public Image image;
        public float alpha;
        public float dripY;
    }

    private struct DropletParticle
    {
        public RectTransform rect;
        public Image image;
        public Vector2 pos;
        public Vector2 vel;
        public float age;
        public float lifetime;
        public float baseAlpha;
        public float baseSize;
        public float dripSpeed;
        public bool isStreak;
        public bool active;
    }

    private GameObject canvasRoot;
    private Image flashOverlay;
    private float flashAlpha;

    private readonly SplatterLayer[] splatters = new SplatterLayer[3];
    private readonly ResidueLayer[] residues = new ResidueLayer[2];
    private DropletParticle[] droplets;
    private int nextDropletCursor;
    private int splatterCycle;
    private float timeSinceLastImpact;
    private bool isVisible;

    private void Awake()
    {
        BuildOverlayHierarchy();
        ClearImmediate();
    }

    private void OnEnable()
    {
        OnBloodImpactRequested += HandleBloodImpact;
        OnBloodClearRequested += ClearImmediate;
    }

    private void OnDisable()
    {
        OnBloodImpactRequested -= HandleBloodImpact;
        OnBloodClearRequested -= ClearImmediate;
    }

    private void BuildOverlayHierarchy()
    {
        canvasRoot = new GameObject(
            "ExecutionBloodVFXCanvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler));
        canvasRoot.transform.SetParent(transform, false);

        Canvas c = canvasRoot.GetComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay;
        c.sortingOrder = canvasSortingOrder;

        CanvasScaler scaler = canvasRoot.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // 1. 猩红闪屏冲击层
        GameObject flashGo = new GameObject("CrimsonImpactFlash", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        flashGo.transform.SetParent(canvasRoot.transform, false);
        StretchFull(flashGo.GetComponent<RectTransform>());
        flashOverlay = flashGo.GetComponent<Image>();
        flashOverlay.color = new Color(0.55f, 0.02f, 0.04f, 0f);
        flashOverlay.raycastTarget = false;

        // 2. 镜头挂血下滑残痕层（左右双侧）
        for (int i = 0; i < residues.Length; i++)
        {
            GameObject rGo = new GameObject($"LensResidue_{i}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            rGo.transform.SetParent(canvasRoot.transform, false);
            RectTransform rt = rGo.GetComponent<RectTransform>();
            StretchFull(rt);
            rt.localScale = i == 0 ? new Vector3(1.06f, 1.06f, 1f) : new Vector3(-1.08f, 1.06f, 1f);
            Image img = rGo.GetComponent<Image>();
            img.sprite = lensResidueSprite;
            img.color = new Color(0.88f, 0.14f, 0.16f, 0f);
            img.raycastTarget = false;
            img.enabled = lensResidueSprite != null;
            residues[i] = new ResidueLayer { rect = rt, image = img };
        }

        // 3. 3 个不同方位的冲击大泼血层（右下、左上镜像、左下镜像）
        Vector3[] splatScales =
        {
            new Vector3(1.04f, 1.04f, 1f),    // 右下原向
            new Vector3(-1.06f, -1.06f, 1f),  // 左上翻转
            new Vector3(-1.05f, 1.05f, 1f),   // 左下水平镜像
        };
        for (int i = 0; i < splatters.Length; i++)
        {
            GameObject sGo = new GameObject($"ImpactSplatter_{i}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            sGo.transform.SetParent(canvasRoot.transform, false);
            RectTransform rt = sGo.GetComponent<RectTransform>();
            StretchFull(rt);
            rt.localScale = splatScales[i];
            Image img = sGo.GetComponent<Image>();
            img.sprite = impactSplatterSprite;
            img.color = new Color(0.95f, 0.16f, 0.18f, 0f);
            img.raycastTarget = false;
            img.enabled = impactSplatterSprite != null;
            splatters[i] = new SplatterLayer { rect = rt, image = img, baseScale = splatScales[i] };
        }

        // 4. 2x2 高光血滴与飞溅血丝粒子池
        int count = Mathf.Clamp(maxDropletPool, 12, 48);
        droplets = new DropletParticle[count];
        for (int i = 0; i < count; i++)
        {
            GameObject dGo = new GameObject($"BloodDrop_{i:00}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            dGo.transform.SetParent(canvasRoot.transform, false);
            RectTransform rt = dGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            Image img = dGo.GetComponent<Image>();
            img.raycastTarget = false;
            img.preserveAspect = true;
            dGo.SetActive(false);
            droplets[i] = new DropletParticle { rect = rt, image = img, active = false };
        }
    }

    private void HandleBloodImpact(float intensity, int directionHint)
    {
        float clamped = Mathf.Clamp(intensity, 0.2f, 1.6f);
        timeSinceLastImpact = 0f;
        if (!isVisible)
        {
            isVisible = true;
            if (canvasRoot != null) canvasRoot.SetActive(true);
        }

        // 1. 猩红冲击闪屏（重击闪光更强烈但迅速回落，不遮死视线）
        float targetFlash = Mathf.Clamp(0.16f + clamped * 0.24f, 0.14f, 0.50f);
        flashAlpha = Mathf.Max(flashAlpha, targetFlash);

        // 2. 触发对应方位的角落泼血层
        if (directionHint >= 2)
        {
            // 咬合/终结全屏重击：三方位泼血同时爆发
            for (int i = 0; i < splatters.Length; i++)
            {
                TriggerSplatterLayer(i, Mathf.Clamp01(0.75f + clamped * 0.22f));
            }
        }
        else
        {
            int idx = directionHint < 0 ? (splatterCycle % 2 == 0 ? 1 : 2)
                    : (directionHint > 0 ? 0 : (splatterCycle % splatters.Length));
            splatterCycle++;
            TriggerSplatterLayer(idx, Mathf.Clamp01(0.52f + clamped * 0.35f));
            if (clamped >= 0.95f)
            {
                TriggerSplatterLayer((idx + 1) % splatters.Length, Mathf.Clamp01(0.45f + clamped * 0.28f));
            }
        }

        // 3. 镜头挂血残留逐步加深（两侧挂血顺着镜片缓慢下滑）
        for (int i = 0; i < residues.Length; i++)
        {
            bool triggerThis = directionHint >= 2 || (i == 0 && directionHint >= 0) || (i == 1 && directionHint <= 0);
            if (triggerThis)
            {
                float add = 0.34f * clamped;
                if (residues[i].alpha < 0.05f)
                {
                    residues[i].dripY = UnityEngine.Random.Range(40f, 95f);
                }
                residues[i].alpha = Mathf.Clamp(residues[i].alpha + add, 0f, 0.92f);
            }
        }

        // 4. 喷射湿润血滴与高速飞溅血丝（留出画面中央让玩家看清猴子面部）
        int burstCount = Mathf.RoundToInt(Mathf.Lerp(6f, 16f, Mathf.Clamp01(clamped / 1.35f)));
        SpawnDropletBurst(burstCount, clamped, directionHint);
    }

    private void TriggerSplatterLayer(int idx, float targetAlpha)
    {
        if (idx < 0 || idx >= splatters.Length) return;
        splatters[idx].alpha = Mathf.Max(splatters[idx].alpha, targetAlpha);
        splatters[idx].punchTimer = 0.22f;
    }

    private void SpawnDropletBurst(int count, float intensity, int directionHint)
    {
        if (droplets == null || dropletSprites == null || dropletSprites.Length == 0) return;

        for (int n = 0; n < count; n++)
        {
            int slot = nextDropletCursor;
            nextDropletCursor = (nextDropletCursor + 1) % droplets.Length;
            ref DropletParticle p = ref droplets[slot];

            // 选取 2x2 血滴图集子图：1=细长血丝，0/2/3=粘附血滴与血渍
            bool makeStreak = UnityEngine.Random.value < 0.42f;
            int spIdx = makeStreak ? 1 : (UnityEngine.Random.value < 0.35f ? 2 : (UnityEngine.Random.value < 0.55f ? 0 : 3));
            Sprite sp = dropletSprites[Mathf.Clamp(spIdx, 0, dropletSprites.Length - 1)];
            if (sp == null)
            {
                for (int k = 0; k < dropletSprites.Length; k++)
                {
                    if (dropletSprites[k] != null) { sp = dropletSprites[k]; break; }
                }
            }
            if (sp == null) continue;

            // 沿屏幕外环爆发，留出中央视野看清怪物面部与血盆大口
            float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            if (directionHint < 0) angle = UnityEngine.Random.Range(Mathf.PI * 0.55f, Mathf.PI * 1.45f);
            else if (directionHint > 0) angle = UnityEngine.Random.Range(-Mathf.PI * 0.45f, Mathf.PI * 0.45f);

            Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            float startR = makeStreak ? UnityEngine.Random.Range(180f, 440f) : UnityEngine.Random.Range(320f, 680f);
            p.pos = new Vector2(dir.x * startR * 1.25f, dir.y * startR * 0.72f);
            p.vel = makeStreak
                ? dir * UnityEngine.Random.Range(560f, 1180f) * Mathf.Clamp(intensity, 0.6f, 1.4f)
                : dir * UnityEngine.Random.Range(140f, 360f);

            p.isStreak = makeStreak;
            p.age = 0f;
            p.lifetime = makeStreak ? UnityEngine.Random.Range(0.28f, 0.52f) : UnityEngine.Random.Range(2.4f, 4.2f);
            p.baseAlpha = UnityEngine.Random.Range(0.76f, 0.96f);
            p.baseSize = makeStreak
                ? UnityEngine.Random.Range(95f, 190f) * Mathf.Clamp(intensity, 0.75f, 1.35f)
                : UnityEngine.Random.Range(46f, 128f) * Mathf.Clamp(intensity, 0.75f, 1.35f);
            p.dripSpeed = makeStreak ? 0f : UnityEngine.Random.Range(10f, 32f);
            p.active = true;

            p.image.sprite = sp;
            p.image.color = new Color(0.95f, 0.14f, 0.16f, p.baseAlpha);
            p.rect.anchoredPosition = p.pos;
            p.rect.sizeDelta = new Vector2(p.baseSize, p.baseSize);

            if (makeStreak)
            {
                // 图集子图 1 尖端指向右上 45°，按飞行速度方向旋转对齐
                float deg = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 45f;
                p.rect.localRotation = Quaternion.Euler(0f, 0f, deg);
            }
            else
            {
                p.rect.localRotation = Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(-30f, 30f));
            }
            p.rect.localScale = Vector3.one * 1.25f;
            p.rect.gameObject.SetActive(true);
        }
    }

    public void ClearImmediate()
    {
        isVisible = false;
        flashAlpha = 0f;
        timeSinceLastImpact = 0f;

        if (flashOverlay != null)
        {
            Color fc = flashOverlay.color;
            fc.a = 0f;
            flashOverlay.color = fc;
        }

        for (int i = 0; i < splatters.Length; i++)
        {
            splatters[i].alpha = 0f;
            splatters[i].punchTimer = 0f;
            if (splatters[i].image != null)
            {
                Color c = splatters[i].image.color;
                c.a = 0f;
                splatters[i].image.color = c;
            }
        }

        for (int i = 0; i < residues.Length; i++)
        {
            residues[i].alpha = 0f;
            residues[i].dripY = 0f;
            if (residues[i].image != null)
            {
                Color c = residues[i].image.color;
                c.a = 0f;
                residues[i].image.color = c;
            }
        }

        if (droplets != null)
        {
            for (int i = 0; i < droplets.Length; i++)
            {
                droplets[i].active = false;
                if (droplets[i].rect != null)
                {
                    droplets[i].rect.gameObject.SetActive(false);
                }
            }
        }

        if (canvasRoot != null)
        {
            canvasRoot.SetActive(false);
        }
    }

    private void Update()
    {
        if (!isVisible) return;

        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        timeSinceLastImpact += dt;
        bool anyAlive = false;

        // 1. 闪屏迅速衰减
        if (flashAlpha > 0.001f)
        {
            flashAlpha = Mathf.MoveTowards(flashAlpha, 0f, flashFadeSpeed * dt);
            anyAlive = true;
        }
        if (flashOverlay != null)
        {
            Color fc = flashOverlay.color;
            fc.a = flashAlpha;
            flashOverlay.color = fc;
        }

        // 2. 角落泼血层弹性缩放与长时间后柔和消退
        for (int i = 0; i < splatters.Length; i++)
        {
            ref SplatterLayer s = ref splatters[i];
            if (s.alpha <= 0.001f) continue;

            if (s.punchTimer > 0f)
            {
                s.punchTimer = Mathf.Max(0f, s.punchTimer - dt);
                float p = s.punchTimer / 0.22f;
                float boost = 1f + 0.14f * (p * p);
                s.rect.localScale = s.baseScale * boost;
            }
            else
            {
                s.rect.localScale = s.baseScale;
            }

            if (timeSinceLastImpact > splatterDecayDelay)
            {
                s.alpha = Mathf.MoveTowards(s.alpha, 0f, 0.35f * dt);
            }

            Color c = s.image.color;
            c.a = s.alpha;
            s.image.color = c;
            if (s.alpha > 0.001f) anyAlive = true;
        }

        // 3. 镜头挂血顺着镜片缓慢下滑
        for (int i = 0; i < residues.Length; i++)
        {
            ref ResidueLayer r = ref residues[i];
            if (r.alpha <= 0.001f) continue;

            r.dripY = Mathf.Max(-140f, r.dripY - residueDripSpeed * dt);
            r.rect.anchoredPosition = new Vector2(0f, r.dripY);

            if (timeSinceLastImpact > splatterDecayDelay + 0.5f)
            {
                r.alpha = Mathf.MoveTowards(r.alpha, 0f, 0.28f * dt);
            }

            Color c = r.image.color;
            c.a = r.alpha;
            r.image.color = c;
            if (r.alpha > 0.001f) anyAlive = true;
        }

        // 4. 血丝飞溅与湿血滴下滑
        if (droplets != null)
        {
            for (int i = 0; i < droplets.Length; i++)
            {
                ref DropletParticle p = ref droplets[i];
                if (!p.active) continue;

                p.age += dt;
                if (p.age >= p.lifetime)
                {
                    p.active = false;
                    p.rect.gameObject.SetActive(false);
                    continue;
                }

                anyAlive = true;
                float norm = p.age / p.lifetime;

                if (p.isStreak)
                {
                    p.pos += p.vel * dt;
                    p.vel *= Mathf.Exp(-3.2f * dt);
                    float a = p.baseAlpha * (1f - norm * norm);
                    Color c = p.image.color;
                    c.a = a;
                    p.image.color = c;
                    p.rect.localScale = Vector3.one * Mathf.Lerp(1.15f, 0.85f, norm);
                }
                else
                {
                    // 初段快速贴上镜片，随后阻力停住并顺着重力缓缓下滑
                    p.pos += p.vel * dt;
                    p.vel *= Mathf.Exp(-12f * dt);
                    p.pos.y -= p.dripSpeed * dt;

                    float popScale = p.age < 0.09f ? Mathf.Lerp(1.32f, 1f, p.age / 0.09f) : 1f;
                    p.rect.localScale = Vector3.one * popScale;

                    float fadeOut = norm > 0.72f ? 1f - (norm - 0.72f) / 0.28f : 1f;
                    Color c = p.image.color;
                    c.a = p.baseAlpha * fadeOut;
                    p.image.color = c;
                }

                p.rect.anchoredPosition = p.pos;
            }
        }

        if (!anyAlive)
        {
            ClearImmediate();
        }
    }

    private static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
    }
}
