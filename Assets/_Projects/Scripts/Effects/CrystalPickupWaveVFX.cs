using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 红水晶 / 黄水晶拾取三维加速光波球特效控制器：
/// - 复用 LevelOne.OnShardPicked 拾取通知，普通紫碎片继续走原有屏幕碎晶流程，红/黄水晶触发真实三维球壳扩散；
/// - 球心在拾取瞬间记录于玩家胸口附近的世界坐标，发出后留在原地扩散，不受玩家继续移动影响；
/// - 红色波、黄色第一波、黄色第二波均严格使用持续加速规律：
///     distanceProgress = 0.15 * p + 0.85 * p * p
///     radius = startRadius + (endRadius - startRadius) * distanceProgress
/// - 球壳支持双面与内部视角清晰可见（非仅依赖 Fresnel），并允许墙体正常深度遮挡；
/// - 配套三维方向速度拖尾（CrystalPickup_VelocityStreak.png，右向尖端严格朝向粒子飞行方向）与少量碎晶/闪点（shardEffect.png）；
/// - 全实例池化回收，连续拾取时每次的颜色、球心、旋转与计时完全独立。
/// </summary>
public class CrystalPickupWaveVFX : MonoBehaviour
{
    private static readonly int TintColorId = Shader.PropertyToID("_TintColor");
    private static readonly int CoreHighlightColorId = Shader.PropertyToID("_CoreHighlightColor");
    private static readonly int BrightnessId = Shader.PropertyToID("_Brightness");
    private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
    private static readonly int ArcSharpnessId = Shader.PropertyToID("_ArcSharpness");
    private static readonly int RingScaleId = Shader.PropertyToID("_RingScale");
    private static readonly int ShellFillAlphaId = Shader.PropertyToID("_ShellFillAlpha");
    private static readonly int FresnelBoostId = Shader.PropertyToID("_FresnelBoost");
    private static readonly int AngleOffsetId = Shader.PropertyToID("_AngleOffset");
    private static readonly int SpinPhaseId = Shader.PropertyToID("_SpinPhase");
    private static readonly int NearCamFadeId = Shader.PropertyToID("_NearCamFade");

    // shardEffect.png (1536x1024, 3x2 切片) UV 矩形：0/1/2 为 3 种碎晶，3 为四角星闪点
    private static readonly Rect[] ShardAtlasUVs =
    {
        new Rect(0f / 3f, 0.5f, 1f / 3f, 0.5f),
        new Rect(1f / 3f, 0.5f, 1f / 3f, 0.5f),
        new Rect(2f / 3f, 0.5f, 1f / 3f, 0.5f),
        new Rect(0f / 3f, 0.0f, 1f / 3f, 0.5f),
    };

    [Header("关卡引用与共享材质")]
    [SerializeField] private LevelOne levelOne;
    [SerializeField] private Material redWaveShellMaterial;
    [SerializeField] private Material goldWaveShellMaterial;
    [SerializeField] private Material velocityStreakMaterial;
    [SerializeField] private Material shardAccentMaterial;

    [Header("球心位置与通用扩散半径（单位：米）")]
    [Tooltip("相对玩家根节点高度偏移（玩家根节点约 Y=0.254，眼睛约 0.40，+0.06 位于胸口附近）")]
    [SerializeField] private float chestHeightOffset = 0.06f;
    [SerializeField] private float startRadius = 0.35f;
    [SerializeField] private float endRadius = 7.0f;
    [SerializeField] private float nearCameraSoftFade = 0.045f;

    [Header("红水晶参数（单道锐利扫描波）")]
    [SerializeField] private float redDuration = 0.35f;
    [SerializeField] private float redBrightness = 2.65f;
    [SerializeField, Range(0f, 1.5f)] private float redOpacity = 1.0f;
    [SerializeField, Range(0.5f, 3.0f)] private float redArcSharpness = 1.48f;
    [SerializeField] private float redRingScale = 0.455f;
    [SerializeField, Range(0f, 0.3f)] private float redShellFillAlpha = 0.068f;
    [SerializeField] private float redSpinSpeedDeg = 72f;
    [SerializeField] private Color redTintColor = new Color(1.00f, 0.14f, 0.20f, 1f);
    [SerializeField] private Color redCoreHighlight = new Color(1.00f, 0.84f, 0.86f, 1f);
    [SerializeField] private int redStreakCount = 22;
    [SerializeField] private float redStreakDuration = 0.25f;
    [SerializeField] private int redShardAccentCount = 6;

    [Header("黄水晶参数（双道错角加速时间波）")]
    [SerializeField] private float yellowDuration = 0.45f;
    [SerializeField] private float yellowSecondWaveDelay = 0.08f;
    [SerializeField] private float yellowFirstWaveBrightness = 2.55f;
    [SerializeField] private float yellowSecondWaveBrightness = 1.68f;
    [SerializeField] private float yellowSecondWaveAngleOffsetDeg = 22.5f;
    [SerializeField, Range(0f, 1.5f)] private float yellowOpacity = 1.0f;
    [SerializeField, Range(0.5f, 3.0f)] private float yellowArcSharpness = 0.96f;
    [SerializeField] private float yellowRingScale = 0.455f;
    [SerializeField, Range(0f, 0.3f)] private float yellowShellFillAlpha = 0.062f;
    [SerializeField] private float yellowSpinSpeedDeg = 34f;
    [SerializeField] private Color yellowFirstTint = new Color(1.00f, 0.78f, 0.18f, 1f);
    [SerializeField] private Color yellowSecondTint = new Color(0.96f, 0.64f, 0.12f, 1f);
    [SerializeField] private Color yellowCoreHighlight = new Color(1.00f, 0.97f, 0.74f, 1f);
    [SerializeField] private int yellowStreakCount = 26;
    [SerializeField] private float yellowStreakDuration = 0.34f;
    [SerializeField] private int yellowShardAccentCount = 8;

