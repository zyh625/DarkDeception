using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// 第一关 8 扇暗门随机分配与交互总控器：
/// 1. 每局将 8 个分散暗门后的短走廊随机分配给：
///    - 1 个红水晶（RedCrystal）
///    - 1 个黄水晶（YellowCrystal）
///    - 2 只睡觉的猴子（开门惊醒 -> 播放 WakeUp -> 跨出门加入追逐阵营）
///    - 4 个传送门（每局再随机两两配对 A<->B、C<->D）
/// 2. 玩家视角对准未开启的可交互暗门时，在屏幕正中心显示 UI_ServiceBell_InteractHand.png 交互图标；
/// 3. 按下交互键（E / F / 鼠标左键 / 手柄北键）后向内永久开门，并维护玩家进出暗室的实时状态。
/// </summary>
public class SecretRoomManager : MonoBehaviour
{
    public enum RoomContentType
    {
        RedCrystal = 0,
        YellowCrystal = 1,
        SleepingMonkey = 2,
        Portal = 3
    }

    private static SecretRoomManager activeInstance;

    [Header("8 扇可交互暗门（其余 13 扇为纯装饰门）")]
    [SerializeField] private SecretRoomDoor[] secretDoors;

    [Header("门后随机内容预制体")]
    [SerializeField] private GameObject redCrystalPrefab;
    [SerializeField] private GameObject yellowCrystalPrefab;
    [SerializeField] private SecretPortal portalPrefab;
    [SerializeField] private float crystalSpawnHeight = 0.46f;

    [Header("屏幕中心交互手势提示（UI_ServiceBell_InteractHand）")]
    [SerializeField] private Sprite interactHandSprite;
    [SerializeField] private float interactDistance = 1.42f;
    [SerializeField] private float bellInteractDistance = 1.62f;
    [SerializeField] private float statueInteractDistance = 2.05f;
    [SerializeField] private float woodSealInteractDistance = 1.65f;
    [SerializeField] private float handIconSizePx = 76f;

    private LevelOne levelOne;
    private PlayerInteract player;
    private readonly List<GameObject> spawnedRoomContents = new();
    private readonly List<MonkeyAI> sleepingRoomMonkeys = new();
    private readonly List<SecretPortal> spawnedPortals = new();

    private GameObject handPromptRoot;
    private RectTransform handIconRect;
    private Image portalFlashImage;
    private float portalFlashAlpha;
    private SecretRoomDoor currentLookDoor;
    private WallServiceBellInteractable currentLookBell;
    private StatueInteractTrigger currentLookStatue;
    private WoodSealInteractable currentLookSeal;
    private int interactRayMask;

    public static void TriggerPortalFlash()
    {
        if (activeInstance == null) return;
        activeInstance.portalFlashAlpha = 0.62f;
        if (activeInstance.portalFlashImage != null)
        {
            Color c = activeInstance.portalFlashImage.color;
            c.a = activeInstance.portalFlashAlpha;
            activeInstance.portalFlashImage.color = c;
            activeInstance.portalFlashImage.gameObject.SetActive(true);
        }
    }

    private void Awake()
    {
        activeInstance = this;
        levelOne = GetComponent<LevelOne>();
        if (levelOne == null) levelOne = FindAnyObjectByType<LevelOne>();

        // 射线检测：墙体与房门在 Layer 7 (Wall)，也兼容 Default (0)，忽略 Trigger 与 Player (3)
        int playerLayer = LayerMask.NameToLayer("Player");
        interactRayMask = playerLayer >= 0 ? ~(1 << playerLayer) : ~0;

        SecretRoomDoor.ResetOccupancyState();
        SecretPortal.ResetSharedCooldown();
        CreateCenterPromptAndFlashUI();
    }

    private void OnDestroy()
    {
        if (activeInstance == this)
        {
            activeInstance = null;
        }
        SecretRoomDoor.ResetOccupancyState();
    }

    /// <summary>
    /// 由 LevelOne.Awake() 在 BuildNavMesh() 之前调用：
    /// 为 8 扇暗门背后建立 NavMesh NotWalkable 排除体与室内微光。
    /// </summary>
    public void PrepareRoomNavMeshExclusions()
    {
        EnsureDoorsDiscovered();
        if (secretDoors == null) return;

        for (int i = 0; i < secretDoors.Length; i++)
        {
            if (secretDoors[i] != null)
            {
                secretDoors[i].EnsureNavMeshExclusionAndLighting();
            }
        }
    }

