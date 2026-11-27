using System;
using UnityEngine;

public enum LevelLaunchMode
{
    NewGame = 0,
    ContinueSave = 1,
    ReplayLevel = 2
}

/// <summary>
/// 全局设置与玩家档案/存档管理器（统一服务主菜单与局内所有面板）：
/// - 音效音量、背景音乐音量、鼠标灵敏度（0.2x ~ 2.5x）、镜头晃动开关；
/// - 进行中的主线存档（当前关卡、已收集碎片数、剩余命数）；
/// - 独立保留的玩家档案纪录（已解锁关卡数、各关历史最高收集数与最快通关时间）。
/// </summary>
public static class GameSettings
{
    public const float MinMouseSensitivity = 0.2f;
    public const float MaxMouseSensitivity = 2.5f;

    private const string KeySfxVolume = "Settings_SfxVolume";
    private const string KeyBgmVolume = "Settings_BgmVolume";
    private const string KeyMouseSensitivity = "Settings_MouseSensitivity";
    private const string KeyCameraShake = "Settings_CameraShake";

    private const string KeyHasActiveSave = "Save_HasActiveRun";
    private const string KeySaveLevel = "Save_CurrentLevel";
    private const string KeySaveShards = "Save_CurrentShards";
    private const string KeySaveTotalShards = "Save_TotalShards";
    private const string KeySaveLives = "Save_RemainingLives";

    private const string KeyUnlockedLevels = "Profile_UnlockedLevels";
    private const string KeyLevel1BestShards = "LevelOne_BestShards";
    private const string KeyLevel1BestTime = "LevelOne_BestTime";

    public static event Action OnSettingsChanged;

    public static LevelLaunchMode PendingLaunchMode { get; set; } = LevelLaunchMode.NewGame;

    public static float SfxVolume
    {
        get => Mathf.Clamp01(PlayerPrefs.GetFloat(KeySfxVolume, 0.8f));
        set
        {
            float v = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(KeySfxVolume, v);
            ApplyRuntimeSettings();
            OnSettingsChanged?.Invoke();
        }
    }

    public static float BgmVolume
    {
        get => Mathf.Clamp01(PlayerPrefs.GetFloat(KeyBgmVolume, 0.7f));
        set
        {
            float v = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(KeyBgmVolume, v);
            ApplyRuntimeSettings();
            OnSettingsChanged?.Invoke();
        }
    }

    public static float MouseSensitivity
    {
        get => Mathf.Clamp(PlayerPrefs.GetFloat(KeyMouseSensitivity, 1.0f), MinMouseSensitivity, MaxMouseSensitivity);
        set
        {
            float v = Mathf.Clamp(value, MinMouseSensitivity, MaxMouseSensitivity);
            PlayerPrefs.SetFloat(KeyMouseSensitivity, v);
            OnSettingsChanged?.Invoke();
        }
    }

    public static bool CameraShakeEnabled
    {
        get => PlayerPrefs.GetInt(KeyCameraShake, 1) != 0;
        set
        {
            PlayerPrefs.SetInt(KeyCameraShake, value ? 1 : 0);
            ApplyRuntimeSettings();
            OnSettingsChanged?.Invoke();
        }
    }

    public static void ApplyRuntimeSettings()
    {
        HapticFeedback.GlobalShakeScale = CameraShakeEnabled ? 1f : 0f;
        // 保持全局 AudioListener 为 1，由 AudioManager 与各音效源直接按 SfxVolume/BgmVolume 独立线性控制，避免音量平方衰减
        AudioListener.volume = 1f;
    }

    public static void SaveToDisk()
    {
        PlayerPrefs.Save();
    }

    // =========================================================================
    // 主线存档（继续游戏 / 新游戏覆盖确认）
    // =========================================================================