    [Header("可选调试快捷键（默认关闭；开启后 F7 测红波、F8 测黄波）")]
    [SerializeField] private bool enableDebugHotkeys = false;

    private struct ActiveShellWave
    {
        public GameObject Go;
        public Transform Tr;
        public MeshRenderer Renderer;
        public MaterialPropertyBlock Mpb;
        public Vector3 OriginWorld;
        public float BirthTime;
        public float Duration;
        public float StartRadius;
        public float EndRadius;
        public float Brightness;
        public float BaseOpacity;
        public float ArcSharpness;
        public float RingScale;
        public float ShellFillAlpha;
        public float AngleOffsetRad;
        public float SpinSpeedRad;
        public Color TintColor;
        public Color CoreColor;
        public bool Activated;
    }

    private struct StreakParticle
    {
        public Vector3 Dir;
        public Vector3 PerpA;
        public Vector3 PerpB;
        public float Delay;
        public float Duration;
        public float StartDist;
        public float InitialSpeed;
        public float Acceleration;
        public float BaseLength;
        public float LengthPerSpeed;
        public float HalfWidth;
        public Color BaseColor;
        public bool FlashOutTail;
    }

    private struct ShardAccentParticle
    {
        public Vector3 Dir;
        public float Delay;
        public float Duration;
        public float StartDist;
        public float InitialSpeed;
        public float Acceleration;
        public float Size;
        public float SpinDeg;
        public float SpinSpeedDeg;
        public int AtlasIdx;
        public Color BaseColor;
        public bool TwinkleOut;
    }

    private class ParticleBurstSlot
    {
        public GameObject RootGo;
        public Transform RootTr;
        public Mesh StreakMesh;
        public MeshRenderer StreakRenderer;
        public Mesh ShardMesh;
        public MeshRenderer ShardRenderer;

        public Vector3 OriginWorld;
        public float SpawnTime;
        public float MaxLifetime;

        public StreakParticle[] Streaks;
        public int StreakCount;
        public ShardAccentParticle[] Shards;
        public int ShardCount;

        // 复用的动态网格缓冲（零 GC）
        public Vector3[] StreakVerts;
        public Vector2[] StreakUVs;
        public Color32[] StreakColors;
        public int[] StreakIndices;

        public Vector3[] ShardVerts;
        public Vector2[] ShardUVs;
        public Color32[] ShardColors;
        public int[] ShardIndices;
    }

    private const int MaxStreaksPerBurst = 40;
    private const int MaxShardsPerBurst = 16;

    private Mesh unitSphereMesh;
    private Transform poolContainer;
    private readonly Queue<GameObject> shellPool = new();
    private readonly Queue<ParticleBurstSlot> burstPool = new();
    private readonly List<ActiveShellWave> activeShells = new();
    private readonly List<ParticleBurstSlot> activeBursts = new();

    private void Awake()
    {
        if (levelOne == null)
        {
            levelOne = FindAnyObjectByType<LevelOne>();
        }

        unitSphereMesh = BuildSmoothUnitSphere(48, 32);

        GameObject containerGo = new GameObject("CrystalPickupWavePool");
        poolContainer = containerGo.transform;

        // 预热对象池（3 个球壳 + 2 组粒子槽），首次拾取零卡顿
        for (int i = 0; i < 3; i++)
        {
            shellPool.Enqueue(CreateShellInstance(i));
        }
        for (int i = 0; i < 2; i++)
        {
            burstPool.Enqueue(CreateBurstSlot(i));
        }
    }

    private void OnEnable()
    {
        if (levelOne == null) levelOne = FindAnyObjectByType<LevelOne>();
        if (levelOne != null)
        {
            levelOne.OnShardPicked += HandleShardPicked;
        }
    }

    private void OnDisable()
    {
        if (levelOne != null)
        {
            levelOne.OnShardPicked -= HandleShardPicked;
        }
    }

    private void OnDestroy()
    {
        if (unitSphereMesh != null)
        {
            Destroy(unitSphereMesh);
        }
        if (poolContainer != null)
        {
            Destroy(poolContainer.gameObject);
        }
    }

    private void HandleShardPicked(ShardColorType shardType)
    {
        // 普通紫碎片继续沿用原有屏幕碎晶流程；仅红水晶、黄水晶触发三维加速光波球
        if (shardType != ShardColorType.Red && shardType != ShardColorType.Yellow) return;

        Vector3 origin = ResolvePlayerChestWorldPos();
        PlayWaveAt(shardType, origin);
    }

    /// <summary>
    /// 在指定世界坐标处独立触发一次红水晶或黄水晶三维光波特效（球心留在该世界位置不动）。
    /// </summary>
    public void PlayWaveAt(ShardColorType shardType, Vector3 worldOrigin)
    {
        if (shardType == ShardColorType.Red)
        {
            SpawnRedWaveBurst(worldOrigin);
        }
        else if (shardType == ShardColorType.Yellow)
        {
            SpawnYellowWaveBurst(worldOrigin);
        }
    }

