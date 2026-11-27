using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 主菜单全界面交互控制器（挂载于 MainMenuCanvas）：
/// - 主界面 5 按钮悬停/键盘切换高亮（右侧血红棱形指示器 + 发光底），选中「继续游戏」时右侧裂隙前浮出实时存档卡；
/// - 「新游戏」有进行中存档时弹出覆盖确认弹窗（保留历史纪录与已解锁关卡），无存档直接淡黑进入第一关；
/// - 「选择关卡」横向 4 关卡片选择、展示真实最高收集与最快用时、锁定态抖动提示，独立重玩不覆盖主线存档；
/// - 「设置」暗晶面板支持音效、背景音乐、鼠标灵敏度滑块调节及带拨钮左右跳动的镜头晃动开关；
/// - 支持 ESC 返回上一级面板与平滑黑幕场景过渡。
/// </summary>
public class MainMenuController : MonoBehaviour
{
    private static readonly Color ToggleTextOnColor = new Color(0.86f, 0.78f, 0.98f, 1f);
    private static readonly Color ToggleTextOffColor = new Color(0.55f, 0.54f, 0.62f, 0.90f);
    private static readonly Color HeartFullColor = Color.white;
    private static readonly Color HeartLostColor = new Color(0.24f, 0.22f, 0.28f, 0.45f);
    private static readonly Color ActiveBtnColor = new Color(1f, 0.96f, 1f, 0.98f);
    private static readonly Color NormalBtnColor = new Color(0.86f, 0.88f, 0.93f, 0.92f);
    private static readonly Color DisabledBtnColor = new Color(0.45f, 0.46f, 0.52f, 0.52f);

    [Header("场景配置")]
    [SerializeField] private string levelOneSceneName = "LevelOne";
    [SerializeField] private float sceneFadeDuration = 0.45f;

    [Header("主界面与面板根节点（留空按场景名称自动查找）")]
    [SerializeField] private GameObject mainView;
    [SerializeField] private GameObject saveCard;
    [SerializeField] private GameObject levelSelectPanel;
    [SerializeField] private GameObject settingsPanel;
    [SerializeField] private GameObject overwriteConfirmModal;

    [Header("存档卡生命图标")]
    [SerializeField] private Sprite fullHeartSprite;
    [SerializeField] private Sprite depletedHeartSprite;

    [Header("主菜单按钮未选中 / 悬停选中底板")]
    [SerializeField] private Sprite unselectedButtonSprite;
    [SerializeField] private Sprite selectedButtonSprite;

    private struct MainButtonEntry
    {
        public Button button;
        public Image background;
        public GameObject selectedGlow;
        public GameObject selectedIndicator;
        public Text label;
        public Image icon;
    }

    private struct LevelCardEntry
    {
        public int levelIndex;
        public RectTransform root;
        public Button cardButton;
        public GameObject selectedHighlight;
        public GameObject selectedIndicator;
        public GameObject lockedVeil;
        public GameObject lockedIcon;
        public GameObject emblemImage;
        public Text levelNameText;
        public Text bestShardsText;
        public Text bestTimeText;
        public Vector2 restingPos;
    }

    private readonly MainButtonEntry[] mainButtons = new MainButtonEntry[5];
    private readonly LevelCardEntry[] levelCards = new LevelCardEntry[4];

    private Text saveLevelText;
    private Text saveShardsText;
    private Text saveLivesText;
    private readonly Image[] saveHearts = new Image[3];

    private Button btnEnterLevel;
    private Button btnLevelSelectBack;
    private Text levelSelectSubtitle;

    private Slider settingsSfxSlider;
    private Slider settingsBgmSlider;
    private Slider settingsMouseSensSlider;
    private Text settingsSfxValueText;
    private Text settingsBgmValueText;
    private Text settingsMouseSensValueText;
    private Button settingsShakeToggleBtn;
    private RectTransform settingsShakeKnob;
    private Image settingsShakeActiveFill;
    private Text settingsShakeStateText;
    private float settingsShakeKnobTravelX = 42f;
    private float settingsShakeVisualT = 1f;

    private CanvasGroup screenFadeGroup;
    private int currentMainIndex;
    private int selectedLevelIndex = 1;
    private bool hasActiveSave;
    private bool isTransitioning;

