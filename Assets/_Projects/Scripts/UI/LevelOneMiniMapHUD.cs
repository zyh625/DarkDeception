using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public enum MiniMapZoomLevel
{
    Max = 0,     // 默认最大化：范围最小、内容最大、细节最丰富（含水晶轻微浮动与闪烁）
    Medium = 1,  // 中等：范围适中、不显示细节（静态圆点/方框，隐藏电梯）
    Min = 2      // 最小化：范围最广、不显示细节（紧凑静态圆点/方框，隐藏电梯）
}

/// <summary>
/// 第一关手持小地图控制器（3 档缩放版）：
/// - 默认不显示；按 Space 在左下角播放「取出/收回」动画，取出完成后才在上方显示剩余水晶计数器；
/// - 地图视口与内容尺寸自动匹配玩家在场景中调整好的 MiniMapPanel 大小，呈偏圆形且四周柔和渐隐；
/// - 按 Z 在 3 档缩放间循环（默认 Max 最大化 -> Medium 中等 -> Min 最小化 -> Max）：
///   * Max（默认最大化）：视野更小、内容更大、显示全部精细图标，且地图上的水晶带有错开相位的轻微浮动与星辉闪烁细节；
///   * Medium（中等）与 Min（最小化）：不显示细节，仅显示静态圆点/方框（玩家黑点、水晶紫点、猴子红点、雕像灰框，电梯隐藏）；
/// - 复用 LevelOne 状态驱动目标高亮（收齐普通碎片高亮雕像；启动雕像解锁出口高亮电梯）。
/// </summary>
public class LevelOneMiniMapHUD : MonoBehaviour
{
    private static readonly int ClipCenterId = Shader.PropertyToID("_ClipCenter");
    private static readonly int ClipRadiiId = Shader.PropertyToID("_ClipRadii");
    private static readonly int ClipAxesId = Shader.PropertyToID("_ClipAxes");

    [Header("关卡、计数器与真实世界参考点")]
    [SerializeField] private LevelOne levelOne;
    [SerializeField] private ShardCounterHUD shardCounterHUD;
    [SerializeField] private Transform statueTarget;
    [SerializeField] private Transform elevatorTarget;
    [SerializeField] private Vector3 elevatorCenterLocalOffset = new Vector3(0f, 0f, 0.45f);

    [Header("迷宫世界坐标范围（80×80 真实迷宫）")]
    [SerializeField] private Vector2 worldMin = new Vector2(-39f, -41f);
    [SerializeField] private Vector2 worldMax = new Vector2(41f, 39f);

    [Header("3 档局部视野范围（单位：米，越小内容越大）")]
    [SerializeField] private float maxZoomVisibleMeters = 10.5f;    // 第 1 档：最大化（默认，视野更小、内容更大、细节丰富）
    [SerializeField] private float mediumZoomVisibleMeters = 22f;   // 第 2 档：中等（简化圆点，无细节）
    [SerializeField] private float minZoomVisibleMeters = 36f;      // 第 3 档：最小化（视野较广，紧凑简化圆点，无细节）
    [SerializeField] private float zoomLerpSpeed = 10f;
    [SerializeField] private bool rotateMapWithPlayer = true;       // 地图随玩家转向旋转（地图上方始终为面朝方向）
    [SerializeField] private float mapRotationSmoothSpeed = 26f;    // 地图转向平滑速度（<=0 为零延迟严格锁定）
    [SerializeField, Range(0.30f, 0.85f)] private float radialInnerFadeStart = 0.60f;
    [SerializeField] private Vector2 ovalRadiiScale = new Vector2(0.98f, 0.98f);

    [Header("Space 取出 / 收回动画参数")]
    [SerializeField] private float equipDuration = 0.44f;
    [SerializeField] private float holsterDuration = 0.34f;
    [SerializeField] private float hiddenOffsetY = -460f;
    [SerializeField] private float hiddenTiltDeg = 11f;

    [Header("小地图 UI 结构")]
    [SerializeField] private RectTransform mapVisualRoot;
    [SerializeField] private CanvasGroup mapCanvasGroup;
    [SerializeField] private RectTransform mapViewport;
    [SerializeField] private RectTransform mapContent;
    [SerializeField] private Image mazeLayoutImage;
    [SerializeField] private RectTransform shardsContainer;
    [SerializeField] private RectTransform enemiesContainer;
    [SerializeField] private RectTransform playerMarker;
    [SerializeField] private Image playerIcon;
    [SerializeField] private RectTransform statueMarker;
    [SerializeField] private Image statueIcon;
    [SerializeField] private Image statueHighlightRing;
    [SerializeField] private RectTransform elevatorMarker;
    [SerializeField] private Image elevatorIcon;
    [SerializeField] private Image elevatorHighlightRing;

    [Header("材质与双模式图标贴图")]
    [SerializeField] private Material radialFadeMaterial;
    [SerializeField] private Sprite playerArrowSprite;
    [SerializeField] private Sprite shardStarSprite;
    [SerializeField] private Sprite enemyDetailSprite;
    [SerializeField] private Sprite statueDetailSprite;
    [SerializeField] private Sprite elevatorDetailSprite;
    [SerializeField] private Sprite dotCircleSprite;
    [SerializeField] private Sprite squareDotSprite;

    [Header("第 1 档（最大化 / 精细细节）屏幕像素尺寸")]
    [SerializeField] private float maxPlayerSize = 24f;
    [SerializeField] private float maxShardSize = 20f;
    [SerializeField] private float maxEnemySize = 24f;
    [SerializeField] private float maxStatueSize = 28f;
    [SerializeField] private float maxElevatorSize = 28f;