    [ContextMenu("测试红水晶光波 (Red)")]
    public void TestRedWaveAtPlayer()
    {
        PlayWaveAt(ShardColorType.Red, ResolvePlayerChestWorldPos());
    }

    [ContextMenu("测试黄水晶光波 (Yellow)")]
    public void TestYellowWaveAtPlayer()
    {
        PlayWaveAt(ShardColorType.Yellow, ResolvePlayerChestWorldPos());
    }

    private Vector3 ResolvePlayerChestWorldPos()
    {
        Transform playerTr = levelOne != null ? levelOne.PlayerTransform : null;
        if (playerTr == null && PlayerInteract.ActivePlayer != null)
        {
            playerTr = PlayerInteract.ActivePlayer.transform;
        }
        if (playerTr != null)
        {
            return playerTr.position + Vector3.up * chestHeightOffset;
        }
        Camera cam = Camera.main;
        if (cam != null)
        {
            return cam.transform.position - Vector3.up * 0.08f;
        }
        return Vector3.up * 0.32f;
    }

    private void SpawnRedWaveBurst(Vector3 origin)
    {
        float now = Time.time;
        float baseAngle = Random.Range(0f, Mathf.PI * 2f);

        // 1. 红水晶单道锐利猩红扫描球壳
        SpawnShell(
            redWaveShellMaterial,
            origin,
            Quaternion.Euler(Random.Range(-10f, 10f), Random.Range(0f, 360f), Random.Range(-10f, 10f)),
            now,
            Mathf.Max(0.08f, redDuration),
            startRadius,
            endRadius,
            redBrightness,
            redOpacity,
            redArcSharpness,
            redRingScale,
            redShellFillAlpha,
            baseAngle,
            redSpinSpeedDeg * Mathf.Deg2Rad,
            redTintColor,
            redCoreHighlight);

        // 2. 红色多方向加速拖尾（起飞后持续加速拉长，先于球壳结束）+ 少量红晶闪点点缀
        ParticleBurstSlot slot = RentBurstSlot();
        slot.OriginWorld = origin;
        slot.RootTr.position = origin;
        slot.SpawnTime = now;

        int sCount = Mathf.Clamp(redStreakCount, 0, MaxStreaksPerBurst);
        slot.StreakCount = sCount;
        float maxLife = 0.05f;
        float streakDurBase = Mathf.Clamp(redStreakDuration, 0.08f, Mathf.Max(0.08f, redDuration - 0.04f));

        for (int i = 0; i < sCount; i++)
        {
            Vector3 dir = SampleFibonacciDirection(i, sCount, 0.18f);
            BuildOrthonormalBasis(dir, out Vector3 perpA, out Vector3 perpB);
            float delay = Random.Range(0f, 0.025f);
            float dur = streakDurBase * Random.Range(0.80f, 1.05f);
            if (delay + dur > redDuration - 0.02f) dur = Mathf.Max(0.07f, redDuration - 0.02f - delay);

            Color col = Color.Lerp(redTintColor, redCoreHighlight, Random.Range(0.08f, 0.48f));
            slot.Streaks[i] = new StreakParticle
            {
                Dir = dir,
                PerpA = perpA,
                PerpB = perpB,
                Delay = delay,
                Duration = dur,
                StartDist = Random.Range(0.16f, 0.34f),
                InitialSpeed = Random.Range(4.5f, 7.5f),
                Acceleration = Random.Range(56f, 88f), // 起飞后持续加快，拖尾随速度明显拉长
                BaseLength = Random.Range(0.14f, 0.24f),
                LengthPerSpeed = 0.048f,
                HalfWidth = Random.Range(0.024f, 0.042f),
                BaseColor = col,
                FlashOutTail = false
            };
            maxLife = Mathf.Max(maxLife, delay + dur);
        }

        int shCount = Mathf.Clamp(redShardAccentCount, 0, MaxShardsPerBurst);
        slot.ShardCount = shCount;
        for (int i = 0; i < shCount; i++)
        {
            Vector3 dir = SampleFibonacciDirection(i, Mathf.Max(1, shCount), 0.30f);
            float delay = Random.Range(0f, 0.03f);
            float dur = Random.Range(0.18f, Mathf.Max(0.20f, redDuration * 0.85f));
            bool isSparkle = (i % 2 == 1);
            slot.Shards[i] = new ShardAccentParticle
            {
                Dir = dir,
                Delay = delay,
                Duration = dur,
                StartDist = Random.Range(0.24f, 0.45f),
                InitialSpeed = Random.Range(1.8f, 3.6f),
                Acceleration = Random.Range(10f, 18f),
                Size = isSparkle ? Random.Range(0.08f, 0.13f) : Random.Range(0.055f, 0.085f),
                SpinDeg = Random.Range(0f, 360f),
                SpinSpeedDeg = Random.Range(-220f, 220f),
                AtlasIdx = isSparkle ? 3 : (i % 3),
                BaseColor = isSparkle ? redCoreHighlight : redTintColor,
                TwinkleOut = false
            };
            maxLife = Mathf.Max(maxLife, delay + dur);
        }

        slot.MaxLifetime = maxLife;
        slot.RootGo.SetActive(true);
        activeBursts.Add(slot);
    }

