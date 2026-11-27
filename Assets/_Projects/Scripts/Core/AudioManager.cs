using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// 全局音效管理器（挂载于 LevelManager）：
/// 1. 统一接入 Assets/_Projects/Musics 下全部 9 个音效（玩家行走/奔跑、猴子巡逻/奔跑脚步、猴子发现/惊醒/处决嘶吼、水晶拾取、服务铃）；
/// 2. 猴子的脚步声与嘶吼声严格依据与玩家的直线距离衰减（附带空间立体声定位）；
/// 3. 支持通过快捷键（- / =、小键盘 - / +、[ / ]、M 静音）及暂停/设置面板滑块实时调节音效大小，并在屏幕顶部短暂显示音量提示条。
/// </summary>
public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    [Header("玩家与水晶音效")]
    [SerializeField] private AudioClip playerFootstepWalkClip;
    [SerializeField] private AudioClip playerFootstepRunClip;
    [SerializeField] private AudioClip crystalPickupClip;
    [SerializeField] private AudioClip bellRingLoopClip;

    [Header("猴子脚步与嘶吼音效")]
    [SerializeField] private AudioClip monkeyFootstepPatrolClip;
    [SerializeField] private AudioClip monkeyFootstepRunClip;
    [SerializeField] private AudioClip monkeyVoiceSpotClip;
    [SerializeField] private AudioClip monkeyVoiceWakeClip;
    [SerializeField] private AudioClip monkeyVoiceExecuteClip;

    [Header("基础音量倍率")]
    [SerializeField, Range(0f, 1.5f)] private float playerWalkVolume = 0.52f;
    [SerializeField, Range(0f, 1.5f)] private float playerRunVolume = 0.68f;
    [SerializeField, Range(0f, 1.5f)] private float crystalPickupVolume = 0.85f;
    [SerializeField, Range(0f, 1.5f)] private float monkeyPatrolStepVolume = 0.72f;
    [SerializeField, Range(0f, 1.5f)] private float monkeyRunStepVolume = 0.88f;
    [SerializeField, Range(0f, 1.5f)] private float monkeySpotRoarVolume = 0.92f;
    [SerializeField, Range(0f, 1.5f)] private float monkeyWakeRoarVolume = 0.96f;
    [SerializeField, Range(0f, 1.5f)] private float monkeyExecuteRoarVolume = 1.00f;

    [Header("猴子音效直线距离衰减参数（米）")]
    [SerializeField] private float patrolStepMinDist = 1.2f;
    [SerializeField] private float patrolStepMaxDist = 13.5f;
    [SerializeField] private float runStepMinDist = 1.5f;
    [SerializeField] private float runStepMaxDist = 18.5f;
    [SerializeField] private float voiceSpotMinDist = 2.0f;
    [SerializeField] private float voiceSpotMaxDist = 23.0f;
    [SerializeField] private float voiceWakeMinDist = 2.5f;
    [SerializeField] private float voiceWakeMaxDist = 30.0f;

    [Header("快捷键调节音效")]
    [SerializeField] private float volumeStepPerKeyPress = 0.10f;
    [SerializeField] private float volumeToastDuration = 1.85f;

    private const int SpatialPoolSize = 18;
    private AudioSource ui2DSource;
    private AudioSource executeSource;
    private AudioSource[] spatialPool;
    private float[] spatialBaseVolumes;
    private int nextSpatialIndex;

    private float savedVolumeBeforeMute = 0.8f;

    // 快捷键调节音效时的顶部临时提示条
    private GameObject volumeToastRoot;
    private CanvasGroup volumeToastGroup;
    private Image volumeToastFill;
    private Text volumeToastText;
    private float volumeToastTimer;

    public AudioClip BellRingLoopClip => bellRingLoopClip;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        GameSettings.ApplyRuntimeSettings();
        InitAudioSources();
        CreateVolumeToastHUD();
    }

    private void OnEnable()
    {
        GameSettings.OnSettingsChanged += HandleSettingsChanged;
    }

    private void OnDisable()
    {
        GameSettings.OnSettingsChanged -= HandleSettingsChanged;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void InitAudioSources()
    {
        ui2DSource = gameObject.AddComponent<AudioSource>();
        ui2DSource.playOnAwake = false;
        ui2DSource.loop = false;
        ui2DSource.spatialBlend = 0f;

        executeSource = gameObject.AddComponent<AudioSource>();
        executeSource.playOnAwake = false;
        executeSource.loop = false;
        executeSource.spatialBlend = 0f;

        spatialPool = new AudioSource[SpatialPoolSize];
        spatialBaseVolumes = new float[SpatialPoolSize];
        Transform poolParent = new GameObject("SpatialSFXPool").transform;
        poolParent.SetParent(transform, false);

        for (int i = 0; i < SpatialPoolSize; i++)
        {
            GameObject go = new GameObject($"SFX3D_{i:00}");
            go.transform.SetParent(poolParent, false);
            AudioSource src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            // 保留适度 3D 方位感，同时将 Unity 内置最小衰减距离设为极大值，确保音量完全由我们按与玩家的直线距离精确控制
            src.spatialBlend = 0.68f;
            src.dopplerLevel = 0f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = 500f;
            src.maxDistance = 1000f;
            spatialPool[i] = src;
        }
    }

    private void Update()
    {
        HandleVolumeHotkeys();
        UpdateVolumeToastHUD();
    }

    private void HandleVolumeHotkeys()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        bool decrease = kb.minusKey.wasPressedThisFrame
            || kb.numpadMinusKey.wasPressedThisFrame
            || kb.leftBracketKey.wasPressedThisFrame;
        bool increase = kb.equalsKey.wasPressedThisFrame
            || kb.numpadPlusKey.wasPressedThisFrame
            || kb.rightBracketKey.wasPressedThisFrame;
        bool toggleMute = kb.mKey.wasPressedThisFrame;

        if (!decrease && !increase && !toggleMute) return;

        float current = GameSettings.SfxVolume;
        float next = current;

        if (toggleMute)
        {
            if (current > 0.01f)
            {
                savedVolumeBeforeMute = current;
                next = 0f;
            }
            else
            {
                next = savedVolumeBeforeMute > 0.05f ? savedVolumeBeforeMute : 0.8f;
            }
        }
        else if (increase)
        {
            next = Mathf.Clamp01(Mathf.Round((current + volumeStepPerKeyPress) * 20f) / 20f);
        }
        else if (decrease)
        {
            next = Mathf.Clamp01(Mathf.Round((current - volumeStepPerKeyPress) * 20f) / 20f);
        }

        GameSettings.SfxVolume = next;
        GameSettings.SaveToDisk();
        ShowVolumeToast(next);

        // 调节非静音时播放轻柔水晶微响提示当前音量大小
        if (next > 0.01f && crystalPickupClip != null && ui2DSource != null)
        {
            ui2DSource.pitch = 1.25f;
            ui2DSource.PlayOneShot(crystalPickupClip, Mathf.Clamp01(0.24f * next));
        }
    }

    private void HandleSettingsChanged()
    {
        float sfx = GameSettings.SfxVolume;
        if (spatialPool != null)
        {
            for (int i = 0; i < spatialPool.Length; i++)
            {
                AudioSource src = spatialPool[i];
                if (src != null && src.isPlaying)
                {
                    src.volume = Mathf.Clamp01(spatialBaseVolumes[i] * sfx);
                }
            }
        }

        if (executeSource != null && executeSource.isPlaying)
        {
            executeSource.volume = Mathf.Clamp01(monkeyExecuteRoarVolume * sfx);
        }

        // 若暂停界面正在打开，顺便同步更新其滑块数值
        LevelOneOverlayUI overlay = FindAnyObjectByType<LevelOneOverlayUI>();
        if (overlay != null)
        {
            overlay.RefreshPauseSettingsUI();
        }
    }

    /// <summary>
    /// 计算声源到玩家的直线距离衰减系数（0~1，超出 maxDist 为 0）。
    /// </summary>
    public static float ComputeStraightLineDistanceAttenuation(Vector3 sourceWorldPos, float minDist, float maxDist)
    {
        Vector3 listenerPos = GetPlayerListenerPosition();
        float dist = Vector3.Distance(sourceWorldPos, listenerPos);
        if (dist >= maxDist) return 0f;
        if (dist <= minDist) return 1f;

        float linear = 1f - (dist - minDist) / Mathf.Max(0.01f, maxDist - minDist);
        // 平滑二次滚降：靠近时临场压迫感强，远处迅速隐约渐弱
        return linear * linear;
    }

    private static Vector3 GetPlayerListenerPosition()
    {
        Camera cam = Camera.main;
        if (cam != null) return cam.transform.position;
        if (PlayerInteract.ActivePlayer != null) return PlayerInteract.ActivePlayer.transform.position;
        return Vector3.zero;
    }

    private void PlaySpatialAttenuated(AudioClip clip, Vector3 worldPos, float minDist, float maxDist, float baseVolume, float pitch)
    {
        if (clip == null || spatialPool == null) return;
        float sfxVol = GameSettings.SfxVolume;
        if (sfxVol <= 0.001f) return;

        float distAtten = ComputeStraightLineDistanceAttenuation(worldPos, minDist, maxDist);
        if (distAtten <= 0.004f) return;

        // 优先挑选空闲通道，否则轮询复用最旧通道
        int slot = -1;
        for (int i = 0; i < spatialPool.Length; i++)
        {
            int idx = (nextSpatialIndex + i) % spatialPool.Length;
            if (!spatialPool[idx].isPlaying)
            {
                slot = idx;
                break;
            }
        }
        if (slot < 0)
        {
            slot = nextSpatialIndex;
        }
        nextSpatialIndex = (slot + 1) % spatialPool.Length;

        AudioSource src = spatialPool[slot];
        src.transform.position = worldPos;
        src.clip = clip;
        src.pitch = pitch;
        float unscaledVol = Mathf.Clamp01(baseVolume * distAtten);
        spatialBaseVolumes[slot] = unscaledVol;
        src.volume = Mathf.Clamp01(unscaledVol * sfxVol);
        src.Play();
    }

    // =========================================================================
    // 对外统一静态调用接口
    // =========================================================================

    public static void PlayPlayerFootstep(bool running)
    {
        if (Instance == null || Instance.ui2DSource == null) return;
        float sfx = GameSettings.SfxVolume;
        if (sfx <= 0.001f) return;

        AudioClip clip = running ? Instance.playerFootstepRunClip : Instance.playerFootstepWalkClip;
        if (clip == null) return;

        float baseVol = running ? Instance.playerRunVolume : Instance.playerWalkVolume;
        Instance.ui2DSource.pitch = running
            ? UnityEngine.Random.Range(0.95f, 1.07f)
            : UnityEngine.Random.Range(0.93f, 1.05f);
        Instance.ui2DSource.PlayOneShot(clip, Mathf.Clamp01(baseVol * sfx));
    }

    public static void PlayCrystalPickup(ShardColorType shardType)
    {
        if (Instance == null || Instance.ui2DSource == null || Instance.crystalPickupClip == null) return;
        float sfx = GameSettings.SfxVolume;
        if (sfx <= 0.001f) return;

        float pitch = shardType switch
        {
            ShardColorType.Red => 0.88f,
            ShardColorType.Yellow => 1.14f,
            _ => UnityEngine.Random.Range(0.96f, 1.06f)
        };
        float volScale = shardType == ShardColorType.NormalPurple ? 1.0f : 1.15f;

        Instance.ui2DSource.pitch = pitch;
        Instance.ui2DSource.PlayOneShot(Instance.crystalPickupClip, Mathf.Clamp01(Instance.crystalPickupVolume * volScale * sfx));
    }

    public static void PlayMonkeyFootstep(Vector3 monkeyWorldPos, bool isRunning, bool isBossKing = false)
    {
        if (Instance == null) return;
        AudioClip clip = (isRunning || isBossKing)
            ? Instance.monkeyFootstepRunClip
            : Instance.monkeyFootstepPatrolClip;
        if (clip == null) return;

        float minD = isRunning || isBossKing ? Instance.runStepMinDist : Instance.patrolStepMinDist;
        float maxD = isBossKing
            ? Instance.runStepMaxDist * 1.28f
            : (isRunning ? Instance.runStepMaxDist : Instance.patrolStepMaxDist);
        float vol = isBossKing
            ? Instance.monkeyRunStepVolume * 1.12f
            : (isRunning ? Instance.monkeyRunStepVolume : Instance.monkeyPatrolStepVolume);
        float pitch = isBossKing
            ? UnityEngine.Random.Range(0.76f, 0.86f)
            : UnityEngine.Random.Range(0.93f, 1.07f);

        Instance.PlaySpatialAttenuated(clip, monkeyWorldPos, minD, maxD, vol, pitch);
    }

    public static void PlayMonkeySpotRoar(Vector3 monkeyWorldPos, bool isBossKing = false)
    {
        if (Instance == null || Instance.monkeyVoiceSpotClip == null) return;

        float minD = Instance.voiceSpotMinDist;
        float maxD = isBossKing ? Instance.voiceSpotMaxDist * 1.32f : Instance.voiceSpotMaxDist;
        float vol = isBossKing ? Instance.monkeySpotRoarVolume * 1.12f : Instance.monkeySpotRoarVolume;
        float pitch = isBossKing
            ? UnityEngine.Random.Range(0.74f, 0.82f)
            : UnityEngine.Random.Range(0.94f, 1.06f);

        Instance.PlaySpatialAttenuated(Instance.monkeyVoiceSpotClip, monkeyWorldPos, minD, maxD, vol, pitch);
    }

    public static void PlayMonkeyWakeRoar(Vector3 monkeyWorldPos, bool isBossKing = false, bool globalBossAlert = false)
    {
        if (Instance == null || Instance.monkeyVoiceWakeClip == null) return;

        float minD = globalBossAlert ? 6.0f : Instance.voiceWakeMinDist;
        float maxD = globalBossAlert ? 65.0f : (isBossKing ? Instance.voiceWakeMaxDist * 1.35f : Instance.voiceWakeMaxDist);
        float vol = isBossKing ? Instance.monkeyWakeRoarVolume * 1.15f : Instance.monkeyWakeRoarVolume;
        float pitch = isBossKing
            ? UnityEngine.Random.Range(0.72f, 0.80f)
            : UnityEngine.Random.Range(0.95f, 1.05f);

        Instance.PlaySpatialAttenuated(Instance.monkeyVoiceWakeClip, monkeyWorldPos, minD, maxD, vol, pitch);
    }

    public static void PlayMonkeyExecuteRoar()
    {
        if (Instance == null || Instance.executeSource == null || Instance.monkeyVoiceExecuteClip == null) return;
        float sfx = GameSettings.SfxVolume;
        if (sfx <= 0.001f) return;

        Instance.executeSource.Stop();
        Instance.executeSource.clip = Instance.monkeyVoiceExecuteClip;
        Instance.executeSource.pitch = UnityEngine.Random.Range(0.96f, 1.04f);
        Instance.executeSource.volume = Mathf.Clamp01(Instance.monkeyExecuteRoarVolume * sfx);
        Instance.executeSource.Play();
    }

    /// <summary>
    /// 首领入场仰天怒吼音效（低沉浑厚、全屏冲击感）。
    /// </summary>
    public static void PlayKingEntranceRoar()
    {
        if (Instance == null || Instance.executeSource == null) return;
        float sfx = GameSettings.SfxVolume;
        if (sfx <= 0.001f) return;

        AudioClip clip = Instance.monkeyVoiceWakeClip != null
            ? Instance.monkeyVoiceWakeClip
            : Instance.monkeyVoiceExecuteClip;
        if (clip == null) return;

        Instance.executeSource.Stop();
        Instance.executeSource.clip = clip;
        Instance.executeSource.pitch = 0.74f;
        Instance.executeSource.volume = Mathf.Clamp01(Instance.monkeyWakeRoarVolume * 1.15f * sfx);
        Instance.executeSource.Play();
    }

    public static void StopExecuteRoar()
    {
        if (Instance != null && Instance.executeSource != null && Instance.executeSource.isPlaying)
        {
            Instance.executeSource.Stop();
        }
    }

    // =========================================================================
    // 快捷键调节音效时的屏幕顶部提示条
    // =========================================================================

    private void CreateVolumeToastHUD()
    {
        GameObject canvasGo = new GameObject(
            "AudioVolumeToastCanvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler));
        canvasGo.transform.SetParent(transform, false);

        Canvas c = canvasGo.GetComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay;
        c.sortingOrder = 460;

        CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        GameObject panel = new GameObject(
            "VolumeToastPanel",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(CanvasGroup),
            typeof(Image),
            typeof(Outline));
        panel.transform.SetParent(canvasGo.transform, false);

        RectTransform prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = new Vector2(0.5f, 1f);
        prt.anchorMax = new Vector2(0.5f, 1f);
        prt.pivot = new Vector2(0.5f, 1f);
        prt.anchoredPosition = new Vector2(0f, -28f);
        prt.sizeDelta = new Vector2(360f, 64f);

        Image bg = panel.GetComponent<Image>();
        bg.color = new Color(0.06f, 0.05f, 0.09f, 0.84f);
        bg.raycastTarget = false;

        Outline border = panel.GetComponent<Outline>();
        border.effectColor = new Color(0.84f, 0.68f, 0.34f, 0.85f);
        border.effectDistance = new Vector2(1.5f, -1.5f);

        GameObject txtGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        txtGo.transform.SetParent(panel.transform, false);
        RectTransform trt = txtGo.GetComponent<RectTransform>();
        trt.anchorMin = new Vector2(0f, 0.42f);
        trt.anchorMax = new Vector2(1f, 1f);
        trt.offsetMin = new Vector2(16f, 0f);
        trt.offsetMax = new Vector2(-16f, -4f);

        volumeToastText = txtGo.GetComponent<Text>();
        volumeToastText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        volumeToastText.fontSize = 20;
        volumeToastText.fontStyle = FontStyle.Bold;
        volumeToastText.alignment = TextAnchor.MiddleCenter;
        volumeToastText.color = new Color(0.96f, 0.92f, 0.84f, 1f);
        volumeToastText.raycastTarget = false;

        GameObject barBgGo = new GameObject("BarBg", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        barBgGo.transform.SetParent(panel.transform, false);
        RectTransform bgrt = barBgGo.GetComponent<RectTransform>();
        bgrt.anchorMin = new Vector2(0f, 0f);
        bgrt.anchorMax = new Vector2(1f, 0f);
        bgrt.pivot = new Vector2(0.5f, 0f);
        bgrt.anchoredPosition = new Vector2(0f, 12f);
        bgrt.sizeDelta = new Vector2(-36f, 10f);
        Image barBg = barBgGo.GetComponent<Image>();
        barBg.color = new Color(0.22f, 0.20f, 0.28f, 0.90f);
        barBg.raycastTarget = false;

        GameObject barFillGo = new GameObject("BarFill", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        barFillGo.transform.SetParent(barBgGo.transform, false);
        RectTransform frt = barFillGo.GetComponent<RectTransform>();
        frt.anchorMin = Vector2.zero;
        frt.anchorMax = Vector2.one;
        frt.offsetMin = Vector2.zero;
        frt.offsetMax = Vector2.zero;
        volumeToastFill = barFillGo.GetComponent<Image>();
        volumeToastFill.color = new Color(0.94f, 0.76f, 0.32f, 1f);
        volumeToastFill.type = Image.Type.Filled;
        volumeToastFill.fillMethod = Image.FillMethod.Horizontal;
        volumeToastFill.fillAmount = GameSettings.SfxVolume;
        volumeToastFill.raycastTarget = false;

        volumeToastGroup = panel.GetComponent<CanvasGroup>();
        volumeToastGroup.interactable = false;
        volumeToastGroup.blocksRaycasts = false;
        volumeToastRoot = panel;
        volumeToastRoot.SetActive(false);
    }

    private void ShowVolumeToast(float volume)
    {
        if (volumeToastRoot == null) return;
        int pct = Mathf.RoundToInt(Mathf.Clamp01(volume) * 100f);
        if (volumeToastText != null)
        {
            volumeToastText.text = pct <= 0
                ? "音效已静音  [M 恢复 | - / = 调节]"
                : $"音效音量 {pct}%  [- / = 调节 | M 静音]";
        }
        if (volumeToastFill != null)
        {
            volumeToastFill.fillAmount = Mathf.Clamp01(volume);
        }
        volumeToastTimer = Mathf.Max(0.5f, volumeToastDuration);
        volumeToastGroup.alpha = 1f;
        volumeToastRoot.SetActive(true);
    }

    private void UpdateVolumeToastHUD()
    {
        if (volumeToastRoot == null || !volumeToastRoot.activeSelf) return;

        volumeToastTimer -= Time.unscaledDeltaTime;
        if (volumeToastTimer <= 0f)
        {
            volumeToastRoot.SetActive(false);
            return;
        }

        if (volumeToastTimer < 0.40f && volumeToastGroup != null)
        {
            volumeToastGroup.alpha = Mathf.Clamp01(volumeToastTimer / 0.40f);
        }
    }
}
