using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 第一关通用界面与转场控制器：
/// - 开场淡入 & 切场景平滑黑幕过渡；
/// - 复活过场动画（RebornTransitionPanel：3->2 / 2->1 心脏碎裂，暗幕中点安全复位玩家与猴子）；
/// - 死亡结算（FailurePanel：柔和淡入 + 能量槽动态填充 + 历史最佳收集数 + 默认聚焦「重新开始」）；
/// - 胜利结算（SuccessPanel：暖光淡入 + 本次/历史最快用时对比 + 新纪录提示 + 默认聚焦「下一关」）；
/// - 暂停面板（PausePanel：继续、重开、主菜单二次确认、音效/音乐/鼠标灵敏度滑块、带拨钮左右跳动效果的镜头晃动开关）。
/// </summary>
public class LevelOneOverlayUI : MonoBehaviour
{
    private static readonly Color ToggleTextOnColor = new Color(0.95f, 0.90f, 0.78f, 1f);
    private static readonly Color ToggleTextOffColor = new Color(0.56f, 0.54f, 0.56f, 0.92f);

    [Header("关卡与场景跳转")]
    [SerializeField] private LevelOne levelOne;
    [SerializeField] private string mainMenuSceneName = "MainMenu";
    [SerializeField] private string nextLevelSceneName = "";
    [SerializeField] private bool fadeInOnStart = true;
    [SerializeField] private float sceneFadeDuration = 0.42f;

    [Header("复活过渡面板（RebornTransitionPanel）")]
    [SerializeField] private GameObject rebornPanel;
    [SerializeField] private Animator rebornAnimator;
    [SerializeField] private float rebornResetDelay = 1.55f;
    [SerializeField] private float rebornTotalDuration = 2.85f;

    [Header("死亡失败界面（FailurePanel）")]
    [SerializeField] private GameObject failurePanel;
    [SerializeField] private CanvasGroup failureCanvasGroup;
    [SerializeField] private Image failureEnergyFill;
    [SerializeField] private Text failureShardCountText;
    [SerializeField] private Text failureBestRecordText;
    [SerializeField] private Button failureMainMenuButton;
    [SerializeField] private Button failureRestartButton;
    [SerializeField] private float failureFadeDuration = 0.48f;

    [Header("逃脱胜利界面（SuccessPanel）")]
    [SerializeField] private GameObject successPanel;
    [SerializeField] private CanvasGroup successCanvasGroup;
    [SerializeField] private Image successEnergyFill;
    [SerializeField] private Text successShardCountText;
    [SerializeField] private Text successCurrentTimeText;
    [SerializeField] private Text successBestTimeText;
    [SerializeField] private Text successRecordStatusText;
    [SerializeField] private Button successMainMenuButton;
    [SerializeField] private Button successNextLevelButton;
    [SerializeField] private Button successRestartButton;
    [SerializeField] private float successFadeDuration = 0.55f;

    [Header("暂停界面（PausePanel）")]
    [SerializeField] private GameObject pausePanel;

    private CanvasGroup screenFadeGroup;
    private GameObject executionSkipHintRoot;
    private CanvasGroup executionSkipHintGroup;
    private bool isTransitioningScene;

    private GameObject pauseConfirmDialog;
    private Slider pauseSfxSlider;
    private Slider pauseBgmSlider;
    private Slider pauseMouseSensSlider;
    private Text pauseSfxValueText;
    private Text pauseBgmValueText;
    private Text pauseMouseSensValueText;
    private Text pauseShakeStateText;
    private RectTransform pauseShakeKnob;
    private Image pauseShakeActiveFill;
    private float pauseShakeKnobTravelX = 45f;
    private float pauseShakeVisualT = 1f;
    private Action pendingConfirmAction;

    private void Awake()
    {
        if (levelOne == null) levelOne = GetComponent<LevelOne>();
        if (levelOne == null) levelOne = FindAnyObjectByType<LevelOne>();

        AutoBindPanelReferences();
        CreateScreenFadeCurtain();
        CreateExecutionSkipHint();
        GameSettings.ApplyRuntimeSettings();
        WireButtonsAndSliders();

        if (rebornPanel != null) rebornPanel.SetActive(false);
        if (failurePanel != null) failurePanel.SetActive(false);
        if (successPanel != null) successPanel.SetActive(false);
    }

    private void Start()
    {
        if (fadeInOnStart && screenFadeGroup != null)
        {
            StartCoroutine(FadeCurtainRoutine(1f, 0f, 0.5f));
        }
    }

