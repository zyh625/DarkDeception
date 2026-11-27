using System;
using System.Collections;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.InputSystem;

public enum LevelObjectiveState
{
    CollectShards = 0,
    ActivateStatue = 1,
    EscapeElevator = 2
}

public class LevelOne : MonoBehaviour
{
    [Header("导航网格与吊灯生成")]
    [SerializeField] private NavMeshSurface navMeshSurface;
    [SerializeField] private bool buildNavMeshOnAwake = true;
    [SerializeField] private ChandelierController chandelierPrefab;
    [SerializeField] private Transform[] chandelierBorn;

    [Header("玩家与水晶")]
    [SerializeField] private PlayerInteract playerInteract;
    [SerializeField] private Transform shard;
    [SerializeField] private Transform[] shardBorn;
    [SerializeField] private Transform playerBorn;
    [SerializeField] private Transform playerRespawnPoint;

    [Header("开局电梯与场景关键点")]
    [SerializeField] private ElevatorDoor startElevator;
    [SerializeField] private Transform statueTarget;

    [Header("敌人设置（场景中现成摆好的猴子或动态生成都支持）")]
    [SerializeField] private MonkeyAI[] sceneMonkeys;
    [SerializeField] private MonkeyAI monkeyPrefab;
    [SerializeField] private Transform[] monkeyBorn;
    [SerializeField] private Transform[] patrolPoints;
    [SerializeField] private float shardAlertDelay = 1.0f;

    [Header("特殊水晶能力（红=地图透视敌人，黄=敌人时间静止）")]
    [SerializeField] private float redCrystalRevealDuration = 16.0f;
    [SerializeField] private float yellowCrystalTimeStopDuration = 7.0f;

    [Header("首领追逐阶段（收齐水晶后安全屋出生两只首领 + 玩家四角随机复活）")]
    [SerializeField] private MonkeyAI kingBossPrefab;
    [SerializeField] private Transform[] bossSpawnPoints;
    [SerializeField] private Transform[] cornerRespawnPoints;

    [Header("生命与界面过渡")]
    [SerializeField] private int maxLives = 3;
    [SerializeField] private GameObject pauseMenu;
    [SerializeField] private GameObject failMenu;
    [SerializeField] private GameObject successMenu;
    [SerializeField] private GameObject rebornTransitionPanel;
    [SerializeField] private LevelOneOverlayUI overlayUI;
    [SerializeField] private float victoryDoorCloseDelay = 0.65f;

    [Header("局内 HUD 与拾取反馈")]
    [SerializeField] private ShardCounterHUD shardCounterHUD;
    [SerializeField] private LevelOneMiniMapHUD miniMapHUD;
    [SerializeField] private ShardPickupScreenVFX shardPickupVFX;
    [SerializeField] private SecretRoomManager secretRoomManager;

    private readonly List<Transform> shards = new();
    private readonly List<MonkeyAI> monkeys = new();
    private readonly List<(Vector3 pos, Quaternion rot)> monkeyHomePoses = new();
    private readonly List<ChandelierController> spawnedChandeliers = new();

    private PlayerInteract player;
    private int curShard;
    private int maxShard;
    private int curLives;
    private int deathCount;
    private bool isHandlingCatch;
    private bool isExecutionPlaying;
    private float executionElapsed;
    private MonkeyAI activeExecutionKiller;
    private MonkeyAI activeExecutionBossReal;
    private MonkeyAI activeBossExecutionProxy;
    private float enemyRevealRemainingTime;
    private float timeStopRemainingTime;
    private bool isBossPhaseSpawned;
    private bool isBossEntrancePlaying;
    private bool isGameOver;
    private bool isRunTimerActive;
    private bool isCampaignRun;
    private float elapsedRunTime;
    private LevelObjectiveState objectiveState = LevelObjectiveState.CollectShards;

    public int CurrentNormalShards => curShard;
    public int TotalNormalShards => maxShard;
    public int RemainingLives => curLives;
    public float ElapsedRunTime => elapsedRunTime;
    public LevelObjectiveState ObjectiveState => objectiveState;
    public bool HasCollectedAllShards => objectiveState >= LevelObjectiveState.ActivateStatue;
    public bool IsStatueActivated => objectiveState >= LevelObjectiveState.EscapeElevator;
    public bool IsExitUnlocked => objectiveState >= LevelObjectiveState.EscapeElevator;
    public bool AreEnemiesRevealedOnMap => enemyRevealRemainingTime > 0f;
    public float EnemyRevealRemainingTime => Mathf.Max(0f, enemyRevealRemainingTime);
    public bool IsTimeStopped => timeStopRemainingTime > 0f;
    public float TimeStopRemainingTime => Mathf.Max(0f, timeStopRemainingTime);
    public bool CanUseHUD => !isHandlingCatch && !isBossEntrancePlaying && !isGameOver && Time.timeScale > 0.01f;
    public Transform PlayerTransform => player != null ? player.transform : null;
    public Transform StatueTransform => statueTarget;
    public Transform ElevatorTransform => startElevator != null ? startElevator.transform : null;
    public IReadOnlyList<Transform> SpawnedShards => shards;
    public IReadOnlyList<MonkeyAI> ActiveMonkeys => monkeys;

    public event Action<int, int> OnShardProgressChanged;
    public event Action<ShardColorType> OnShardPicked;
    public event Action<LevelObjectiveState> OnObjectiveStateChanged;

