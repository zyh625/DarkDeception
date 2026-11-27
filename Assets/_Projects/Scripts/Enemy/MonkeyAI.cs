using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MonkeyMovement))]
public class MonkeyAI : MonoBehaviour
{
    public enum MonkeyState
    {
        Idle,
        Patrol,
        Track,
        Sleep,
        Wake,
        Frozen,
        Executing
    }

    [Header("初始状态")]
    [SerializeField] private MonkeyState initialState = MonkeyState.Patrol;
    [SerializeField] private bool isBossKing = false;

    [Header("视觉与感知")]
    [SerializeField] private LayerMask wall;
    [SerializeField] private float viewDistance = 8f;
    [SerializeField, Range(10f, 180f)] private float viewAngle = 110f;
    [SerializeField] private float eyeHeight = 0.7f;
    [SerializeField] private float playerTargetOffsetY = 0.15f;
    [SerializeField] private float checkInterval = 0.1f;
    [SerializeField] private float catchFallbackRadius = 0.62f;

    [Header("巡逻设置")]
    [SerializeField] private float patrolRadius = 6f;
    [SerializeField] private float idleWaitTime = 1.5f;
    [SerializeField] private Transform[] patrolPoints;

    public enum MonkeyMotionTier
    {
        Idle = 0,
        Patrol = 1,
        NormalRun = 2,
        SprintRun = 3
    }

    public event Action<MonkeyAI> OnCatchPlayer;

    private static readonly List<MonkeyAI> activeMonkeys = new();
    public static IReadOnlyList<MonkeyAI> ActiveMonkeys => activeMonkeys;

    private MonkeyState state;
    private MonkeyMovement movement;
    private MonkeyBox monkeyBox;
    private MonkeyAnimation monkeyAnimation;
    private BoxCollider bodyCollider;
    private PlayerInteract player;

    private float stateTimer;
    private float checkTimer;
    private bool isSeeingPlayer;
    private int lastPatrolIndex = -1;

    // 暗门睡觉猴子专属状态（开门惊醒 -> 播 WakeUp -> 跨出门加入猴子阵营）
    private bool isRoomSleeper;
    private bool isWakingStepOut;
    private Vector3 roomCorridorExitPos;
    private Quaternion roomFaceOutRot;
    private Action<MonkeyAI> onAwakenedJoinedPack;
    private Coroutine wakeStepOutRoutine;

    // 玩家躲进暗门房间内时的门口附近绕圈巡逻状态
    private bool isOrbitingPlayerRoom;
    private float roomOrbitRepickTimer;

    // 墙面服务铃吸引状态：记录因“亲眼看到玩家”而脱离吸引的响铃编号（不影响其他未看见玩家的猴子）
    private int bellBrokenBySightSeqId = -1;
    private int activeBellLureSeqId = -1;

    // 黄水晶时间静止状态、首领包抄配置与距离衰减音效计时器
    private bool isTimeFrozen;
    private MonkeyState stateBeforeTimeFreeze = MonkeyState.Patrol;
    private int bossSlotIndex;
    private Vector3 bossInitialHeading = Vector3.forward;
    private float footstepAudioTimer = 0.25f;
    private float spotRoarCooldown;
    private float bossAmbientRoarTimer = 4.5f;
    private bool wasSeeingPlayerPrev;
    private Renderer[] cachedRenderers;

    public MonkeyState CurrentState => state;
    public bool IsSeeingPlayer => isSeeingPlayer;
    public bool IsAsleepInSecretRoom => state == MonkeyState.Sleep;
    public bool IsBossKing => isBossKing;
    public bool IsTimeFrozen => isTimeFrozen;

    /// <summary>
    /// 供相机震动与吊灯晃动读取的当前运动档位：
    /// Idle（静止/休眠/处决）、Patrol（巡逻）、NormalRun（普通奔跑追查）、SprintRun（直视玩家狂奔）。
    /// </summary>
    public MonkeyMotionTier MotionTier
    {
        get
        {
            if (state == MonkeyState.Sleep || state == MonkeyState.Wake ||
                state == MonkeyState.Frozen || state == MonkeyState.Executing ||
                state == MonkeyState.Idle)
            {
                return MonkeyMotionTier.Idle;
            }

            float speed = movement != null ? movement.Speed : 0f;
            if (speed < 0.08f)
            {
                return MonkeyMotionTier.Idle;
            }

            if (state == MonkeyState.Patrol)
            {
                return MonkeyMotionTier.Patrol;
            }

            // Track 状态下：直视玩家冲刺为狂奔，听声/水晶追踪为普通奔跑
            return isSeeingPlayer ? MonkeyMotionTier.SprintRun : MonkeyMotionTier.NormalRun;
        }
    }

