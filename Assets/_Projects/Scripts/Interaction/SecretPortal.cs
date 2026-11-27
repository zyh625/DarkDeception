using UnityEngine;

/// <summary>
/// 暗门内的双向配对传送门（每局随机将 4 个传送门两两对应 A<->B, C<->D）：
/// - 玩家步入传送门镜面触发区域后，瞬移至配对传送门前方并面朝出口方向；
/// - 若目标传送门所在房间的门尚未打开，则自动将其向内侧打开避免玩家被反锁在房间内；
/// - 全局共享短暂冷却时间，杜绝刚传送到达又瞬间弹回。
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class SecretPortal : MonoBehaviour
{
    private static float sharedNextTeleportTime;

    [Header("传送与反馈参数")]
    [SerializeField] private float teleportCooldown = 1.35f;
    [SerializeField] private float exitForwardOffset = 0.62f;
    [SerializeField] private float proximityFallbackRadius = 0.30f;
    [SerializeField] private Light portalGlowLight;
    [SerializeField] private float glowPulseSpeed = 2.6f;

    private SecretPortal pairedPortal;
    private SecretRoomDoor ownerRoom;
    private float baseGlowIntensity;
    private float pulsePhase;

    public SecretPortal PairedPortal => pairedPortal;
    public SecretRoomDoor OwnerRoom => ownerRoom;

    public Vector3 ExitWorldPos => ownerRoom != null
        ? ownerRoom.PortalExitAnchor
        : transform.TransformPoint(new Vector3(0f, 0f, exitForwardOffset));

    public Quaternion ExitWorldRot => ownerRoom != null
        ? ownerRoom.FacingOutwardRotation
        : Quaternion.LookRotation(transform.forward, Vector3.up);

    public static void ResetSharedCooldown()
    {
        sharedNextTeleportTime = 0f;
    }

    private void Awake()
    {
        BoxCollider box = GetComponent<BoxCollider>();
        if (box != null)
        {
            box.isTrigger = true;
        }

        if (portalGlowLight == null)
        {
            portalGlowLight = GetComponentInChildren<Light>(true);
        }
        if (portalGlowLight != null)
        {
            baseGlowIntensity = portalGlowLight.intensity;
        }
        pulsePhase = Random.Range(0f, Mathf.PI * 2f);
    }

    public void Bind(SecretRoomDoor room, SecretPortal partner)
    {
        ownerRoom = room;
        pairedPortal = partner;
    }

    private void Update()
    {
        if (portalGlowLight != null)
        {
            float wave = 0.85f + 0.15f * Mathf.Sin(Time.time * glowPulseSpeed + pulsePhase);
            portalGlowLight.intensity = baseGlowIntensity * wave;
        }

        // 近距兜底检测（防止 CharacterController 贴镜面但未触发 OnTriggerEnter）
        if (pairedPortal == null || Time.time < sharedNextTeleportTime) return;
        PlayerInteract player = PlayerInteract.ActivePlayer;
        if (player == null || !player.IsAlive || player.IsPaused) return;
        if (ownerRoom != null && !ownerRoom.IsOpened) return;

        Vector3 localP = transform.InverseTransformPoint(player.transform.position);
        if (Mathf.Abs(localP.x) <= 0.28f && localP.z >= -0.22f && localP.z <= proximityFallbackRadius)
        {
            TeleportPlayer(player);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (pairedPortal == null || Time.time < sharedNextTeleportTime) return;
        if (!other.TryGetComponent(out PlayerInteract player)) return;
        if (!player.IsAlive || player.IsPaused) return;
        if (ownerRoom != null && !ownerRoom.IsOpened) return;

        TeleportPlayer(player);
    }

    private void TeleportPlayer(PlayerInteract player)
    {
        if (pairedPortal == null || player == null) return;

        sharedNextTeleportTime = Time.time + Mathf.Max(0.4f, teleportCooldown);

        // 若目标传送门所在房间尚未开门，自动向内打开以免玩家被关在门内
        if (pairedPortal.ownerRoom != null && !pairedPortal.ownerRoom.IsOpened)
        {
            pairedPortal.ownerRoom.OpenDoor();
        }

        Vector3 targetPos = pairedPortal.ExitWorldPos;
        targetPos.y = player.transform.position.y;
        Quaternion targetRot = pairedPortal.ExitWorldRot;

        player.TeleportSafe(targetPos, targetRot);
        SecretRoomDoor.RefreshPlayerOccupancy(player.transform);

        HapticFeedback.RequestShake(0.0085f, 0.18f);
        SecretRoomManager.TriggerPortalFlash();
    }
}
