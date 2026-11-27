using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 80x80 走廊网格有向单行道与防扎堆分流路由器：
/// 1. 严格禁止在走廊内原地 180° 掉头（状态为 (cell, headingDir)，不允许走 Opposite(headingDir)）；
/// 2. 实时记录每只活跃猴子的当前格与未来规划路径，当某条路线已有其他猴子（合入本猴将 >= 2 只）时，
///    自动施加高额拥堵代价，促使猴子在路口分流改走另一条包抄/巡逻路线，彻底消除猴子扎堆。
/// </summary>
public static class MonkeyRoutePlanner
{
    public const int GridSize = 80;
    private const int CellCount = GridSize * GridSize;
    private const int StateCount = CellCount * 4;
    private const int ReservedLookaheadCells = 24;
    private const int BossReservedLookaheadCells = 42;

    // 0 = East (+X), 1 = West (-X), 2 = North (+Z), 3 = South (-Z)
    private static readonly int[] DirDC = { 1, -1, 0, 0 };
    private static readonly int[] DirDR = { 0, 0, -1, 1 };
    private static readonly Vector3[] DirWorld =
    {
        Vector3.right,
        Vector3.left,
        Vector3.forward,
        Vector3.back
    };

    private static bool isBuilt;
    private static readonly bool[] walkable = new bool[CellCount];
    private static readonly byte[] openMask = new byte[CellCount];

    // 每只活跃猴子已声明的路线（用于拥堵分流统计）
    private struct MonkeyReservation
    {
        public int currentCell;
        public int headingDir;
        public List<int> routeCells;
    }

    private static readonly Dictionary<object, MonkeyReservation> reservations = new();

    // 复用寻路缓冲，避免每次重规划产生 GC
    private static readonly float[] gScore = new float[StateCount];
    private static readonly int[] parentState = new int[StateCount];
    private static readonly int[] visitedStamp = new int[StateCount];
    private static int curStamp = 1;

    private static readonly int[] heapStates = new int[StateCount + 8];
    private static readonly float[] heapF = new float[StateCount + 8];
    private static int heapSize;

    // 每帧重算的格子拥堵计数（复用数组零 GC）
    private static readonly int[] crowdBuf = new int[CellCount];
    private static readonly int[] crowdStamp = new int[CellCount];
    private static int curCrowdStamp = 1;

    public static bool IsReady => isBuilt;

    public static int OppositeDir(int dir) => dir >= 0 && dir < 4 ? (dir ^ 1) : -1;

    public static Vector3 DirToWorld(int dir) => (dir >= 0 && dir < 4) ? DirWorld[dir] : Vector3.forward;