    private void Awake()
    {
        movement = GetComponent<MonkeyMovement>();
        bodyCollider = GetComponent<BoxCollider>();
        monkeyBox = GetComponentInChildren<MonkeyBox>(true);
        if (monkeyBox == null)
        {
            Transform boxTr = transform.Find("MonkeyBox");
            if (boxTr != null)
            {
                monkeyBox = boxTr.gameObject.AddComponent<MonkeyBox>();
            }
        }
        monkeyAnimation = GetComponentInChildren<MonkeyAnimation>(true);
        cachedRenderers = GetComponentsInChildren<Renderer>(true);
    }

    private void OnEnable()
    {
        if (!activeMonkeys.Contains(this))
        {
            activeMonkeys.Add(this);
        }
        if (monkeyBox != null)
        {
            monkeyBox.OnHitPlayer += HandleHitPlayer;
        }
    }

    private void OnDisable()
    {
        activeMonkeys.Remove(this);
        if (monkeyBox != null)
        {
            monkeyBox.OnHitPlayer -= HandleHitPlayer;
        }
    }

    private void Start()
    {
        if (isRoomSleeper)
        {
            // 暗门睡觉猴子已在 SetupAsSecretRoomSleeper 中配置完毕，保持 Sleep 状态等待开门惊醒
            return;
        }

        if (isBossKing)
        {
            StartBossHunt();
            return;
        }

        state = initialState;
        if (state == MonkeyState.Patrol)
        {
            movement.EnsureOnNavMesh();
            StartNextPatrol();
        }
    }

    public void Init(PlayerInteract targetPlayer, Transform[] sharedPatrolPoints = null)
    {
        player = targetPlayer;
        if (sharedPatrolPoints != null && sharedPatrolPoints.Length > 0)
        {
            patrolPoints = sharedPatrolPoints;
        }
    }

    /// <summary>
    /// 配置为首领猴子（收集全部水晶后在安全屋出生的两只首领）：
    /// 实时获取玩家位置、持续快跑、开启强防重叠路线夹击包围玩家。
    /// </summary>
    public void ConfigureAsKingBoss(int slotIndex, Vector3 initialHeading)
    {
        isBossKing = true;
        bossSlotIndex = slotIndex;
        bossInitialHeading = initialHeading.sqrMagnitude > 0.01f ? initialHeading.normalized : transform.forward;
        bossAmbientRoarTimer = 3.5f + (slotIndex % 2) * 3.8f;

        if (movement == null) movement = GetComponent<MonkeyMovement>();
        if (movement != null)
        {
            movement.SetBossPincerMode(true, slotIndex);
        }
        StartBossHunt();
    }

    private void StartBossHunt()
    {
        if (movement == null) movement = GetComponent<MonkeyMovement>();
        if (movement != null)
        {
            movement.SetNavAgentEnabled(true);
            movement.EnsureOnNavMesh(2.5f);
            movement.SetBossPincerMode(true, bossSlotIndex);
            movement.SetInitialCommittedHeading(bossInitialHeading);
        }

        isOrbitingPlayerRoom = false;
        isSeeingPlayer = true;
        SetState(MonkeyState.Track);

        if (!isTimeFrozen && player != null && player.IsAlive)
        {
            if (SecretRoomDoor.IsPlayerInsideAnyRoom)
            {
                StartNearbyRoomOrbit();
            }
            else if (movement != null)
            {
                movement.Track(player.transform.position, true, true);
            }
        }
    }

    /// <summary>
    /// 黄水晶（静止沙漏）时间静止效果：对普通猴子与首领同样生效，定格移动、动作与抓捕判定。
    /// </summary>
    public void SetTimeFrozen(bool frozen)
    {
        if (state == MonkeyState.Executing || state == MonkeyState.Sleep) return;

        if (frozen)
        {
            if (isTimeFrozen) return;
            stateBeforeTimeFreeze = state;
            isTimeFrozen = true;
            if (monkeyBox != null) monkeyBox.SetActive(false);
            if (movement != null) movement.PauseForTimeStop(true);
            if (monkeyAnimation != null) monkeyAnimation.SetTimeFrozen(true);
        }
        else
        {
            if (!isTimeFrozen) return;
            isTimeFrozen = false;
            if (monkeyAnimation != null) monkeyAnimation.SetTimeFrozen(false);
            if (state == MonkeyState.Executing || state == MonkeyState.Frozen || state == MonkeyState.Sleep) return;

            if (monkeyBox != null && !isWakingStepOut) monkeyBox.SetActive(true);
            if (movement != null) movement.PauseForTimeStop(false);

            if (isBossKing)
            {
                SetState(MonkeyState.Track);
                if (player != null && player.IsAlive && !SecretRoomDoor.IsPlayerInsideAnyRoom && movement != null)
                {
                    movement.Track(player.transform.position, true, true);
                }
            }
            else if (stateBeforeTimeFreeze == MonkeyState.Patrol)
            {
                StartNextPatrol();
            }
        }
    }