    private void Awake()
    {
        Time.timeScale = 1f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        GameSettings.ApplyRuntimeSettings();
        AutoBindSceneHierarchy();
        CreateScreenFadeCurtain();
        WireAllInteractions();
    }

    private void Start()
    {
        RefreshSaveStateAndMainView();
        ShowMainViewOnly();
        StartCoroutine(FadeCurtainRoutine(1f, 0f, 0.5f));
    }

    private void Update()
    {
        // 平滑更新设置界面震动拨动开关的滑块位置与填充显隐
        if (settingsPanel != null && settingsPanel.activeInHierarchy && settingsShakeKnob != null)
        {
            float target = GameSettings.CameraShakeEnabled ? 1f : 0f;
            if (!Mathf.Approximately(settingsShakeVisualT, target))
            {
                settingsShakeVisualT = Mathf.MoveTowards(settingsShakeVisualT, target, Time.unscaledDeltaTime * 9f);
                ApplySettingsShakeVisual(settingsShakeVisualT);
            }
        }

        if (isTransitioning) return;

        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        // ESC 返回上一级弹窗或主界面
        if (kb.escapeKey.wasPressedThisFrame)
        {
            if (overwriteConfirmModal != null && overwriteConfirmModal.activeSelf)
            {
                CloseOverwriteModal();
            }
            else if (settingsPanel != null && settingsPanel.activeSelf)
            {
                CloseSettingsPanel();
            }
            else if (levelSelectPanel != null && levelSelectPanel.activeSelf)
            {
                CloseLevelSelectPanel();
            }
        }
        else if (levelSelectPanel != null && levelSelectPanel.activeSelf)
        {
            // 选关页面支持左右方向键切换已解锁关卡
            if (kb.leftArrowKey.wasPressedThisFrame || kb.aKey.wasPressedThisFrame)
            {
                TryStepLevelSelection(-1);
            }
            else if (kb.rightArrowKey.wasPressedThisFrame || kb.dKey.wasPressedThisFrame)
            {
                TryStepLevelSelection(1);
            }
        }
    }

    // =========================================================================
    // 主界面逻辑
    // =========================================================================

    private void RefreshSaveStateAndMainView()
    {
        hasActiveSave = GameSettings.HasActiveSave;

        // 1. 「继续游戏」无存档时灰显不可点，有存档时正常可用并默认选中
        MainButtonEntry cont = mainButtons[0];
        if (cont.button != null)
        {
            cont.button.interactable = hasActiveSave;
            if (cont.background != null) cont.background.color = hasActiveSave ? ActiveBtnColor : DisabledBtnColor;
            if (cont.label != null) cont.label.color = hasActiveSave ? new Color(0.95f, 0.93f, 0.89f, 1f) : new Color(0.50f, 0.50f, 0.54f, 0.65f);
            if (cont.icon != null) cont.icon.color = hasActiveSave ? new Color(1f, 0.97f, 0.90f, 1f) : new Color(0.45f, 0.45f, 0.50f, 0.55f);
        }

        // 2. 填充右侧存档卡真实数据
        if (hasActiveSave)
        {
            if (saveLevelText != null) saveLevelText.text = GameSettings.GetLevelDisplayName(GameSettings.SaveLevelIndex);
            if (saveShardsText != null) saveShardsText.text = $"{GameSettings.SaveShards} / {GameSettings.SaveTotalShards}";
            int lives = GameSettings.SaveRemainingLives;
            if (saveLivesText != null) saveLivesText.text = $"{lives}/3";
            for (int i = 0; i < saveHearts.Length; i++)
            {
                if (saveHearts[i] != null)
                {
                    bool alive = i < lives;
                    if (alive && fullHeartSprite != null)
                    {
                        saveHearts[i].sprite = fullHeartSprite;
                    }
                    else if (!alive && depletedHeartSprite != null)
                    {
                        saveHearts[i].sprite = depletedHeartSprite;
                    }
                    saveHearts[i].color = (alive || depletedHeartSprite != null) ? HeartFullColor : HeartLostColor;
                }
            }
        }

        SelectMainButton(hasActiveSave ? 0 : 1);
    }