    private void Update()
    {
        // 使用 unscaledDeltaTime，确保 Time.timeScale == 0 暂停期间拨动开关依然顺滑跳到另一侧
        if (pausePanel != null && pausePanel.activeInHierarchy && pauseShakeKnob != null)
        {
            float target = GameSettings.CameraShakeEnabled ? 1f : 0f;
            if (!Mathf.Approximately(pauseShakeVisualT, target))
            {
                pauseShakeVisualT = Mathf.MoveTowards(pauseShakeVisualT, target, Time.unscaledDeltaTime * 9f);
                ApplyShakeToggleVisual(pauseShakeVisualT);
            }
        }

        // 处决跳过提示轻微呼吸闪烁
        if (executionSkipHintRoot != null && executionSkipHintRoot.activeSelf && executionSkipHintGroup != null)
        {
            executionSkipHintGroup.alpha = 0.72f + 0.28f * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 3.2f));
        }
    }

    public void ShowExecutionSkipHint(bool visible)
    {
        if (executionSkipHintRoot != null)
        {
            executionSkipHintRoot.SetActive(visible);
        }
    }

    public void RefreshPauseSettingsUI()
    {
        if (pauseSfxSlider != null)
        {
            float v = GameSettings.SfxVolume;
            pauseSfxSlider.SetValueWithoutNotify(v);
            if (pauseSfxValueText != null) pauseSfxValueText.text = $"{Mathf.RoundToInt(v * 100f)}%";
        }

        if (pauseBgmSlider != null)
        {
            float v = GameSettings.BgmVolume;
            pauseBgmSlider.SetValueWithoutNotify(v);
            if (pauseBgmValueText != null) pauseBgmValueText.text = $"{Mathf.RoundToInt(v * 100f)}%";
        }

        if (pauseMouseSensSlider != null)
        {
            float v = GameSettings.MouseSensitivity;
            pauseMouseSensSlider.SetValueWithoutNotify(v);
            if (pauseMouseSensValueText != null) pauseMouseSensValueText.text = $"{Mathf.RoundToInt(v * 100f)}%";
        }

        pauseShakeVisualT = GameSettings.CameraShakeEnabled ? 1f : 0f;
        ApplyShakeToggleVisual(pauseShakeVisualT);
    }

    public void PlayRebornTransition(int livesBeforeDeath, Action onMidDarkness, Action onFinished)
    {
        StartCoroutine(RebornRoutine(livesBeforeDeath, onMidDarkness, onFinished));
    }

    /// <summary>
    /// 处决动画结束后的标准复活过场：
    /// 1) 先黑屏（掩盖处决结束帧并复位抓捕猴子）；
    /// 2) 淡入播放复活心碎过渡动画（3->2 或 2->1）；
    /// 3) 复活动画结束再次先进入黑屏；
    /// 4) 黑屏期间直接将玩家复活至安全屋并复位所有猴子，最后淡出黑屏进入安全屋游玩。
    /// </summary>
    public void PlayPostExecuteRebornFlow(
        int livesBeforeDeath,
        Action onEnteredFirstBlack,
        Action onSafeRoomRespawnInBlack,
        Action onFinished)
    {
        StartCoroutine(PostExecuteRebornRoutine(livesBeforeDeath, onEnteredFirstBlack, onSafeRoomRespawnInBlack, onFinished));
    }

    /// <summary>
    /// 最后一条命处决动画结束后的标准死亡流程：先平滑黑屏，再接死亡失败界面。
    /// </summary>
    public void PlayPostExecuteFailureFlow(int collected, int total)
    {
        StartCoroutine(PostExecuteFailureRoutine(collected, total));
    }

    public void ShowFailure(int collected, int total)
    {
        StartCoroutine(ShowFailureRoutine(collected, total));
    }

    public void ShowSuccess(int collected, int total, float elapsedSeconds)
    {
        StartCoroutine(ShowSuccessRoutine(collected, total, elapsedSeconds));
    }

    /// <summary>
    /// 逃入电梯且电梯门关上后的胜利过场：逐渐黑屏 -> 黑幕下铺设胜利结算界面 -> 平滑淡出黑幕呈现胜利界面。
    /// </summary>
    public void PlayPostEscapeVictoryFlow(int collected, int total, float elapsedSeconds)
    {
        StartCoroutine(PostEscapeVictoryRoutine(collected, total, elapsedSeconds));
    }

    /// <summary>
    /// 供过场演出（如首领电梯入场动画）直接调用全屏黑幕渐隐/渐显。
    /// </summary>
    public Coroutine FadeScreenCurtain(float fromAlpha, float toAlpha, float duration)
    {
        return StartCoroutine(FadeCurtainRoutine(fromAlpha, toAlpha, duration));
    }

    public void HidePauseConfirmIfOpen()
    {
        pendingConfirmAction = null;
        if (pauseConfirmDialog != null)
        {
            pauseConfirmDialog.SetActive(false);
        }
    }

    private IEnumerator RebornRoutine(int livesBeforeDeath, Action onMidDarkness, Action onFinished)
    {
        if (rebornPanel == null)
        {
            onMidDarkness?.Invoke();
            onFinished?.Invoke();
            yield break;
        }

        rebornPanel.SetActive(true);
        if (rebornAnimator == null)
        {
            rebornAnimator = rebornPanel.GetComponent<Animator>();
        }
        if (rebornAnimator != null)
        {
            rebornAnimator.updateMode = AnimatorUpdateMode.UnscaledTime;
            rebornAnimator.SetInteger("LivesBeforeDeath", livesBeforeDeath);
            string stateName = livesBeforeDeath >= 3 ? "Reborn_3To2" : "Reborn_2To1";
            rebornAnimator.Play(stateName, 0, 0f);
            rebornAnimator.Update(0f);
        }

        float resetTime = Mathf.Clamp(rebornResetDelay, 0.4f, Mathf.Max(0.5f, rebornTotalDuration - 0.35f));
        yield return new WaitForSecondsRealtime(resetTime);

        onMidDarkness?.Invoke();

        float remain = Mathf.Max(0.1f, rebornTotalDuration - resetTime);
        yield return new WaitForSecondsRealtime(remain);

        rebornPanel.SetActive(false);
        onFinished?.Invoke();
    }

    private IEnumerator PostExecuteRebornRoutine(
        int livesBeforeDeath,
        Action onEnteredFirstBlack,
        Action onSafeRoomRespawnInBlack,
        Action onFinished)
    {
        ShowExecutionSkipHint(false);

        // 1. 处决播完/跳过后先进入纯黑屏
        yield return FadeCurtainRoutine(0f, 1f, 0.30f);
        onEnteredFirstBlack?.Invoke();
        yield return new WaitForSecondsRealtime(0.10f);

        // 2. 在黑幕后开启复活过渡动画，并加纯黑底板防止 3D 场景在淡入瞬间露底
        if (rebornPanel != null)
        {
            EnsureRebornSolidBackplate();
            rebornPanel.SetActive(true);
            if (rebornAnimator == null)
            {
                rebornAnimator = rebornPanel.GetComponent<Animator>();
            }
            if (rebornAnimator != null)
            {
                rebornAnimator.updateMode = AnimatorUpdateMode.UnscaledTime;
                rebornAnimator.SetInteger("LivesBeforeDeath", livesBeforeDeath);
                string stateName = livesBeforeDeath >= 3 ? "Reborn_3To2" : "Reborn_2To1";
                rebornAnimator.Play(stateName, 0, 0f);
                rebornAnimator.Update(0f);
            }

            // 淡出黑幕展示复活动画（3->2 或 2->1）
            yield return FadeCurtainRoutine(1f, 0f, 0.22f);

            // 等待心碎复活动画播放完毕（在自带尾帧淡出之前切入黑屏）
            float holdDur = Mathf.Clamp(rebornTotalDuration - 0.62f, 1.4f, 2.35f);
            yield return new WaitForSecondsRealtime(holdDur);

            // 3. 复活动画结束前先进纯黑屏
            yield return FadeCurtainRoutine(0f, 1f, 0.28f);
            rebornPanel.SetActive(false);
        }

        // 4. 在纯黑屏下直接将玩家复活至安全屋并复位所有猴子
        onSafeRoomRespawnInBlack?.Invoke();
        yield return new WaitForSecondsRealtime(0.15f);

        // 5. 黑屏淡出，玩家直接置身安全屋恢复操作
        yield return FadeCurtainRoutine(1f, 0f, 0.42f);
        onFinished?.Invoke();
    }

    private IEnumerator PostExecuteFailureRoutine(int collected, int total)
    {
        ShowExecutionSkipHint(false);

        // 1. 最后一条命处决播完/跳过后先黑屏
        yield return FadeCurtainRoutine(0f, 1f, 0.38f);
        ExecutionBloodScreenVFX.RequestClear();
        yield return new WaitForSecondsRealtime(0.15f);

        // 2. 在黑幕后准备好死亡界面，再淡出黑幕并填充能量槽
        int safeTotal = Mathf.Max(1, total);
        int clampedCollected = Mathf.Clamp(collected, 0, safeTotal);
        int best = GameSettings.UpdateBestShards(1, clampedCollected);

        if (failureShardCountText != null) failureShardCountText.text = $"{clampedCollected}/{safeTotal}";
        if (failureBestRecordText != null) failureBestRecordText.text = $"本关最佳 {best}/{safeTotal}";
        if (failureEnergyFill != null) failureEnergyFill.fillAmount = 0f;

        if (failurePanel != null)
        {
            failurePanel.SetActive(true);
            CanvasGroup cg = EnsureCanvasGroup(failurePanel, ref failureCanvasGroup);
            cg.alpha = 1f;
            cg.interactable = false;
            cg.blocksRaycasts = true;
        }

        yield return FadeCurtainRoutine(1f, 0f, 0.38f);

        if (failurePanel != null && failureCanvasGroup != null)
        {
            failureCanvasGroup.interactable = true;
        }

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if (failureRestartButton != null && EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(failureRestartButton.gameObject);
        }

        if (failureEnergyFill != null)
        {
            float targetFill = (float)clampedCollected / safeTotal;
            float fillDur = 0.68f;
            float elapsed = 0f;
            while (elapsed < fillDur)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / fillDur);
                failureEnergyFill.fillAmount = targetFill * (1f - Mathf.Pow(1f - t, 3f));
                yield return null;
            }
            failureEnergyFill.fillAmount = targetFill;
        }
    }

    private void EnsureRebornSolidBackplate()
    {
        if (rebornPanel == null) return;
        Transform existing = rebornPanel.transform.Find("RebornSolidBackplate");
        if (existing != null) return;

        GameObject bg = new GameObject("RebornSolidBackplate", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        bg.transform.SetParent(rebornPanel.transform, false);
        bg.transform.SetAsFirstSibling();

        RectTransform rt = bg.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        Image img = bg.GetComponent<Image>();
        img.color = Color.black;
        img.raycastTarget = false;
    }

    private IEnumerator ShowFailureRoutine(int collected, int total)
    {
        int safeTotal = Mathf.Max(1, total);
        int clampedCollected = Mathf.Clamp(collected, 0, safeTotal);
        int best = GameSettings.UpdateBestShards(1, clampedCollected);

        if (failureShardCountText != null)
        {
            failureShardCountText.text = $"{clampedCollected}/{safeTotal}";
        }
        if (failureBestRecordText != null)
        {
            failureBestRecordText.text = $"本关最佳 {best}/{safeTotal}";
        }
        if (failureEnergyFill != null)
        {
            failureEnergyFill.fillAmount = 0f;
        }

        if (failurePanel != null)
        {
            failurePanel.SetActive(true);
            CanvasGroup cg = EnsureCanvasGroup(failurePanel, ref failureCanvasGroup);
            cg.alpha = 0f;
            cg.interactable = false;
            cg.blocksRaycasts = true;

            float targetFill = (float)clampedCollected / safeTotal;
            float fadeDur = Mathf.Max(0.1f, failureFadeDuration);
            float fillDur = 0.72f;
            float totalDur = Mathf.Max(fadeDur, fillDur);
            float elapsed = 0f;

            while (elapsed < totalDur)
            {
                elapsed += Time.unscaledDeltaTime;
                float fadeT = Mathf.Clamp01(elapsed / fadeDur);
                cg.alpha = fadeT * fadeT * (3f - 2f * fadeT);

                if (failureEnergyFill != null)
                {
                    float fillT = Mathf.Clamp01(elapsed / fillDur);
                    float easedFill = 1f - Mathf.Pow(1f - fillT, 3f);
                    failureEnergyFill.fillAmount = targetFill * easedFill;
                }
                yield return null;
            }

            cg.alpha = 1f;
            cg.interactable = true;
            if (failureEnergyFill != null) failureEnergyFill.fillAmount = targetFill;
        }

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        if (failureRestartButton != null && EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(failureRestartButton.gameObject);
        }
    }

    private IEnumerator ShowSuccessRoutine(int collected, int total, float elapsedSeconds)
    {
        int safeTotal = Mathf.Max(1, total);
        int clampedCollected = Mathf.Clamp(collected, 0, safeTotal);
        float safeTime = Mathf.Max(0.01f, elapsedSeconds);

        GameSettings.UpdateBestShards(1, clampedCollected);
        GameSettings.UpdateBestTime(1, safeTime, out bool isFirstClear, out bool isNewRecord, out float prevBestTime);
        float displayBestTime = (isFirstClear || isNewRecord) ? safeTime : prevBestTime;

        if (successShardCountText != null)
        {
            successShardCountText.text = $"{clampedCollected}/{safeTotal}";
        }
        if (successCurrentTimeText != null)
        {
            successCurrentTimeText.text = GameSettings.FormatRunTime(safeTime);
        }
        if (successBestTimeText != null)
        {
            successBestTimeText.text = GameSettings.FormatRunTime(displayBestTime);
        }
        if (successRecordStatusText != null)
        {
            if (isFirstClear)
            {
                successRecordStatusText.text = "首次通关";
            }
            else if (isNewRecord)
            {
                successRecordStatusText.text = $"新纪录 · 快了 {(prevBestTime - safeTime):0.00} 秒";
            }
            else
            {
                successRecordStatusText.text = $"距纪录差 {(safeTime - prevBestTime):0.00} 秒";
            }
        }
        if (successEnergyFill != null)
        {
            successEnergyFill.fillAmount = 0f;
        }

        if (successPanel != null)
        {
            successPanel.SetActive(true);
            CanvasGroup cg = EnsureCanvasGroup(successPanel, ref successCanvasGroup);
            cg.alpha = 0f;
            cg.interactable = false;
            cg.blocksRaycasts = true;

            float fadeDur = Mathf.Max(0.1f, successFadeDuration);
            float fillDur = 0.65f;
            float totalDur = Mathf.Max(fadeDur, fillDur);
            float elapsed = 0f;

            while (elapsed < totalDur)
            {
                elapsed += Time.unscaledDeltaTime;
                float fadeT = Mathf.Clamp01(elapsed / fadeDur);
                cg.alpha = fadeT * fadeT * (3f - 2f * fadeT);

                if (successEnergyFill != null)
                {
                    float fillT = Mathf.Clamp01(elapsed / fillDur);
                    float easedFill = 1f - Mathf.Pow(1f - fillT, 3f);
                    successEnergyFill.fillAmount = easedFill;
                }
                yield return null;
            }

            cg.alpha = 1f;
            cg.interactable = true;
            if (successEnergyFill != null) successEnergyFill.fillAmount = 1f;
        }

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        if (successNextLevelButton != null && EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(successNextLevelButton.gameObject);
        }
    }

    private IEnumerator PostEscapeVictoryRoutine(int collected, int total, float elapsedSeconds)
    {
        // 1. 电梯门关上后逐渐黑屏
        yield return FadeCurtainRoutine(0f, 1f, 0.92f);
        yield return new WaitForSecondsRealtime(0.18f);

        // 2. 在纯黑幕后准备好胜利结算界面数据
        int safeTotal = Mathf.Max(1, total);
        int clampedCollected = Mathf.Clamp(collected, 0, safeTotal);
        float safeTime = Mathf.Max(0.01f, elapsedSeconds);

        GameSettings.UpdateBestShards(1, clampedCollected);
        GameSettings.UpdateBestTime(1, safeTime, out bool isFirstClear, out bool isNewRecord, out float prevBestTime);
        float displayBestTime = (isFirstClear || isNewRecord) ? safeTime : prevBestTime;

        if (successShardCountText != null) successShardCountText.text = $"{clampedCollected}/{safeTotal}";
        if (successCurrentTimeText != null) successCurrentTimeText.text = GameSettings.FormatRunTime(safeTime);
        if (successBestTimeText != null) successBestTimeText.text = GameSettings.FormatRunTime(displayBestTime);
        if (successRecordStatusText != null)
        {
            if (isFirstClear) successRecordStatusText.text = "首次通关";
            else if (isNewRecord) successRecordStatusText.text = $"新纪录 · 快了 {(prevBestTime - safeTime):0.00} 秒";
            else successRecordStatusText.text = $"距纪录差 {(safeTime - prevBestTime):0.00} 秒";
        }
        if (successEnergyFill != null) successEnergyFill.fillAmount = 0f;

        if (successPanel != null)
        {
            successPanel.SetActive(true);
            CanvasGroup cg = EnsureCanvasGroup(successPanel, ref successCanvasGroup);
            cg.alpha = 1f;
            cg.interactable = false;
            cg.blocksRaycasts = true;
        }

        // 3. 淡出黑幕进入胜利结算界面并动态充满能量槽
        yield return FadeCurtainRoutine(1f, 0f, 0.45f);

        if (successPanel != null && successCanvasGroup != null)
        {
            successCanvasGroup.interactable = true;
        }

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if (successNextLevelButton != null && EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(successNextLevelButton.gameObject);
        }

        if (successEnergyFill != null)
        {
            float fillDur = 0.65f;
            float elapsed = 0f;
            while (elapsed < fillDur)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / fillDur);
                successEnergyFill.fillAmount = 1f - Mathf.Pow(1f - t, 3f);
                yield return null;
            }
            successEnergyFill.fillAmount = 1f;
        }
    }

    public void RestartCurrentLevel()
    {
        if (isTransitioningScene) return;
        GameSettings.SaveToDisk();
        if (GameSettings.PendingLaunchMode != LevelLaunchMode.ReplayLevel)
        {
            GameSettings.PendingLaunchMode = LevelLaunchMode.NewGame;
        }
        StartCoroutine(FadeAndLoadSceneRoutine(SceneManager.GetActiveScene().name));
    }

    public void ReturnToMainMenu()
    {
        if (isTransitioningScene) return;
        GameSettings.SaveToDisk();
        string target = !string.IsNullOrEmpty(mainMenuSceneName) && Application.CanStreamedLevelBeLoaded(mainMenuSceneName)
            ? mainMenuSceneName
            : SceneManager.GetActiveScene().name;
        StartCoroutine(FadeAndLoadSceneRoutine(target));
    }

    public void ProceedToNextLevel()
    {
        if (isTransitioningScene) return;
        GameSettings.SaveToDisk();
        string target = !string.IsNullOrEmpty(nextLevelSceneName) && Application.CanStreamedLevelBeLoaded(nextLevelSceneName)
            ? nextLevelSceneName
            : SceneManager.GetActiveScene().name;
        StartCoroutine(FadeAndLoadSceneRoutine(target));
    }

    private IEnumerator FadeAndLoadSceneRoutine(string sceneName)
    {
        isTransitioningScene = true;
        yield return FadeCurtainRoutine(0f, 1f, sceneFadeDuration);
        Time.timeScale = 1f;
        SceneManager.LoadScene(sceneName);
    }

    private IEnumerator FadeCurtainRoutine(float fromAlpha, float toAlpha, float dur)
    {
        if (screenFadeGroup == null) yield break;

        screenFadeGroup.gameObject.SetActive(true);
        screenFadeGroup.blocksRaycasts = toAlpha > 0.05f;
        screenFadeGroup.alpha = fromAlpha;

        float elapsed = 0f;
        float safeDur = Mathf.Max(0.05f, dur);
        while (elapsed < safeDur)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / safeDur);
            screenFadeGroup.alpha = Mathf.Lerp(fromAlpha, toAlpha, t * t * (3f - 2f * t));
            yield return null;
        }

        screenFadeGroup.alpha = toAlpha;
        if (toAlpha <= 0.01f)
        {
            screenFadeGroup.blocksRaycasts = false;
            screenFadeGroup.gameObject.SetActive(false);
        }
    }

    private void CreateScreenFadeCurtain()
    {
        GameObject go = new GameObject("ScreenTransitionCurtain", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup), typeof(Image));
        go.transform.SetParent(transform, false);

        Canvas c = go.GetComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay;
        c.sortingOrder = 500;

        Image img = go.GetComponent<Image>();
        img.color = Color.black;
        img.raycastTarget = true;

        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        screenFadeGroup = go.GetComponent<CanvasGroup>();
        screenFadeGroup.alpha = fadeInOnStart ? 1f : 0f;
        screenFadeGroup.interactable = false;
        screenFadeGroup.blocksRaycasts = false;
        go.SetActive(fadeInOnStart);
    }

    private void CreateExecutionSkipHint()
    {
        GameObject root = new GameObject(
            "ExecutionSkipHintCanvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler));
        root.transform.SetParent(transform, false);

        Canvas c = root.GetComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay;
        c.sortingOrder = 450;

        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        GameObject badge = new GameObject(
            "ExecutionSkipBadge",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(CanvasGroup),
            typeof(Image),
            typeof(Outline));
        badge.transform.SetParent(root.transform, false);

        RectTransform brt = badge.GetComponent<RectTransform>();
        brt.anchorMin = new Vector2(1f, 0f);
        brt.anchorMax = new Vector2(1f, 0f);
        brt.pivot = new Vector2(1f, 0f);
        brt.anchoredPosition = new Vector2(-38f, 32f);
        brt.sizeDelta = new Vector2(228f, 46f);

        Image bg = badge.GetComponent<Image>();
        bg.color = new Color(0.06f, 0.04f, 0.08f, 0.80f);
        bg.raycastTarget = false;

        Outline border = badge.GetComponent<Outline>();
        border.effectColor = new Color(0.74f, 0.24f, 0.30f, 0.85f);
        border.effectDistance = new Vector2(1.5f, -1.5f);

        GameObject txtGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(Shadow));
        txtGo.transform.SetParent(badge.transform, false);

        RectTransform trt = txtGo.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;

        Text label = txtGo.GetComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.text = "按 Enter 跳过";
        label.fontSize = 23;
        label.fontStyle = FontStyle.Bold;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = new Color(0.96f, 0.92f, 0.84f, 1f);
        label.raycastTarget = false;

        Shadow sh = txtGo.GetComponent<Shadow>();
        sh.effectColor = new Color(0f, 0f, 0f, 0.85f);
        sh.effectDistance = new Vector2(1.5f, -1.5f);

        executionSkipHintGroup = badge.GetComponent<CanvasGroup>();
        executionSkipHintGroup.interactable = false;
        executionSkipHintGroup.blocksRaycasts = false;

        executionSkipHintRoot = root;
        executionSkipHintRoot.SetActive(false);
    }

    private void WireButtonsAndSliders()
    {
        if (failureRestartButton != null)
        {
            failureRestartButton.onClick.RemoveAllListeners();
            failureRestartButton.onClick.AddListener(RestartCurrentLevel);
        }
        if (failureMainMenuButton != null)
        {
            failureMainMenuButton.onClick.RemoveAllListeners();
            failureMainMenuButton.onClick.AddListener(ReturnToMainMenu);
        }

        if (successMainMenuButton != null)
        {
            successMainMenuButton.onClick.RemoveAllListeners();
            successMainMenuButton.onClick.AddListener(ReturnToMainMenu);
        }
        if (successNextLevelButton != null)
        {
            successNextLevelButton.onClick.RemoveAllListeners();
            successNextLevelButton.onClick.AddListener(ProceedToNextLevel);
        }
        if (successRestartButton != null)
        {
            successRestartButton.onClick.RemoveAllListeners();
            successRestartButton.onClick.AddListener(RestartCurrentLevel);
        }

        WirePausePanelControls();
    }

    private void WirePausePanelControls()
    {
        if (pausePanel == null) return;
        Transform root = pausePanel.transform;

        Button resumeBtn = FindComponentByName<Button>(root, "Btn_Resume");
        if (resumeBtn != null)
        {
            resumeBtn.onClick.RemoveAllListeners();
            resumeBtn.onClick.AddListener(() =>
            {
                HidePauseConfirmIfOpen();
                GameSettings.SaveToDisk();
                if (levelOne != null) levelOne.ResumeGame();
            });
        }

        Transform confirmTr = FindChildRecursive(root, "ConfirmOverlay");
        if (confirmTr == null) confirmTr = FindChildRecursive(root, "ConfirmDialog");
        if (confirmTr != null)
        {
            pauseConfirmDialog = confirmTr.gameObject;
            pauseConfirmDialog.SetActive(false);

            Button confirmBtn = FindComponentByName<Button>(confirmTr, "Btn_Confirm");
            Button cancelBtn = FindComponentByName<Button>(confirmTr, "Btn_Cancel");
            if (confirmBtn != null)
            {
                confirmBtn.onClick.RemoveAllListeners();
                confirmBtn.onClick.AddListener(() =>
                {
                    Action act = pendingConfirmAction;
                    HidePauseConfirmIfOpen();
                    act?.Invoke();
                });
            }
            if (cancelBtn != null)
            {
                cancelBtn.onClick.RemoveAllListeners();
                cancelBtn.onClick.AddListener(HidePauseConfirmIfOpen);
            }
        }

        Button pauseRestartBtn = FindComponentByName<Button>(root, "Btn_Restart");
        if (pauseRestartBtn != null)
        {
            pauseRestartBtn.onClick.RemoveAllListeners();
            pauseRestartBtn.onClick.AddListener(() =>
            {
                if (pauseConfirmDialog != null)
                {
                    pendingConfirmAction = RestartCurrentLevel;
                    pauseConfirmDialog.SetActive(true);
                }
                else
                {
                    RestartCurrentLevel();
                }
            });
        }

        Button pauseMenuBtn = FindComponentByName<Button>(root, "Btn_MainMenu");
        if (pauseMenuBtn != null)
        {
            pauseMenuBtn.onClick.RemoveAllListeners();
            pauseMenuBtn.onClick.AddListener(() =>
            {
                if (pauseConfirmDialog != null)
                {
                    pendingConfirmAction = ReturnToMainMenu;
                    pauseConfirmDialog.SetActive(true);
                }
                else
                {
                    ReturnToMainMenu();
                }
            });
        }

        // 1. 音效滑块
        pauseSfxSlider = FindComponentByName<Slider>(root, "SFXSlider");
        if (pauseSfxSlider != null)
        {
            pauseSfxValueText = FindComponentByName<Text>(pauseSfxSlider.transform.parent, "ValueText");
            pauseSfxSlider.onValueChanged.RemoveAllListeners();
            pauseSfxSlider.onValueChanged.AddListener(v =>
            {
                GameSettings.SfxVolume = v;
                if (pauseSfxValueText != null) pauseSfxValueText.text = $"{Mathf.RoundToInt(v * 100f)}%";
            });
        }

        // 2. 背景音乐滑块
        pauseBgmSlider = FindComponentByName<Slider>(root, "BGMSlider");
        if (pauseBgmSlider != null)
        {
            pauseBgmValueText = FindComponentByName<Text>(pauseBgmSlider.transform.parent, "ValueText");
            pauseBgmSlider.onValueChanged.RemoveAllListeners();
            pauseBgmSlider.onValueChanged.AddListener(v =>
            {
                GameSettings.BgmVolume = v;
                if (pauseBgmValueText != null) pauseBgmValueText.text = $"{Mathf.RoundToInt(v * 100f)}%";
            });
        }

        // 3. 鼠标灵敏度滑块（20% ~ 250%，实时驱动 FirstView）
        pauseMouseSensSlider = FindComponentByName<Slider>(root, "MouseSensitivitySlider");
        if (pauseMouseSensSlider != null)
        {
            pauseMouseSensSlider.minValue = GameSettings.MinMouseSensitivity;
            pauseMouseSensSlider.maxValue = GameSettings.MaxMouseSensitivity;
            pauseMouseSensValueText = FindComponentByName<Text>(pauseMouseSensSlider.transform.parent, "ValueText");
            pauseMouseSensSlider.onValueChanged.RemoveAllListeners();
            pauseMouseSensSlider.onValueChanged.AddListener(v =>
            {
                GameSettings.MouseSensitivity = v;
                if (pauseMouseSensValueText != null) pauseMouseSensValueText.text = $"{Mathf.RoundToInt(v * 100f)}%";
            });
        }

        // 4. 镜头晃动拨动开关（点击后拨钮平滑跳到另一侧，同步显隐高亮填充与文字）
        Button shakeBtn = FindComponentByName<Button>(root, "ShakeToggleButton");
        if (shakeBtn != null)
        {
            Transform row = shakeBtn.transform.parent;
            pauseShakeStateText = FindComponentByName<Text>(row, "StateText");
            pauseShakeKnob = FindChildRecursive(shakeBtn.transform, "Knob") as RectTransform;
            pauseShakeActiveFill = FindComponentByName<Image>(shakeBtn.transform, "ActiveFill");

            if (pauseShakeKnob != null && Mathf.Abs(pauseShakeKnob.anchoredPosition.x) > 5f)
            {
                pauseShakeKnobTravelX = Mathf.Abs(pauseShakeKnob.anchoredPosition.x);
            }

            shakeBtn.onClick.RemoveAllListeners();
            shakeBtn.onClick.AddListener(() =>
            {
                bool nextOn = !GameSettings.CameraShakeEnabled;
                GameSettings.CameraShakeEnabled = nextOn;
                GameSettings.SaveToDisk();
                // 立即更新文字并让拨钮滑跳至另一侧
                UpdateShakeTextOnly(nextOn);
            });
        }

        RefreshPauseSettingsUI();
    }

    private void ApplyShakeToggleVisual(float t)
    {
        float eased = t * t * (3f - 2f * t);
        if (pauseShakeKnob != null)
        {
            Vector2 pos = pauseShakeKnob.anchoredPosition;
            pos.x = Mathf.Lerp(-pauseShakeKnobTravelX, pauseShakeKnobTravelX, eased);
            pauseShakeKnob.anchoredPosition = pos;
        }
        if (pauseShakeActiveFill != null)
        {
            Color c = pauseShakeActiveFill.color;
            c.a = eased * 0.92f;
            pauseShakeActiveFill.color = c;
            pauseShakeActiveFill.enabled = eased > 0.01f;
        }
        UpdateShakeTextOnly(GameSettings.CameraShakeEnabled);
    }

    private void UpdateShakeTextOnly(bool isOn)
    {
        if (pauseShakeStateText != null)
        {
            pauseShakeStateText.text = isOn ? "开启" : "关闭";
            pauseShakeStateText.color = isOn ? ToggleTextOnColor : ToggleTextOffColor;
        }
    }

    private void AutoBindPanelReferences()
    {
        if (rebornPanel != null && rebornAnimator == null)
        {
            rebornAnimator = rebornPanel.GetComponent<Animator>();
        }

        if (failurePanel != null)
        {
            Transform ft = failurePanel.transform;
            if (failureCanvasGroup == null) failureCanvasGroup = failurePanel.GetComponent<CanvasGroup>();
            if (failureEnergyFill == null) failureEnergyFill = FindComponentByName<Image>(ft, "EnergyBarFill");
            if (failureShardCountText == null) failureShardCountText = FindComponentByName<Text>(ft, "ShardCountText");
            if (failureBestRecordText == null) failureBestRecordText = FindComponentByName<Text>(ft, "BestRecordText");
            if (failureMainMenuButton == null) failureMainMenuButton = FindComponentByName<Button>(ft, "Btn_MainMenu");
            if (failureRestartButton == null) failureRestartButton = FindComponentByName<Button>(ft, "Btn_Restart");
        }

        if (successPanel != null)
        {
            Transform st = successPanel.transform;
            if (successCanvasGroup == null) successCanvasGroup = successPanel.GetComponent<CanvasGroup>();
            if (successEnergyFill == null) successEnergyFill = FindComponentByName<Image>(st, "EnergyBarFill");
            if (successShardCountText == null) successShardCountText = FindComponentByName<Text>(st, "ShardCountText");
            if (successCurrentTimeText == null) successCurrentTimeText = FindComponentByName<Text>(st, "CurrentTimeText");
            if (successBestTimeText == null) successBestTimeText = FindComponentByName<Text>(st, "BestTimeText");
            if (successRecordStatusText == null) successRecordStatusText = FindComponentByName<Text>(st, "RecordStatusText");
            if (successMainMenuButton == null) successMainMenuButton = FindComponentByName<Button>(st, "Btn_MainMenu");
            if (successNextLevelButton == null) successNextLevelButton = FindComponentByName<Button>(st, "Btn_NextLevel");
            if (successRestartButton == null) successRestartButton = FindComponentByName<Button>(st, "Btn_Restart");
        }
    }

    private static CanvasGroup EnsureCanvasGroup(GameObject go, ref CanvasGroup cached)
    {
        if (cached != null) return cached;
        cached = go.GetComponent<CanvasGroup>();
        if (cached == null) cached = go.AddComponent<CanvasGroup>();
        return cached;
    }

    private static Transform FindChildRecursive(Transform root, string targetName)
    {
        if (root == null) return null;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform ch = root.GetChild(i);
            if (ch.name == targetName) return ch;
            Transform nested = FindChildRecursive(ch, targetName);
            if (nested != null) return nested;
        }
        return null;
    }

    private static T FindComponentByName<T>(Transform root, string targetName) where T : Component
    {
        Transform tr = FindChildRecursive(root, targetName);
        return tr != null ? tr.GetComponent<T>() : null;
    }
}