    public void SetVisualVisible(bool visible)
    {
        if (cachedRenderers == null || cachedRenderers.Length == 0)
        {
            cachedRenderers = GetComponentsInChildren<Renderer>(true);
        }
        for (int i = 0; i < cachedRenderers.Length; i++)
        {
            if (cachedRenderers[i] != null)
            {
                cachedRenderers[i].enabled = visible;
            }
        }
    }

    /// <summary>
    /// 配置为暗门后的睡觉猴子：禁用 NavMeshAgent 与抓捕判定，播放 Ashjaw_Monkey_Sleep 等待玩家开门。
    /// </summary>
    public void SetupAsSecretRoomSleeper(
        Vector3 sleepPos,
        Quaternion faceDoorRot,
        Vector3 corridorExitPos,
        PlayerInteract targetPlayer,
        Transform[] sharedPatrolPoints,
        Action<MonkeyAI> onJoinedPack)
    {
        if (movement == null) movement = GetComponent<MonkeyMovement>();
        if (bodyCollider == null) bodyCollider = GetComponent<BoxCollider>();
        if (monkeyBox == null) monkeyBox = GetComponentInChildren<MonkeyBox>(true);
        if (monkeyAnimation == null) monkeyAnimation = GetComponentInChildren<MonkeyAnimation>(true);

        isRoomSleeper = true;
        isWakingStepOut = false;
        roomCorridorExitPos = corridorExitPos;
        roomFaceOutRot = faceDoorRot;
        onAwakenedJoinedPack = onJoinedPack;

        Init(targetPlayer, sharedPatrolPoints);

        initialState = MonkeyState.Sleep;
        state = MonkeyState.Sleep;
        isSeeingPlayer = false;

        if (movement != null) movement.SetNavAgentEnabled(false);
        if (monkeyBox != null) monkeyBox.SetActive(false);
        if (bodyCollider != null) bodyCollider.enabled = false;

        transform.SetPositionAndRotation(sleepPos, faceDoorRot);

        if (monkeyAnimation != null)
        {
            monkeyAnimation.EnterSleep();
        }
    }

    /// <summary>
    /// 当玩家打开睡觉猴子所在的暗门时调用：播放 WakeUp 惊醒动画，随后跨出门槛加入猴子阵营追击玩家。
    /// </summary>
    public void WakeUpFromSecretRoom()
    {
        if (state != MonkeyState.Sleep) return;

        SetState(MonkeyState.Wake);
        isWakingStepOut = true;
        HapticFeedback.RequestShake(0.011f, 0.24f);
        AudioManager.PlayMonkeyWakeRoar(transform.position, isBossKing);

        if (wakeStepOutRoutine != null)
        {
            StopCoroutine(wakeStepOutRoutine);
        }
        wakeStepOutRoutine = StartCoroutine(WakeAndStepOutRoutine());
    }

    private IEnumerator WakeAndStepOutRoutine()
    {
        // 1. 播放 Ashjaw_Monkey_WakeUp 惊醒起身动画
        bool wakeAnimFinished = false;
        if (monkeyAnimation != null)
        {
            monkeyAnimation.PlayWakeUp(() => wakeAnimFinished = true);
            float guard = 0f;
            while (!wakeAnimFinished && guard < 3.0f)
            {
                if (state != MonkeyState.Wake) yield break;
                if (!isTimeFrozen) guard += Time.deltaTime;
                yield return null;
            }
        }

        if (state != MonkeyState.Wake) yield break;

        // 2. 起身完毕后沿门朝向跨出门槛进入外侧走廊（此时门外 NavMesh 在走廊格上）
        Vector3 startPos = transform.position;
        Vector3 exitPos = roomCorridorExitPos;
        exitPos.y = startPos.y;
        float dist = Vector3.Distance(startPos, exitPos);
        float stepSpeed = movement != null ? Mathf.Max(1.85f, movement.TrackSpeed * 0.88f) : 2.10f;
        float maxRunSpeed = movement != null ? movement.RunSpeed : 2.72f;
        float duration = Mathf.Max(0.35f, dist / stepSpeed);

        float elapsed = 0f;
        bool hitboxRestored = false;
        while (elapsed < duration)
        {
            if (state != MonkeyState.Wake) yield break;
            if (isTimeFrozen)
            {
                yield return null;
                continue;
            }

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            transform.position = Vector3.Lerp(startPos, exitPos, t);
            transform.rotation = Quaternion.Slerp(transform.rotation, roomFaceOutRot, 1f - Mathf.Exp(-12f * Time.deltaTime));

            if (monkeyAnimation != null)
            {
                monkeyAnimation.SetManualStepOutMotion(stepSpeed, maxRunSpeed);
            }

            // 跨过门槛中段后恢复碰撞与抓捕判定（若玩家堵在门口直接扑抓）
            if (!hitboxRestored && t >= 0.48f)
            {
                hitboxRestored = true;
                if (bodyCollider != null) bodyCollider.enabled = true;
                if (monkeyBox != null) monkeyBox.SetActive(true);
            }

            yield return null;
        }

        if (state != MonkeyState.Wake) yield break;

        transform.SetPositionAndRotation(exitPos, roomFaceOutRot);
        isWakingStepOut = false;
        wakeStepOutRoutine = null;

        if (bodyCollider != null) bodyCollider.enabled = true;
        if (monkeyBox != null) monkeyBox.SetActive(true);
        if (monkeyAnimation != null) monkeyAnimation.ClearSleepAndManualState();

        // 3. 正式切入 NavMesh 并加入猴子追逐阵营（此后绝不会再进入任何暗门房间）
        if (movement != null)
        {
            movement.SetNavAgentEnabled(true);
            movement.EnsureOnNavMesh(2.5f);
            movement.SetInitialCommittedHeading(roomFaceOutRot * Vector3.forward);
        }

        Action<MonkeyAI> joinCb = onAwakenedJoinedPack;
        onAwakenedJoinedPack = null;
        joinCb?.Invoke(this);

        if (SecretRoomDoor.IsPlayerInsideAnyRoom)
        {
            StartNearbyRoomOrbit();
        }
        else if (player != null && player.IsAlive)
        {
            SetState(MonkeyState.Track);
            movement.Track(player.transform.position, true);
        }
        else
        {
            StartNextPatrol();
        }
    }