    private void Awake()
    {
        Time.timeScale = 1f;
        curLives = Mathf.Max(1, maxLives);
        deathCount = 0;
        objectiveState = LevelObjectiveState.CollectShards;
        isHandlingCatch = false;
        isExecutionPlaying = false;
        executionElapsed = 0f;
        activeExecutionKiller = null;
        activeExecutionBossReal = null;
        activeBossExecutionProxy = null;
        enemyRevealRemainingTime = 0f;
        timeStopRemainingTime = 0f;
        isBossPhaseSpawned = false;
        isBossEntrancePlaying = false;
        isGameOver = false;
        isRunTimerActive = false;
        elapsedRunTime = 0f;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        if (overlayUI == null) overlayUI = GetComponent<LevelOneOverlayUI>();
        if (miniMapHUD == null) miniMapHUD = FindAnyObjectByType<LevelOneMiniMapHUD>();
        if (secretRoomManager == null) secretRoomManager = GetComponent<SecretRoomManager>();
        if (pauseMenu != null) pauseMenu.SetActive(false);
        if (failMenu != null) failMenu.SetActive(false);
        if (successMenu != null) successMenu.SetActive(false);
        if (rebornTransitionPanel != null) rebornTransitionPanel.SetActive(false);

        // 1. 先实例化所有走廊吊灯、标记 8 扇暗门室内不可通行区域并构建 NavMeshSurface，再生成水晶、玩家、猴子与暗门内容
        GameSettings.ApplyRuntimeSettings();
        WallServiceBellInteractable.StopAllRingingBells();
        isCampaignRun = GameSettings.PendingLaunchMode != LevelLaunchMode.ReplayLevel;

        SpawnChandeliers();
        if (secretRoomManager != null) secretRoomManager.PrepareRoomNavMeshExclusions();
        EnsureNavMeshBuilt();
        MonkeyRoutePlanner.RebuildGraph();
        SpawnShards();
        ApplyContinueSaveIfNeeded();
        SpawnPlayer();
        SetupMonkeys();
        if (secretRoomManager != null) secretRoomManager.InitializeAndRandomizeRooms(this, player, monkeyPrefab, patrolPoints);
    }

    private void ApplyContinueSaveIfNeeded()
    {
        if (GameSettings.PendingLaunchMode != LevelLaunchMode.ContinueSave) return;
        if (!GameSettings.HasActiveSave || GameSettings.SaveLevelIndex != 1) return;

        curLives = Mathf.Clamp(GameSettings.SaveRemainingLives, 1, Mathf.Max(1, maxLives));
        int targetRestoredShards = Mathf.Clamp(GameSettings.SaveShards, 0, Mathf.Max(0, maxShard - 1));
        int disabled = 0;

        for (int i = 0; i < shards.Count && disabled < targetRestoredShards; i++)
        {
            Transform st = shards[i];
            if (st == null) continue;
            ShardBox box = st.GetComponent<ShardBox>();
            if (box == null || box.ShardType == ShardColorType.NormalPurple)
            {
                st.gameObject.SetActive(false);
                disabled++;
            }
        }
        curShard = disabled;
    }

    private void PersistCampaignProgressIfNeeded()
    {
        if (!isCampaignRun || isGameOver) return;
        GameSettings.UpdateActiveSave(1, curShard, Mathf.Max(1, maxShard), curLives);
    }

    private void Start()
    {
        OnShardProgressChanged?.Invoke(curShard, maxShard);
        OnObjectiveStateChanged?.Invoke(objectiveState);
    }