    private void SelectMainButton(int index)
    {
        if (index < 0 || index >= mainButtons.Length) return;
        if (index == 0 && !hasActiveSave) return;

        currentMainIndex = index;

        for (int i = 0; i < mainButtons.Length; i++)
        {
            bool isSelected = i == index;
            bool isDisabled = i == 0 && !hasActiveSave;
            MainButtonEntry entry = mainButtons[i];

            if (entry.background != null)
            {
                if (selectedButtonSprite != null && unselectedButtonSprite != null)
                {
                    entry.background.sprite = isSelected ? selectedButtonSprite : unselectedButtonSprite;
                    entry.background.type = Image.Type.Simple;
                }
                if (!isDisabled)
                {
                    entry.background.color = isSelected ? ActiveBtnColor : NormalBtnColor;
                }
            }

            // 直接切换底板贴图时隐藏叠加发光层避免边角穿帮；未配置双图时仍退回显隐 SelectedGlow
            if (entry.selectedGlow != null)
            {
                bool useDirectSwap = selectedButtonSprite != null && unselectedButtonSprite != null;
                entry.selectedGlow.SetActive(!useDirectSwap && isSelected);
            }
            if (entry.selectedIndicator != null) entry.selectedIndicator.SetActive(isSelected);
        }

        // 仅当选中「继续游戏」且存在进行中存档时，裂隙前浮出存档卡；选中「新游戏」等其他项时隐藏存档卡，保留纯净紫色裂隙
        if (saveCard != null)
        {
            saveCard.SetActive(index == 0 && hasActiveSave);
        }
    }

    private void OnClickContinue()
    {
        if (isTransitioning || !hasActiveSave) return;
        GameSettings.PendingLaunchMode = LevelLaunchMode.ContinueSave;
        StartCoroutine(FadeAndLoadSceneRoutine(levelOneSceneName));
    }

    private void OnClickNewGame()
    {
        if (isTransitioning) return;
        if (hasActiveSave && overwriteConfirmModal != null)
        {
            overwriteConfirmModal.SetActive(true);
            return;
        }

        StartFreshNewGame();
    }

    private void OnClickConfirmOverwrite()
    {
        if (isTransitioning) return;
        CloseOverwriteModal();
        GameSettings.ClearActiveSave();
        StartFreshNewGame();
    }

    private void CloseOverwriteModal()
    {
        if (overwriteConfirmModal != null) overwriteConfirmModal.SetActive(false);
    }

    private void StartFreshNewGame()
    {
        GameSettings.PendingLaunchMode = LevelLaunchMode.NewGame;
        StartCoroutine(FadeAndLoadSceneRoutine(levelOneSceneName));
    }

    // =========================================================================
    // 横向选关面板逻辑
    // =========================================================================

    private void OpenLevelSelectPanel()
    {
        if (isTransitioning) return;
        if (mainView != null) mainView.SetActive(false);
        if (settingsPanel != null) settingsPanel.SetActive(false);
        if (overwriteConfirmModal != null) overwriteConfirmModal.SetActive(false);
        if (levelSelectPanel != null) levelSelectPanel.SetActive(true);

        RefreshLevelSelectCards();
        SelectLevelCard(Mathf.Clamp(selectedLevelIndex, 1, GameSettings.UnlockedLevelCount));
    }

    private void CloseLevelSelectPanel()
    {
        if (levelSelectPanel != null) levelSelectPanel.SetActive(false);
        if (mainView != null) mainView.SetActive(true);
        SelectMainButton(2);
    }

    private void RefreshLevelSelectCards()
    {
        int unlocked = GameSettings.UnlockedLevelCount;
        if (levelSelectSubtitle != null)
        {
            levelSelectSubtitle.text = $"已解锁 {unlocked} / 4  ·  独立重玩不影响进行中的主线存档";
        }

        for (int i = 0; i < levelCards.Length; i++)
        {
            LevelCardEntry card = levelCards[i];
            int lvl = card.levelIndex;
            // 当前版本仅第一关实装可进入
            bool isPlayable = lvl == 1 && lvl <= unlocked;

            if (card.lockedVeil != null) card.lockedVeil.SetActive(!isPlayable);
            if (card.lockedIcon != null) card.lockedIcon.SetActive(!isPlayable);
            if (card.emblemImage != null) card.emblemImage.SetActive(isPlayable);

            if (isPlayable)
            {
                if (card.levelNameText != null) card.levelNameText.text = GameSettings.GetLevelDisplayName(lvl);
                if (card.bestShardsText != null) card.bestShardsText.text = $"{GameSettings.GetBestShards(lvl)} / 192";
                if (card.bestTimeText != null) card.bestTimeText.text = GameSettings.FormatRunTime(GameSettings.GetBestTime(lvl));
            }
            else
            {
                if (card.levelNameText != null) card.levelNameText.text = $"第{ToChineseNum(lvl)}关 · 未解锁";
                if (card.bestShardsText != null) card.bestShardsText.text = "-- / ---";
                if (card.bestTimeText != null) card.bestTimeText.text = "--:--.--";
            }
        }
    }