    /// <summary>
    /// 墙壁服务铃被敲响时调用：立刻将所有活跃猴子的目的地更新为响铃位置，
    /// 响铃期间持续奔向该铃铛（除非某只猴子亲眼看到玩家，才会将该猴子的目标改回玩家）。
    /// </summary>
    public static void NotifyAllMonkeysBellRung(Vector3 bellCorridorPos, int ringSeqId)
    {
        for (int i = 0; i < activeMonkeys.Count; i++)
        {
            MonkeyAI mk = activeMonkeys[i];
            if (mk != null)
            {
                mk.RespondToBellLure(bellCorridorPos, ringSeqId);
            }
        }
    }

    public void RespondToBellLure(Vector3 bellCorridorPos, int ringSeqId)
    {
        // 首领实时锁定玩家位置，不受普通墙面服务铃干扰
        if (isBossKing || isTimeFrozen) return;
        if (state == MonkeyState.Executing || state == MonkeyState.Frozen ||
            state == MonkeyState.Sleep || state == MonkeyState.Wake)
        {
            return;
        }

        activeBellLureSeqId = ringSeqId;
        if (bellBrokenBySightSeqId == ringSeqId)
        {
            bellBrokenBySightSeqId = -1;
        }
        isOrbitingPlayerRoom = false;
        isSeeingPlayer = false;
        SetState(MonkeyState.Track);
        movement.Track(bellCorridorPos, false, true);
    }

    private bool IsCurrentlyLuredByBell()
    {
        if (!WallServiceBellInteractable.HasActiveRingingBell) return false;
        int curSeq = WallServiceBellInteractable.ActiveRingSequenceId;
        return bellBrokenBySightSeqId != curSeq;
    }

    /// <summary>
    /// 水晶被拾取后的延迟信号入口：仅当未直接看见玩家且处于正常巡逻/追踪状态时前往调查。
    /// </summary>
    public void InvestigatePosition(Vector3 worldPos)
    {
        if (state == MonkeyState.Executing || state == MonkeyState.Frozen ||
            state == MonkeyState.Sleep || state == MonkeyState.Wake)
        {
            return;
        }

        // 铃铛正在响且本猴尚未亲眼看到玩家时，优先以响铃为目的地，不被水晶信号打断
        if (IsCurrentlyLuredByBell())
        {
            SetState(MonkeyState.Track);
            movement.Track(WallServiceBellInteractable.ActiveBellTargetPos, false);
            return;
        }

        // 玩家在暗门房间内时不尝试冲入房间，改为门外附近绕圈巡逻
        if (SecretRoomDoor.IsPlayerInsideAnyRoom)
        {
            StartNearbyRoomOrbit();
            return;
        }

        // 视野优先级最高：正看着玩家追时不被水晶信号带偏
        if (isSeeingPlayer) return;

        SetState(MonkeyState.Track);
        movement.Track(worldPos, false);
    }