    private void SpawnYellowWaveBurst(Vector3 origin)
    {
        float now = Time.time;
        float baseAngle = Random.Range(0f, Mathf.PI * 2f);
        float baseYaw = Random.Range(0f, 360f);
        float staggerRad = yellowSecondWaveAngleOffsetDeg * Mathf.Deg2Rad;
        float dur = Mathf.Max(0.10f, yellowDuration);
        float delay2 = Mathf.Max(0.01f, yellowSecondWaveDelay);

        // 1. 黄水晶第一道金色球壳（较亮，立即出发）
        SpawnShell(
            goldWaveShellMaterial,
            origin,
            Quaternion.Euler(0f, baseYaw, 0f),
            now,
            dur,
            startRadius,
            endRadius,
            yellowFirstWaveBrightness,
            yellowOpacity,
            yellowArcSharpness,
            yellowRingScale,
            yellowShellFillAlpha,
            baseAngle,
            yellowSpinSpeedDeg * Mathf.Deg2Rad,
            yellowFirstTint,
            yellowCoreHighlight);

        // 2. 黄水晶第二道金色球壳（约 0.08s 后从同一个球心出发，稍暗且刻度错开角度）
        SpawnShell(
            goldWaveShellMaterial,
            origin,
            Quaternion.Euler(14f, baseYaw + yellowSecondWaveAngleOffsetDeg, -11f),
            now + delay2,
            dur,
            startRadius,
            endRadius,
            yellowSecondWaveBrightness,
            yellowOpacity * 0.90f,
            yellowArcSharpness * 1.04f,
            yellowRingScale,
            yellowShellFillAlpha * 0.78f,
            baseAngle + staggerRad,
            -yellowSpinSpeedDeg * 0.85f * Mathf.Deg2Rad,
            yellowSecondTint,
            yellowCoreHighlight);

        // 3. 金色球面法线向外飞出的更短、更亮拖尾，结束时依次闪灭 + 少量金色闪点
        ParticleBurstSlot slot = RentBurstSlot();
        slot.OriginWorld = origin;
        slot.RootTr.position = origin;
        slot.SpawnTime = now;

        int sCount = Mathf.Clamp(yellowStreakCount, 0, MaxStreaksPerBurst);
        slot.StreakCount = sCount;
        float maxLife = 0.05f;
        float baseStreakDur = Mathf.Clamp(yellowStreakDuration, 0.10f, dur);

        for (int i = 0; i < sCount; i++)
        {
            Vector3 dir = SampleFibonacciDirection(i, sCount, 0.16f);
            BuildOrthonormalBasis(dir, out Vector3 perpA, out Vector3 perpB);

            // 前半随第一波、后半随第二波出发，并把结束时刻均匀错开以实现“依次闪灭”
            bool secondWave = (i >= sCount / 2);
            float orderT = (float)i / Mathf.Max(1, sCount - 1);
            float delay = (secondWave ? delay2 : 0f) + Random.Range(0f, 0.025f);
            float streakLife = baseStreakDur * Mathf.Lerp(0.68f, 1.08f, orderT);
            float maxAllowed = (secondWave ? delay2 : 0f) + dur - 0.03f - delay;
            streakLife = Mathf.Clamp(streakLife, 0.10f, Mathf.Max(0.10f, maxAllowed));

            Color col = Color.Lerp(yellowFirstTint, yellowCoreHighlight, Random.Range(0.35f, 0.80f));
            slot.Streaks[i] = new StreakParticle
            {
                Dir = dir,
                PerpA = perpA,
                PerpB = perpB,
                Delay = delay,
                Duration = streakLife,
                StartDist = Random.Range(0.20f, 0.36f),
                InitialSpeed = Random.Range(3.6f, 6.2f),
                Acceleration = Random.Range(40f, 64f),
                BaseLength = Random.Range(0.085f, 0.145f), // 比红色更短、颗粒更紧凑明亮
                LengthPerSpeed = 0.026f,
                HalfWidth = Random.Range(0.026f, 0.044f),
                BaseColor = col,
                FlashOutTail = true // 结束瞬间短促增亮后依次闪灭
            };
            maxLife = Mathf.Max(maxLife, delay + streakLife);
        }

        int shCount = Mathf.Clamp(yellowShardAccentCount, 0, MaxShardsPerBurst);
        slot.ShardCount = shCount;
        for (int i = 0; i < shCount; i++)
        {
            Vector3 dir = SampleFibonacciDirection(i, Mathf.Max(1, shCount), 0.25f);
            float orderT = (float)i / Mathf.Max(1, shCount - 1);
            float delay = (i >= shCount / 2 ? delay2 : 0f) + Random.Range(0f, 0.02f);
            float sDur = Mathf.Lerp(0.22f, dur * 0.88f, orderT);
            bool isSparkle = (i % 3 != 0);
            slot.Shards[i] = new ShardAccentParticle
            {
                Dir = dir,
                Delay = delay,
                Duration = sDur,
                StartDist = Random.Range(0.26f, 0.48f),
                InitialSpeed = Random.Range(1.6f, 3.2f),
                Acceleration = Random.Range(9f, 16f),
                Size = isSparkle ? Random.Range(0.085f, 0.145f) : Random.Range(0.055f, 0.085f),
                SpinDeg = Random.Range(0f, 360f),
                SpinSpeedDeg = Random.Range(-180f, 180f),
                AtlasIdx = isSparkle ? 3 : (i % 3),
                BaseColor = isSparkle ? yellowCoreHighlight : yellowFirstTint,
                TwinkleOut = true
            };
            maxLife = Mathf.Max(maxLife, delay + sDur);
        }

        slot.MaxLifetime = maxLife;
        slot.RootGo.SetActive(true);
        activeBursts.Add(slot);
    }