    private void OnClickLevelCard(int levelIndex)
    {
        bool isPlayable = levelIndex == 1 && levelIndex <= GameSettings.UnlockedLevelCount;
        if (!isPlayable)
        {
            // 未解锁卡片轻微左右抖动反馈，不切换选中态
            int idx = levelIndex - 1;
            if (idx >= 0 && idx < levelCards.Length && levelCards[idx].root != null)
            {
                StartCoroutine(ShakeLockedCardRoutine(levelCards[idx]));
            }
            return;
        }

        SelectLevelCard(levelIndex);
    }

    private void SelectLevelCard(int levelIndex)
    {
        selectedLevelIndex = levelIndex;
        for (int i = 0; i < levelCards.Length; i++)
        {
            bool sel = levelCards[i].levelIndex == levelIndex;
            if (levelCards[i].selectedHighlight != null) levelCards[i].selectedHighlight.SetActive(sel);
            if (levelCards[i].selectedIndicator != null) levelCards[i].selectedIndicator.SetActive(sel);
        }

        if (btnEnterLevel != null)
        {
            btnEnterLevel.interactable = selectedLevelIndex == 1;
        }
    }

    private void TryStepLevelSelection(int dir)
    {
        int candidate = Mathf.Clamp(selectedLevelIndex + dir, 1, levelCards.Length);
        if (candidate == 1)
        {
            SelectLevelCard(1);
        }
        else
        {
            OnClickLevelCard(candidate);
        }
    }

    private void OnClickEnterSelectedLevel()
    {
        if (isTransitioning || selectedLevelIndex != 1) return;
        // 选关模式以独立重玩启动，不覆盖主线存档
        GameSettings.PendingLaunchMode = LevelLaunchMode.ReplayLevel;
        StartCoroutine(FadeAndLoadSceneRoutine(levelOneSceneName));
    }

    private IEnumerator ShakeLockedCardRoutine(LevelCardEntry card)
    {
        float dur = 0.24f;
        float elapsed = 0f;
        while (elapsed < dur)
        {
            elapsed += Time.unscaledDeltaTime;
            float p = elapsed / dur;
            float offsetX = Mathf.Sin(p * Mathf.PI * 4f) * (1f - p) * 9f;
            card.root.anchoredPosition = card.restingPos + new Vector2(offsetX, 0f);
            yield return null;
        }
        card.root.anchoredPosition = card.restingPos;
    }

    // =========================================================================
    // 设置面板逻辑（含鼠标灵敏度 + 震动拨钮跳动）
    // =========================================================================

    private void OpenSettingsPanel()
    {
        if (isTransitioning) return;
        if (settingsPanel != null) settingsPanel.SetActive(true);
        RefreshSettingsPanelUI();
    }

    private void CloseSettingsPanel()
    {
        GameSettings.SaveToDisk();
        if (settingsPanel != null) settingsPanel.SetActive(false);
        SelectMainButton(3);
    }

    private void RefreshSettingsPanelUI()
    {
        if (settingsSfxSlider != null)
        {
            float v = GameSettings.SfxVolume;
            settingsSfxSlider.SetValueWithoutNotify(v);
            if (settingsSfxValueText != null) settingsSfxValueText.text = $"{Mathf.RoundToInt(v * 100f)}%";
        }

        if (settingsBgmSlider != null)
        {
            float v = GameSettings.BgmVolume;
            settingsBgmSlider.SetValueWithoutNotify(v);
            if (settingsBgmValueText != null) settingsBgmValueText.text = $"{Mathf.RoundToInt(v * 100f)}%";
        }

        if (settingsMouseSensSlider != null)
        {
            float v = GameSettings.MouseSensitivity;
            settingsMouseSensSlider.SetValueWithoutNotify(v);
            if (settingsMouseSensValueText != null) settingsMouseSensValueText.text = $"{Mathf.RoundToInt(v * 100f)}%";
        }

        settingsShakeVisualT = GameSettings.CameraShakeEnabled ? 1f : 0f;
        ApplySettingsShakeVisual(settingsShakeVisualT);
    }