    /// <summary>
    /// 抓住玩家后由关卡管理器调用：停下移动、转向玩家并播放处决动画。
    /// </summary>
    public void PlayExecute(int catchType, Vector3 targetPos, Action onFinished)
    {
        SetState(MonkeyState.Executing);
        isSeeingPlayer = false;
        if (monkeyBox != null) monkeyBox.SetActive(false);

        movement.Stop();
        movement.FaceTarget(targetPos);

        if (monkeyAnimation != null)
        {
            monkeyAnimation.Catch(catchType, onFinished);
        }
        else
        {
            onFinished?.Invoke();
        }
    }

    public void SkipExecution()
    {
        if (monkeyAnimation != null)
        {
            monkeyAnimation.SkipExecution();
        }
    }

    public void ResetExecutionAnimOnly()
    {
        if (monkeyAnimation != null)
        {
            monkeyAnimation.ResetAnimation();
        }
    }

    public void AdjustExecutionPosition(Vector3 worldPos, Vector3 lookTargetPos)
    {
        movement.WarpTo(worldPos, transform.rotation);
        movement.FaceTarget(lookTargetPos);
    }

    /// <summary>
    /// 其他猴子或全局事件冻结时调用，防止多只猴子继续追赶或二次触发。
    /// </summary>
    public void Freeze()
    {
        if (wakeStepOutRoutine != null)
        {
            StopCoroutine(wakeStepOutRoutine);
            wakeStepOutRoutine = null;
        }
        isWakingStepOut = false;
        isOrbitingPlayerRoom = false;
        SetState(MonkeyState.Frozen);
        isSeeingPlayer = false;
        if (monkeyBox != null) monkeyBox.SetActive(false);
        movement.Stop();
    }

    public void WakeUp()
    {
        // 暗门睡觉猴子只能通过打开对应房门惊醒，不受开局走出电梯全局唤醒影响
        if (isRoomSleeper || state != MonkeyState.Sleep) return;
        movement.EnsureOnNavMesh();
        StartNextPatrol();
    }

    /// <summary>
    /// 玩家复活过场期间调用：重置动画与抓捕碰撞体，将猴子安全瞬移回初始出生点并恢复巡逻。
    /// </summary>
    public void ResetAfterRespawn(Vector3 spawnPos, Quaternion spawnRot)
    {
        if (wakeStepOutRoutine != null)
        {
            StopCoroutine(wakeStepOutRoutine);
            wakeStepOutRoutine = null;
        }
        isTimeFrozen = false;
        isWakingStepOut = false;
        isOrbitingPlayerRoom = false;
        isSeeingPlayer = false;
        wasSeeingPlayerPrev = false;
        bellBrokenBySightSeqId = -1;
        activeBellLureSeqId = -1;
        checkTimer = 0f;
        footstepAudioTimer = 0.25f;
        SetVisualVisible(true);
        if (bodyCollider != null) bodyCollider.enabled = true;
        if (monkeyAnimation != null) monkeyAnimation.ResetAnimation();
        if (monkeyBox != null) monkeyBox.SetActive(true);
        movement.PauseForTimeStop(false);
        movement.SetNavAgentEnabled(true);
        movement.WarpTo(spawnPos, spawnRot);
        if (isBossKing)
        {
            StartBossHunt();
        }
        else
        {
            StartNextPatrol();
        }
    }

    private void Update()
    {
        if (isTimeFrozen || state == MonkeyState.Executing || state == MonkeyState.Frozen ||
            state == MonkeyState.Sleep || state == MonkeyState.Wake)
        {
            return;
        }

        float dt = Time.deltaTime;
        stateTimer += dt;
        checkTimer += dt;
        if (spotRoarCooldown > 0f) spotRoarCooldown -= dt;

        if (checkTimer >= checkInterval)
        {
            checkTimer = 0f;
            CheckPlayer();
        }

        UpdateFootstepAndRoarAudio(dt);

        // 近身贴脸抓捕兜底（即使玩家从侧面/背面撞到实体碰撞体未触发前方 Trigger 也能立即进入处决）
        if (TryProximityCatchFallback())
        {
            return;
        }

        switch (state)
        {
            case MonkeyState.Idle:
                UpdateIdle();
                break;
            case MonkeyState.Patrol:
                UpdatePatrol();
                break;
            case MonkeyState.Track:
                UpdateTrack();
                break;
        }
    }

    private void UpdateFootstepAndRoarAudio(float dt)
    {
        if (movement == null) return;

        float speed = movement.Speed;
        if (speed > 0.16f)
        {
            bool running = isBossKing || state == MonkeyState.Track;
            float baseInterval = isBossKing
                ? 0.26f
                : (running ? Mathf.Lerp(0.34f, 0.22f, movement.NormalizedSpeed) : 0.44f);
            footstepAudioTimer -= dt;
            if (footstepAudioTimer <= 0f)
            {
                AudioManager.PlayMonkeyFootstep(transform.position, running, isBossKing);
                footstepAudioTimer = baseInterval;
            }
        }
        else
        {
            footstepAudioTimer = Mathf.Min(footstepAudioTimer, 0.15f);
        }

        // 首领追击期间定时发出低沉压迫嘶吼，提示玩家附近走廊首领逼近
        if (isBossKing && state == MonkeyState.Track && player != null && player.IsAlive)
        {
            bossAmbientRoarTimer -= dt;
            if (bossAmbientRoarTimer <= 0f)
            {
                AudioManager.PlayMonkeySpotRoar(transform.position, isBossKing: true);
                bossAmbientRoarTimer = UnityEngine.Random.Range(6.8f, 10.5f);
            }
        }
    }