    public static bool HasActiveSave => PlayerPrefs.GetInt(KeyHasActiveSave, 0) != 0;
    public static int SaveLevelIndex => Mathf.Max(1, PlayerPrefs.GetInt(KeySaveLevel, 1));
    public static int SaveShards => Mathf.Max(0, PlayerPrefs.GetInt(KeySaveShards, 0));
    public static int SaveTotalShards => Mathf.Max(1, PlayerPrefs.GetInt(KeySaveTotalShards, 192));
    public static int SaveRemainingLives => Mathf.Clamp(PlayerPrefs.GetInt(KeySaveLives, 3), 1, 3);

    public static void UpdateActiveSave(int levelIndex, int collectedShards, int totalShards, int remainingLives)
    {
        PlayerPrefs.SetInt(KeyHasActiveSave, 1);
        PlayerPrefs.SetInt(KeySaveLevel, Mathf.Max(1, levelIndex));
        PlayerPrefs.SetInt(KeySaveShards, Mathf.Max(0, collectedShards));
        PlayerPrefs.SetInt(KeySaveTotalShards, Mathf.Max(1, totalShards));
        PlayerPrefs.SetInt(KeySaveLives, Mathf.Clamp(remainingLives, 1, 3));
        PlayerPrefs.Save();
    }

    public static void ClearActiveSave()
    {
        PlayerPrefs.SetInt(KeyHasActiveSave, 0);
        PlayerPrefs.DeleteKey(KeySaveShards);
        PlayerPrefs.DeleteKey(KeySaveLives);
        PlayerPrefs.Save();
    }

    // =========================================================================
    // 玩家档案纪录（新游戏覆盖时依然保留）
    // =========================================================================

    public static int UnlockedLevelCount
    {
        get => Mathf.Clamp(PlayerPrefs.GetInt(KeyUnlockedLevels, 1), 1, 4);
        set
        {
            int next = Mathf.Clamp(Mathf.Max(UnlockedLevelCount, value), 1, 4);
            PlayerPrefs.SetInt(KeyUnlockedLevels, next);
            PlayerPrefs.Save();
        }
    }

    public static int GetBestShards(int levelIndex)
    {
        string key = levelIndex == 1 ? KeyLevel1BestShards : $"Level{levelIndex}_BestShards";
        return Mathf.Max(0, PlayerPrefs.GetInt(key, 0));
    }

    public static float GetBestTime(int levelIndex)
    {
        string key = levelIndex == 1 ? KeyLevel1BestTime : $"Level{levelIndex}_BestTime";
        return PlayerPrefs.GetFloat(key, -1f);
    }

    public static int UpdateBestShards(int levelIndex, int collected)
    {
        string key = levelIndex == 1 ? KeyLevel1BestShards : $"Level{levelIndex}_BestShards";
        int prev = Mathf.Max(0, PlayerPrefs.GetInt(key, 0));
        int best = Mathf.Max(prev, Mathf.Max(0, collected));
        if (best > prev)
        {
            PlayerPrefs.SetInt(key, best);
            PlayerPrefs.Save();
        }
        return best;
    }

    public static void UpdateBestTime(int levelIndex, float elapsedSeconds, out bool isFirstClear, out bool isNewRecord, out float previousBestTime)
    {
        string key = levelIndex == 1 ? KeyLevel1BestTime : $"Level{levelIndex}_BestTime";
        previousBestTime = PlayerPrefs.GetFloat(key, -1f);
        isFirstClear = previousBestTime <= 0f;
        isNewRecord = !isFirstClear && elapsedSeconds < previousBestTime;

        if (isFirstClear || isNewRecord)
        {
            PlayerPrefs.SetFloat(key, elapsedSeconds);
            PlayerPrefs.Save();
        }
    }

    public static string FormatRunTime(float seconds)
    {
        if (seconds <= 0f) return "--:--.--";
        int mins = Mathf.FloorToInt(seconds / 60f);
        float rem = seconds - mins * 60f;
        return $"{mins:00}:{rem:00.00}";
    }

    public static string GetLevelDisplayName(int levelIndex)
    {
        return levelIndex switch
        {
            1 => "第一关 · 猴影酒店",
            2 => "第二关 · 敬请期待",
            3 => "第三关 · 敬请期待",
            4 => "第四关 · 敬请期待",
            _ => $"第 {levelIndex} 关"
        };
    }
}