    [Header("第 1 档水晶轻微浮动与闪烁细节")]
    [SerializeField] private float shardFloatAmplitudePx = 2.5f;
    [SerializeField] private float shardFloatSpeed = 2.5f;
    [SerializeField] private float shardTwinkleSpeed = 3.4f;
    [SerializeField] private float shardScaleMin = 0.88f;
    [SerializeField] private float shardScaleMax = 1.18f;
    [SerializeField] private Color detailShardSparkleColor = new Color(0.96f, 0.38f, 1.00f, 1.00f);

    [Header("第 2 档（中等 / 无细节简化点）屏幕像素尺寸")]
    [SerializeField] private float mediumPlayerDotSize = 10.5f;
    [SerializeField] private float mediumShardDotSize = 8.5f;
    [SerializeField] private float mediumEnemyDotSize = 10.0f;
    [SerializeField] private float mediumStatueSquareSize = 11.5f;

    [Header("第 3 档（最小化 / 无细节紧凑点）屏幕像素尺寸")]
    [SerializeField] private float minPlayerDotSize = 8.0f;
    [SerializeField] private float minShardDotSize = 6.2f;
    [SerializeField] private float minEnemyDotSize = 7.5f;
    [SerializeField] private float minStatueSquareSize = 8.8f;

    [Header("状态与圆点配色")]
    [SerializeField] private Color detailPlayerColor = new Color(0.12f, 0.10f, 0.12f, 1.00f);
    [SerializeField] private Color simplifiedPlayerColor = new Color(0.05f, 0.05f, 0.06f, 1.00f);
    [SerializeField] private Color detailShardColor = new Color(0.52f, 0.14f, 0.90f, 0.94f);
    [SerializeField] private Color simplifiedShardColor = new Color(0.60f, 0.20f, 0.96f, 0.96f);
    [SerializeField] private Color simplifiedEnemyColor = new Color(0.92f, 0.16f, 0.18f, 1.00f);
    [SerializeField] private Color subduedLandmarkColor = new Color(0.38f, 0.36f, 0.40f, 0.90f);
    [SerializeField] private Color simplifiedStatueGreyColor = new Color(0.48f, 0.48f, 0.52f, 0.96f);
    [SerializeField] private Color statueHighlightColor = new Color(1.00f, 0.84f, 0.36f, 1.00f);
    [SerializeField] private Color statueRingColor = new Color(0.95f, 0.56f, 1.00f, 0.90f);
    [SerializeField] private Color elevatorHighlightColor = new Color(0.42f, 0.96f, 1.00f, 1.00f);
    [SerializeField] private Color elevatorRingColor = new Color(0.32f, 0.92f, 1.00f, 0.92f);
    [SerializeField] private float pulseSpeed = 4.2f;

    private readonly List<RectTransform> shardRects = new();
    private readonly List<Image> shardImages = new();
    private readonly List<Vector2> shardBasePositions = new();
    private readonly List<float> shardPhases = new();
    private readonly List<RectTransform> enemyRects = new();
    private readonly List<Image> enemyImages = new();
    private readonly Vector3[] viewportCorners = new Vector3[4];

    private Canvas rootCanvas;
    private Material runtimeFadeMat;
    private LevelObjectiveState currentState = LevelObjectiveState.CollectShards;
    private bool markersBuilt;

    private bool isMapOpen;
    private MiniMapZoomLevel currentZoomLevel = MiniMapZoomLevel.Max;
    private float equipProgress; // 0 = 收起隐藏, 1 = 完全取出
    private bool counterRevealedForCurrentOpen;
    private float currentZoomScale = 1f;
    private float currentMapYaw;
    private bool hasInitializedMapYaw;
    private float baseContentSize = 2616f;
    private float lastMeasuredViewportSpan = -1f;

    public bool IsMapOpen => isMapOpen;
    public MiniMapZoomLevel CurrentZoomLevel => currentZoomLevel;
    public bool IsDetailedMode => currentZoomLevel == MiniMapZoomLevel.Max;

    private void Awake()
    {
        AutoBindMissingHierarchyRefs();

        if (radialFadeMaterial == null && mazeLayoutImage != null && mazeLayoutImage.material != null)
        {
            radialFadeMaterial = mazeLayoutImage.material;
        }
        if (radialFadeMaterial != null)
        {
            runtimeFadeMat = new Material(radialFadeMaterial);
            ApplyRadialMaterialToStaticGraphics();
        }

        if (mapCanvasGroup == null && mapVisualRoot != null)
        {
            mapCanvasGroup = mapVisualRoot.GetComponent<CanvasGroup>();
            if (mapCanvasGroup == null)
            {
                mapCanvasGroup = mapVisualRoot.gameObject.AddComponent<CanvasGroup>();
            }
        }

        ConfigureBaseContentDimensions();

        // 默认不显示地图与上方计数器，默认缩放档位为最大化（Max）
        isMapOpen = false;
        currentZoomLevel = MiniMapZoomLevel.Max;
        equipProgress = 0f;
        counterRevealedForCurrentOpen = false;
        currentZoomScale = 1f;
        ApplyEquipTransform(0f);

        if (mapVisualRoot != null)
        {
            mapVisualRoot.gameObject.SetActive(false);
        }
        if (shardCounterHUD != null)
        {
            shardCounterHUD.SetVisible(false, true);
        }
    }