    private void SpawnShell(
        Material mat,
        Vector3 originWorld,
        Quaternion initialRot,
        float birthTime,
        float duration,
        float rStart,
        float rEnd,
        float brightness,
        float opacity,
        float arcSharpness,
        float ringScale,
        float shellFillAlpha,
        float angleOffsetRad,
        float spinSpeedRad,
        Color tintColor,
        Color coreColor)
    {
        if (mat == null) return;

        GameObject go = shellPool.Count > 0 ? shellPool.Dequeue() : CreateShellInstance(activeShells.Count);
        Transform tr = go.transform;
        tr.position = originWorld;
        tr.rotation = initialRot;
        tr.localScale = Vector3.one * rStart;

        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = mat;

        ActiveShellWave wave = new ActiveShellWave
        {
            Go = go,
            Tr = tr,
            Renderer = mr,
            Mpb = new MaterialPropertyBlock(),
            OriginWorld = originWorld,
            BirthTime = birthTime,
            Duration = duration,
            StartRadius = rStart,
            EndRadius = rEnd,
            Brightness = brightness,
            BaseOpacity = opacity,
            ArcSharpness = arcSharpness,
            RingScale = ringScale,
            ShellFillAlpha = shellFillAlpha,
            AngleOffsetRad = angleOffsetRad,
            SpinSpeedRad = spinSpeedRad,
            TintColor = tintColor,
            CoreColor = coreColor,
            Activated = false
        };

        // 延迟出生（如黄水晶第二波）在出生时刻到来前保持隐藏，不提前占屏
        go.SetActive(false);
        activeShells.Add(wave);
    }

