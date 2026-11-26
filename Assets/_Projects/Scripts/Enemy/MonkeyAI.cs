using System;
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

    [Header("视觉与感知")]
    [SerializeField] private LayerMask wall;
    [SerializeField] private float viewDistance = 8f;
    [SerializeField, Range(10f, 180f)] private float viewAngle = 110f;
    [SerializeField] private float eyeHeight = 0.7f;
    [SerializeField] private float playerTargetOffsetY = 0.15f;
    [SerializeField] private float checkInterval = 0.1f;

    [Header("巡逻设置")]
    [SerializeField] private float patrolRadius = 6f;
    [SerializeField] private float idleWaitTime = 1.5f;
    [SerializeField] private Transform[] patrolPoints;

    public event Action<MonkeyAI> OnCatchPlayer;

    private static readonly List<MonkeyAI> activeMonkeys = new();

    private MonkeyState state;
    private MonkeyMovement movement;
    private MonkeyBox monkeyBox;
    private MonkeyAnimation monkeyAnimation;
    private PlayerInteract player;

    private float stateTimer;
    private float checkTimer;
    private bool isSeeingPlayer;
    private int lastPatrolIndex = -1;

    private void Awake()
    {
        movement = GetComponent<MonkeyMovement>();
        monkeyBox = GetComponentInChildren<MonkeyBox>(true);
        monkeyAnimation = GetComponentInChildren<MonkeyAnimation>(true);
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
        state = initialState;
        if (state == MonkeyState.Patrol)
        {
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
    /// 水晶被拾取后的延迟信号入口：仅当未直接看见玩家且处于正常巡逻/追踪状态时前往调查。
    /// </summary>
    public void InvestigatePosition(Vector3 worldPos)
    {
        if (state == MonkeyState.Executing || state == MonkeyState.Frozen ||
            state == MonkeyState.Sleep || state == MonkeyState.Wake)
        {
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

    /// <summary>
    /// 其他猴子或全局事件冻结时调用，防止多只猴子继续追赶或二次触发。
    /// </summary>
    public void Freeze()
    {
        SetState(MonkeyState.Frozen);
        isSeeingPlayer = false;
        if (monkeyBox != null) monkeyBox.SetActive(false);
        movement.Stop();
    }

    public void WakeUp()
    {
        if (state != MonkeyState.Sleep) return;
        StartNextPatrol();
    }

    private void Update()
    {
        if (state == MonkeyState.Executing || state == MonkeyState.Frozen ||
            state == MonkeyState.Sleep || state == MonkeyState.Wake)
        {
            return;
        }

        stateTimer += Time.deltaTime;
        checkTimer += Time.deltaTime;
        if (checkTimer >= checkInterval)
        {
            checkTimer = 0f;
            CheckPlayer();
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

    private void UpdateIdle()
    {
        if (stateTimer >= idleWaitTime)
        {
            StartNextPatrol();
        }
    }

    private void UpdatePatrol()
    {
        if (movement.HasReachedDestination())
        {
            SetState(MonkeyState.Idle);
        }
    }

    private void UpdateTrack()
    {
        // 丢视野/声音后不立刻放弃，继续追到最后一次收到信号的位置，到达后再切回巡逻
        if (!isSeeingPlayer && movement.HasReachedDestination())
        {
            SetState(MonkeyState.Idle);
        }
    }

    private bool CheckPlayer()
    {
        if (player == null || !player.IsAlive || !player.gameObject.activeInHierarchy)
        {
            isSeeingPlayer = false;
            return false;
        }

        // 优先级 1：直视视野（无遮挡时加速追击）
        if (SeePlayer())
        {
            isSeeingPlayer = true;
            SetState(MonkeyState.Track);
            movement.Track(player.transform.position, true);
            return true;
        }

        isSeeingPlayer = false;

        // 优先级 2：听到玩家脚步/跑步声
        if (player.Listen(transform.position))
        {
            SetState(MonkeyState.Track);
            movement.Track(player.transform.position, false);
            return true;
        }

        return false;
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

        // 优先从未被其他猴子选作当前目标的巡逻点里挑选，让多只猴子自然分散巡逻
        List<int> unoccupied = new();
        for (int i = 0; i < patrolPoints.Length; i++)
        {
            if (i == lastPatrolIndex || patrolPoints[i] == null) continue;
            if (!IsPatrolPointClaimedByOther(i))
            {
                unoccupied.Add(i);
            }
        }

        if (unoccupied.Count > 0)
        {
            return unoccupied[UnityEngine.Random.Range(0, unoccupied.Count)];
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
        if (state == MonkeyState.Executing || state == MonkeyState.Frozen) return;
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
