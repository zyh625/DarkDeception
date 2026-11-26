using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LevelOne : MonoBehaviour
{
    [Header("玩家与水晶")]
    [SerializeField] private PlayerInteract playerInteract;
    [SerializeField] private Transform shard;
    [SerializeField] private Transform[] shardBorn;
    [SerializeField] private Transform playerBorn;

    [Header("开局电梯")]
    [SerializeField] private ElevatorDoor startElevator;

    [Header("敌人设置（场景中现成摆好的猴子或动态生成都支持）")]
    [SerializeField] private MonkeyAI[] sceneMonkeys;
    [SerializeField] private MonkeyAI monkeyPrefab;
    [SerializeField] private Transform[] monkeyBorn;
    [SerializeField] private Transform[] patrolPoints;
    [SerializeField] private float shardAlertDelay = 1.0f;

    [Header("生命与界面")]
    [SerializeField] private int maxLives = 3;
    [SerializeField] private GameObject pauseMenu;
    [SerializeField] private GameObject failMenu;

    private readonly List<Transform> shards = new();
    private readonly List<MonkeyAI> monkeys = new();

    private PlayerInteract player;
    private int curShard;
    private int maxShard;
    private int curLives;
    private bool isHandlingCatch;
    private bool isGameOver;

    private void Awake()
    {
        Time.timeScale = 1f;
        curLives = Mathf.Max(1, maxLives);

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        if (pauseMenu != null) pauseMenu.SetActive(false);
        if (failMenu != null) failMenu.SetActive(false);

        SpawnShards();
        SpawnPlayer();
        SetupMonkeys();
    }

    private void OnEnable()
    {
        if (player != null)
        {
            player.pickUp += PickUpShard;
            player.pauseAction += PauseControl;
        }

        if (startElevator != null)
        {
            startElevator.OnPlayerExitedAndLocked += HandlePlayerLeftElevator;
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
            player.pickUp -= PickUpShard;
            player.pauseAction -= PauseControl;
        }

        if (startElevator != null)
        {
            startElevator.OnPlayerExitedAndLocked -= HandlePlayerLeftElevator;
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

    private void SpawnShards()
    {
        if (shard == null || shardBorn == null) return;

        foreach (Transform t in shardBorn)
        {
            if (t == null) continue;
            Transform spawned = Instantiate(shard, t.position, t.rotation);
            shards.Add(spawned);
            maxShard++;
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
            }
        }

        foreach (MonkeyAI monkey in monkeys)
        {
            monkey.Init(player, patrolPoints);
        }
    }

    private void PickUpShard(Vector3 shardPos)
    {
        curShard++;
        StartCoroutine(NotifyMonkeysShardPicked(shardPos));

        if (maxShard > 0 && curShard >= maxShard)
        {
            OpenExit();
        }
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

        // 保证时间流动，避免处于暂停态时卡住处决动画
        Time.timeScale = 1f;
        if (pauseMenu != null) pauseMenu.SetActive(false);

        // 先根据当前剩余命数选择处决动作（1~3），再扣命，避免传入 0 导致无法切换动画
        int catchAnimIndex = Mathf.Clamp(curLives, 1, 3);
        curLives = Mathf.Max(0, curLives - 1);

        if (player != null)
        {
            player.CatchBy(killer.transform);
        }

        // 冻结场上其他所有猴子，避免挡镜头或二次触发
        foreach (MonkeyAI monkey in monkeys)
        {
            if (monkey != null && monkey != killer)
            {
                monkey.Freeze();
            }
        }

        Vector3 targetPos = player != null ? player.transform.position : killer.transform.position + killer.transform.forward;
        killer.PlayExecute(catchAnimIndex, targetPos, ShowFailMenu);
    }

    private void ShowFailMenu()
    {
        isGameOver = true;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        if (failMenu != null)
        {
            failMenu.SetActive(true);
        }
    }

    private void PauseControl(bool pause)
    {
        if (isHandlingCatch || isGameOver) return;

        Time.timeScale = pause ? 0f : 1f;
        Cursor.lockState = pause ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = pause;

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
        // 玩家走出电梯且门锁死后，可唤醒处于睡眠状态的猴子
        foreach (MonkeyAI monkey in monkeys)
        {
            if (monkey != null)
            {
                monkey.WakeUp();
            }
        }
    }

    private void OpenExit() // 开启出口，进入boss追逐战
    {
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
