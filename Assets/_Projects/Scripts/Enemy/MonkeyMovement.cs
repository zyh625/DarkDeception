using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class MonkeyMovement : MonoBehaviour
{
    [Header("移动速度（按当前玩家 1.0 / 1.7 的比例设定）")]
    [SerializeField] private float patrolSpeed = 1.0f;
    [SerializeField] private float trackSpeed = 1.4f;
    [SerializeField] private float runSpeed = 1.9f;

    private NavMeshAgent agent;

    public float Speed => agent != null ? agent.velocity.magnitude : 0f;
    public float NormalizedSpeed => runSpeed > 0.001f ? Mathf.Clamp01(Speed / runSpeed) : 0f;

    private bool IsAgentReady => agent != null && agent.enabled && agent.isOnNavMesh;

    private void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        agent.speed = patrolSpeed;
    }

    public void Track(Vector3 targetPos, bool seePlayer)
    {
        if (!IsAgentReady) return;
        agent.isStopped = false;
        agent.speed = seePlayer ? runSpeed : trackSpeed;
        agent.SetDestination(targetPos);
    }

    public void PatrolTo(Vector3 targetPos)
    {
        if (!IsAgentReady) return;
        agent.isStopped = false;
        agent.speed = patrolSpeed;
        agent.SetDestination(targetPos);
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
        if (agent.pathPending) return false;
        return !agent.hasPath || agent.remainingDistance <= agent.stoppingDistance + 0.05f;
    }

    public void Stop()
    {
        if (agent == null) return;
        if (agent.isOnNavMesh)
        {
            agent.isStopped = true;
            agent.ResetPath();
        }
        agent.velocity = Vector3.zero;
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
