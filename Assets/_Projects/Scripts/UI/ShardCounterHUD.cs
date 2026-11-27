using TMPro;
using UnityEngine;

/// <summary>
/// 局内左下角普通碎片计数器：
/// - 仅显示本局剩余普通水晶数量（例如 192 -> 120 -> 0）；
/// - 默认随小地图隐藏，仅在按下空格完成取出地图动画后才淡入显示在地图上方；
/// - 保留用户在场景中摆好的位置，不在运行时覆盖坐标。
/// </summary>
public class ShardCounterHUD : MonoBehaviour
{
    [SerializeField] private LevelOne levelOne;
    [SerializeField] private TMP_Text countText;
    [SerializeField] private RectTransform pulseTarget;
    [SerializeField] private CanvasGroup canvasGroup;
    [SerializeField] private float punchScale = 1.16f;
    [SerializeField] private float punchDuration = 0.18f;
    [SerializeField] private float revealDuration = 0.18f;
    [SerializeField] private float hideDuration = 0.12f;
    [SerializeField] private float revealSlideOffsetY = -10f;

    private RectTransform selfRect;
    private Vector2 restingAnchoredPos;
    private bool hasCachedRestingPos;

    private float punchTimer;
    private bool hasReceivedFirstUpdate;
    private bool targetVisible;
    private float visibleProgress; // 0 = 隐藏, 1 = 完全显示

    private void Awake()
    {
        selfRect = transform as RectTransform;
        if (selfRect != null)
        {
            restingAnchoredPos = selfRect.anchoredPosition;
            hasCachedRestingPos = true;
        }

        if (canvasGroup == null)
        {
            canvasGroup = GetComponent<CanvasGroup>();
            if (canvasGroup == null)
            {
                canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }
        }

        // 默认隐藏，等待按下空格并取出地图后再显示
        targetVisible = false;
        visibleProgress = 0f;
        ApplyVisibilityVisuals(0f);
    }

    private void OnEnable()
    {
        if (levelOne == null)
        {
            levelOne = FindAnyObjectByType<LevelOne>();
        }

        if (levelOne != null)
        {
            levelOne.OnShardProgressChanged += HandleShardProgressChanged;
            SetRemainingFromProgress(levelOne.CurrentNormalShards, levelOne.TotalNormalShards, false);
        }
    }

    private void OnDisable()
    {
        if (levelOne != null)
        {
            levelOne.OnShardProgressChanged -= HandleShardProgressChanged;
        }
    }

    private void HandleShardProgressChanged(int collected, int total)
    {
        SetRemainingFromProgress(collected, total, hasReceivedFirstUpdate && collected > 0);
        hasReceivedFirstUpdate = true;
    }

    public void SetRemainingFromProgress(int collected, int total, bool playPulse)
    {
        int remaining = Mathf.Max(0, total - collected);
        if (countText != null)
        {
            countText.text = remaining.ToString();
        }

        if (playPulse && targetVisible && pulseTarget != null)
        {
            punchTimer = punchDuration;
        }
    }

    /// <summary>
    /// 由 LevelOneMiniMapHUD 在地图取出动画结束后显示、收回地图时隐藏。
    /// </summary>
    public void SetVisible(bool visible, bool instant = false)
    {
        targetVisible = visible;
        if (instant)
        {
            visibleProgress = visible ? 1f : 0f;
            ApplyVisibilityVisuals(visibleProgress);
        }
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // 1. 跟随地图显隐淡入/淡出
        float goal = targetVisible ? 1f : 0f;
        if (!Mathf.Approximately(visibleProgress, goal))
        {
            float dur = targetVisible ? revealDuration : hideDuration;
            float step = dur > 0.001f ? dt / dur : 1f;
            visibleProgress = Mathf.MoveTowards(visibleProgress, goal, step);
            ApplyVisibilityVisuals(visibleProgress);
        }

        // 2. 拾取普通碎片时的轻微缩放脉冲
        if (punchTimer > 0f && pulseTarget != null)
        {
            punchTimer = Mathf.Max(0f, punchTimer - dt);
            float t = 1f - (punchTimer / Mathf.Max(0.001f, punchDuration));
            float s = t < 0.35f
                ? Mathf.Lerp(1f, punchScale, t / 0.35f)
                : Mathf.Lerp(punchScale, 1f, (t - 0.35f) / 0.65f);
            pulseTarget.localScale = Vector3.one * s;
        }
    }

    private void ApplyVisibilityVisuals(float p)
    {
        float eased = p * p * (3f - 2f * p);
        if (canvasGroup != null)
        {
            canvasGroup.alpha = eased;
            canvasGroup.blocksRaycasts = false;
            canvasGroup.interactable = false;
        }

        if (hasCachedRestingPos && selfRect != null)
        {
            selfRect.anchoredPosition = restingAnchoredPos + new Vector2(0f, Mathf.Lerp(revealSlideOffsetY, 0f, eased));
        }
    }
}