    private void Update()
    {
        // 处决演出期间允许按 Enter 跳过动画（保留 0.2s 防误触缓冲）
        if (isExecutionPlaying)
        {
            executionElapsed += Time.unscaledDeltaTime;
            if (executionElapsed >= 0.20f)
            {
                Keyboard kb = Keyboard.current;
                if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame))
                {
                    SkipCurrentExecution();
                }
            }
            return;
        }

        // 计时从玩家走出电梯开始，暂停/过场期间不计，逃进电梯时停止
        if (!isHandlingCatch && !isBossEntrancePlaying && !isGameOver && Time.timeScale > 0.01f)
        {
            float dt = Time.deltaTime;
            if (isRunTimerActive)
            {
                elapsedRunTime += dt;
            }

            // 红水晶（洞察之眼）：临时在地图上显示所有活跃敌人（普通猴子与首领）
            if (enemyRevealRemainingTime > 0f)
            {
                enemyRevealRemainingTime = Mathf.Max(0f, enemyRevealRemainingTime - dt);
            }

            // 黄水晶（静止沙漏）：临时冻结所有活跃敌人（普通猴子与首领）
            if (timeStopRemainingTime > 0f)
            {
                timeStopRemainingTime -= dt;
                if (timeStopRemainingTime <= 0f)
                {
                    timeStopRemainingTime = 0f;
                    ApplyTimeStopToAllEnemies(false);
                }
            }
        }
    }

    private void SkipCurrentExecution()
    {
        if (!isExecutionPlaying) return;
        if (overlayUI != null) overlayUI.ShowExecutionSkipHint(false);

        if (activeExecutionKiller != null)
        {
            activeExecutionKiller.SkipExecution();
        }
    }

    private void OnEnable()
    {
        if (player != null)
        {
            player.onPickUpTyped += PickUpShard;
            player.pauseAction += PauseControl;
        }

        if (startElevator != null)
        {
            startElevator.OnPlayerExitedAndLocked += HandlePlayerLeftElevator;
            startElevator.OnPlayerEnteredForEscape += HandlePlayerEnteredEscapeElevator;
        }

        foreach (MonkeyAI monkey in monkeys)
        {
            if (monkey != null)
            {
                monkey.OnCatchPlayer += HandlePlayerCaught;
            }
        }
    }

    private void OnDisable()
    {
        if (player != null)
        {
            player.onPickUpTyped -= PickUpShard;
            player.pauseAction -= PauseControl;
        }

        if (startElevator != null)
        {
            startElevator.OnPlayerExitedAndLocked -= HandlePlayerLeftElevator;
            startElevator.OnPlayerEnteredForEscape -= HandlePlayerEnteredEscapeElevator;
        }

        foreach (MonkeyAI monkey in monkeys)
        {
            if (monkey != null)
            {
                monkey.OnCatchPlayer -= HandlePlayerCaught;
            }
        }
    }

    private void OnDestroy()
    {
        Time.timeScale = 1f;
    }

    private void EnsureNavMeshBuilt()
    {
        if (navMeshSurface == null)
        {
            navMeshSurface = FindAnyObjectByType<NavMeshSurface>();
        }

        if (navMeshSurface != null && (buildNavMeshOnAwake || navMeshSurface.navMeshData == null))
        {
            navMeshSurface.BuildNavMesh();
        }
    }

    private void SpawnChandeliers()
    {
        if (chandelierPrefab == null || chandelierBorn == null || chandelierBorn.Length == 0) return;

        Transform container = new GameObject("SpawnedChandeliers").transform;
        for (int i = 0; i < chandelierBorn.Length; i++)
        {
            Transform sp = chandelierBorn[i];
            if (sp == null) continue;
            ChandelierController inst = Instantiate(chandelierPrefab, sp.position, sp.rotation, container);
            inst.name = $"Chandelier_{i:000}";
            spawnedChandeliers.Add(inst);
        }
    }

    private void SpawnShards()
    {
        if (shard == null || shardBorn == null) return;

        foreach (Transform t in shardBorn)
        {
            if (t == null) continue;
            Transform spawned = Instantiate(shard, t.position, t.rotation);
            shards.Add(spawned);

            ShardBox box = spawned.GetComponent<ShardBox>();
            if (box == null || box.ShardType == ShardColorType.NormalPurple)
            {
                maxShard++;
            }
        }
    }

    private void SpawnPlayer()
    {
        if (playerInteract == null) return;

        Vector3 pos = playerBorn != null ? playerBorn.position : Vector3.zero;
        Quaternion rot = playerBorn != null ? playerBorn.rotation : Quaternion.identity;
        player = Instantiate(playerInteract, pos, rot);
    }

    private void SetupMonkeys()
    {
        if (sceneMonkeys != null)
        {
            foreach (MonkeyAI monkey in sceneMonkeys)
            {
                if (monkey != null && !monkeys.Contains(monkey))
                {
                    monkeys.Add(monkey);
                    monkeyHomePoses.Add((monkey.transform.position, monkey.transform.rotation));
                }
            }
        }

        if (monkeyPrefab != null && monkeyBorn != null)
        {
            foreach (Transform spawnPoint in monkeyBorn)
            {
                if (spawnPoint == null) continue;
                MonkeyAI spawned = Instantiate(monkeyPrefab, spawnPoint.position, spawnPoint.rotation);
                monkeys.Add(spawned);
                monkeyHomePoses.Add((spawnPoint.position, spawnPoint.rotation));
            }
        }

        foreach (MonkeyAI monkey in monkeys)
        {
            monkey.Init(player, patrolPoints);
        }
    }

    /// <summary>
    /// 暗门后的睡觉猴子被惊醒并跨出房门后调用：正式加入猴子阵营（同步抓捕事件、复活复位点与小地图红点）。
    /// </summary>
    public void RegisterAwakenedMonkey(MonkeyAI monkey, Vector3 corridorHomePos, Quaternion corridorHomeRot)
    {
        if (monkey == null) return;
        // 若已进入首领追逐或逃生阶段，不再接收普通猴子加入
        if (objectiveState >= LevelObjectiveState.ActivateStatue)
        {
            Destroy(monkey.gameObject);
            return;
        }
        if (!monkeys.Contains(monkey))
        {
            monkeys.Add(monkey);
            monkeyHomePoses.Add((corridorHomePos, corridorHomeRot));
            monkey.OnCatchPlayer += HandlePlayerCaught;
            if (IsTimeStopped)
            {
                monkey.SetTimeFrozen(true);
            }
        }
    }

    private void PickUpShard(Vector3 shardPos, ShardColorType shardType)
    {
        if (isGameOver) return;

        AudioManager.PlayCrystalPickup(shardType);

        if (shardPickupVFX != null)
        {
            shardPickupVFX.PlayBurst(shardType);
        }
        OnShardPicked?.Invoke(shardType);

        if (shardType == ShardColorType.Red)
        {
            // 红水晶（洞察之眼）：临时在小地图透视显示全部敌人位置（普通猴子与首领均有效）
            enemyRevealRemainingTime = Mathf.Max(enemyRevealRemainingTime, Mathf.Max(2f, redCrystalRevealDuration));
        }
        else if (shardType == ShardColorType.Yellow)
        {
            // 黄水晶（静止沙漏）：临时定格全部活跃敌人（普通猴子与首领均有效）
            timeStopRemainingTime = Mathf.Max(timeStopRemainingTime, Mathf.Max(1.5f, yellowCrystalTimeStopDuration));
            ApplyTimeStopToAllEnemies(true);
        }
        else if (shardType == ShardColorType.NormalPurple)
        {
            curShard = Mathf.Min(curShard + 1, Mathf.Max(maxShard, curShard + 1));
            OnShardProgressChanged?.Invoke(curShard, maxShard);
            PersistCampaignProgressIfNeeded();
            StartCoroutine(NotifyMonkeysShardPicked(shardPos));

            if (objectiveState == LevelObjectiveState.CollectShards && maxShard > 0 && curShard >= maxShard)
            {
                SetObjectiveState(LevelObjectiveState.ActivateStatue);
                StartCoroutine(PlayKingBossEntranceCutsceneRoutine());
            }
        }
    }

    /// <summary>
    /// 收集完全部普通水晶后的首领电梯入场演出：
    /// 1) 切黑转场至安全屋电梯前，相机正对紧闭的电梯门；
    /// 2) 电梯门打开的同时相机向后拉远并压低仰拍，轿厢内只站着一只愤怒喘息的首领；
    /// 3) 首领仰天怒吼（同步低沉吼声与强震动）-> 重步走出电梯 -> 在大厅左右四处张望搜寻；
    /// 4) 屏幕逐渐黑屏并切回玩家视角，安全屋正式刷新两只首领展开包抄追逐战！
    /// </summary>
    private IEnumerator PlayKingBossEntranceCutsceneRoutine()
    {
        if (isBossEntrancePlaying || isBossPhaseSpawned) yield break;
        isBossEntrancePlaying = true;

        // 1. 立即暂停玩家操作、收起地图、清除全部普通猴子与暗门沉睡猴子
        if (pauseMenu != null) pauseMenu.SetActive(false);
        if (miniMapHUD != null) miniMapHUD.ForceCloseImmediate();
        if (player != null) player.SetCutsceneFrozen(true);

        DespawnAllActiveEnemies();
        if (secretRoomManager != null)
        {
            secretRoomManager.DespawnAllSleepingMonkeys();
        }
        WallServiceBellInteractable.StopAllRingingBells();

        // 若未配置首领或电梯，兜底直接进入追逐战
        FirstView firstView = player != null ? player.GetComponentInChildren<FirstView>(true) : null;
        if (startElevator == null || firstView == null || kingBossPrefab == null)
        {
            isBossEntrancePlaying = false;
            if (player != null) player.SetCutsceneFrozen(false);
            BeginKingBossPhase();
            yield break;
        }

        // 2. 平滑切入黑幕以切镜至电梯口
        if (overlayUI != null)
        {
            yield return overlayUI.FadeScreenCurtain(0f, 1f, 0.35f);
        }

        // 3. 在黑幕后复位关闭电梯门，并在轿厢内生成唯一一只用于演出的愤怒首领
        startElevator.SnapClosedForBossCutscene();

        Transform liftTr = startElevator.transform;
        Vector3 liftPos = liftTr.position;
        Vector3 outDir = -liftTr.forward;
        outDir.y = 0f;
        if (outDir.sqrMagnitude < 0.001f) outDir = Vector3.back;
        else outDir.Normalize();
        Vector3 rightDir = Vector3.Cross(Vector3.up, outDir).normalized;

        Vector3 bossInsidePos = liftPos - outDir * 0.56f;
        Vector3 bossLobbyPos = liftPos + outDir * 0.54f;
        Quaternion bossFaceOutRot = Quaternion.LookRotation(outDir, Vector3.up);

        MonkeyAI actor = Instantiate(kingBossPrefab, bossInsidePos, bossFaceOutRot);
        actor.name = "KingBossEntranceCutsceneActor";
        actor.enabled = false;
        MonkeyMovement actorMove = actor.GetComponent<MonkeyMovement>();
        if (actorMove != null)
        {
            actorMove.SetNavAgentEnabled(false);
            actorMove.enabled = false;
        }
        MonkeyBox actorBox = actor.GetComponentInChildren<MonkeyBox>(true);
        if (actorBox != null) actorBox.SetActive(false);
        BoxCollider actorBodyCol = actor.GetComponent<BoxCollider>();
        if (actorBodyCol != null) actorBodyCol.enabled = false;

        // 演出期间关闭蒙皮视锥剔除，确保低角度仰拍贴近首领时不出现闪烁
        SkinnedMeshRenderer[] actorSmrs = actor.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < actorSmrs.Length; i++)
        {
            if (actorSmrs[i] != null) actorSmrs[i].updateWhenOffscreen = true;
        }

        MonkeyAnimation actorAnim = actor.GetComponentInChildren<MonkeyAnimation>(true);
        if (actorAnim != null)
        {
            actorAnim.PlayEntranceCutscene();
        }

        // 镜头起始构图：正对紧闭的电梯门（距离 1.12m，齐眼高度 0.44m）
        Vector3 camStartPos = liftPos + outDir * 1.12f + Vector3.up * 0.44f;
        Vector3 lookStart = liftPos + Vector3.up * 0.46f;
        firstView.BeginCutsceneCamera(camStartPos, Quaternion.LookRotation(lookStart - camStartPos, Vector3.up), 58f);

        yield return new WaitForSecondsRealtime(0.08f);

        // 4. 黑幕淡出，展现紧闭的电梯门
        if (overlayUI != null)
        {
            yield return overlayUI.FadeScreenCurtain(1f, 0f, 0.30f);
        }

        // 5. 完整 5.20 秒首领入场运镜（与 Ashjaw_King_Entrance.fbx 156 帧 @ 30fps 精确同步）
        float totalDur = 5.20f;
        float elapsed = 0f;
        bool doorOpened = false;
        bool doorClosedBehind = false;
        bool roarTriggered = false;
        int stompCount = 0;

        // 保持在安全屋大厅南墙（z=-2.04）内侧的安全拉远仰拍机位
        Vector3 camPullLowPos = liftPos + outDir * 1.82f + rightDir * 0.08f + Vector3.up * 0.19f;
        Vector3 camEndLowPos = liftPos + outDir * 1.88f - rightDir * 0.06f + Vector3.up * 0.21f;

        while (elapsed < totalDur)
        {
            float dt = Time.deltaTime;
            elapsed += dt;

            // 0.36s：电梯门缓缓打开，同时相机开始拉远并压低仰拍
            if (!doorOpened && elapsed >= 0.36f)
            {
                doorOpened = true;
                startElevator.OpenForBossCutscene();
            }

            // 1.56s（约第 48 帧仰天怒吼爆发瞬间）：触发浑厚首领怒吼音效与重度震屏
            if (!roarTriggered && elapsed >= 1.56f)
            {
                roarTriggered = true;
                AudioManager.PlayKingEntranceRoar();
                HapticFeedback.RequestShake(0.024f, 0.92f);
            }

            // 2.80s ~ 4.00s（第 85~121 帧）：首领沉重 3 步迈出电梯进入大厅
            if (elapsed >= 2.80f && elapsed <= 4.02f)
            {
                float walkT = Mathf.Clamp01((elapsed - 2.80f) / 1.20f);
                float easedWalk = walkT * walkT * (3f - 2f * walkT);
                actor.transform.position = Vector3.Lerp(bossInsidePos, bossLobbyPos, easedWalk);

                // 三步落地同步重脚步声与轻微地板震颤
                if (stompCount == 0 && elapsed >= 3.02f)
                {
                    stompCount = 1;
                    AudioManager.PlayMonkeyFootstep(actor.transform.position, true, isBossKing: true);
                    HapticFeedback.RequestShake(0.008f, 0.14f);
                }
                else if (stompCount == 1 && elapsed >= 3.42f)
                {
                    stompCount = 2;
                    AudioManager.PlayMonkeyFootstep(actor.transform.position, true, isBossKing: true);
                    HapticFeedback.RequestShake(0.008f, 0.14f);
                }
                else if (stompCount == 2 && elapsed >= 3.82f)
                {
                    stompCount = 3;
                    AudioManager.PlayMonkeyFootstep(actor.transform.position, true, isBossKing: true);
                    HapticFeedback.RequestShake(0.010f, 0.16f);
                }
            }
            else if (elapsed > 4.02f)
            {
                actor.transform.position = bossLobbyPos;
                if (!doorClosedBehind)
                {
                    doorClosedBehind = true;
                    startElevator.CloseAndLock();
                }
            }

            // 运镜插值：正对电梯 -> 同步开门拉远压低仰拍 -> 仰天怒吼特写微推 -> 跟踪走出与四周张望
            Vector3 camPos;
            Vector3 lookTarget;
            float fov;

            if (elapsed < 0.36f)
            {
                camPos = camStartPos;
                lookTarget = lookStart;
                fov = 58f;
            }
            else if (elapsed < 1.50f)
            {
                float p = Mathf.Clamp01((elapsed - 0.36f) / 1.14f);
                float sm = p * p * (3f - 2f * p);
                camPos = Vector3.Lerp(camStartPos, camPullLowPos, sm);
                lookTarget = Vector3.Lerp(lookStart, bossInsidePos + Vector3.up * 0.60f, sm);
                fov = Mathf.Lerp(58f, 50f, sm);
            }
            else if (elapsed < 2.80f)
            {
                float p = Mathf.Clamp01((elapsed - 1.50f) / 1.30f);
                float roarPeak = Mathf.Sin(p * Mathf.PI);
                camPos = camPullLowPos + Vector3.down * (0.025f * roarPeak) + outDir * (-0.06f * roarPeak);
                lookTarget = bossInsidePos + Vector3.up * (0.60f + 0.14f * roarPeak);
                fov = Mathf.Lerp(50f, 45f, roarPeak);
            }
            else
            {
                float p = Mathf.Clamp01((elapsed - 2.80f) / (totalDur - 2.80f));
                float sm = p * p * (3f - 2f * p);
                camPos = Vector3.Lerp(camPullLowPos, camEndLowPos, sm);
                lookTarget = actor.transform.position + Vector3.up * 0.62f;
                fov = Mathf.Lerp(50f, 54f, sm);
            }

            Quaternion camRot = Quaternion.LookRotation((lookTarget - camPos).normalized, Vector3.up);
            firstView.UpdateCutsceneCamera(camPos, camRot, fov);

            yield return null;
        }

        // 6. 屏幕逐渐黑屏并切回玩家视角，正式开始双首领追逐战！
        if (overlayUI != null)
        {
            yield return overlayUI.FadeScreenCurtain(0f, 1f, 0.85f);
        }

        if (actor != null)
        {
            Destroy(actor.gameObject);
        }
        startElevator.CloseAndLock();
        firstView.EndCutsceneCamera();

        BeginKingBossPhase();

        yield return new WaitForSecondsRealtime(0.12f);

        isBossEntrancePlaying = false;
        if (player != null)
        {
            player.SetCutsceneFrozen(false);
        }
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        if (overlayUI != null)
        {
            yield return overlayUI.FadeScreenCurtain(1f, 0f, 0.45f);
        }
    }

    private void ApplyTimeStopToAllEnemies(bool frozen)
    {
        for (int i = 0; i < monkeys.Count; i++)
        {
            if (monkeys[i] != null)
            {
                monkeys[i].SetTimeFrozen(frozen);
            }
        }
        if (secretRoomManager != null)
        {
            secretRoomManager.SetSleepingMonkeysTimeFrozen(frozen);
        }
    }

    /// <summary>
    /// 收集完所有普通水晶后触发：
    /// 1) 清除全部普通猴子（含暗门中仍在沉睡或苏醒的猴子）；
    /// 2) 在安全屋出生两只首领猴子，实时追踪玩家并互相配合走不同路线包围玩家。
    /// </summary>
    private void BeginKingBossPhase()
    {
        if (isBossPhaseSpawned) return;
        isBossPhaseSpawned = true;

        DespawnAllActiveEnemies();
        if (secretRoomManager != null)
        {
            secretRoomManager.DespawnAllSleepingMonkeys();
        }

        MonkeyAI bossAsset = kingBossPrefab != null ? kingBossPrefab : monkeyPrefab;
        if (bossAsset == null) return;

        // 默认两只首领出生在安全屋大厅左右两侧，并赋予相反初始朝向，一出生即分头包抄
        Vector3[] fallbackPositions =
        {
            new Vector3(-0.50f, 0f, -1.50f),
            new Vector3(1.50f, 0f, -1.50f)
        };
        Vector3[] initialHeadings =
        {
            Vector3.left,
            Vector3.right
        };

        for (int i = 0; i < 2; i++)
        {
            Vector3 spawnPos;
            Quaternion spawnRot;
            if (bossSpawnPoints != null && i < bossSpawnPoints.Length && bossSpawnPoints[i] != null)
            {
                spawnPos = bossSpawnPoints[i].position;
                spawnRot = bossSpawnPoints[i].rotation;
            }
            else
            {
                spawnPos = fallbackPositions[i];
                spawnRot = Quaternion.LookRotation(initialHeadings[i], Vector3.up);
            }

            MonkeyAI boss = Instantiate(bossAsset, spawnPos, spawnRot);
            boss.name = $"KingMonkeyBoss_{i + 1}";
            boss.Init(player, patrolPoints);
            boss.ConfigureAsKingBoss(i, spawnRot * Vector3.forward);
            boss.OnCatchPlayer += HandlePlayerCaught;

            monkeys.Add(boss);
            monkeyHomePoses.Add((spawnPos, spawnRot));

            if (IsTimeStopped)
            {
                boss.SetTimeFrozen(true);
            }
        }

        Vector3 alertPos = monkeys.Count > 0 ? monkeys[0].transform.position : Vector3.zero;
        AudioManager.PlayMonkeyWakeRoar(alertPos, isBossKing: true, globalBossAlert: true);
        HapticFeedback.RequestShake(0.016f, 0.42f);
    }

    private void DespawnAllActiveEnemies()
    {
        for (int i = 0; i < monkeys.Count; i++)
        {
            MonkeyAI mk = monkeys[i];
            if (mk == null) continue;
            mk.OnCatchPlayer -= HandlePlayerCaught;
            Destroy(mk.gameObject);
        }
        monkeys.Clear();
        monkeyHomePoses.Clear();
        MonkeyRoutePlanner.ClearAllReservations();
    }

    /// <summary>
    /// 收齐普通碎片后点击交互雕像：立即消除全部首领，推进状态并打开逃生电梯门。
    /// </summary>
    public void ActivateStatue()
    {
        if (isGameOver || objectiveState != LevelObjectiveState.ActivateStatue) return;

        DespawnAllActiveEnemies();
        AudioManager.PlayCrystalPickup(ShardColorType.Yellow);
        SetObjectiveState(LevelObjectiveState.EscapeElevator);
        OpenExit();
    }

    private void SetObjectiveState(LevelObjectiveState nextState)
    {
        if (objectiveState == nextState) return;
        objectiveState = nextState;
        OnObjectiveStateChanged?.Invoke(objectiveState);
    }

    private IEnumerator NotifyMonkeysShardPicked(Vector3 shardPos)
    {
        if (shardAlertDelay > 0f)
        {
            yield return new WaitForSeconds(shardAlertDelay);
        }

        if (isHandlingCatch || isGameOver) yield break;

        foreach (MonkeyAI monkey in monkeys)
        {
            if (monkey != null)
            {
                monkey.InvestigatePosition(shardPos);
            }
        }
    }

    private void HandlePlayerCaught(MonkeyAI killer)
    {
        if (isHandlingCatch || isGameOver || killer == null) return;
        isHandlingCatch = true;
        isExecutionPlaying = true;
        executionElapsed = 0f;
        activeExecutionBossReal = null;
        activeBossExecutionProxy = null;

        Time.timeScale = 1f;
        if (pauseMenu != null) pauseMenu.SetActive(false);

        // 播放处决动画时立即关闭左下角地图与上方水晶计数器
        if (miniMapHUD != null)
        {
            miniMapHUD.ForceCloseImmediate();
        }

        int livesBeforeDeath = curLives;
        deathCount++;
        curLives = Mathf.Max(0, curLives - 1);

        // 处决动画选择规则（MonkeyAnimator: 3=ExecuteOne抓咬, 2=ExecuteTwo猛击踩踏, 1=ExecuteThree最后扔出玩家）：
        // - 最后一条命死亡（livesBeforeDeath <= 1）：固定播放第 3 种（扔出玩家）
        // - 其他命数死亡：按死亡次数在第 1 种（抓咬）与第 2 种（猛击踩踏）之间来回切换
        int catchAnimIndex = livesBeforeDeath <= 1
            ? 1
            : (deathCount % 2 == 1 ? 3 : 2);

        // 若是首领抓住玩家：隐藏首领并临时在同位置生成普通猴子替身来完整播放普通猴子的处决动画
        MonkeyAI executionActor = killer;
        if (killer.IsBossKing && monkeyPrefab != null)
        {
            activeExecutionBossReal = killer;
            killer.Freeze();
            killer.SetVisualVisible(false);

            activeBossExecutionProxy = Instantiate(monkeyPrefab, killer.transform.position, killer.transform.rotation);
            activeBossExecutionProxy.name = "BossExecutionNormalMonkeyProxy";
            MonkeyMovement proxyMove = activeBossExecutionProxy.GetComponent<MonkeyMovement>();
            if (proxyMove != null) proxyMove.SetNavAgentEnabled(false);
            MonkeyBox proxyBox = activeBossExecutionProxy.GetComponentInChildren<MonkeyBox>(true);
            if (proxyBox != null) proxyBox.SetActive(false);
            BoxCollider proxyBodyCol = activeBossExecutionProxy.GetComponent<BoxCollider>();
            if (proxyBodyCol != null) proxyBodyCol.enabled = false;
            executionActor = activeBossExecutionProxy;
        }
        activeExecutionKiller = executionActor;

        // 调整猴子与玩家之间的防穿模站位距离（特别是抓咬与拳击前探幅度大，需避开穿模与穿墙）
        PositionExecutionStage(executionActor, catchAnimIndex);

        if (player != null)
        {
            player.CatchBy(executionActor.transform, catchAnimIndex);
        }

        foreach (MonkeyAI monkey in monkeys)
        {
            if (monkey != null && monkey != killer)
            {
                monkey.Freeze();
            }
        }

        if (overlayUI != null)
        {
            overlayUI.ShowExecutionSkipHint(true);
        }

        AudioManager.PlayMonkeyExecuteRoar();

        Vector3 targetPos = player != null ? player.transform.position : executionActor.transform.position + executionActor.transform.forward;
        executionActor.PlayExecute(catchAnimIndex, targetPos, () => HandleExecuteFinished(livesBeforeDeath, executionActor));
    }

    private void PositionExecutionStage(MonkeyAI killer, int catchAnimIndex)
    {
        if (killer == null || player == null) return;

        // 依据 Blender 实测各处决动作最大前探与甩出跑道需求设定安全间距（GrabBite 1.04m / PunchStomp 1.40m / ArmSlam 1.15m）
        float desiredDist = catchAnimIndex switch
        {
            3 => 1.04f,
            2 => 1.40f,
            _ => 1.15f
        };
        float requiredRunway = catchAnimIndex == 1 ? 1.50f : desiredDist + 0.25f;

        Vector3 killerPos = killer.transform.position;
        Vector3 playerPos = player.transform.position;
        Vector3 origDir = playerPos - killerPos;
        origDir.y = 0f;
        if (origDir.sqrMagnitude < 0.0004f)
        {
            origDir = killer.transform.forward;
            origDir.y = 0f;
            if (origDir.sqrMagnitude < 0.0004f) origDir = Vector3.forward;
        }
        origDir.Normalize();

        int wallMask = LayerMask.GetMask("Wall");
        if (wallMask == 0) wallMask = 1 << 7;

        Vector3 castOrigin = killerPos + Vector3.up * 0.30f;

        // 若被抓在窄走廊横向贴墙位置（前后净空不足），自动顺着走廊开阔方向对齐处决舞台，彻底避免穿墙与贴脸穿模
        Vector3 dir = origDir;
        EvaluateAxisClearance(castOrigin, origDir, requiredRunway, wallMask, out float bestFront, out float bestBack);
        if (bestFront + bestBack < requiredRunway)
        {
            Vector3[] candidates =
            {
                origDir,
                -origDir,
                Vector3.forward,
                Vector3.back,
                Vector3.right,
                Vector3.left
            };

            float bestScore = bestFront + bestBack + bestFront * 0.35f;
            for (int i = 0; i < candidates.Length; i++)
            {
                Vector3 cand = candidates[i];
                EvaluateAxisClearance(castOrigin, cand, requiredRunway, wallMask, out float cf, out float cb);
                float alignBonus = Vector3.Dot(cand, origDir) * 0.15f;
                float score = Mathf.Min(cf, requiredRunway) + Mathf.Min(cb, 0.65f) + cf * 0.35f + alignBonus;
                if (score > bestScore)
                {
                    bestScore = score;
                    dir = cand;
                    bestFront = cf;
                    bestBack = cb;
                }
            }
        }

        float safeFront = Mathf.Max(0.32f, bestFront - 0.14f);
        float playerDist = Mathf.Min(desiredDist, safeFront);
        float deficit = desiredDist - playerDist;

        // 若前方空间仍略紧，让猴子略微后退腾出空间（ArmSlam 还会多预留前向抛飞跑道）
        float extraRunwayNeed = catchAnimIndex == 1 ? Mathf.Max(0f, 1.35f - safeFront) : 0f;
        float totalShiftNeed = Mathf.Max(deficit, extraRunwayNeed);

        Vector3 finalKillerPos = killerPos;
        if (totalShiftNeed > 0.03f)
        {
            float backShift = Mathf.Clamp(bestBack - 0.24f, 0f, totalShiftNeed);
            if (backShift > 0.02f)
            {
                finalKillerPos = killerPos - dir * backShift;
                playerDist = Mathf.Min(desiredDist, safeFront + backShift);
            }
        }

        Vector3 finalPlayerPos = new Vector3(
            finalKillerPos.x + dir.x * playerDist,
            playerPos.y,
            finalKillerPos.z + dir.z * playerDist);

        player.TeleportSafe(finalPlayerPos);
        killer.AdjustExecutionPosition(finalKillerPos, finalPlayerPos);
    }

    private static void EvaluateAxisClearance(Vector3 origin, Vector3 dir, float maxDist, int wallMask, out float frontClear, out float backClear)
    {
        frontClear = maxDist + 0.16f;
        if (Physics.SphereCast(origin, 0.14f, dir, out RaycastHit fHit, maxDist + 0.16f, wallMask, QueryTriggerInteraction.Ignore))
        {
            frontClear = fHit.distance;
        }

        backClear = maxDist * 0.6f + 0.24f;
        if (Physics.SphereCast(origin, 0.18f, -dir, out RaycastHit bHit, backClear, wallMask, QueryTriggerInteraction.Ignore))
        {
            backClear = bHit.distance;
        }
    }

    private void HandleExecuteFinished(int livesBeforeDeath, MonkeyAI killer)
    {
        if (!isExecutionPlaying) return;
        isExecutionPlaying = false;
        activeExecutionKiller = null;

        if (overlayUI != null)
        {
            overlayUI.ShowExecutionSkipHint(false);
        }

        // 还有剩余命数（3->2 或 2->1）：先黑屏 -> 播放复活过渡动画 -> 复活动画结束先进黑屏 -> 直接复活在安全屋
        if (curLives > 0)
        {
            if (overlayUI != null)
            {
                overlayUI.PlayPostExecuteRebornFlow(
                    livesBeforeDeath,
                    () => CleanupExecutionBehindBlack(killer),
                    ResetEntitiesForReborn,
                    CompleteReborn);
            }
            else
            {
                CleanupExecutionBehindBlack(killer);
                ResetEntitiesForReborn();
                CompleteReborn();
            }
            return;
        }

        // 命数耗尽：先黑屏 -> 再进入死亡失败界面
        ShowFailMenuAfterExecute();
    }

    private void CleanupExecutionBehindBlack(MonkeyAI killer)
    {
        AudioManager.StopExecuteRoar();
        ExecutionBloodScreenVFX.RequestClear();
        if (activeBossExecutionProxy != null)
        {
            Destroy(activeBossExecutionProxy.gameObject);
            activeBossExecutionProxy = null;
        }
        if (activeExecutionBossReal != null)
        {
            activeExecutionBossReal.SetVisualVisible(true);
            activeExecutionBossReal = null;
        }
        if (killer != null && killer.gameObject != null)
        {
            killer.ResetExecutionAnimOnly();
        }
        if (player != null)
        {
            FirstView fv = player.GetComponentInChildren<FirstView>();
            if (fv != null) fv.ClearExecutionCamera();
        }
    }

    private void ShowFailMenuAfterExecute()
    {
        AudioManager.StopExecuteRoar();
        isGameOver = true;
        isRunTimerActive = false;
        if (isCampaignRun) GameSettings.ClearActiveSave();

        if (overlayUI != null)
        {
            overlayUI.PlayPostExecuteFailureFlow(curShard, maxShard);
            return;
        }

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if (failMenu != null)
        {
            failMenu.SetActive(true);
        }
    }

    private void ResetEntitiesForReborn()
    {
        timeStopRemainingTime = 0f;

        Vector3 respawnPos;
        Quaternion respawnRot;

        // 首领追逐阶段被抓后：玩家随机复活在迷宫四个角落中的任意角落，两只首领各自回到安全屋出生点复活
        if (objectiveState == LevelObjectiveState.ActivateStatue && TryPickRandomCornerRespawn(out Vector3 cornerPos, out Quaternion cornerRot))
        {
            respawnPos = cornerPos;
            respawnRot = cornerRot;
        }
        else
        {
            // 普通收集阶段：复活在安全屋大厅（因为开局离开后电梯已锁死），若未配置则退回 playerBorn
            Transform safeSpawn = playerRespawnPoint != null ? playerRespawnPoint : playerBorn;
            respawnPos = safeSpawn != null ? safeSpawn.position : new Vector3(0.5f, 0.258f, -1.35f);
            respawnRot = safeSpawn != null ? safeSpawn.rotation : Quaternion.Euler(0f, 180f, 0f);
        }

        if (player != null)
        {
            player.Revive(respawnPos, respawnRot);
        }

        MonkeyRoutePlanner.ClearAllReservations();
        WallServiceBellInteractable.StopAllRingingBells();
        WoodSealInteractable.ResetAllSeals();

        for (int i = 0; i < monkeys.Count; i++)
        {
            if (monkeys[i] == null) continue;
            (Vector3 homePos, Quaternion homeRot) = i < monkeyHomePoses.Count
                ? monkeyHomePoses[i]
                : (monkeys[i].transform.position, monkeys[i].transform.rotation);
            monkeys[i].ResetAfterRespawn(homePos, homeRot);
        }
    }

    private bool TryPickRandomCornerRespawn(out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero;
        rot = Quaternion.identity;

        if (cornerRespawnPoints != null && cornerRespawnPoints.Length > 0)
        {
            List<Transform> valid = new(cornerRespawnPoints.Length);
            for (int i = 0; i < cornerRespawnPoints.Length; i++)
            {
                if (cornerRespawnPoints[i] != null) valid.Add(cornerRespawnPoints[i]);
            }
            if (valid.Count > 0)
            {
                Transform picked = valid[UnityEngine.Random.Range(0, valid.Count)];
                pos = picked.position;
                rot = picked.rotation;
                return true;
            }
        }
        return false;
    }

    private void CompleteReborn()
    {
        isHandlingCatch = false;
        PersistCampaignProgressIfNeeded();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void ShowFailMenu()
    {
        isGameOver = true;
        isRunTimerActive = false;
        if (isCampaignRun) GameSettings.ClearActiveSave();

        if (overlayUI != null)
        {
            overlayUI.ShowFailure(curShard, maxShard);
            return;
        }

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if (failMenu != null)
        {
            failMenu.SetActive(true);
        }
    }

    private void HandlePlayerEnteredEscapeElevator(PlayerInteract escapedPlayer)
    {
        if (isGameOver || isHandlingCatch || objectiveState != LevelObjectiveState.EscapeElevator) return;

        isGameOver = true;
        isRunTimerActive = false;
        if (isCampaignRun) GameSettings.ClearActiveSave();

        if (pauseMenu != null) pauseMenu.SetActive(false);

        if (player != null)
        {
            player.FreezeForEndScreen();
        }

        foreach (MonkeyAI monkey in monkeys)
        {
            if (monkey != null)
            {
                monkey.Freeze();
            }
        }

        StartCoroutine(VictoryTransitionRoutine());
    }

    private IEnumerator VictoryTransitionRoutine()
    {
        // 等待逃生电梯门完全关闭，再逐渐黑屏并进入胜利结算界面
        float waitDoorClose = startElevator != null
            ? Mathf.Max(victoryDoorCloseDelay, startElevator.CloseDuration + 0.12f)
            : Mathf.Max(0.90f, victoryDoorCloseDelay);
        yield return new WaitForSecondsRealtime(waitDoorClose);

        if (overlayUI != null)
        {
            overlayUI.PlayPostEscapeVictoryFlow(curShard, maxShard, elapsedRunTime);
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (successMenu != null) successMenu.SetActive(true);
        }
    }

    private void PauseControl(bool pause)
    {
        if (isHandlingCatch || isGameOver) return;

        Time.timeScale = pause ? 0f : 1f;
        Cursor.lockState = pause ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = pause;

        if (pause && overlayUI != null)
        {
            overlayUI.RefreshPauseSettingsUI();
        }
        if (!pause && overlayUI != null)
        {
            overlayUI.HidePauseConfirmIfOpen();
            GameSettings.SaveToDisk();
        }

        if (pauseMenu != null)
        {
            pauseMenu.SetActive(pause);
        }
    }

    /// <summary>
    /// 供暂停界面的“返回游戏”按钮直接绑定调用。
    /// </summary>
    public void ResumeGame()
    {
        if (player != null)
        {
            player.SetPaused(false);
        }
        PauseControl(false);
    }

    private void HandlePlayerLeftElevator()
    {
        isRunTimerActive = true;
        PersistCampaignProgressIfNeeded();

        foreach (MonkeyAI monkey in monkeys)
        {
            if (monkey != null)
            {
                monkey.WakeUp();
            }
        }
    }

    private void OpenExit()
    {
        if (startElevator != null)
        {
            startElevator.UnlockAndOpenForEscape();
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (shardBorn != null)
        {
            Gizmos.color = new Color(0.85f, 0.25f, 1f, 0.85f);
            foreach (Transform t in shardBorn)
            {
                if (t != null) Gizmos.DrawSphere(t.position, 0.12f);
            }
        }

        if (chandelierBorn != null)
        {
            Gizmos.color = new Color(1f, 0.78f, 0.3f, 0.9f);
            foreach (Transform t in chandelierBorn)
            {
                if (t != null) Gizmos.DrawSphere(t.position, 0.16f);
            }
        }

        if (patrolPoints != null)
        {
            Gizmos.color = new Color(0.2f, 1f, 0.45f, 0.9f);
            foreach (Transform t in patrolPoints)
            {
                if (t != null) Gizmos.DrawSphere(t.position + Vector3.up * 0.25f, 0.25f);
            }
        }
    }
}