    private void AutoBindMissingHierarchyRefs()
    {
        if (mapVisualRoot == null) mapVisualRoot = transform.Find("MapVisualRoot") as RectTransform;
        if (mapVisualRoot != null && mapViewport == null) mapViewport = mapVisualRoot.Find("MapViewport") as RectTransform;
        if (mapViewport != null && mapContent == null) mapContent = mapViewport.Find("MapContent") as RectTransform;
        if (mapContent != null && mazeLayoutImage == null)
        {
            Transform t = mapContent.Find("MazeLayout");
            if (t != null) mazeLayoutImage = t.GetComponent<Image>();
        }
        if (mapContent != null && shardsContainer == null) shardsContainer = mapContent.Find("ShardsLayer") as RectTransform;
        if (mapContent != null && enemiesContainer == null) enemiesContainer = mapContent.Find("EnemiesLayer") as RectTransform;
        if (mapViewport != null && playerMarker == null) playerMarker = mapViewport.Find("PlayerMarker") as RectTransform;
        if (playerMarker != null && playerIcon == null) playerIcon = playerMarker.GetComponent<Image>();
        if (mapContent != null && statueMarker == null) statueMarker = mapContent.Find("StatueMarker") as RectTransform;
        if (statueMarker != null && statueIcon == null)
        {
            Transform t = statueMarker.Find("StatueIcon");
            if (t != null) statueIcon = t.GetComponent<Image>();
        }
        if (statueMarker != null && statueHighlightRing == null)
        {
            Transform t = statueMarker.Find("StatueHighlightRing");
            if (t != null) statueHighlightRing = t.GetComponent<Image>();
        }
        if (mapContent != null && elevatorMarker == null) elevatorMarker = mapContent.Find("ElevatorMarker") as RectTransform;
        if (elevatorMarker != null && elevatorIcon == null)
        {
            Transform t = elevatorMarker.Find("ElevatorIcon");
            if (t != null) elevatorIcon = t.GetComponent<Image>();
        }
        if (elevatorMarker != null && elevatorHighlightRing == null)
        {
            Transform t = elevatorMarker.Find("ElevatorHighlightRing");
            if (t != null) elevatorHighlightRing = t.GetComponent<Image>();
        }
    }

    private void OnEnable()
    {
        if (levelOne == null)
        {
            levelOne = FindAnyObjectByType<LevelOne>();
        }
        if (shardCounterHUD == null)
        {
            shardCounterHUD = FindAnyObjectByType<ShardCounterHUD>();
        }

        if (levelOne != null)
        {
            if (statueTarget == null) statueTarget = levelOne.StatueTransform;
            if (elevatorTarget == null) elevatorTarget = levelOne.ElevatorTransform;
            levelOne.OnObjectiveStateChanged += HandleObjectiveStateChanged;
        }
    }

    private void Start()
    {
        ConfigureBaseContentDimensions();
        BuildDynamicMarkers();

        if (levelOne != null)
        {
            HandleObjectiveStateChanged(levelOne.ObjectiveState);
        }
        else
        {
            ApplyAllMarkerVisualStyles();
        }

        if (shardCounterHUD != null)
        {
            shardCounterHUD.SetVisible(false, true);
        }
    }

    private void OnDisable()
    {
        if (levelOne != null)
        {
            levelOne.OnObjectiveStateChanged -= HandleObjectiveStateChanged;
        }
    }

    private void OnDestroy()
    {
        if (runtimeFadeMat != null)
        {
            Destroy(runtimeFadeMat);
        }
    }

    private void Update()
    {
        HandleInput();
        UpdateEquipAnimation();
        UpdateZoomScale();
    }

    private void LateUpdate()
    {
        if (mapVisualRoot == null || !mapVisualRoot.gameObject.activeSelf) return;

        // 视口尺寸（如首帧布局完成或窗口缩放）变化时自动同步内容底图与水晶基准坐标
        RefreshContentDimensionsIfNeeded();

        UpdateRadialShaderClipRect();
        UpdateMapScrollAndPositions();
        UpdateMarkerScreenSizes();
        UpdateMaxModeShardDetails();
        UpdateHighlightPulse();
    }

    private void HandleInput()
    {
        if (levelOne != null && !levelOne.CanUseHUD) return;
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        // 空格键：取出 / 收回左下角小地图
        if (kb.spaceKey.wasPressedThisFrame)
        {
            ToggleMap();
        }

        // Z 键：地图开启时在 3 档缩放间循环（Max -> Medium -> Min -> Max）
        if (isMapOpen && kb.zKey.wasPressedThisFrame)
        {
            CycleZoomLevel();
        }
    }

    public void ToggleMap()
    {
        isMapOpen = !isMapOpen;

        if (isMapOpen)
        {
            counterRevealedForCurrentOpen = false;
            if (mapVisualRoot != null && !mapVisualRoot.gameObject.activeSelf)
            {
                mapVisualRoot.gameObject.SetActive(true);
            }
            if (shardCounterHUD != null)
            {
                shardCounterHUD.SetVisible(false, true);
            }
            RefreshContentDimensionsIfNeeded();
            SnapMapYawToPlayer();
            ApplyAllMarkerVisualStyles();
        }
        else
        {
            counterRevealedForCurrentOpen = false;
            if (shardCounterHUD != null)
            {
                shardCounterHUD.SetVisible(false, false);
            }
        }
    }