    private void ApplySettingsShakeVisual(float t)
    {
        float eased = t * t * (3f - 2f * t);
        if (settingsShakeKnob != null)
        {
            Vector2 pos = settingsShakeKnob.anchoredPosition;
            pos.x = Mathf.Lerp(-settingsShakeKnobTravelX, settingsShakeKnobTravelX, eased);
            settingsShakeKnob.anchoredPosition = pos;
        }
        if (settingsShakeActiveFill != null)
        {
            Color c = settingsShakeActiveFill.color;
            c.a = eased * 0.92f;
            settingsShakeActiveFill.color = c;
            settingsShakeActiveFill.enabled = eased > 0.01f;
        }
        UpdateSettingsShakeText(GameSettings.CameraShakeEnabled);
    }

    private void UpdateSettingsShakeText(bool isOn)
    {
        if (settingsShakeStateText != null)
        {
            settingsShakeStateText.text = isOn ? "开启" : "关闭";
            settingsShakeStateText.color = isOn ? ToggleTextOnColor : ToggleTextOffColor;
        }
    }

    // =========================================================================
    // 退出与场景切换
    // =========================================================================

    private void OnClickExitGame()
    {
        if (isTransitioning) return;
        StartCoroutine(FadeAndQuitRoutine());
    }

    private void ShowMainViewOnly()
    {
        if (mainView != null) mainView.SetActive(true);
        if (levelSelectPanel != null) levelSelectPanel.SetActive(false);
        if (settingsPanel != null) settingsPanel.SetActive(false);
        if (overwriteConfirmModal != null) overwriteConfirmModal.SetActive(false);
    }

    private IEnumerator FadeAndLoadSceneRoutine(string sceneName)
    {
        isTransitioning = true;
        GameSettings.SaveToDisk();
        yield return FadeCurtainRoutine(0f, 1f, sceneFadeDuration);
        Time.timeScale = 1f;
        SceneManager.LoadScene(sceneName);
    }