    private void UpdateIdle()
    {
        if (stateTimer >= idleWaitTime)
        {
            StartNextPatrol();
        }
    }

    private void UpdatePatrol()
    {
        if (roomOrbitRepickTimer > 0f)
        {
            roomOrbitRepickTimer -= Time.deltaTime;
        }

        if (movement.HasReachedDestination())
        {
            // 若玩家仍躲在暗门内且本猴处于附近绕圈巡逻，继续在门口周围走廊接力绕圈
            if (isOrbitingPlayerRoom && SecretRoomDoor.IsPlayerInsideAnyRoom)
            {
                StartNearbyRoomOrbit();
                return;
            }

            isOrbitingPlayerRoom = false;
            // 单向连续巡逻：不原地停顿后掉头，直接衔接下一个分散巡逻点
            StartNextPatrol();
        }
    }

    private void UpdateTrack()
    {
        if (isBossKing)
        {
            if (SecretRoomDoor.IsPlayerInsideAnyRoom)
            {
                StartNearbyRoomOrbit();
                return;
            }
            if (player != null && player.IsAlive && movement.HasReachedDestination())
            {
                movement.Track(player.transform.position, true, true);
            }
            return;
        }

        // 响铃期间（且尚未亲眼看到玩家）始终保持铃铛作为目的地；若提前赶到铃铛位置，响铃期间在原地附近停留/巡回直到响铃结束
        if (IsCurrentlyLuredByBell())
        {
            if (movement.HasReachedDestination())
            {
                movement.Track(WallServiceBellInteractable.ActiveBellTargetPos, false);
            }
            return;
        }

        if (SecretRoomDoor.IsPlayerInsideAnyRoom)
        {
            StartNearbyRoomOrbit();
            return;
        }

        // 丢视野/声音后不立刻放弃，继续沿单向路线追到最后信号位置，到达后直接继续向前巡逻（不原地掉头）
        if (!isSeeingPlayer && movement.HasReachedDestination())
        {
            StartNextPatrol();
        }
    }