    /// <summary>
    /// 由 LevelOne.Awake() 在玩家与初始猴子生成完毕后调用：
    /// 对 8 扇暗门洗牌并随机生成：1 红水晶、1 黄水晶、2 睡觉猴子、4 随机两两配对传送门。
    /// </summary>
    public void InitializeAndRandomizeRooms(
        LevelOne ownerLevel,
        PlayerInteract targetPlayer,
        MonkeyAI monkeyPrefab,
        Transform[] sharedPatrolPoints)
    {
        levelOne = ownerLevel;
        player = targetPlayer;
        EnsureDoorsDiscovered();

        if (secretDoors == null || secretDoors.Length == 0)
        {
            return;
        }

        // 收集有效暗门并执行 Fisher-Yates 随机洗牌
        List<SecretRoomDoor> shuffledDoors = new(secretDoors.Length);
        for (int i = 0; i < secretDoors.Length; i++)
        {
            if (secretDoors[i] != null)
            {
                shuffledDoors.Add(secretDoors[i]);
            }
        }
        ShuffleList(shuffledDoors);

        // 固定 8 项内容池：1 红水晶、1 黄水晶、2 睡觉猴子、4 传送门
        RoomContentType[] pool =
        {
            RoomContentType.RedCrystal,
            RoomContentType.YellowCrystal,
            RoomContentType.SleepingMonkey,
            RoomContentType.SleepingMonkey,
            RoomContentType.Portal,
            RoomContentType.Portal,
            RoomContentType.Portal,
            RoomContentType.Portal
        };

        Transform contentContainer = new GameObject("SecretRoomContents").transform;
        spawnedPortals.Clear();
        sleepingRoomMonkeys.Clear();
        List<SecretRoomDoor> portalRooms = new(4);

        int count = Mathf.Min(shuffledDoors.Count, pool.Length);
        for (int i = 0; i < count; i++)
        {
            SecretRoomDoor door = shuffledDoors[i];
            RoomContentType type = pool[i];

            switch (type)
            {
                case RoomContentType.RedCrystal:
                    SpawnCrystal(redCrystalPrefab, door, contentContainer, "SecretRedCrystal");
                    break;

                case RoomContentType.YellowCrystal:
                    SpawnCrystal(yellowCrystalPrefab, door, contentContainer, "SecretYellowCrystal");
                    break;

                case RoomContentType.SleepingMonkey:
                    SpawnSleepingMonkey(monkeyPrefab, door, targetPlayer, sharedPatrolPoints, contentContainer);
                    break;

                case RoomContentType.Portal:
                    SecretPortal portal = SpawnPortal(door, contentContainer);
                    if (portal != null)
                    {
                        spawnedPortals.Add(portal);
                        portalRooms.Add(door);
                    }
                    break;
            }
        }

        // 将 4 个传送门再随机洗牌并两两双向配对（0<->1，2<->3）
        if (spawnedPortals.Count >= 2)
        {
            List<int> order = new(spawnedPortals.Count);
            for (int i = 0; i < spawnedPortals.Count; i++) order.Add(i);
            ShuffleList(order);

            for (int i = 0; i + 1 < order.Count; i += 2)
            {
                int a = order[i];
                int b = order[i + 1];
                spawnedPortals[a].Bind(portalRooms[a], spawnedPortals[b]);
                spawnedPortals[b].Bind(portalRooms[b], spawnedPortals[a]);
            }
        }
    }

    private void SpawnCrystal(GameObject prefab, SecretRoomDoor door, Transform parent, string objName)
    {
        if (prefab == null || door == null) return;

        Vector3 pos = door.ContentCenterAnchor;
        pos.y = crystalSpawnHeight;
        GameObject inst = Instantiate(prefab, pos, door.FacingOutwardRotation, parent);
        inst.name = objName;
        spawnedRoomContents.Add(inst);
    }