    private void Update()
    {
        if (enableDebugHotkeys)
        {
            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.f7Key.wasPressedThisFrame) TestRedWaveAtPlayer();
                if (kb.f8Key.wasPressedThisFrame) TestYellowWaveAtPlayer();
            }
        }

        float now = Time.time;
        UpdateActiveShells(now);
        UpdateActiveBursts(now);
    }

    private void UpdateActiveShells(float now)
    {
        for (int i = activeShells.Count - 1; i >= 0; i--)
        {
            ActiveShellWave w = activeShells[i];
            float elapsed = now - w.BirthTime;

            if (elapsed < 0f)
            {
                // 尚未到延迟出生时刻（例如黄水晶第二道波 0.08s 延迟）
                continue;
            }

            if (elapsed >= w.Duration)
            {
                // 寿命结束，干净回收到对象池
                w.Go.SetActive(false);
                shellPool.Enqueue(w.Go);
                activeShells.RemoveAt(i);
                continue;
            }

            if (!w.Activated)
            {
                w.Activated = true;
                w.Go.SetActive(true);
            }

            // 严格按照持续加速规律计算当前球壳半径：
            // distanceProgress = 0.15 * p + 0.85 * p * p
            float p = Mathf.Clamp01(elapsed / w.Duration);
            float distanceProgress = 0.15f * p + 0.85f * p * p;
            float radius = w.StartRadius + (w.EndRadius - w.StartRadius) * distanceProgress;

            w.Tr.position = w.OriginWorld;
            w.Tr.localScale = Vector3.one * radius;

            // 透明度独立于半径控制：拾取初帧锐利亮起，中段清晰扫过，后半程边加速边平滑变淡
            float fadeIn = p < 0.07f ? Mathf.Lerp(0.72f, 1.0f, p / 0.07f) : 1.0f;
            float fadeOut = p > 0.42f ? Mathf.Pow(Mathf.Clamp01(1f - (p - 0.42f) / 0.58f), 1.55f) : 1.0f;
            float curOpacity = w.BaseOpacity * fadeIn * fadeOut;

            // 初期瞬亮冲击略高于中后段，强化“拾取瞬间周围亮起一圈锐利弧纹”的观感
            float initialFlashMul = 1.0f + 0.28f * Mathf.Clamp01(1f - p / 0.18f);

            w.Mpb.SetColor(TintColorId, w.TintColor);
            w.Mpb.SetColor(CoreHighlightColorId, w.CoreColor);
            w.Mpb.SetFloat(BrightnessId, w.Brightness * initialFlashMul);
            w.Mpb.SetFloat(OpacityId, curOpacity);
            w.Mpb.SetFloat(ArcSharpnessId, w.ArcSharpness);
            w.Mpb.SetFloat(RingScaleId, w.RingScale);
            w.Mpb.SetFloat(ShellFillAlphaId, w.ShellFillAlpha);
            w.Mpb.SetFloat(FresnelBoostId, 0.30f);
            w.Mpb.SetFloat(AngleOffsetId, w.AngleOffsetRad);
            w.Mpb.SetFloat(SpinPhaseId, elapsed * w.SpinSpeedRad);
            w.Mpb.SetFloat(NearCamFadeId, nearCameraSoftFade);
            w.Renderer.SetPropertyBlock(w.Mpb);

            activeShells[i] = w;
        }
    }

    private void UpdateActiveBursts(float now)
    {
        Camera cam = Camera.main;
        Vector3 camRight = cam != null ? cam.transform.right : Vector3.right;
        Vector3 camUp = cam != null ? cam.transform.up : Vector3.up;

        for (int b = activeBursts.Count - 1; b >= 0; b--)
        {
            ParticleBurstSlot slot = activeBursts[b];
            float burstElapsed = now - slot.SpawnTime;
            if (burstElapsed >= slot.MaxLifetime)
            {
                slot.RootGo.SetActive(false);
                burstPool.Enqueue(slot);
                activeBursts.RemoveAt(b);
                continue;
            }

            // 1. 更新三维速度拖尾（十字双交叉面片：尖端 U=1 严格朝向飞行方向，无论 Game 还是 Scene 侧面都立体清晰）
            int sv = 0;
            for (int i = 0; i < slot.StreakCount; i++)
            {
                StreakParticle sp = slot.Streaks[i];
                float t = burstElapsed - sp.Delay;
                if (t < 0f || t >= sp.Duration)
                {
                    WriteDegenerateStreak(slot.StreakVerts, slot.StreakColors, sv);
                    sv += 8;
                    continue;
                }

                float u = t / sp.Duration;
                // 匀加速向外运动：s = s0 + v0*t + 0.5*a*t^2，瞬时速度 v = v0 + a*t
                float speed = sp.InitialSpeed + sp.Acceleration * t;
                float headDist = sp.StartDist + sp.InitialSpeed * t + 0.5f * sp.Acceleration * t * t;
                float length = sp.BaseLength + speed * sp.LengthPerSpeed;
                float tailDist = Mathf.Max(0.05f, headDist - length);

                float alpha;
                if (sp.FlashOutTail)
                {
                    // 黄色拖尾：更亮，尾段短促闪亮后迅速依次闪灭
                    float flash = u > 0.72f && u < 0.88f ? 1.38f : 1.0f;
                    float fadeIn = Mathf.Clamp01(u / 0.08f);
                    float fadeOut = u > 0.78f ? Mathf.Clamp01(1f - (u - 0.78f) / 0.22f) : 1.0f;
                    alpha = Mathf.Clamp01(fadeIn * fadeOut * flash);
                }
                else
                {
                    // 红色拖尾：迅疾拉长，先于球壳干脆收束淡出
                    float fadeIn = Mathf.Clamp01(u / 0.10f);
                    float fadeOut = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Max(0f, u - 0.35f) / 0.65f), 1.4f);
                    alpha = Mathf.Clamp01(fadeIn * fadeOut);
                }

                Vector3 tailPos = sp.Dir * tailDist;
                Vector3 headPos = sp.Dir * headDist;
                float hw = sp.HalfWidth * Mathf.Lerp(1.15f, 0.72f, u);
                Color32 col32 = ToColor32WithAlpha(sp.BaseColor, alpha);

                // 交叉面片 A（法向 PerpB，展宽 PerpA）：UV 左侧 U=0 为尾，右侧 U=1 为贴图尖端（朝向飞行方向！）
                Vector3 offA = sp.PerpA * hw;
                slot.StreakVerts[sv + 0] = tailPos - offA;
                slot.StreakVerts[sv + 1] = headPos - offA;
                slot.StreakVerts[sv + 2] = headPos + offA;
                slot.StreakVerts[sv + 3] = tailPos + offA;

                // 交叉面片 B（法向 PerpA，展宽 PerpB）
                Vector3 offB = sp.PerpB * hw;
                slot.StreakVerts[sv + 4] = tailPos - offB;
                slot.StreakVerts[sv + 5] = headPos - offB;
                slot.StreakVerts[sv + 6] = headPos + offB;
                slot.StreakVerts[sv + 7] = tailPos + offB;

                for (int k = 0; k < 8; k++) slot.StreakColors[sv + k] = col32;
                sv += 8;
            }
            for (; sv < MaxStreaksPerBurst * 8; sv += 8)
            {
                WriteDegenerateStreak(slot.StreakVerts, slot.StreakColors, sv);
            }
            slot.StreakMesh.vertices = slot.StreakVerts;
            slot.StreakMesh.colors32 = slot.StreakColors;
            slot.StreakMesh.bounds = new Bounds(Vector3.zero, Vector3.one * (endRadius * 2.4f));

            // 2. 更新少量三维碎晶与四角闪点（shardEffect.png）
            int qv = 0;
            for (int i = 0; i < slot.ShardCount; i++)
            {
                ShardAccentParticle p = slot.Shards[i];
                float t = burstElapsed - p.Delay;
                if (t < 0f || t >= p.Duration)
                {
                    WriteDegenerateQuad(slot.ShardVerts, slot.ShardColors, qv);
                    qv += 4;
                    continue;
                }

                float u = t / p.Duration;
                float dist = p.StartDist + p.InitialSpeed * t + 0.5f * p.Acceleration * t * t;
                Vector3 center = p.Dir * dist;

                float scale = u < 0.16f
                    ? Mathf.Lerp(0.35f, 1.12f, u / 0.16f)
                    : Mathf.Lerp(1.12f, 0.30f, (u - 0.16f) / 0.84f);

                float alpha;
                if (p.TwinkleOut)
                {
                    float glint = u > 0.68f && u < 0.86f ? 1.35f : 1.0f;
                    alpha = Mathf.Clamp01((1f - Mathf.Pow(u, 1.8f)) * glint);
                }
                else
                {
                    alpha = Mathf.Clamp01(1f - Mathf.Pow(u, 1.5f));
                }

                float rad = (p.SpinDeg + p.SpinSpeedDeg * t) * Mathf.Deg2Rad;
                float cs = Mathf.Cos(rad);
                float sn = Mathf.Sin(rad);
                float hs = p.Size * scale * 0.5f;
                Vector3 rVec = (camRight * cs + camUp * sn) * hs;
                Vector3 uVec = (-camRight * sn + camUp * cs) * hs;

                slot.ShardVerts[qv + 0] = center - rVec - uVec;
                slot.ShardVerts[qv + 1] = center + rVec - uVec;
                slot.ShardVerts[qv + 2] = center + rVec + uVec;
                slot.ShardVerts[qv + 3] = center - rVec + uVec;

                Rect uvR = ShardAtlasUVs[Mathf.Clamp(p.AtlasIdx, 0, ShardAtlasUVs.Length - 1)];
                slot.ShardUVs[qv + 0] = new Vector2(uvR.xMin, uvR.yMin);
                slot.ShardUVs[qv + 1] = new Vector2(uvR.xMax, uvR.yMin);
                slot.ShardUVs[qv + 2] = new Vector2(uvR.xMax, uvR.yMax);
                slot.ShardUVs[qv + 3] = new Vector2(uvR.xMin, uvR.yMax);

                Color32 col32 = ToColor32WithAlpha(p.BaseColor, alpha);
                for (int k = 0; k < 4; k++) slot.ShardColors[qv + k] = col32;
                qv += 4;
            }
            for (; qv < MaxShardsPerBurst * 4; qv += 4)
            {
                WriteDegenerateQuad(slot.ShardVerts, slot.ShardColors, qv);
            }
            slot.ShardMesh.vertices = slot.ShardVerts;
            slot.ShardMesh.uv = slot.ShardUVs;
            slot.ShardMesh.colors32 = slot.ShardColors;
            slot.ShardMesh.bounds = new Bounds(Vector3.zero, Vector3.one * (endRadius * 2.4f));
        }
    }

    private GameObject CreateShellInstance(int index)
    {
        GameObject go = new GameObject($"CrystalWaveShell_{index:00}", typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(poolContainer, false);

        MeshFilter mf = go.GetComponent<MeshFilter>();
        mf.sharedMesh = unitSphereMesh;

        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        mr.sharedMaterial = redWaveShellMaterial;

        go.SetActive(false);
        return go;
    }

    private ParticleBurstSlot RentBurstSlot()
    {
        return burstPool.Count > 0 ? burstPool.Dequeue() : CreateBurstSlot(activeBursts.Count);
    }

    private ParticleBurstSlot CreateBurstSlot(int index)
    {
        GameObject root = new GameObject($"CrystalWaveParticles_{index:00}");
        root.transform.SetParent(poolContainer, false);

        // 1. 速度拖尾批处理网格
        GameObject streakGo = new GameObject("VelocityStreaks", typeof(MeshFilter), typeof(MeshRenderer));
        streakGo.transform.SetParent(root.transform, false);

        Mesh streakMesh = new Mesh { name = $"VelocityStreaksMesh_{index:00}" };
        streakMesh.MarkDynamic();
        int sVertCount = MaxStreaksPerBurst * 8;
        int sIdxCount = MaxStreaksPerBurst * 12;
        Vector3[] sVerts = new Vector3[sVertCount];
        Vector2[] sUVs = new Vector2[sVertCount];
        Color32[] sCols = new Color32[sVertCount];
        int[] sIdxs = new int[sIdxCount];

        for (int i = 0; i < MaxStreaksPerBurst; i++)
        {
            int vb = i * 8;
            int ib = i * 12;
            // 面片 A & B：U=0 为尾部，U=1 为尖端朝右的头部
            for (int plane = 0; plane < 2; plane++)
            {
                int pvb = vb + plane * 4;
                int pib = ib + plane * 6;
                sUVs[pvb + 0] = new Vector2(0f, 0f);
                sUVs[pvb + 1] = new Vector2(1f, 0f);
                sUVs[pvb + 2] = new Vector2(1f, 1f);
                sUVs[pvb + 3] = new Vector2(0f, 1f);

                sIdxs[pib + 0] = pvb + 0;
                sIdxs[pib + 1] = pvb + 1;
                sIdxs[pib + 2] = pvb + 2;
                sIdxs[pib + 3] = pvb + 0;
                sIdxs[pib + 4] = pvb + 2;
                sIdxs[pib + 5] = pvb + 3;
            }
        }
        streakMesh.vertices = sVerts;
        streakMesh.uv = sUVs;
        streakMesh.colors32 = sCols;
        streakMesh.triangles = sIdxs;
        streakGo.GetComponent<MeshFilter>().sharedMesh = streakMesh;

        MeshRenderer streakMr = streakGo.GetComponent<MeshRenderer>();
        streakMr.sharedMaterial = velocityStreakMaterial;
        streakMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        streakMr.receiveShadows = false;
        streakMr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        streakMr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

        // 2. 少量三维碎晶/闪点批处理网格
        GameObject shardGo = new GameObject("ShardAccents", typeof(MeshFilter), typeof(MeshRenderer));
        shardGo.transform.SetParent(root.transform, false);

        Mesh shardMesh = new Mesh { name = $"ShardAccentsMesh_{index:00}" };
        shardMesh.MarkDynamic();
        int qVertCount = MaxShardsPerBurst * 4;
        int qIdxCount = MaxShardsPerBurst * 6;
        Vector3[] qVerts = new Vector3[qVertCount];
        Vector2[] qUVs = new Vector2[qVertCount];
        Color32[] qCols = new Color32[qVertCount];
        int[] qIdxs = new int[qIdxCount];

        for (int i = 0; i < MaxShardsPerBurst; i++)
        {
            int vb = i * 4;
            int ib = i * 6;
            qIdxs[ib + 0] = vb + 0;
            qIdxs[ib + 1] = vb + 1;
            qIdxs[ib + 2] = vb + 2;
            qIdxs[ib + 3] = vb + 0;
            qIdxs[ib + 4] = vb + 2;
            qIdxs[ib + 5] = vb + 3;
        }
        shardMesh.vertices = qVerts;
        shardMesh.uv = qUVs;
        shardMesh.colors32 = qCols;
        shardMesh.triangles = qIdxs;
        shardGo.GetComponent<MeshFilter>().sharedMesh = shardMesh;

        MeshRenderer shardMr = shardGo.GetComponent<MeshRenderer>();
        shardMr.sharedMaterial = shardAccentMaterial;
        shardMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        shardMr.receiveShadows = false;
        shardMr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        shardMr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

        root.SetActive(false);

        return new ParticleBurstSlot
        {
            RootGo = root,
            RootTr = root.transform,
            StreakMesh = streakMesh,
            StreakRenderer = streakMr,
            ShardMesh = shardMesh,
            ShardRenderer = shardMr,
            Streaks = new StreakParticle[MaxStreaksPerBurst],
            Shards = new ShardAccentParticle[MaxShardsPerBurst],
            StreakVerts = sVerts,
            StreakUVs = sUVs,
            StreakColors = sCols,
            StreakIndices = sIdxs,
            ShardVerts = qVerts,
            ShardUVs = qUVs,
            ShardColors = qCols,
            ShardIndices = qIdxs
        };
    }

    private static void WriteDegenerateStreak(Vector3[] verts, Color32[] cols, int baseIdx)
    {
        Color32 zeroCol = new Color32(0, 0, 0, 0);
        for (int k = 0; k < 8; k++)
        {
            verts[baseIdx + k] = Vector3.zero;
            cols[baseIdx + k] = zeroCol;
        }
    }

    private static void WriteDegenerateQuad(Vector3[] verts, Color32[] cols, int baseIdx)
    {
        Color32 zeroCol = new Color32(0, 0, 0, 0);
        for (int k = 0; k < 4; k++)
        {
            verts[baseIdx + k] = Vector3.zero;
            cols[baseIdx + k] = zeroCol;
        }
    }

    private static Color32 ToColor32WithAlpha(Color c, float alpha)
    {
        byte r = (byte)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
        byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
        byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
        byte a = (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * 255f), 0, 255);
        return new Color32(r, g, b, a);
    }

    private static Vector3 SampleFibonacciDirection(int index, int total, float jitter)
    {
        float y = 1f - ((index + 0.5f) / Mathf.Max(1, total)) * 2f;
        float radiusAtY = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
        float theta = index * 2.39996323f; // 黄金角均匀分布于完整三维球面
        Vector3 baseDir = new Vector3(Mathf.Cos(theta) * radiusAtY, y, Mathf.Sin(theta) * radiusAtY);
        Vector3 jittered = baseDir + Random.insideUnitSphere * jitter;
        return jittered.sqrMagnitude > 0.0001f ? jittered.normalized : baseDir;
    }

    private static void BuildOrthonormalBasis(Vector3 forward, out Vector3 perpA, out Vector3 perpB)
    {
        Vector3 upRef = Mathf.Abs(forward.y) > 0.92f ? Vector3.right : Vector3.up;
        perpA = Vector3.Cross(forward, upRef).normalized;
        perpB = Vector3.Cross(forward, perpA).normalized;
    }

    /// <summary>
    /// 生成半径严格为 1.0 米的平滑三维 UV 球体网格，确保 localScale 直接等于真实世界半径（米）。
    /// </summary>
    private static Mesh BuildSmoothUnitSphere(int lonSegments, int latSegments)
    {
        Mesh mesh = new Mesh { name = "CrystalWaveUnitSphere" };
        int vertCount = (lonSegments + 1) * (latSegments + 1);
        Vector3[] verts = new Vector3[vertCount];
        Vector3[] norms = new Vector3[vertCount];
        Vector2[] uvs = new Vector2[vertCount];

        int v = 0;
        for (int lat = 0; lat <= latSegments; lat++)
        {
            float vFrac = (float)lat / latSegments;
            float phi = vFrac * Mathf.PI;
            float sinPhi = Mathf.Sin(phi);
            float cosPhi = Mathf.Cos(phi);

            for (int lon = 0; lon <= lonSegments; lon++)
            {
                float uFrac = (float)lon / lonSegments;
                float theta = uFrac * Mathf.PI * 2f;
                Vector3 dir = new Vector3(sinPhi * Mathf.Cos(theta), cosPhi, sinPhi * Mathf.Sin(theta));
                verts[v] = dir;
                norms[v] = dir;
                uvs[v] = new Vector2(uFrac, 1f - vFrac);
                v++;
            }
        }

        int[] tris = new int[lonSegments * latSegments * 6];
        int t = 0;
        int rowStride = lonSegments + 1;
        for (int lat = 0; lat < latSegments; lat++)
        {
            for (int lon = 0; lon < lonSegments; lon++)
            {
                int i0 = lat * rowStride + lon;
                int i1 = i0 + 1;
                int i2 = i0 + rowStride;
                int i3 = i2 + 1;

                tris[t++] = i0;
                tris[t++] = i1;
                tris[t++] = i2;
                tris[t++] = i1;
                tris[t++] = i3;
                tris[t++] = i2;
            }
        }

        mesh.vertices = verts;
        mesh.normals = norms;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
        return mesh;
    }
}