    /// <summary>
    /// 播放处决动画或终局演出时强制立刻关闭左下角小地图与上方水晶计数器。
    /// </summary>
    public void ForceCloseImmediate()
    {
        isMapOpen = false;
        equipProgress = 0f;
        counterRevealedForCurrentOpen = false;
        ApplyEquipTransform(0f);

        if (mapVisualRoot != null)
        {
            mapVisualRoot.gameObject.SetActive(false);
        }
        if (shardCounterHUD != null)
        {
            shardCounterHUD.SetVisible(false, true);
        }
    }

    public void CycleZoomLevel()
    {
        MiniMapZoomLevel next = currentZoomLevel switch
        {
            MiniMapZoomLevel.Max => MiniMapZoomLevel.Medium,
            MiniMapZoomLevel.Medium => MiniMapZoomLevel.Min,
            _ => MiniMapZoomLevel.Max
        };
        SetZoomLevel(next);
    }

    public void SetZoomLevel(MiniMapZoomLevel level)
    {
        if (currentZoomLevel == level) return;
        currentZoomLevel = level;
        ApplyAllMarkerVisualStyles();
    }

    private void UpdateEquipAnimation()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        float target = isMapOpen ? 1f : 0f;
        if (!Mathf.Approximately(equipProgress, target))
        {
            float dur = isMapOpen ? equipDuration : holsterDuration;
            float step = dur > 0.001f ? dt / dur : 1f;
            equipProgress = Mathf.MoveTowards(equipProgress, target, step);
            ApplyEquipTransform(equipProgress);

            // 取出动画接近完成时，才在地图上方显示剩余水晶计数
            if (isMapOpen && !counterRevealedForCurrentOpen && equipProgress >= 0.90f)
            {
                counterRevealedForCurrentOpen = true;
                if (shardCounterHUD != null)
                {
                    shardCounterHUD.SetVisible(true, false);
                }
            }

            // 完全收回后关闭视觉节点，零开销且完全不可见
            if (!isMapOpen && equipProgress <= 0.001f && mapVisualRoot != null)
            {
                mapVisualRoot.gameObject.SetActive(false);
            }
        }
    }

    private void ApplyEquipTransform(float p)
    {
        if (mapVisualRoot == null) return;

        // 根据当前面板实际高度保证隐藏位移足够把整张地图完全收至屏幕下方之外
        RectTransform panelRt = transform as RectTransform;
        float panelHeight = panelRt != null && panelRt.rect.height > 10f
            ? panelRt.rect.height
            : (panelRt != null ? panelRt.sizeDelta.y : 435f);
        float safeHiddenY = Mathf.Min(hiddenOffsetY, -(panelHeight + 36f));

        float easeOut = 1f - Mathf.Pow(1f - p, 3f);
        float overshoot = Mathf.Sin(p * Mathf.PI) * (isMapOpen ? 0.045f : 0.015f);

        float posY = Mathf.Lerp(safeHiddenY, 0f, easeOut) + overshoot * 150f;
        float scaleX = Mathf.Lerp(0.86f, 1.0f, easeOut) + overshoot * 0.25f;
        float scaleY = Mathf.Lerp(0.24f, 1.0f, easeOut) + overshoot * 0.85f;
        float rotZ = Mathf.Lerp(hiddenTiltDeg, 0f, easeOut) - overshoot * 32f;

        mapVisualRoot.anchoredPosition = new Vector2(0f, posY);
        mapVisualRoot.localScale = new Vector3(scaleX, scaleY, 1f);
        mapVisualRoot.localRotation = Quaternion.Euler(0f, 0f, rotZ);

        if (mapCanvasGroup != null)
        {
            mapCanvasGroup.alpha = Mathf.Clamp01(p * 1.45f);
        }
    }

    private float GetTargetZoomScale()
    {
        float maxM = Mathf.Max(5f, maxZoomVisibleMeters);
        float targetMeters = currentZoomLevel switch
        {
            MiniMapZoomLevel.Max => maxM,
            MiniMapZoomLevel.Medium => Mathf.Max(maxM + 2f, mediumZoomVisibleMeters),
            _ => Mathf.Max(maxM + 4f, minZoomVisibleMeters)
        };
        return Mathf.Clamp(maxM / targetMeters, 0.15f, 1f);
    }

    private void UpdateZoomScale()
    {
        float targetScale = GetTargetZoomScale();
        if (!Mathf.Approximately(currentZoomScale, targetScale))
        {
            currentZoomScale = Mathf.Lerp(currentZoomScale, targetScale, 1f - Mathf.Exp(-zoomLerpSpeed * Time.deltaTime));
            if (Mathf.Abs(currentZoomScale - targetScale) < 0.001f)
            {
                currentZoomScale = targetScale;
            }
        }
    }

    private float MeasureViewportSpan()
    {
        if (mapViewport == null) return 344f;

        Rect r = mapViewport.rect;
        if (r.width > 20f && r.height > 20f)
        {
            return Mathf.Min(r.width, r.height);
        }

        // 若首帧尚未完成布局，按锚点与父级 MiniMapPanel 的 sizeDelta 估算真实视口尺寸
        RectTransform panelRt = transform as RectTransform;
        if (panelRt != null)
        {
            Vector2 pSz = panelRt.rect.width > 20f ? panelRt.rect.size : panelRt.sizeDelta;
            float estW = (mapViewport.anchorMax.x - mapViewport.anchorMin.x) * pSz.x + mapViewport.sizeDelta.x;
            float estH = (mapViewport.anchorMax.y - mapViewport.anchorMin.y) * pSz.y + mapViewport.sizeDelta.y;
            float minEst = Mathf.Min(estW, estH);
            if (minEst > 20f) return minEst;
        }

        float fallback = Mathf.Min(mapViewport.sizeDelta.x, mapViewport.sizeDelta.y);
        return fallback > 20f ? fallback : 344f;
    }

    private void ConfigureBaseContentDimensions()
    {
        if (mapViewport == null || mapContent == null) return;

        float vpSpan = MeasureViewportSpan();
        lastMeasuredViewportSpan = vpSpan;

        float worldSpan = Mathf.Max(1f, worldMax.x - worldMin.x);
        float pxPerMeter = vpSpan / Mathf.Max(5f, maxZoomVisibleMeters);
        baseContentSize = worldSpan * pxPerMeter;
        mapContent.sizeDelta = new Vector2(baseContentSize, baseContentSize);

        RecacheShardBasePositions();
    }

    private void RefreshContentDimensionsIfNeeded()
    {
        if (mapViewport == null || mapContent == null) return;
        float vpSpan = MeasureViewportSpan();
        if (Mathf.Abs(vpSpan - lastMeasuredViewportSpan) > 1.5f)
        {
            ConfigureBaseContentDimensions();
        }
    }

    private void RecacheShardBasePositions()
    {
        if (!markersBuilt || levelOne == null) return;
        IReadOnlyList<Transform> spawnedShards = levelOne.SpawnedShards;
        int count = Mathf.Min(shardRects.Count, spawnedShards.Count);
        for (int i = 0; i < count; i++)
        {
            if (spawnedShards[i] != null)
            {
                Vector2 bp = WorldToContentPosition(spawnedShards[i].position);
                shardBasePositions[i] = bp;
                if (!IsDetailedMode)
                {
                    shardRects[i].anchoredPosition = bp;
                }
            }
        }
    }

    private void ApplyRadialMaterialToStaticGraphics()
    {
        if (runtimeFadeMat == null) return;

        if (mazeLayoutImage != null) mazeLayoutImage.material = runtimeFadeMat;
        if (statueIcon != null) statueIcon.material = runtimeFadeMat;
        if (statueHighlightRing != null) statueHighlightRing.material = runtimeFadeMat;
        if (elevatorIcon != null) elevatorIcon.material = runtimeFadeMat;
        if (elevatorHighlightRing != null) elevatorHighlightRing.material = runtimeFadeMat;
        if (playerIcon != null) playerIcon.material = runtimeFadeMat;
    }

    private void UpdateRadialShaderClipRect()
    {
        if (runtimeFadeMat == null || mapViewport == null) return;

        if (rootCanvas == null)
        {
            Canvas c = GetComponentInParent<Canvas>();
            rootCanvas = c != null ? c.rootCanvas : null;
        }

        // GetWorldCorners -> 转换到 Root Canvas 本地空间，与 UI_MiniMapRadialFade 顶点坐标完全对齐
        mapViewport.GetWorldCorners(viewportCorners);
        Transform canvasTr = rootCanvas != null ? rootCanvas.transform : null;
        if (canvasTr != null)
        {
            for (int i = 0; i < 4; i++)
            {
                viewportCorners[i] = canvasTr.InverseTransformPoint(viewportCorners[i]);
            }
        }

        Vector2 bl = viewportCorners[0];
        Vector2 tl = viewportCorners[1];
        Vector2 tr = viewportCorners[2];
        Vector2 br = viewportCorners[3];

        Vector2 center = (bl + tl + tr + br) * 0.25f;
        Vector2 rightVec = (br - bl) * 0.5f;
        Vector2 upVec = (tl - bl) * 0.5f;

        float rx = Mathf.Max(2f, rightVec.magnitude * ovalRadiiScale.x);
        float ry = Mathf.Max(2f, upVec.magnitude * ovalRadiiScale.y);
        Vector2 rightAxis = rightVec.sqrMagnitude > 1e-5f ? rightVec.normalized / rx : new Vector2(1f / rx, 0f);
        Vector2 upAxis = upVec.sqrMagnitude > 1e-5f ? upVec.normalized / ry : new Vector2(0f, 1f / ry);

        runtimeFadeMat.SetVector(ClipCenterId, new Vector4(center.x, center.y, 0f, 0f));
        runtimeFadeMat.SetVector(ClipRadiiId, new Vector4(rx, ry, radialInnerFadeStart, 0f));
        runtimeFadeMat.SetVector(ClipAxesId, new Vector4(rightAxis.x, rightAxis.y, upAxis.x, upAxis.y));
    }

    private void BuildDynamicMarkers()
    {
        if (markersBuilt || levelOne == null || mapContent == null) return;
        markersBuilt = true;

        if (shardsContainer != null)
        {
            IReadOnlyList<Transform> spawnedShards = levelOne.SpawnedShards;
            for (int i = 0; i < spawnedShards.Count; i++)
            {
                (RectTransform rt, Image img) = CreateMarker(shardsContainer, $"ShardDot_{i:000}", shardStarSprite, maxShardSize, detailShardColor);
                Vector2 basePos = spawnedShards[i] != null ? WorldToContentPosition(spawnedShards[i].position) : Vector2.zero;
                rt.anchoredPosition = basePos;
                shardRects.Add(rt);
                shardImages.Add(img);
                shardBasePositions.Add(basePos);
                // 使用黄金角错开每颗水晶的浮动与闪烁相位，避免整齐同步跳动
                shardPhases.Add(i * 2.3999632f);
            }
        }

        if (enemiesContainer != null)
        {
            IReadOnlyList<MonkeyAI> activeMonkeys = levelOne.ActiveMonkeys;
            for (int i = 0; i < activeMonkeys.Count; i++)
            {
                (RectTransform rt, Image img) = CreateMarker(enemiesContainer, $"EnemyDot_{i:00}", enemyDetailSprite, maxEnemySize, Color.white);
                enemyRects.Add(rt);
                enemyImages.Add(img);
            }
        }

        ApplyAllMarkerVisualStyles();
    }

    private void SnapMapYawToPlayer()
    {
        if (!rotateMapWithPlayer)
        {
            currentMapYaw = 0f;
            hasInitializedMapYaw = true;
            return;
        }

        Transform playerTr = levelOne != null ? levelOne.PlayerTransform : null;
        if (playerTr != null)
        {
            currentMapYaw = playerTr.eulerAngles.y;
            hasInitializedMapYaw = true;
        }
    }

    private void UpdateMapScrollAndPositions()
    {
        if (mapContent == null) return;

        Transform playerTr = levelOne != null ? levelOne.PlayerTransform : null;
        Vector3 focusWorldPos = playerTr != null
            ? playerTr.position
            : (elevatorTarget != null ? elevatorTarget.position : Vector3.zero);

        // 1. 地图转向：开启随玩家转向时，将 MapContent 旋转 +playerYaw，使地图上方始终与面朝方向一致
        if (rotateMapWithPlayer && playerTr != null)
        {
            float targetYaw = playerTr.eulerAngles.y;
            if (!hasInitializedMapYaw || mapRotationSmoothSpeed <= 0f)
            {
                currentMapYaw = targetYaw;
                hasInitializedMapYaw = true;
            }
            else
            {
                float blend = 1f - Mathf.Exp(-mapRotationSmoothSpeed * Time.deltaTime);
                currentMapYaw = Mathf.LerpAngle(currentMapYaw, targetYaw, blend);
            }
        }
        else
        {
            currentMapYaw = 0f;
        }

        Quaternion mapRot = Quaternion.Euler(0f, 0f, currentMapYaw);
        Quaternion uprightRot = Quaternion.Euler(0f, 0f, -currentMapYaw);

        // 2. 缩放、旋转并居中滚动到玩家所在局部区域（偏移量需同步随地图旋转）
        mapContent.localScale = Vector3.one * currentZoomScale;
        mapContent.localRotation = mapRot;

        Vector2 playerContentPos = WorldToContentPosition(focusWorldPos);
        Vector3 rotatedPlayerOffset = mapRot * new Vector3(playerContentPos.x * currentZoomScale, playerContentPos.y * currentZoomScale, 0f);
        mapContent.anchoredPosition = new Vector2(-rotatedPlayerOffset.x, -rotatedPlayerOffset.y);

        // 3. 玩家标记始终位于偏圆形小地图正中：地图随视角旋转时箭头恒定朝上，固定朝北模式才旋转箭头
        if (playerMarker != null)
        {
            playerMarker.anchoredPosition = Vector2.zero;
            if (!IsDetailedMode || rotateMapWithPlayer)
            {
                playerMarker.localRotation = Quaternion.identity;
            }
            else if (playerTr != null)
            {
                playerMarker.localRotation = Quaternion.Euler(0f, 0f, -playerTr.eulerAngles.y);
            }
        }

        // 4. 场景真实雕像与电梯位置（反向抵消地图旋转，确保地标图标在屏幕上始终保持正立）
        if (statueMarker != null && statueTarget != null)
        {
            statueMarker.anchoredPosition = WorldToContentPosition(statueTarget.position);
            statueMarker.localRotation = uprightRot;
        }
        if (elevatorMarker != null && elevatorTarget != null)
        {
            Vector3 liftWorldCenter = elevatorTarget.TransformPoint(elevatorCenterLocalOffset);
            elevatorMarker.anchoredPosition = WorldToContentPosition(liftWorldCenter);
            elevatorMarker.localRotation = uprightRot;
        }

        if (levelOne == null) return;

        // 5. 实时隐藏已拾取的普通水晶，同步敌人位置并保持敌人图标正立
        IReadOnlyList<Transform> spawnedShards = levelOne.SpawnedShards;
        int shardCount = Mathf.Min(shardRects.Count, spawnedShards.Count);
        for (int i = 0; i < shardCount; i++)
        {
            Transform st = spawnedShards[i];
            bool visible = st != null && st.gameObject.activeInHierarchy;
            if (shardRects[i].gameObject.activeSelf != visible)
            {
                shardRects[i].gameObject.SetActive(visible);
            }
        }

        IReadOnlyList<MonkeyAI> activeMonkeys = levelOne.ActiveMonkeys;
        if (enemiesContainer != null && activeMonkeys.Count > enemyRects.Count)
        {
            for (int i = enemyRects.Count; i < activeMonkeys.Count; i++)
            {
                (RectTransform rt, Image img) = CreateMarker(enemiesContainer, $"EnemyDot_{i:00}", enemyDetailSprite, maxEnemySize, Color.white);
                enemyRects.Add(rt);
                enemyImages.Add(img);
            }
            ApplyAllMarkerVisualStyles();
        }

        // 常态下无法通过地图看到敌人位置；仅在拾取红水晶（洞察之眼）生效期间显示全部活跃敌人（含普通猴子与首领）
        bool revealEnemies = levelOne.AreEnemiesRevealedOnMap;
        float revealRemain = levelOne.EnemyRevealRemainingTime;
        bool revealBlinkOn = revealRemain > 3.0f || Mathf.Sin(Time.unscaledTime * 16f) >= 0f;

        for (int i = 0; i < enemyRects.Count; i++)
        {
            MonkeyAI mk = i < activeMonkeys.Count ? activeMonkeys[i] : null;
            bool visible = revealEnemies && revealBlinkOn && mk != null && mk.gameObject.activeInHierarchy && !mk.IsAsleepInSecretRoom;
            if (enemyRects[i].gameObject.activeSelf != visible)
            {
                enemyRects[i].gameObject.SetActive(visible);
            }
            if (visible)
            {
                enemyRects[i].anchoredPosition = WorldToContentPosition(mk.transform.position);
                enemyRects[i].localRotation = uprightRot;
                float bossScale = mk.IsBossKing ? 1.32f : 1.0f;
                enemyRects[i].localScale = new Vector3(bossScale, bossScale, 1f);
            }
        }
    }

    private void UpdateMarkerScreenSizes()
    {
        float invZoom = 1f / Mathf.Max(0.15f, currentZoomScale);

        float pScreen, shScreen, enScreen, stScreen;
        switch (currentZoomLevel)
        {
            case MiniMapZoomLevel.Max:
                pScreen = maxPlayerSize;
                shScreen = maxShardSize;
                enScreen = maxEnemySize;
                stScreen = maxStatueSize;
                break;
            case MiniMapZoomLevel.Medium:
                pScreen = mediumPlayerDotSize;
                shScreen = mediumShardDotSize;
                enScreen = mediumEnemyDotSize;
                stScreen = mediumStatueSquareSize;
                break;
            default:
                pScreen = minPlayerDotSize;
                shScreen = minShardDotSize;
                enScreen = minEnemyDotSize;
                stScreen = minStatueSquareSize;
                break;
        }

        if (playerMarker != null)
        {
            playerMarker.sizeDelta = new Vector2(pScreen, pScreen);
        }

        float shSize = shScreen * invZoom;
        for (int i = 0; i < shardRects.Count; i++)
        {
            shardRects[i].sizeDelta = new Vector2(shSize, shSize);
        }

        float enSize = enScreen * invZoom;
        for (int i = 0; i < enemyRects.Count; i++)
        {
            enemyRects[i].sizeDelta = new Vector2(enSize, enSize);
        }

        if (statueIcon != null)
        {
            float stSize = stScreen * invZoom;
            statueIcon.rectTransform.sizeDelta = new Vector2(stSize, stSize);
            if (statueHighlightRing != null)
            {
                float ringS = stSize * 2.15f;
                statueHighlightRing.rectTransform.sizeDelta = new Vector2(ringS, ringS);
            }
        }

        if (elevatorIcon != null)
        {
            float elSize = maxElevatorSize * invZoom;
            elevatorIcon.rectTransform.sizeDelta = new Vector2(elSize, elSize);
            if (elevatorHighlightRing != null)
            {
                float ringE = elSize * 2.15f;
                elevatorHighlightRing.rectTransform.sizeDelta = new Vector2(ringE, ringE);
            }
        }
    }

    /// <summary>
    /// 仅在「最大化（Max）」档位下驱动地图上水晶的轻微上下浮动与星辉闪烁细节；
    /// 中等与最小化档位不执行动画，保持纯净静态圆点。
    /// </summary>
    private void UpdateMaxModeShardDetails()
    {
        if (!IsDetailedMode || shardRects.Count == 0) return;

        float now = Time.unscaledTime;
        float invZoom = 1f / Mathf.Max(0.15f, currentZoomScale);
        float bobAmpContent = shardFloatAmplitudePx * invZoom;

        // 将「屏幕正上方」转换回旋转后的 MapContent 局部方向，保证地图转向时水晶依然沿屏幕上下轻浮并保持正立
        Vector3 upVec = Quaternion.Euler(0f, 0f, -currentMapYaw) * Vector3.up;
        Vector2 screenUpInContent = new Vector2(upVec.x, upVec.y);

        for (int i = 0; i < shardRects.Count; i++)
        {
            RectTransform rt = shardRects[i];
            if (rt == null || !rt.gameObject.activeSelf) continue;

            float phase = shardPhases[i];

            // 1. 轻微上下浮动与微幅摆动（每颗水晶独立相位，且始终相对屏幕正立）
            float bobY = Mathf.Sin(now * shardFloatSpeed + phase) * bobAmpContent;
            rt.anchoredPosition = shardBasePositions[i] + screenUpInContent * bobY;
            float tiltDeg = Mathf.Sin(now * (shardFloatSpeed * 0.78f) + phase * 0.83f) * 6f;
            rt.localRotation = Quaternion.Euler(0f, 0f, -currentMapYaw + tiltDeg);

            // 2. 柔和呼吸 + 星光闪耀波峰（高亮颜色与细微缩放，在浅色羊皮纸上清晰可见）
            float breath = 0.5f + 0.5f * Mathf.Sin(now * shardTwinkleSpeed + phase * 1.37f);
            float glintWave = 0.5f + 0.5f * Mathf.Sin(now * (shardTwinkleSpeed * 1.65f) + phase * 2.11f);
            float sparkle = glintWave * glintWave * glintWave * glintWave;
            float shimmer = Mathf.Clamp01(breath * 0.42f + sparkle * 0.85f);

            float scale = Mathf.Lerp(shardScaleMin, shardScaleMax, shimmer);
            rt.localScale = new Vector3(scale, scale, 1f);

            Image img = shardImages[i];
            if (img != null)
            {
                Color c = Color.Lerp(detailShardColor, detailShardSparkleColor, shimmer);
                c.a = Mathf.Lerp(0.72f, 1.00f, shimmer);
                img.color = c;
            }
        }
    }

    private void HandleObjectiveStateChanged(LevelObjectiveState state)
    {
        currentState = state;
        ApplyAllMarkerVisualStyles();
    }

    private void ApplyAllMarkerVisualStyles()
    {
        bool detailed = IsDetailedMode;
        bool highlightStatue = currentState == LevelObjectiveState.ActivateStatue;
        bool highlightElevator = currentState == LevelObjectiveState.EscapeElevator;

        // 1. 玩家：最大化黑色箭头 / 中等与最小化黑色小圆点
        if (playerIcon != null)
        {
            playerIcon.sprite = !detailed && dotCircleSprite != null ? dotCircleSprite : playerArrowSprite;
            playerIcon.color = detailed ? detailPlayerColor : simplifiedPlayerColor;
        }

        // 2. 水晶：最大化紫色星标（带浮动与闪烁）/ 中等与最小化纯静态紫色小圆点（复位浮动与缩放）
        Sprite targetShardSprite = !detailed && dotCircleSprite != null ? dotCircleSprite : shardStarSprite;
        Color targetShardColor = detailed ? detailShardColor : simplifiedShardColor;
        for (int i = 0; i < shardImages.Count; i++)
        {
            shardImages[i].sprite = targetShardSprite;
            shardImages[i].color = targetShardColor;
            if (!detailed && i < shardBasePositions.Count)
            {
                shardRects[i].anchoredPosition = shardBasePositions[i];
                shardRects[i].localScale = Vector3.one;
                shardRects[i].localRotation = Quaternion.identity;
            }
        }

        // 3. 敌人：最大化精细图标 / 中等与最小化红色小圆点
        Sprite targetEnemySprite = !detailed && dotCircleSprite != null ? dotCircleSprite : enemyDetailSprite;
        Color targetEnemyColor = detailed ? Color.white : simplifiedEnemyColor;
        for (int i = 0; i < enemyImages.Count; i++)
        {
            enemyImages[i].sprite = targetEnemySprite;
            enemyImages[i].color = targetEnemyColor;
        }

        // 4. 雕像：最大化精细雕像图标 / 中等与最小化灰色小方框（收齐碎片后仍保留高亮）
        if (statueIcon != null)
        {
            statueIcon.sprite = !detailed && squareDotSprite != null ? squareDotSprite : statueDetailSprite;
            statueIcon.color = highlightStatue
                ? statueHighlightColor
                : (detailed ? subduedLandmarkColor : simplifiedStatueGreyColor);
        }
        if (statueHighlightRing != null)
        {
            statueHighlightRing.gameObject.SetActive(highlightStatue);
            statueHighlightRing.color = statueRingColor;
        }
        if (statueMarker != null && !highlightStatue)
        {
            statueMarker.localScale = Vector3.one;
        }

        // 5. 电梯：仅最大化档显示并在解锁出口后高亮 / 中等与最小化隐藏不显示
        if (elevatorMarker != null)
        {
            elevatorMarker.gameObject.SetActive(detailed);
        }
        if (elevatorIcon != null)
        {
            elevatorIcon.sprite = elevatorDetailSprite;
            elevatorIcon.color = highlightElevator ? elevatorHighlightColor : subduedLandmarkColor;
        }
        if (elevatorHighlightRing != null)
        {
            elevatorHighlightRing.gameObject.SetActive(detailed && highlightElevator);
            elevatorHighlightRing.color = elevatorRingColor;
        }
        if (elevatorMarker != null && !highlightElevator)
        {
            elevatorMarker.localScale = Vector3.one;
        }
    }

    private void UpdateHighlightPulse()
    {
        float wave = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * pulseSpeed);

        if (currentState == LevelObjectiveState.ActivateStatue && statueMarker != null)
        {
            statueMarker.localScale = Vector3.one * Mathf.Lerp(1.06f, 1.24f, wave);
            if (statueHighlightRing != null)
            {
                statueHighlightRing.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.95f, 1.32f, wave);
                Color c = statueRingColor;
                c.a = Mathf.Lerp(0.42f, 0.94f, 1f - wave * 0.45f);
                statueHighlightRing.color = c;
            }
        }
        else if (IsDetailedMode && currentState == LevelObjectiveState.EscapeElevator && elevatorMarker != null)
        {
            elevatorMarker.localScale = Vector3.one * Mathf.Lerp(1.06f, 1.24f, wave);
            if (elevatorHighlightRing != null)
            {
                elevatorHighlightRing.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.95f, 1.32f, wave);
                Color c = elevatorRingColor;
                c.a = Mathf.Lerp(0.42f, 0.94f, 1f - wave * 0.45f);
                elevatorHighlightRing.color = c;
            }
        }
    }

    public Vector2 WorldToContentPosition(Vector3 worldPos)
    {
        float nx = Mathf.InverseLerp(worldMin.x, worldMax.x, worldPos.x);
        float nz = Mathf.InverseLerp(worldMin.y, worldMax.y, worldPos.z);
        return new Vector2((nx - 0.5f) * baseContentSize, (nz - 0.5f) * baseContentSize);
    }

    private (RectTransform, Image) CreateMarker(RectTransform parent, string name, Sprite sprite, float size, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(size, size);

        Image img = go.GetComponent<Image>();
        img.sprite = sprite;
        img.color = color;
        img.raycastTarget = false;
        img.preserveAspect = true;
        if (runtimeFadeMat != null)
        {
            img.material = runtimeFadeMat;
        }
        return (rt, img);
    }
}
