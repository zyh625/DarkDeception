using System;
using System.Collections;
using UnityEngine;

[RequireComponent(typeof(Animator))]
public class MonkeyAnimation : MonoBehaviour
{
    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int CatchHash = Animator.StringToHash("Catch");

    [Header("动画过渡与处决回调")]
    [SerializeField] private float speedDampTime = 0.1f;
    [SerializeField, Range(0.5f, 1f)] private float finishNormalizedTime = 0.9f;

    private MonkeyMovement movement;
    private Animator animator;
    private Action onExecuteFinished;
    private Coroutine executeWaitRoutine;
    private bool isExecuting;

    private void Awake()
    {
        movement = GetComponentInParent<MonkeyMovement>();
        animator = GetComponent<Animator>();
    }

    private void Update()
    {
        if (isExecuting || movement == null || animator == null) return;
        animator.SetFloat(SpeedHash, movement.NormalizedSpeed, speedDampTime, Time.deltaTime);
    }

    /// <summary>
    /// 播放处决动画（1~3），动画快结束时触发 onFinished。
    /// 支持两种触发：在 FBX 处决动画上添加 Animation Event 调用 OnExecuteAnimEnd()，
    /// 或未添加事件时自动在 normalizedTime 达到 finishNormalizedTime 时兜底触发。
    /// </summary>
    public void Catch(int catchType, Action onFinished = null)
    {
        isExecuting = true;
        onExecuteFinished = onFinished;

        if (animator == null)
        {
            NotifyExecuteFinished();
            return;
        }

        int index = Mathf.Clamp(catchType, 1, 3);
        animator.SetFloat(SpeedHash, 0f);
        animator.SetInteger(CatchHash, index);

        if (executeWaitRoutine != null)
        {
            StopCoroutine(executeWaitRoutine);
        }
        executeWaitRoutine = StartCoroutine(WaitExecuteFallback());
    }

    /// <summary>
    /// 供处决 AnimationClip 上的 Animation Event 调用。
    /// </summary>
    public void OnExecuteAnimEnd()
    {
        NotifyExecuteFinished();
    }

    public void ResetAnimation()
    {
        if (executeWaitRoutine != null)
        {
            StopCoroutine(executeWaitRoutine);
            executeWaitRoutine = null;
        }
        onExecuteFinished = null;
        isExecuting = false;

        if (animator != null)
        {
            animator.SetInteger(CatchHash, 0);
            animator.SetFloat(SpeedHash, 0f);
        }
    }

    private IEnumerator WaitExecuteFallback()
    {
        // 等待状态机切入 Execute 状态（最多等 0.5 秒避免状态名不一致卡死）
        float enterTimer = 0f;
        while (enterTimer < 0.5f)
        {
            AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
            if (!animator.IsInTransition(0) && !info.IsName("Locomotion"))
            {
                break;
            }
            enterTimer += Time.deltaTime;
            yield return null;
        }

        // 等待处决动画播放到指定进度（默认 90%），同时设置安全上限防止死等
        float playTimer = 0f;
        while (playTimer < 5f)
        {
            AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
            if (!animator.IsInTransition(0) && info.normalizedTime >= finishNormalizedTime)
            {
                break;
            }
            playTimer += Time.deltaTime;
            yield return null;
        }

        executeWaitRoutine = null;
        NotifyExecuteFinished();
    }

    private void NotifyExecuteFinished()
    {
        if (executeWaitRoutine != null)
        {
            StopCoroutine(executeWaitRoutine);
            executeWaitRoutine = null;
        }

        Action callback = onExecuteFinished;
        onExecuteFinished = null;
        callback?.Invoke();
    }
}