    private bool CheckPlayer()
    {
        if (player == null || !player.IsAlive || !player.gameObject.activeInHierarchy)
        {
            isSeeingPlayer = false;
            wasSeeingPlayerPrev = false;
            return false;
        }

        // 首领专属逻辑：实时接收玩家位置，始终保持快跑包抄状态（仅在玩家躲入暗门内时在门口走廊绕圈守株待兔）
        if (isBossKing)
        {
            if (SecretRoomDoor.IsPlayerInsideAnyRoom)
            {
                isSeeingPlayer = false;
                wasSeeingPlayerPrev = false;
                if (!isOrbitingPlayerRoom || (roomOrbitRepickTimer <= 0f && movement.HasReachedDestination()))
                {
                    StartNearbyRoomOrbit();
                }
                return false;
            }

            if (isOrbitingPlayerRoom)
            {
                isOrbitingPlayerRoom = false;
            }

            bool directSight = SeePlayer();
            if (directSight && !wasSeeingPlayerPrev && spotRoarCooldown <= 0f)
            {
                AudioManager.PlayMonkeySpotRoar(transform.position, isBossKing: true);
                spotRoarCooldown = 5.5f;
            }
            wasSeeingPlayerPrev = directSight;

            isSeeingPlayer = true;
            SetState(MonkeyState.Track);
            movement.Track(player.transform.position, true);
            return true;
        }

        // 1. 墙壁铃铛响铃期间（默认 3 秒）：
        //    - 若本猴亲眼看见玩家（且玩家不在暗门房间内），立刻解除本猴的铃铛吸引并改追玩家；
        //    - 否则在整个响铃期间强制保持铃铛位置为目的地，忽略玩家逃跑脚步声，让玩家可趁机逃离！
        if (IsCurrentlyLuredByBell())
        {
            if (!SecretRoomDoor.IsPlayerInsideAnyRoom && SeePlayer())
            {
                bellBrokenBySightSeqId = WallServiceBellInteractable.ActiveRingSequenceId;
                isOrbitingPlayerRoom = false;
                if (!wasSeeingPlayerPrev && spotRoarCooldown <= 0f)
                {
                    AudioManager.PlayMonkeySpotRoar(transform.position, isBossKing: false);
                    spotRoarCooldown = 5.5f;
                }
                wasSeeingPlayerPrev = true;
                isSeeingPlayer = true;
                SetState(MonkeyState.Track);
                movement.Track(player.transform.position, true, true);
                return true;
            }

            isSeeingPlayer = false;
            wasSeeingPlayerPrev = false;
            isOrbitingPlayerRoom = false;
            SetState(MonkeyState.Track);
            bool newlyLured = activeBellLureSeqId != WallServiceBellInteractable.ActiveRingSequenceId;
            activeBellLureSeqId = WallServiceBellInteractable.ActiveRingSequenceId;
            movement.Track(WallServiceBellInteractable.ActiveBellTargetPos, false, newlyLured);
            return false;
        }

        // 玩家进入暗门房间内部时：猴子不会闯入门内，附近/追击中的猴子在门口外侧走廊绕圈巡逻；
        // 若玩家通过传送门传到远处房间，原房间附近的猴子自动放弃蹲守恢复正常巡逻，避免透视传送终点
        if (SecretRoomDoor.IsPlayerInsideAnyRoom)
        {
            isSeeingPlayer = false;
            Vector3 anchor = SecretRoomDoor.ActiveRoomCorridorAnchor;
            float sqrToRoom = (anchor - transform.position).sqrMagnitude;
            if (isOrbitingPlayerRoom && sqrToRoom > 18f * 18f)
            {
                isOrbitingPlayerRoom = false;
                StartNextPatrol();
                return false;
            }

            bool shouldOrbit = sqrToRoom <= 15f * 15f
                || (state == MonkeyState.Track && sqrToRoom <= 18f * 18f)
                || player.Listen(transform.position);

            if (shouldOrbit && (!isOrbitingPlayerRoom || (roomOrbitRepickTimer <= 0f && movement.HasReachedDestination())))
            {
                StartNearbyRoomOrbit();
            }
            return false;
        }

        if (isOrbitingPlayerRoom)
        {
            isOrbitingPlayerRoom = false;
        }

        // 优先级 1：直视视野（无遮挡时加速追击）
        if (SeePlayer())
        {
            if (!wasSeeingPlayerPrev && spotRoarCooldown <= 0f)
            {
                AudioManager.PlayMonkeySpotRoar(transform.position, isBossKing: false);
                spotRoarCooldown = 5.5f;
            }
            wasSeeingPlayerPrev = true;
            isSeeingPlayer = true;
            SetState(MonkeyState.Track);
            movement.Track(player.transform.position, true);
            return true;
        }

        isSeeingPlayer = false;
        wasSeeingPlayerPrev = false;

        // 优先级 2：听到玩家脚步/跑步声
        if (player.Listen(transform.position))
        {
            SetState(MonkeyState.Track);
            movement.Track(player.transform.position, false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 玩家身处暗门房间内部时，在门口外侧 2.2m~8.5m 的走廊环路中持续选取顺向无拥堵巡逻点绕圈蹲守。
    /// </summary>
    private void StartNearbyRoomOrbit()
    {
        if (!SecretRoomDoor.IsPlayerInsideAnyRoom)
        {
            isOrbitingPlayerRoom = false;
            StartNextPatrol();
            return;
        }

        isOrbitingPlayerRoom = true;
        isSeeingPlayer = false;
        roomOrbitRepickTimer = 0.65f;
        SetState(MonkeyState.Patrol);

        Vector3 anchor = SecretRoomDoor.ActiveRoomCorridorAnchor;
        if (MonkeyRoutePlanner.TryPickNearbyOrbitPoint(
            movement,
            transform.position,
            movement.CommittedHeadingDir,
            anchor,
            2.2f,
            8.5f,
            out Vector3 orbitPos))
        {
            movement.PatrolTo(orbitPos);
        }
        else
        {
            StartNextPatrol();
        }
    }

    private bool SeePlayer()
    {
        Vector3 eyePos = transform.position + Vector3.up * eyeHeight;
        Vector3 targetPos = player.transform.position + Vector3.up * playerTargetOffsetY;
        Vector3 toTarget = targetPos - eyePos;

        float sqrDist = toTarget.sqrMagnitude;
        if (sqrDist > viewDistance * viewDistance)
        {
            return false;
        }

        // 水平前方视野夹角判断：玩家在背后时不会被看见
        Vector3 flatDir = new Vector3(toTarget.x, 0f, toTarget.z);
        if (flatDir.sqrMagnitude > 0.0001f &&
            Vector3.Angle(transform.forward, flatDir) > viewAngle * 0.5f)
        {
            return false;
        }

        float dist = Mathf.Sqrt(sqrDist);
        return !Physics.Raycast(eyePos, toTarget / dist, dist, wall);
    }

    private bool TryProximityCatchFallback()
    {
        if (player == null || !player.IsAlive || !player.gameObject.activeInHierarchy) return false;
        if (SecretRoomDoor.IsPlayerInsideAnyRoom && !isWakingStepOut) return false;
        if (catchFallbackRadius <= 0.05f) return false;

        Vector3 delta = player.transform.position - transform.position;
        delta.y = 0f;
        float sqrDist = delta.sqrMagnitude;
        if (sqrDist > catchFallbackRadius * catchFallbackRadius) return false;

        // 确认中间没有隔墙才判定抓捕
        Vector3 eyePos = transform.position + Vector3.up * 0.35f;
        Vector3 targetPos = player.transform.position + Vector3.up * 0.20f;
        Vector3 toTarget = targetPos - eyePos;
        float dist = toTarget.magnitude;
        if (dist > 0.02f && Physics.Raycast(eyePos, toTarget / dist, dist, wall, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        HandleHitPlayer(player);
        return true;
    }

    private void StartNextPatrol()
    {
        SetState(MonkeyState.Patrol);

        if (patrolPoints != null && patrolPoints.Length > 0)
        {
            int nextIndex = PickNextPatrolPointIndex();
            if (nextIndex >= 0 && patrolPoints[nextIndex] != null)
            {
                lastPatrolIndex = nextIndex;
                movement.PatrolTo(patrolPoints[nextIndex].position);
                return;
            }
        }

        if (movement.TryGetRandomPatrolPoint(transform.position, patrolRadius, out Vector3 randomPos))
        {
            movement.PatrolTo(randomPos);
        }
        else
        {
            SetState(MonkeyState.Idle);
        }
    }

    private int PickNextPatrolPointIndex()
    {
        if (patrolPoints.Length == 1) return 0;

        // 优先选择未被其他猴子认领、且目标区域无其他猴子聚集（拥堵=0）的分散巡逻点
        List<int> bestChoices = new();
        List<int> fallbackChoices = new();
        object myOwner = movement != null ? (object)movement : this;

        for (int i = 0; i < patrolPoints.Length; i++)
        {
            if (i == lastPatrolIndex || patrolPoints[i] == null) continue;
            float sqrDist = (patrolPoints[i].position - transform.position).sqrMagnitude;
            if (sqrDist < 16f) continue; // 跳过过近的巡逻点，鼓励大范围分散游走

            bool claimed = IsPatrolPointClaimedByOther(i);
            int crowd = MonkeyRoutePlanner.EstimateGoalCrowd(myOwner, patrolPoints[i].position);
            if (!claimed && crowd == 0)
            {
                bestChoices.Add(i);
            }
            else if (!claimed)
            {
                fallbackChoices.Add(i);
            }
        }

        if (bestChoices.Count > 0)
        {
            return bestChoices[UnityEngine.Random.Range(0, bestChoices.Count)];
        }
        if (fallbackChoices.Count > 0)
        {
            return fallbackChoices[UnityEngine.Random.Range(0, fallbackChoices.Count)];
        }

        for (int i = 0; i < 6; i++)
        {
            int idx = UnityEngine.Random.Range(0, patrolPoints.Length);
            if (idx != lastPatrolIndex && patrolPoints[idx] != null)
            {
                return idx;
            }
        }
        return (lastPatrolIndex + 1) % patrolPoints.Length;
    }

    private bool IsPatrolPointClaimedByOther(int index)
    {
        for (int i = 0; i < activeMonkeys.Count; i++)
        {
            MonkeyAI other = activeMonkeys[i];
            if (other != null && other != this && other.lastPatrolIndex == index)
            {
                return true;
            }
        }
        return false;
    }

    private void SetState(MonkeyState nextState)
    {
        if (nextState == state) return;
        state = nextState;
        stateTimer = 0f;
    }

    private void HandleHitPlayer(PlayerInteract hitPlayer)
    {
        if (state == MonkeyState.Executing || state == MonkeyState.Frozen || state == MonkeyState.Sleep) return;
        if (SecretRoomDoor.IsPlayerInsideAnyRoom && !isWakingStepOut) return;
        if (player == null) player = hitPlayer;
        OnCatchPlayer?.Invoke(this);
    }

    private void OnDrawGizmosSelected()
    {
        Vector3 eyePos = transform.position + Vector3.up * eyeHeight;
        Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.7f);

        Vector3 leftDir = Quaternion.Euler(0f, -viewAngle * 0.5f, 0f) * transform.forward;
        Vector3 rightDir = Quaternion.Euler(0f, viewAngle * 0.5f, 0f) * transform.forward;
        Gizmos.DrawRay(eyePos, leftDir * viewDistance);
        Gizmos.DrawRay(eyePos, rightDir * viewDistance);
        Gizmos.DrawRay(eyePos, transform.forward * viewDistance);

        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, patrolRadius);
    }
}
