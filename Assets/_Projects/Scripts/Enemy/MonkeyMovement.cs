using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class MonkeyMovement : MonoBehaviour
{
    [Header("移动速度（同步适配玩家行走 1.55 / 奔跑 2.55，最快速度略高于玩家但不夸张）")]
    [SerializeField] private float patrolSpeed = 1.95f;
    [SerializeField] private float trackSpeed = 2.38f;
    [SerializeField] private float runSpeed = 2.72f;

    [Header("平滑转向与过弯设置")]
    [SerializeField] private float patrolTurnSpeedDeg = 400f;
    [SerializeField] private float chaseTurnSpeedDeg = 640f;
    [SerializeField] private float turnSmoothFactor = 14f;
    [SerializeField] private float cornerMinSpeedRatio = 0.80f;
    [SerializeField] private float cornerSteerBlend = 0.55f;
    [SerializeField] private float moveAcceleration = 15f;

    private enum SpeedMode
    {
        Patrol,
        Track,
        Sprint
    }

    private NavMeshAgent agent;
    private SpeedMode currentSpeedMode = SpeedMode.Patrol;
    private bool isChasing;
    private bool isStoppedExternally;
    private bool isBossPincerMode;
    private bool isPausedForTimeStop;
    private float bossReplanStaggerOffset;

    // 有向单行道与防扎堆路径跟踪状态
    private readonly List<int> activeRoute = new(64);
    private int routeCursor;
    private int committedHeadingDir = -1; // 0=+X, 1=-X, 2=+Z, 3=-Z；禁止直接走 OppositeDir
    private int lastCellIdx = -1;
    private Vector3 currentGoalWorldPos;
    private bool hasActiveGoal;
    private bool directForwardLunge;
    private float replanCooldown;

    private float CurrentBaseSpeed => currentSpeedMode switch
    {
        SpeedMode.Sprint => runSpeed,
        SpeedMode.Track => trackSpeed,
        _ => patrolSpeed
    };

    public float Speed => agent != null ? agent.velocity.magnitude : 0f;
    public float RunSpeed => runSpeed;
    public float TrackSpeed => trackSpeed;
    public float NormalizedSpeed => runSpeed > 0.001f ? Mathf.Clamp01(Speed / runSpeed) : 0f;
    public int CommittedHeadingDir => committedHeadingDir;

    public void SetBossPincerMode(bool enabled, int bossIndex = 0)
    {
        isBossPincerMode = enabled;
        bossReplanStaggerOffset = (bossIndex % 2) * 0.14f;
    }

    public void PauseForTimeStop(bool paused)
    {
        isPausedForTimeStop = paused;
        if (agent == null || !agent.enabled || !agent.isOnNavMesh) return;

        if (paused)
        {
            agent.isStopped = true;
            agent.velocity = Vector3.zero;
        }
        else if (!isStoppedExternally)
        {
            agent.isStopped = false;
            agent.speed = CurrentBaseSpeed;
            if (hasActiveGoal)
            {
                ReplanDirectedRoute(currentGoalWorldPos);
            }
        }
    }

    private bool IsAgentReady => agent != null && agent.enabled && agent.isOnNavMesh;

    private void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        agent.updateRotation = false; // 由脚本统一做平滑身体转向，避免僵硬瞬转
        agent.autoBraking = false;
        agent.acceleration = moveAcceleration;
        currentSpeedMode = SpeedMode.Patrol;
        agent.speed = CurrentBaseSpeed;
    }

    private void OnDisable()
    {
        MonkeyRoutePlanner.UnregisterMonkey(this);
    }

    private void Update()
    {
        if (!IsAgentReady || isStoppedExternally || isPausedForTimeStop || agent.isStopped) return;

        float dt = Time.deltaTime;
        if (dt <= 0.00001f) return;

        if (replanCooldown > 0f)
        {
            replanCooldown -= dt;
        }

        // 沿有向单行道路径逐格推进，并持续同步本猴的实时占用格给分流路由器
        AdvanceGridRouteIfNeeded();

        // 结合 NavMesh 期望速度与下一拐角方向，提前预判转弯方向
        Vector3 desiredDir = agent.desiredVelocity;
        desiredDir.y = 0f;
        if (desiredDir.sqrMagnitude < 0.0025f && agent.hasPath)
        {
            desiredDir = agent.steeringTarget - transform.position;
            desiredDir.y = 0f;
        }

        if (desiredDir.sqrMagnitude < 0.0004f) return;
        desiredDir.Normalize();

        Vector3 lookDir = desiredDir;
        if (agent.hasPath)
        {
            Vector3 steerDir = agent.steeringTarget - transform.position;
            steerDir.y = 0f;
            if (steerDir.sqrMagnitude > 0.0025f)
            {
                lookDir = Vector3.Slerp(desiredDir, steerDir.normalized, Mathf.Clamp01(cornerSteerBlend) * 0.45f);
                lookDir.y = 0f;
                if (lookDir.sqrMagnitude > 0.0001f) lookDir.Normalize();
                else lookDir = desiredDir;
            }
        }

        // 1. 身体朝向平滑转向目标行走方向
        float maxDegPerSec = isChasing ? chaseTurnSpeedDeg : patrolTurnSpeedDeg;
        Quaternion targetRot = Quaternion.LookRotation(lookDir, Vector3.up);
        float slerpT = 1f - Mathf.Exp(-turnSmoothFactor * dt);
        Quaternion slerped = Quaternion.Slerp(transform.rotation, targetRot, slerpT);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, slerped, maxDegPerSec * dt);

        // 2. 转弯时按身体与路径夹角轻微降速，交由 NavMeshAgent 原生加速到目标移速
        Vector3 bodyForward = transform.forward;
        bodyForward.y = 0f;
        if (bodyForward.sqrMagnitude > 0.0001f) bodyForward.Normalize();

        float angleToPath = Vector3.Angle(bodyForward, desiredDir);
        float align = Mathf.Clamp01(1f - angleToPath / 110f);
        float curvedSpeed = CurrentBaseSpeed * Mathf.Lerp(cornerMinSpeedRatio, 1f, align * align);
        agent.speed = curvedSpeed;
        agent.acceleration = moveAcceleration;
    }

    /// <summary>
    /// 追击/调查目标位置：
    /// 1) 严禁原地 180° 掉头，始终按当前 committedHeadingDir 向前推进至路口再绕行；
    /// 2) 经过 MonkeyRoutePlanner 计算，当某路线已有 >= 2 只猴子时自动分流改走另一条路；
    /// 3) 若正前方同条直道近距离直视玩家（非背后），直接锁向前方玩家位置冲刺确保精准抓捕。
    /// </summary>
    public void Track(Vector3 targetPos, bool seePlayer, bool forceReplan = false)
    {
        if (!IsAgentReady || isPausedForTimeStop) return;
        isStoppedExternally = false;
        isChasing = true;
        agent.isStopped = false;
        agent.autoBraking = false;
        currentSpeedMode = seePlayer ? SpeedMode.Sprint : SpeedMode.Track;
        agent.speed = CurrentBaseSpeed;

        currentGoalWorldPos = targetPos;
        hasActiveGoal = true;

        MonkeyRoutePlanner.EnsureBuilt();

        // 若正前方近距离直视玩家（与当前前进方向夹角 < 50°，绝不向后掉头），直接直线扑向玩家位置
        if (seePlayer && committedHeadingDir >= 0)
        {
            Vector3 toTarget = targetPos - transform.position;
            toTarget.y = 0f;
            float dist = toTarget.magnitude;
            if (dist < 3.2f && dist > 0.05f &&
                Vector3.Dot(MonkeyRoutePlanner.DirToWorld(committedHeadingDir), toTarget / dist) > 0.64f)
            {
                directForwardLunge = true;
                agent.SetDestination(targetPos);
                int curCell = MonkeyRoutePlanner.WorldToNearestWalkableCell(transform.position);
                activeRoute.Clear();
                activeRoute.Add(curCell);
                activeRoute.Add(MonkeyRoutePlanner.WorldToNearestWalkableCell(targetPos));
                routeCursor = 1;
                MonkeyRoutePlanner.UpdateMonkeyRoute(this, curCell, committedHeadingDir, activeRoute, isBossPincerMode);
                return;
            }
        }

        directForwardLunge = false;
        if (forceReplan || replanCooldown <= 0f || activeRoute.Count == 0 || routeCursor >= activeRoute.Count)
        {
            ReplanDirectedRoute(targetPos);
            replanCooldown = isBossPincerMode ? (0.24f + bossReplanStaggerOffset) : 0.28f;
        }
    }

    public void PatrolTo(Vector3 targetPos)
    {
        if (!IsAgentReady) return;
        isStoppedExternally = false;
        isChasing = false;
        directForwardLunge = false;
        agent.isStopped = false;
        agent.autoBraking = false; // 连续巡逻不急刹，保持单向流利过弯
        currentSpeedMode = SpeedMode.Patrol;
        agent.speed = CurrentBaseSpeed;

        currentGoalWorldPos = targetPos;
        hasActiveGoal = true;

        MonkeyRoutePlanner.EnsureBuilt();
        ReplanDirectedRoute(targetPos);
        replanCooldown = 0.45f;
    }

    private void ReplanDirectedRoute(Vector3 goalWorld)
    {
        if (!MonkeyRoutePlanner.IsReady)
        {
            agent.SetDestination(goalWorld);
            return;
        }

        int curCell = MonkeyRoutePlanner.WorldToNearestWalkableCell(transform.position);
        lastCellIdx = curCell;

        if (MonkeyRoutePlanner.PlanRoute(this, transform.position, committedHeadingDir, goalWorld, activeRoute, isBossPincerMode) &&
            activeRoute.Count > 0)
        {
            routeCursor = activeRoute.Count > 1 ? 1 : 0;
            if (activeRoute.Count > 1)
            {
                int firstDir = MonkeyRoutePlanner.GetDirectionBetweenCells(activeRoute[0], activeRoute[1]);
                if (firstDir >= 0) committedHeadingDir = firstDir;
            }
            ApplyCurrentRouteLookahead();
        }
        else
        {
            agent.SetDestination(goalWorld);
        }
    }

    private void AdvanceGridRouteIfNeeded()
    {
        if (!MonkeyRoutePlanner.IsReady) return;

        int curCell = MonkeyRoutePlanner.WorldToNearestWalkableCell(transform.position);
        bool enteredNewCell = (curCell != lastCellIdx);
        if (enteredNewCell)
        {
            if (lastCellIdx >= 0)
            {
                int movedDir = MonkeyRoutePlanner.GetDirectionBetweenCells(lastCellIdx, curCell);
                if (movedDir >= 0)
                {
                    committedHeadingDir = movedDir;
                }
            }
            lastCellIdx = curCell;

            // 每次踏入 3 岔/4 岔路口时若冷却允许，主动刷新分流路线，发现前方有 >=2 只猴子立即改走岔路
            if (!directForwardLunge && hasActiveGoal && replanCooldown <= 0.10f && MonkeyRoutePlanner.GetCellDegree(curCell) >= 3)
            {
                ReplanDirectedRoute(currentGoalWorldPos);
                replanCooldown = isChasing ? 0.30f : 0.65f;
                return;
            }
        }

        if (directForwardLunge) return;
        if (activeRoute.Count == 0) return;

        // 逐格推进路点游标
        bool advanced = false;
        while (routeCursor < activeRoute.Count)
        {
            Vector3 wp = MonkeyRoutePlanner.CellToWorld(activeRoute[routeCursor]);
            float dx = wp.x - transform.position.x;
            float dz = wp.z - transform.position.z;
            if (dx * dx + dz * dz <= 0.44f * 0.44f)
            {
                if (routeCursor > 0)
                {
                    int segDir = MonkeyRoutePlanner.GetDirectionBetweenCells(activeRoute[routeCursor - 1], activeRoute[routeCursor]);
                    if (segDir >= 0) committedHeadingDir = segDir;
                }
                routeCursor++;
                advanced = true;
            }
            else
            {
                break;
            }
        }

        if (advanced)
        {
            // 更新本猴剩余预占路线，让其他猴子实时避开
            if (routeCursor < activeRoute.Count)
            {
                List<int> rem = activeRoute.GetRange(routeCursor, activeRoute.Count - routeCursor);
                MonkeyRoutePlanner.UpdateMonkeyRoute(this, curCell, committedHeadingDir, rem, isBossPincerMode);
                ApplyCurrentRouteLookahead();
            }
            else
            {
                MonkeyRoutePlanner.UpdateMonkeyRoute(this, curCell, committedHeadingDir, null, isBossPincerMode);
            }
        }
        else if (!agent.pathPending && (!agent.hasPath || agent.remainingDistance <= 0.25f) && routeCursor < activeRoute.Count)
        {
            ApplyCurrentRouteLookahead();
        }
    }

    /// <summary>
    /// 只把当前直行段前瞻（最多 3 格且不超过下一次拐弯格）喂给 NavMeshAgent，
    /// 既保持高速过弯丝滑，又彻底杜绝 NavMeshAgent 擅自走捷径原地 180° 掉头。
    /// </summary>
    private void ApplyCurrentRouteLookahead()
    {
        if (routeCursor >= activeRoute.Count) return;

        int lookIdx = routeCursor;
        int startCell = lookIdx > 0 ? activeRoute[lookIdx - 1] : MonkeyRoutePlanner.WorldToNearestWalkableCell(transform.position);
        int segDir = MonkeyRoutePlanner.GetDirectionBetweenCells(startCell, activeRoute[lookIdx]);
        if (segDir >= 0) committedHeadingDir = segDir;

        int maxLook = Mathf.Min(activeRoute.Count - 1, routeCursor + 2);
        while (lookIdx < maxLook)
        {
            int nextDir = MonkeyRoutePlanner.GetDirectionBetweenCells(activeRoute[lookIdx], activeRoute[lookIdx + 1]);
            if (nextDir != segDir) break;
            lookIdx++;
        }

        Vector3 dest = MonkeyRoutePlanner.CellToWorld(activeRoute[lookIdx]);
        dest.y = transform.position.y;
        agent.SetDestination(dest);
    }

    public bool TryGetRandomPatrolPoint(Vector3 origin, float radius, out Vector3 result)
    {
        for (int i = 0; i < 8; i++)
        {
            Vector2 offset = Random.insideUnitCircle * radius;
            Vector3 candidate = origin + new Vector3(offset.x, 0f, offset.y);
            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, radius, NavMesh.AllAreas))
            {
                if ((hit.position - transform.position).sqrMagnitude > 0.25f)
                {
                    result = hit.position;
                    return true;
                }
            }
        }

        result = origin;
        return false;
    }

    public bool HasReachedDestination()
    {
        if (!IsAgentReady) return true;

        if (MonkeyRoutePlanner.IsReady && !directForwardLunge && activeRoute.Count > 0)
        {
            if (routeCursor < activeRoute.Count) return false;
            Vector3 lastWp = MonkeyRoutePlanner.CellToWorld(activeRoute[activeRoute.Count - 1]);
            float dx = lastWp.x - transform.position.x;
            float dz = lastWp.z - transform.position.z;
            return dx * dx + dz * dz <= 0.55f * 0.55f;
        }

        if (agent.pathPending) return false;
        return !agent.hasPath || agent.remainingDistance <= agent.stoppingDistance + 0.18f;
    }

    public void Stop()
    {
        isStoppedExternally = true;
        hasActiveGoal = false;
        directForwardLunge = false;
        activeRoute.Clear();
        routeCursor = 0;
        MonkeyRoutePlanner.UnregisterMonkey(this);

        if (agent == null) return;
        if (agent.isOnNavMesh)
        {
            agent.isStopped = true;
            agent.ResetPath();
        }
        agent.velocity = Vector3.zero;
    }

    public void SetNavAgentEnabled(bool enabled)
    {
        if (agent == null) agent = GetComponent<NavMeshAgent>();
        if (agent == null) return;

        if (!enabled)
        {
            Stop();
            agent.enabled = false;
        }
        else
        {
            agent.enabled = true;
            agent.updateRotation = false;
            agent.autoBraking = false;
            agent.acceleration = moveAcceleration;
            committedHeadingDir = -1;
            lastCellIdx = -1;
            replanCooldown = 0f;
            isStoppedExternally = false;
        }
    }

    public void SetInitialCommittedHeading(Vector3 worldForward)
    {
        committedHeadingDir = MonkeyRoutePlanner.WorldDirToCardinal(worldForward);
        lastCellIdx = MonkeyRoutePlanner.WorldToNearestWalkableCell(transform.position);
    }

    public bool EnsureOnNavMesh(float sampleRadius = 2f)
    {
        if (agent == null || !agent.enabled) return false;
        if (agent.isOnNavMesh) return true;

        if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, sampleRadius, NavMesh.AllAreas))
        {
            return agent.Warp(hit.position);
        }
        return false;
    }

    public void WarpTo(Vector3 pos, Quaternion rot)
    {
        Stop();
        transform.rotation = rot;
        committedHeadingDir = -1;
        lastCellIdx = -1;
        replanCooldown = 0f;

        if (agent != null && agent.enabled)
        {
            Vector3 target = pos;
            if (NavMesh.SamplePosition(pos, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            {
                target = hit.position;
            }
            if (!agent.Warp(target))
            {
                transform.position = target;
            }
        }
        else
        {
            transform.position = pos;
        }
    }

    public void FaceTarget(Vector3 targetPos)
    {
        Vector3 dir = targetPos - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.0001f)
        {
            transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }
    }
}