    public static int WorldDirToCardinal(Vector3 dir)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return 2;
        if (Mathf.Abs(dir.x) >= Mathf.Abs(dir.z))
        {
            return dir.x >= 0f ? 0 : 1;
        }
        return dir.z >= 0f ? 2 : 3;
    }

    public static int PackCell(int c, int r) => c * GridSize + r;
    public static void UnpackCell(int idx, out int c, out int r)
    {
        c = idx / GridSize;
        r = idx % GridSize;
    }

    public static Vector3 CellToWorld(int cellIdx)
    {
        UnpackCell(cellIdx, out int c, out int r);
        return new Vector3(c - 39.5f, 0f, 39.5f - r);
    }

    public static int WorldToNearestWalkableCell(Vector3 pos)
    {
        int c = Mathf.Clamp(Mathf.RoundToInt(pos.x + 39.5f), 0, GridSize - 1);
        int r = Mathf.Clamp(Mathf.RoundToInt(39.5f - pos.z), 0, GridSize - 1);
        int idx = PackCell(c, r);
        if (!isBuilt || walkable[idx]) return idx;

        // 兜底：若角色贴墙导致取整偏进墙格，在 2 格邻域内找最近可行走格
        int best = idx;
        float bestSqr = float.MaxValue;
        for (int dc = -2; dc <= 2; dc++)
        {
            int nc = c + dc;
            if (nc < 0 || nc >= GridSize) continue;
            for (int dr = -2; dr <= 2; dr++)
            {
                int nr = r + dr;
                if (nr < 0 || nr >= GridSize) continue;
                int nIdx = PackCell(nc, nr);
                if (!walkable[nIdx]) continue;
                Vector3 wp = CellToWorld(nIdx);
                float sq = (wp.x - pos.x) * (wp.x - pos.x) + (wp.z - pos.z) * (wp.z - pos.z);
                if (sq < bestSqr)
                {
                    bestSqr = sq;
                    best = nIdx;
                }
            }
        }
        return best;
    }

    public static int GetDirectionBetweenCells(int fromCell, int toCell)
    {
        UnpackCell(fromCell, out int c0, out int r0);
        UnpackCell(toCell, out int c1, out int r1);
        int dc = c1 - c0;
        int dr = r1 - r0;
        if (dc > 0) return 0;
        if (dc < 0) return 1;
        if (dr < 0) return 2;
        if (dr > 0) return 3;
        return -1;
    }

    public static int GetCellDegree(int cellIdx)
    {
        if (!isBuilt || cellIdx < 0 || cellIdx >= CellCount) return 0;
        byte m = openMask[cellIdx];
        int deg = 0;
        for (int d = 0; d < 4; d++)
        {
            if ((m & (1 << d)) != 0) deg++;
        }
        return deg;
    }

    /// <summary>
    /// 在 LevelOne 构建完 NavMesh 后调用，利用墙体射线与 NavMesh 采样生成精确的 80x80 迷宫走廊图。
    /// </summary>
    public static void RebuildGraph(int wallMask = -1)
    {
        if (wallMask <= 0)
        {
            wallMask = LayerMask.GetMask("Wall");
            if (wallMask == 0) wallMask = 1 << 7;
        }

        Array.Clear(walkable, 0, CellCount);
        Array.Clear(openMask, 0, CellCount);
        reservations.Clear();

        // 从安全大厅 (0.5, -1.35) => (40, 41) 广度优先扫描全部连通走廊
        Queue<int> q = new Queue<int>(2048);
        int seed = PackCell(40, 41);
        if (!NavMesh.SamplePosition(CellToWorld(seed), out _, 0.45f, NavMesh.AllAreas))
        {
            // 若大厅偏移，扫描中心附近找首个 NavMesh 可达格
            for (int r = 35; r <= 45 && q.Count == 0; r++)
            {
                for (int c = 35; c <= 45; c++)
                {
                    int cand = PackCell(c, r);
                    if (NavMesh.SamplePosition(CellToWorld(cand), out _, 0.35f, NavMesh.AllAreas))
                    {
                        seed = cand;
                        break;
                    }
                }
            }
        }

        walkable[seed] = true;
        q.Enqueue(seed);

        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            UnpackCell(cur, out int c, out int r);
            Vector3 pCur = CellToWorld(cur) + Vector3.up * 0.45f;

            for (int d = 0; d < 4; d++)
            {
                int nc = c + DirDC[d];
                int nr = r + DirDR[d];
                if (nc < 0 || nc >= GridSize || nr < 0 || nr >= GridSize) continue;

                int nxt = PackCell(nc, nr);
                Vector3 pNxt = CellToWorld(nxt) + Vector3.up * 0.45f;

                // 两格中心连线不穿墙且目标格在 NavMesh 上（自动避开封闭电梯轿厢内部）
                if (Physics.Linecast(pCur, pNxt, wallMask, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }
                if (!NavMesh.SamplePosition(CellToWorld(nxt), out NavMeshHit hit, 0.35f, NavMesh.AllAreas))
                {
                    continue;
                }
                // 确保采样点确实落在目标格内而非隔壁格
                Vector3 wc = CellToWorld(nxt);
                if (Mathf.Abs(hit.position.x - wc.x) > 0.42f || Mathf.Abs(hit.position.z - wc.z) > 0.42f)
                {
                    continue;
                }

                openMask[cur] |= (byte)(1 << d);
                if (!walkable[nxt])
                {
                    walkable[nxt] = true;
                    q.Enqueue(nxt);
                }
            }
        }

        isBuilt = true;
    }

    public static void EnsureBuilt(int wallMask = -1)
    {
        if (!isBuilt)
        {
            RebuildGraph(wallMask);
        }
    }

    public static void ClearAllReservations()
    {
        reservations.Clear();
    }

    public static void UnregisterMonkey(object owner)
    {
        if (owner == null) return;
        reservations.Remove(owner);
    }

    public static void UpdateMonkeyRoute(object owner, int currentCell, int headingDir, List<int> routeCells, bool isBossPincer = false)
    {
        if (owner == null) return;
        int cap = isBossPincer ? BossReservedLookaheadCells : ReservedLookaheadCells;
        if (!reservations.TryGetValue(owner, out MonkeyReservation res))
        {
            res = new MonkeyReservation
            {
                routeCells = new List<int>(cap + 4)
            };
        }

        res.currentCell = currentCell;
        res.headingDir = headingDir;
        res.routeCells.Clear();

        if (routeCells != null)
        {
            int limit = Mathf.Min(routeCells.Count, cap);
            for (int i = 0; i < limit; i++)
            {
                res.routeCells.Add(routeCells[i]);
            }
        }

        reservations[owner] = res;
    }

    /// <summary>
    /// 统计当前除 excludeOwner 外，其他猴子正在占用或已规划经过各格的数量，
    /// 用于检测“若本猴再走这条路则该路线将 >= 2 只猴子”。
    /// </summary>
    private static void BuildCrowdMapExcluding(object excludeOwner)
    {
        curCrowdStamp++;
        if (curCrowdStamp >= int.MaxValue - 2)
        {
            Array.Clear(crowdStamp, 0, CellCount);
            curCrowdStamp = 1;
        }

        foreach (KeyValuePair<object, MonkeyReservation> kv in reservations)
        {
            if (ReferenceEquals(kv.Key, excludeOwner)) continue;
            MonkeyReservation r = kv.Value;

            // 1. 标记其他猴子实时所在格及其前后相邻格（避免迎面或紧随扎堆）
            MarkCrowdCell(r.currentCell);
            if (r.currentCell >= 0 && r.currentCell < CellCount)
            {
                UnpackCell(r.currentCell, out int cc, out int cr);
                byte m = openMask[r.currentCell];
                for (int d = 0; d < 4; d++)
                {
                    if ((m & (1 << d)) != 0)
                    {
                        MarkCrowdCell(PackCell(cc + DirDC[d], cr + DirDR[d]));
                    }
                }
            }

            // 2. 标记其他猴子未来已规划路径格（同一只猴子在同一格只计 1 次，避免重复累加）
            if (r.routeCells != null)
            {
                int prev = r.currentCell;
                for (int i = 0; i < r.routeCells.Count; i++)
                {
                    int cell = r.routeCells[i];
                    if (cell == prev) continue;
                    MarkCrowdCell(cell);
                    prev = cell;
                }
            }
        }
    }

    private static void MarkCrowdCell(int cellIdx)
    {
        if (cellIdx < 0 || cellIdx >= CellCount) return;
        if (crowdStamp[cellIdx] != curCrowdStamp)
        {
            crowdStamp[cellIdx] = curCrowdStamp;
            crowdBuf[cellIdx] = 1;
        }
        else
        {
            crowdBuf[cellIdx]++;
        }
    }

    private static int GetCrowdAt(int cellIdx)
    {
        if (cellIdx < 0 || cellIdx >= CellCount) return 0;
        return crowdStamp[cellIdx] == curCrowdStamp ? crowdBuf[cellIdx] : 0;
    }

    /// <summary>
    /// 规划一条禁止原地 180° 掉头且自动避开拥堵路线（>= 2 只猴子自动走另一条路线）的路径。
    /// </summary>
    public static bool PlanRoute(
        object owner,
        Vector3 startWorldPos,
        int committedHeadingDir,
        Vector3 goalWorldPos,
        List<int> outRoute,
        bool bossPincerMode = false)
    {
        outRoute.Clear();
        if (!isBuilt) return false;

        int startCell = WorldToNearestWalkableCell(startWorldPos);
        int goalCell = WorldToNearestWalkableCell(goalWorldPos);
        if (!walkable[startCell] || !walkable[goalCell]) return false;

        if (startCell == goalCell)
        {
            // 已在目标格：继续沿当前单向朝前走一格，绝不原地掉头或呆站
            int nextStep = PickUncongestedForwardStep(owner, startCell, committedHeadingDir);
            outRoute.Add(startCell);
            if (nextStep >= 0 && nextStep != startCell)
            {
                outRoute.Add(nextStep);
            }
            UpdateMonkeyRoute(owner, startCell, committedHeadingDir, outRoute, bossPincerMode);
            return outRoute.Count > 0;
        }

        BuildCrowdMapExcluding(owner);
        UnpackCell(goalCell, out int goalC, out int goalR);

        curStamp++;
        if (curStamp >= int.MaxValue - 2)
        {
            Array.Clear(visitedStamp, 0, StateCount);
            curStamp = 1;
        }
        heapSize = 0;

        byte startMask = openMask[startCell];
        int forbidDir = OppositeDir(committedHeadingDir);

        // 检查起点除了禁止的反方向之外是否还有可行出口（本图无死胡同，恒有出口）
        bool hasNonReverseExit = false;
        if (forbidDir >= 0)
        {
            for (int d = 0; d < 4; d++)
            {
                if (d != forbidDir && (startMask & (1 << d)) != 0)
                {
                    hasNonReverseExit = true;
                    break;
                }
            }
        }

        if (committedHeadingDir >= 0 && committedHeadingDir < 4 && hasNonReverseExit)
        {
            int st = startCell * 4 + committedHeadingDir;
            visitedStamp[st] = curStamp;
            gScore[st] = 0f;
            parentState[st] = -1;
            UnpackCell(startCell, out int sc, out int sr);
            HeapPush(st, Mathf.Abs(sc - goalC) + Mathf.Abs(sr - goalR));
        }
        else
        {
            // 初次生成尚无朝向时，允许从任意朝向出发
            UnpackCell(startCell, out int sc, out int sr);
            float h0 = Mathf.Abs(sc - goalC) + Mathf.Abs(sr - goalR);
            for (int d = 0; d < 4; d++)
            {
                if ((startMask & (1 << d)) == 0) continue;
                int st = startCell * 4 + d;
                visitedStamp[st] = curStamp;
                gScore[st] = 0f;
                parentState[st] = -1;
                HeapPush(st, h0);
            }
        }

        int goalState = -1;
        int bestFallbackState = -1;
        float bestFallbackDist = float.MaxValue;

        while (heapSize > 0)
        {
            int curState = HeapPop();
            int curCell = curState >> 2;
            int curDir = curState & 3;
            float curG = gScore[curState];

            if (curCell == goalCell)
            {
                goalState = curState;
                break;
            }

            UnpackCell(curCell, out int cc, out int cr);
            float manhattan = Mathf.Abs(cc - goalC) + Mathf.Abs(cr - goalR);
            if (manhattan < bestFallbackDist)
            {
                bestFallbackDist = manhattan;
                bestFallbackState = curState;
            }

            byte m = openMask[curCell];
            int opp = curDir ^ 1;

            // 计算该格出口数（用于无路可走时的极端兜底，本迷宫所有格度 >= 2）
            int deg = 0;
            for (int d = 0; d < 4; d++)
            {
                if ((m & (1 << d)) != 0) deg++;
            }

            for (int nd = 0; nd < 4; nd++)
            {
                if ((m & (1 << nd)) == 0) continue;
                // 核心规则 1：严格禁止 180° 掉头！
                if (nd == opp && deg > 1) continue;

                int nc = cc + DirDC[nd];
                int nr = cr + DirDR[nd];
                int nxtCell = PackCell(nc, nr);
                if (!walkable[nxtCell]) continue;

                int nxtState = (nxtCell << 2) | nd;

                // 基础步长 + 轻微直行偏好
                float stepCost = 1.0f + (nd != curDir ? 0.06f : 0f);

                // 核心规则 2：拥堵分流代价——若已有其他猴子走这条路（合入本猴将 >= 2 只），大幅提高代价迫使改走另一条路线！
                // 首领包抄模式（bossPincerMode）施加极强防重合惩罚，确保两只首领分别沿完全不同的走廊方向夹击包围玩家。
                int others = GetCrowdAt(nxtCell);
                if (others >= 1)
                {
                    int distAfter = Mathf.Abs(nc - goalC) + Mathf.Abs(nr - goalR);
                    if (bossPincerMode)
                    {
                        stepCost += distAfter > 1 ? (52.0f * others) : (6.0f * others);
                    }
                    else if (distAfter > 2)
                    {
                        stepCost += (others == 1) ? 16.0f : 42.0f;
                    }
                    else
                    {
                        // 距离终点（玩家）仅 2 格内略微放宽，允许从不同走廊包抄来的猴子同时合围抓捕
                        stepCost += 3.5f * others;
                    }
                }

                float candG = curG + stepCost;
                if (visitedStamp[nxtState] != curStamp || candG < gScore[nxtState])
                {
                    visitedStamp[nxtState] = curStamp;
                    gScore[nxtState] = candG;
                    parentState[nxtState] = curState;
                    float h = Mathf.Abs(nc - goalC) + Mathf.Abs(nr - goalR);
                    HeapPush(nxtState, candG + h);
                }
            }
        }

        int finalState = goalState >= 0 ? goalState : bestFallbackState;
        if (finalState < 0) return false;

        // 回溯还原路径
        int trace = finalState;
        int guard = 0;
        while (trace >= 0 && guard++ < CellCount)
        {
            outRoute.Add(trace >> 2);
            trace = parentState[trace];
        }
        outRoute.Reverse();

        UpdateMonkeyRoute(owner, startCell, committedHeadingDir, outRoute, bossPincerMode);
        return outRoute.Count > 0;
    }

    /// <summary>
    /// 在无明确追击目标或刚到巡逻点时，挑一个前方不掉头且当前无其他猴子的下一格。
    /// </summary>
    public static int PickUncongestedForwardStep(object owner, int currentCell, int headingDir)
    {
        if (!isBuilt || currentCell < 0 || currentCell >= CellCount) return -1;
        BuildCrowdMapExcluding(owner);

        byte m = openMask[currentCell];
        int opp = OppositeDir(headingDir);
        UnpackCell(currentCell, out int c, out int r);

        int bestCell = -1;
        float bestScore = float.MaxValue;
        for (int d = 0; d < 4; d++)
        {
            if ((m & (1 << d)) == 0) continue;
            if (d == opp) continue;
            int nxt = PackCell(c + DirDC[d], r + DirDR[d]);
            float score = GetCrowdAt(nxt) * 10f + (d == headingDir ? 0f : 0.4f) + UnityEngine.Random.Range(0f, 0.35f);
            if (score < bestScore)
            {
                bestScore = score;
                bestCell = nxt;
            }
        }
        return bestCell;
    }

    /// <summary>
    /// 评估某巡逻目标点周围是否存在其他猴子扎堆，返回拥堵人数（0 = 完全空闲）。
    /// </summary>
    public static int EstimateGoalCrowd(object owner, Vector3 goalWorldPos)
    {
        if (!isBuilt) return 0;
        BuildCrowdMapExcluding(owner);
        int cell = WorldToNearestWalkableCell(goalWorldPos);
        return GetCrowdAt(cell);
    }

    /// <summary>
    /// 当玩家躲进暗门房间内时，在房间门口外侧走廊附近挑选顺向、不拥堵的绕圈巡逻点，
    /// 使猴子在门口附近走廊持续绕圈游荡蹲守而不闯入门内。
    /// </summary>
    public static bool TryPickNearbyOrbitPoint(
        object owner,
        Vector3 monkeyWorldPos,
        int committedHeadingDir,
        Vector3 anchorWorldPos,
        float minDistFromAnchor,
        float maxDistFromAnchor,
        out Vector3 orbitWorldPos)
    {
        orbitWorldPos = anchorWorldPos;
        if (!isBuilt) return false;

        BuildCrowdMapExcluding(owner);
        int anchorCell = WorldToNearestWalkableCell(anchorWorldPos);
        int monkeyCell = WorldToNearestWalkableCell(monkeyWorldPos);
        UnpackCell(anchorCell, out int ac, out int ar);

        int radius = Mathf.Clamp(Mathf.CeilToInt(maxDistFromAnchor) + 1, 3, 12);
        Vector3 headingVec = committedHeadingDir >= 0 ? DirToWorld(committedHeadingDir) : Vector3.zero;
        float minSqrAnchor = minDistFromAnchor * minDistFromAnchor;
        float maxSqrAnchor = maxDistFromAnchor * maxDistFromAnchor;

        int bestCell = -1;
        float bestScore = float.MaxValue;

        for (int dc = -radius; dc <= radius; dc++)
        {
            int nc = ac + dc;
            if (nc < 0 || nc >= GridSize) continue;
            for (int dr = -radius; dr <= radius; dr++)
            {
                int nr = ar + dr;
                if (nr < 0 || nr >= GridSize) continue;

                int cand = PackCell(nc, nr);
                if (!walkable[cand] || cand == monkeyCell) continue;

                Vector3 wp = CellToWorld(cand);
                float dxA = wp.x - anchorWorldPos.x;
                float dzA = wp.z - anchorWorldPos.z;
                float sqrA = dxA * dxA + dzA * dzA;
                if (sqrA < minSqrAnchor || sqrA > maxSqrAnchor) continue;

                float dxM = wp.x - monkeyWorldPos.x;
                float dzM = wp.z - monkeyWorldPos.z;
                float distM = Mathf.Sqrt(dxM * dxM + dzM * dzM);
                if (distM < 2.2f) continue;

                // 优先挑选无其他猴子占用、顺着当前单向朝向、且靠近门口外圈的走廊格
                float crowdCost = GetCrowdAt(cand) * 14f;
                float forwardPenalty = 0f;
                if (headingVec.sqrMagnitude > 0.5f && distM > 0.1f)
                {
                    float dot = (dxM * headingVec.x + dzM * headingVec.z) / distM;
                    forwardPenalty = (1f - dot) * 3.2f;
                }
                int deg = GetCellDegree(cand);
                float junctionBonus = deg >= 3 ? -1.1f : 0f;
                float ringPref = Mathf.Abs(Mathf.Sqrt(sqrA) - (minDistFromAnchor + maxDistFromAnchor) * 0.45f) * 0.45f;
                float score = crowdCost + forwardPenalty + ringPref + junctionBonus + UnityEngine.Random.Range(0f, 2.2f);

                if (score < bestScore)
                {
                    bestScore = score;
                    bestCell = cand;
                }
            }
        }

        if (bestCell >= 0)
        {
            orbitWorldPos = CellToWorld(bestCell);
            orbitWorldPos.y = monkeyWorldPos.y;
            return true;
        }
        return false;
    }

    private static void HeapPush(int state, float f)
    {
        int i = ++heapSize;
        while (i > 1)
        {
            int p = i >> 1;
            if (heapF[p] <= f) break;
            heapStates[i] = heapStates[p];
            heapF[i] = heapF[p];
            i = p;
        }
        heapStates[i] = state;
        heapF[i] = f;
    }

    private static int HeapPop()
    {
        int top = heapStates[1];
        int lastS = heapStates[heapSize];
        float lastF = heapF[heapSize--];
        int i = 1;
        while (true)
        {
            int l = i << 1;
            if (l > heapSize) break;
            int r = l + 1;
            int smallest = (r <= heapSize && heapF[r] < heapF[l]) ? r : l;
            if (lastF <= heapF[smallest]) break;
            heapStates[i] = heapStates[smallest];
            heapF[i] = heapF[smallest];
            i = smallest;
        }
        if (heapSize > 0)
        {
            heapStates[i] = lastS;
            heapF[i] = lastF;
        }
        return top;
    }
}