    private void SpawnSleepingMonkey(
        MonkeyAI monkeyPrefab,
        SecretRoomDoor door,
        PlayerInteract targetPlayer,
        Transform[] sharedPatrolPoints,
        Transform parent)
    {
        if (monkeyPrefab == null || door == null) return;

        Vector3 sleepPos = door.ContentCenterAnchor;
        Quaternion faceDoorRot = door.FacingOutwardRotation;
        Vector3 corridorExitPos = door.CorridorOutsideAnchor;

        MonkeyAI sleeper = Instantiate(monkeyPrefab, sleepPos, faceDoorRot, parent);
        sleeper.name = $"SleepingMonkey_{door.name}";
        sleeper.SetupAsSecretRoomSleeper(
            sleepPos,
            faceDoorRot,
            corridorExitPos,
            targetPlayer,
            sharedPatrolPoints,
            awakened =>
            {
                if (levelOne != null)
                {
                    levelOne.RegisterAwakenedMonkey(awakened, corridorExitPos, faceDoorRot);
                }
            });

        sleepingRoomMonkeys.Add(sleeper);
        spawnedRoomContents.Add(sleeper.gameObject);

        // 当玩家打开这扇门时，立刻惊醒该睡觉猴子并让其起身跨出门追逐玩家
        door.OnDoorOpened += _ =>
        {
            if (sleeper != null)
            {
                sleeper.WakeUpFromSecretRoom();
            }
        };
    }

    private SecretPortal SpawnPortal(SecretRoomDoor door, Transform parent)
    {
        if (portalPrefab == null || door == null) return null;

        Vector3 pos = door.PortalAnchor;
        pos.y = 0f;
        SecretPortal inst = Instantiate(portalPrefab, pos, door.FacingOutwardRotation, parent);
        inst.name = $"SecretPortal_{door.name}";
        spawnedRoomContents.Add(inst.gameObject);
        return inst;
    }

    /// <summary>
    /// 收集完所有水晶进入首领阶段时调用：立即清除所有尚未苏醒或正在苏醒的暗门普通猴子。
    /// </summary>
    public void DespawnAllSleepingMonkeys()
    {
        for (int i = 0; i < sleepingRoomMonkeys.Count; i++)
        {
            MonkeyAI mk = sleepingRoomMonkeys[i];
            if (mk != null)
            {
                Destroy(mk.gameObject);
            }
        }
        sleepingRoomMonkeys.Clear();
    }

    public void SetSleepingMonkeysTimeFrozen(bool frozen)
    {
        for (int i = 0; i < sleepingRoomMonkeys.Count; i++)
        {
            MonkeyAI mk = sleepingRoomMonkeys[i];
            if (mk != null)
            {
                mk.SetTimeFrozen(frozen);
            }
        }
    }

    private void Update()
    {
        if (player == null)
        {
            player = PlayerInteract.ActivePlayer;
        }

        // 1. 实时同步玩家是否身处任意暗门后的房间内（驱动门外猴子绕圈巡逻）
        SecretRoomDoor.RefreshPlayerOccupancy(player != null ? player.transform : null);

        // 2. 更新传送瞬间青色微光淡出
        if (portalFlashImage != null && portalFlashAlpha > 0.001f)
        {
            if (!portalFlashImage.gameObject.activeSelf)
            {
                portalFlashImage.gameObject.SetActive(true);
            }
            portalFlashAlpha = Mathf.MoveTowards(portalFlashAlpha, 0f, Time.unscaledDeltaTime * 2.4f);
            Color c = portalFlashImage.color;
            c.a = portalFlashAlpha;
            portalFlashImage.color = c;
            if (portalFlashAlpha <= 0.001f)
            {
                portalFlashImage.gameObject.SetActive(false);
            }
        }

        // 3. 检测视角中心是否正对可交互目标（木头封条 / 雕像 / 未开启的暗门 / 墙壁服务铃）
        bool canInteractNow = player != null && player.IsAlive && !player.IsPaused && !player.IsCutsceneFrozen
            && (levelOne == null || levelOne.CanUseHUD);

        currentLookDoor = null;
        currentLookBell = null;
        currentLookStatue = null;
        currentLookSeal = null;
        if (canInteractNow)
        {
            RaycastLookAtInteractiveTarget(out currentLookDoor, out currentLookBell, out currentLookStatue, out currentLookSeal);
        }

        bool hasTarget = currentLookSeal != null || currentLookStatue != null || currentLookDoor != null || currentLookBell != null;
        UpdateHandPromptUI(hasTarget);

        if (hasTarget && IsInteractTriggeredThisFrame())
        {
            if (currentLookSeal != null)
            {
                WoodSealInteractable sealToBreak = currentLookSeal;
                currentLookSeal = null;
                UpdateHandPromptUI(false);
                sealToBreak.BreakByPlayer(player != null ? player.transform.position : Vector3.zero);
            }
            else if (currentLookStatue != null)
            {
                StatueInteractTrigger statueToActivate = currentLookStatue;
                currentLookStatue = null;
                UpdateHandPromptUI(false);
                statueToActivate.Interact();
            }
            else if (currentLookBell != null)
            {
                WallServiceBellInteractable bellToRing = currentLookBell;
                currentLookBell = null;
                UpdateHandPromptUI(false);
                bellToRing.Ring();
            }
            else if (currentLookDoor != null)
            {
                SecretRoomDoor doorToOpen = currentLookDoor;
                currentLookDoor = null;
                UpdateHandPromptUI(false);
                doorToOpen.OpenDoor();
            }
        }
    }