    private IEnumerator FadeAndQuitRoutine()
    {
        isTransitioning = true;
        GameSettings.SaveToDisk();
        yield return FadeCurtainRoutine(0f, 1f, 0.35f);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
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
        GameObject go = new GameObject("MainMenuFadeCurtain", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup), typeof(Image));
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
        screenFadeGroup.alpha = 1f;
        screenFadeGroup.interactable = false;
        screenFadeGroup.blocksRaycasts = false;
    }

    // =========================================================================
    // 自动绑定与事件连线
    // =========================================================================

    private void AutoBindSceneHierarchy()
    {
        Transform root = transform;
        if (mainView == null) mainView = FindChildObject(root, "MainView");
        if (saveCard == null) saveCard = FindChildObject(root, "SaveCard");
        if (levelSelectPanel == null) levelSelectPanel = FindChildObject(root, "LevelSelectPanel");
        if (settingsPanel == null) settingsPanel = FindChildObject(root, "SettingsPanel");
        if (overwriteConfirmModal == null) overwriteConfirmModal = FindChildObject(root, "OverwriteConfirmModal");

        string[] btnNames = { "Btn_Continue", "Btn_NewGame", "Btn_SelectLevel", "Btn_Settings", "Btn_Exit" };
        Transform mv = mainView != null ? mainView.transform : root;
        for (int i = 0; i < btnNames.Length; i++)
        {
            Transform bt = FindChildRecursive(mv, btnNames[i]);
            if (bt == null) continue;
            mainButtons[i] = new MainButtonEntry
            {
                button = bt.GetComponent<Button>(),
                background = bt.GetComponent<Image>(),
                selectedGlow = FindChildObject(bt, "SelectedGlow"),
                selectedIndicator = FindChildObject(bt, "SelectedIndicator"),
                label = FindComponentByName<Text>(bt, "Label"),
                icon = FindComponentByName<Image>(bt, "Icon")
            };
        }

        if (saveCard != null)
        {
            Transform sc = saveCard.transform;
            saveLevelText = FindComponentByName<Text>(sc, "Txt_SaveLevel");
            saveShardsText = FindComponentByName<Text>(sc, "Txt_SaveShards");
            saveLivesText = FindComponentByName<Text>(sc, "Txt_SaveLives");
            saveHearts[0] = FindComponentByName<Image>(sc, "Heart_1");
            saveHearts[1] = FindComponentByName<Image>(sc, "Heart_2");
            saveHearts[2] = FindComponentByName<Image>(sc, "Heart_3");
        }

        if (levelSelectPanel != null)
        {
            Transform ls = levelSelectPanel.transform;
            levelSelectSubtitle = FindComponentByName<Text>(ls, "SubTitle");
            btnEnterLevel = FindComponentByName<Button>(ls, "Btn_EnterLevel");
            btnLevelSelectBack = FindComponentByName<Button>(ls, "Btn_LevelSelectBack");

            for (int i = 0; i < 4; i++)
            {
                Transform ct = FindChildRecursive(ls, $"LevelCard_0{i + 1}");
                if (ct == null) continue;
                RectTransform rt = ct as RectTransform;
                levelCards[i] = new LevelCardEntry
                {
                    levelIndex = i + 1,
                    root = rt,
                    cardButton = ct.GetComponentInChildren<Button>(true),
                    selectedHighlight = FindChildObject(ct, "SelectedHighlight"),
                    selectedIndicator = FindChildObject(ct, "SelectedIndicator"),
                    lockedVeil = FindChildObject(ct, "LockedVeil"),
                    lockedIcon = FindChildObject(ct, "LockedIcon"),
                    emblemImage = FindChildObject(ct, "LevelEmblemImage"),
                    levelNameText = FindComponentByName<Text>(ct, "Txt_LevelName"),
                    bestShardsText = FindComponentByName<Text>(ct, "Txt_BestShards"),
                    bestTimeText = FindComponentByName<Text>(ct, "Txt_BestTime"),
                    restingPos = rt != null ? rt.anchoredPosition : Vector2.zero
                };
            }
        }
    }

    private void WireAllInteractions()
    {
        // 1. 主菜单 5 按钮悬停/选中与点击
        Action[] clickHandlers = { OnClickContinue, OnClickNewGame, OpenLevelSelectPanel, OpenSettingsPanel, OnClickExitGame };
        for (int i = 0; i < mainButtons.Length; i++)
        {
            Button btn = mainButtons[i].button;
            if (btn == null) continue;
            int idx = i;
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => clickHandlers[idx]?.Invoke());

            MenuButtonHoverRelay relay = btn.gameObject.GetComponent<MenuButtonHoverRelay>();
            if (relay == null) relay = btn.gameObject.AddComponent<MenuButtonHoverRelay>();
            relay.Init(() => SelectMainButton(idx));
        }

        // 2. 选关卡片与底部按钮
        for (int i = 0; i < levelCards.Length; i++)
        {
            if (levelCards[i].cardButton == null) continue;
            int lvl = levelCards[i].levelIndex;
            levelCards[i].cardButton.onClick.RemoveAllListeners();
            levelCards[i].cardButton.onClick.AddListener(() => OnClickLevelCard(lvl));
        }

        if (btnEnterLevel != null)
        {
            btnEnterLevel.onClick.RemoveAllListeners();
            btnEnterLevel.onClick.AddListener(OnClickEnterSelectedLevel);
        }
        if (btnLevelSelectBack != null)
        {
            btnLevelSelectBack.onClick.RemoveAllListeners();
            btnLevelSelectBack.onClick.AddListener(CloseLevelSelectPanel);
        }

        // 3. 覆盖存档确认弹窗
        if (overwriteConfirmModal != null)
        {
            Button btnConfirm = FindComponentByName<Button>(overwriteConfirmModal.transform, "Btn_ConfirmOverwrite");
            Button btnCancel = FindComponentByName<Button>(overwriteConfirmModal.transform, "Btn_CancelOverwrite");
            if (btnConfirm != null)
            {
                btnConfirm.onClick.RemoveAllListeners();
                btnConfirm.onClick.AddListener(OnClickConfirmOverwrite);
            }
            if (btnCancel != null)
            {
                btnCancel.onClick.RemoveAllListeners();
                btnCancel.onClick.AddListener(CloseOverwriteModal);
            }
        }

        // 4. 设置面板控件
        if (settingsPanel != null)
        {
            Transform sp = settingsPanel.transform;

            settingsSfxSlider = FindComponentByName<Slider>(sp, "SFXSlider");
            if (settingsSfxSlider != null)
            {
                settingsSfxValueText = FindComponentByName<Text>(settingsSfxSlider.transform.parent, "ValueText");
                settingsSfxSlider.onValueChanged.RemoveAllListeners();
                settingsSfxSlider.onValueChanged.AddListener(v =>
                {
                    GameSettings.SfxVolume = v;
                    if (settingsSfxValueText != null) settingsSfxValueText.text = $"{Mathf.RoundToInt(v * 100f)}%";
                });
            }

            settingsBgmSlider = FindComponentByName<Slider>(sp, "BGMSlider");
            if (settingsBgmSlider != null)
            {
                settingsBgmValueText = FindComponentByName<Text>(settingsBgmSlider.transform.parent, "ValueText");
                settingsBgmSlider.onValueChanged.RemoveAllListeners();
                settingsBgmSlider.onValueChanged.AddListener(v =>
                {
                    GameSettings.BgmVolume = v;
                    if (settingsBgmValueText != null) settingsBgmValueText.text = $"{Mathf.RoundToInt(v * 100f)}%";
                });
            }

            settingsMouseSensSlider = FindComponentByName<Slider>(sp, "MouseSensitivitySlider");
            if (settingsMouseSensSlider != null)
            {
                settingsMouseSensSlider.minValue = GameSettings.MinMouseSensitivity;
                settingsMouseSensSlider.maxValue = GameSettings.MaxMouseSensitivity;
                settingsMouseSensValueText = FindComponentByName<Text>(settingsMouseSensSlider.transform.parent, "ValueText");
                settingsMouseSensSlider.onValueChanged.RemoveAllListeners();
                settingsMouseSensSlider.onValueChanged.AddListener(v =>
                {
                    GameSettings.MouseSensitivity = v;
                    if (settingsMouseSensValueText != null) settingsMouseSensValueText.text = $"{Mathf.RoundToInt(v * 100f)}%";
                });
            }

            settingsShakeToggleBtn = FindComponentByName<Button>(sp, "ShakeToggleButton");
            if (settingsShakeToggleBtn != null)
            {
                Transform row = settingsShakeToggleBtn.transform.parent;
                settingsShakeStateText = FindComponentByName<Text>(row, "StateText");
                settingsShakeKnob = FindChildRecursive(settingsShakeToggleBtn.transform, "Knob") as RectTransform;
                settingsShakeActiveFill = FindComponentByName<Image>(settingsShakeToggleBtn.transform, "ActiveFill");

                if (settingsShakeKnob != null && Mathf.Abs(settingsShakeKnob.anchoredPosition.x) > 5f)
                {
                    settingsShakeKnobTravelX = Mathf.Abs(settingsShakeKnob.anchoredPosition.x);
                }

                settingsShakeToggleBtn.onClick.RemoveAllListeners();
                settingsShakeToggleBtn.onClick.AddListener(() =>
                {
                    bool nextOn = !GameSettings.CameraShakeEnabled;
                    GameSettings.CameraShakeEnabled = nextOn;
                    GameSettings.SaveToDisk();
                    UpdateSettingsShakeText(nextOn);
                });
            }

            Button btnBack = FindComponentByName<Button>(sp, "Btn_SettingsBack");
            if (btnBack != null)
            {
                btnBack.onClick.RemoveAllListeners();
                btnBack.onClick.AddListener(CloseSettingsPanel);
            }

            RefreshSettingsPanelUI();
        }
    }

    private static string ToChineseNum(int n) => n switch { 1 => "一", 2 => "二", 3 => "三", 4 => "四", _ => n.ToString() };

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

    private static GameObject FindChildObject(Transform root, string targetName)
    {
        Transform tr = FindChildRecursive(root, targetName);
        return tr != null ? tr.gameObject : null;
    }

    private static T FindComponentByName<T>(Transform root, string targetName) where T : Component
    {
        Transform tr = FindChildRecursive(root, targetName);
        return tr != null ? tr.GetComponent<T>() : null;
    }
}

public class MenuButtonHoverRelay : MonoBehaviour, IPointerEnterHandler, ISelectHandler
{
    private Action onHighlighted;

    public void Init(Action callback)
    {
        onHighlighted = callback;
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        onHighlighted?.Invoke();
    }

    public void OnSelect(BaseEventData eventData)
    {
        onHighlighted?.Invoke();
    }
}