    private void RaycastLookAtInteractiveTarget(
        out SecretRoomDoor foundDoor,
        out WallServiceBellInteractable foundBell,
        out StatueInteractTrigger foundStatue,
        out WoodSealInteractable foundSeal)
    {
        foundDoor = null;
        foundBell = null;
        foundStatue = null;
        foundSeal = null;

        Camera cam = Camera.main;
        if (cam == null) return;

        float maxDist = Mathf.Max(Mathf.Max(interactDistance, bellInteractDistance), Mathf.Max(statueInteractDistance, woodSealInteractDistance));
        Ray ray = new Ray(cam.transform.position, cam.transform.forward);

        // 1. 精准中心射线检测（优先命中正对物体，确保隔墙不会误触，木头封条正反两侧均命中）
        if (Physics.Raycast(ray, out RaycastHit hit, maxDist, interactRayMask, QueryTriggerInteraction.Ignore))
        {
            WoodSealInteractable seal = hit.collider.GetComponentInParent<WoodSealInteractable>();
            if (seal != null && seal.CanInteract && hit.distance <= Mathf.Max(woodSealInteractDistance, seal.InteractDistance))
            {
                foundSeal = seal;
                return;
            }

            StatueInteractTrigger statue = hit.collider.GetComponentInParent<StatueInteractTrigger>();
            if (statue != null && statue.CanInteract && hit.distance <= Mathf.Max(statueInteractDistance, statue.InteractDistance))
            {
                foundStatue = statue;
                return;
            }

            WallServiceBellInteractable bell = hit.collider.GetComponentInParent<WallServiceBellInteractable>();
            if (bell != null && bell.CanInteract && hit.distance <= bellInteractDistance)
            {
                foundBell = bell;
                return;
            }

            SecretRoomDoor door = hit.collider.GetComponentInParent<SecretRoomDoor>();
            if (door != null && !door.IsOpened && hit.distance <= interactDistance)
            {
                foundDoor = door;
                return;
            }

            // 若中心射线打在墙面上，检查该墙面撞击点极近处（<= 0.14m）是否正贴着墙壁铃铛，提升对准小铃铛的手感
            if (hit.distance <= bellInteractDistance)
            {
                Collider[] nearCols = Physics.OverlapSphere(hit.point, 0.11f, interactRayMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < nearCols.Length; i++)
                {
                    WallServiceBellInteractable nearBell = nearCols[i].GetComponentInParent<WallServiceBellInteractable>();
                    if (nearBell != null && nearBell.CanInteract)
                    {
                        foundBell = nearBell;
                        return;
                    }
                }
            }
            return;
        }

        // 2. 轻微宽容球形射线（仅在正前方未撞墙时生效，提升对准封条/雕像/门边缘/铃铛边缘的手感）
        if (Physics.SphereCast(ray, 0.08f, out RaycastHit sphereHit, maxDist, interactRayMask, QueryTriggerInteraction.Ignore))
        {
            WoodSealInteractable seal = sphereHit.collider.GetComponentInParent<WoodSealInteractable>();
            if (seal != null && seal.CanInteract && sphereHit.distance <= Mathf.Max(woodSealInteractDistance, seal.InteractDistance))
            {
                foundSeal = seal;
                return;
            }

            StatueInteractTrigger statue = sphereHit.collider.GetComponentInParent<StatueInteractTrigger>();
            if (statue != null && statue.CanInteract && sphereHit.distance <= Mathf.Max(statueInteractDistance, statue.InteractDistance))
            {
                foundStatue = statue;
                return;
            }

            WallServiceBellInteractable bell = sphereHit.collider.GetComponentInParent<WallServiceBellInteractable>();
            if (bell != null && bell.CanInteract && sphereHit.distance <= bellInteractDistance)
            {
                foundBell = bell;
                return;
            }

            SecretRoomDoor door = sphereHit.collider.GetComponentInParent<SecretRoomDoor>();
            if (door != null && !door.IsOpened && sphereHit.distance <= interactDistance)
            {
                foundDoor = door;
            }
        }
    }

    private static bool IsInteractTriggeredThisFrame()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null && (kb.eKey.wasPressedThisFrame || kb.fKey.wasPressedThisFrame))
        {
            return true;
        }

        Mouse mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            return true;
        }

        Gamepad gp = Gamepad.current;
        if (gp != null && gp.buttonNorth.wasPressedThisFrame)
        {
            return true;
        }

        return false;
    }

    private void UpdateHandPromptUI(bool show)
    {
        if (handPromptRoot == null) return;

        if (handPromptRoot.activeSelf != show)
        {
            handPromptRoot.SetActive(show);
        }

        if (show && handIconRect != null)
        {
            float pulse = 1f + 0.055f * Mathf.Sin(Time.unscaledTime * 4.8f);
            handIconRect.localScale = new Vector3(pulse, pulse, 1f);
        }
    }

    private void CreateCenterPromptAndFlashUI()
    {
        GameObject canvasGo = new GameObject(
            "SecretRoomInteractCanvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler));
        canvasGo.transform.SetParent(transform, false);

        Canvas c = canvasGo.GetComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay;
        c.sortingOrder = 220;

        CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // 1. 屏幕正中心手型交互提示图标（UI_ServiceBell_InteractHand.png）
        handPromptRoot = new GameObject(
            "InteractHandPrompt",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Shadow));
        handPromptRoot.transform.SetParent(canvasGo.transform, false);

        handIconRect = handPromptRoot.GetComponent<RectTransform>();
        handIconRect.anchorMin = new Vector2(0.5f, 0.5f);
        handIconRect.anchorMax = new Vector2(0.5f, 0.5f);
        handIconRect.pivot = new Vector2(0.5f, 0.5f);
        handIconRect.anchoredPosition = Vector2.zero;
        handIconRect.sizeDelta = new Vector2(handIconSizePx, handIconSizePx);

        Image handImg = handPromptRoot.GetComponent<Image>();
        handImg.sprite = interactHandSprite;
        handImg.preserveAspect = true;
        handImg.color = Color.white;
        handImg.raycastTarget = false;

        Shadow shadow = handPromptRoot.GetComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.65f);
        shadow.effectDistance = new Vector2(2f, -2f);

        handPromptRoot.SetActive(false);

        // 2. 传送门瞬间青光全屏闪光遮罩
        GameObject flashGo = new GameObject(
            "PortalWarpFlash",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image));
        flashGo.transform.SetParent(canvasGo.transform, false);

        RectTransform frt = flashGo.GetComponent<RectTransform>();
        frt.anchorMin = Vector2.zero;
        frt.anchorMax = Vector2.one;
        frt.offsetMin = Vector2.zero;
        frt.offsetMax = Vector2.zero;

        portalFlashImage = flashGo.GetComponent<Image>();
        portalFlashImage.color = new Color(0.28f, 0.90f, 1.00f, 0f);
        portalFlashImage.raycastTarget = false;
        flashGo.SetActive(false);
    }

    private void LateUpdate()
    {
        if (portalFlashImage != null && portalFlashAlpha > 0.001f && !portalFlashImage.gameObject.activeSelf)
        {
            portalFlashImage.gameObject.SetActive(true);
        }
    }

    private void EnsureDoorsDiscovered()
    {
        if (secretDoors != null && secretDoors.Length > 0) return;
        secretDoors = FindObjectsByType<SecretRoomDoor>();
    }

    private static void ShuffleList<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
