using System;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;

/// <summary>
/// 8 扇可交互暗门房间控制器（其余 13 扇不挂载此组件，仅作纯装饰门不可打开）：
/// - 玩家视角对准且在交互距离内时由 SecretRoomManager 在屏幕中心显示手型图标；
/// - 按下交互键后触发门向内侧打开动画（Open），禁用门板阻挡碰撞体，且永久无法再次关上；
/// - 在构建 NavMesh 之前自动于门后短走廊区域生成 NotWalkable 导航排除体，彻底禁止普通猴子进入门内；
/// - 实时追踪玩家是否进入任意暗门房间内部，供猴子切换为“在门外附近走廊绕圈巡逻”。
/// </summary>
public class SecretRoomDoor : MonoBehaviour
{
    private static readonly int OpenTriggerHash = Animator.StringToHash("Open");
    private static readonly List<SecretRoomDoor> allDoors = new();

    public static IReadOnlyList<SecretRoomDoor> AllDoors => allDoors;
    public static bool IsPlayerInsideAnyRoom { get; private set; }
    public static SecretRoomDoor ActivePlayerRoom { get; private set; }
    public static Vector3 ActiveRoomCorridorAnchor { get; private set; }

    [Header("暗门背后短走廊几何参数（单位：米）")]
    [SerializeField] private float roomDepth = 2.05f;
    [SerializeField] private float roomHalfWidth = 0.50f;
    [SerializeField] private float corridorExitOffset = 0.58f;
    [SerializeField] private float contentSpawnDepth = 1.24f;
    [SerializeField] private float portalSpawnDepth = 1.66f;
    [SerializeField] private float portalExitDepth = 1.00f;

    [Header("门内微弱暖光（照亮暗室内容物）")]
    [SerializeField] private bool addInteriorAccentLight = true;
    [SerializeField] private Color interiorLightColor = new Color(1.00f, 0.80f, 0.52f, 1f);
    [SerializeField] private float interiorLightIntensity = 0.52f;
    [SerializeField] private float interiorLightRange = 2.75f;

    private Animator doorAnimator;
    private BoxCollider blockingCollider;
    private bool isOpened;
    private bool navExclusionCreated;
    private bool interiorLightCreated;

    public bool IsOpened => isOpened;
    public event Action<SecretRoomDoor> OnDoorOpened;

    // 门本体 local +Z 指向走廊外侧法线，local -Z 指向门后短走廊内部
    public Vector3 CorridorOutsideAnchor => transform.TransformPoint(new Vector3(0f, 0f, corridorExitOffset));
    public Vector3 ContentCenterAnchor => transform.TransformPoint(new Vector3(0f, 0f, -contentSpawnDepth));
    public Vector3 PortalAnchor => transform.TransformPoint(new Vector3(0f, 0f, -portalSpawnDepth));
    public Vector3 PortalExitAnchor => transform.TransformPoint(new Vector3(0f, 0f, -portalExitDepth));
    public Quaternion FacingOutwardRotation => Quaternion.LookRotation(transform.forward, Vector3.up);

    private void Awake()
    {
        doorAnimator = GetComponentInChildren<Animator>(true);
        blockingCollider = GetComponent<BoxCollider>();
    }

    private void OnEnable()
    {
        if (!allDoors.Contains(this))
        {
            allDoors.Add(this);
        }
    }

    private void OnDisable()
    {
        allDoors.Remove(this);
        if (ActivePlayerRoom == this)
        {
            ActivePlayerRoom = null;
            IsPlayerInsideAnyRoom = false;
        }
    }

    /// <summary>
    /// 在 LevelOne 构建 NavMeshSurface 之前调用：
    /// 1) 在门后整个短走廊内部建立 NotWalkable NavMeshModifierVolume，确保走廊猴子绝对无法进入门内；
    /// 2) 补一盏柔和的门内小暖光，开门后能清楚看到红/黄水晶、传送门或正在沉睡的猴子。
    /// </summary>
    public void EnsureNavMeshExclusionAndLighting()
    {
        if (!navExclusionCreated)
        {
            navExclusionCreated = true;
            // 独立放在场景顶层（不挂在自带 IgnoreFromBuild+ApplyToChildren 的房门节点下），确保 NavMeshSurface 100% 识别并剔除门内区域
            GameObject exclGo = new GameObject($"RoomNavExclusion_{gameObject.name}");
            exclGo.layer = 0;
            exclGo.transform.SetPositionAndRotation(transform.position, transform.rotation);

            // 覆盖门框内侧直到房间最深处（local Z: -2.20 ~ +0.02），严格保留门外走廊可通行 NavMesh
            NavMeshModifierVolume vol = exclGo.AddComponent<NavMeshModifierVolume>();
            vol.center = new Vector3(0f, 0.50f, -(roomDepth * 0.5f + 0.04f));
            vol.size = new Vector3(roomHalfWidth * 2f + 0.24f, 1.20f, roomDepth + 0.16f);
            vol.area = 1; // Not Walkable
        }

        if (addInteriorAccentLight && !interiorLightCreated)
        {
            interiorLightCreated = true;
            GameObject lightGo = new GameObject("RoomInteriorLight");
            lightGo.transform.SetParent(transform, false);
            lightGo.transform.localPosition = new Vector3(0f, 0.86f, -1.05f);

            Light lt = lightGo.AddComponent<Light>();
            lt.type = LightType.Point;
            lt.color = interiorLightColor;
            lt.intensity = interiorLightIntensity;
            lt.range = interiorLightRange;
            lt.shadows = LightShadows.None;
        }
    }

    /// <summary>
    /// 朝内侧永久打开房门（无法再次关上）。
    /// </summary>
    public void OpenDoor()
    {
        if (isOpened) return;
        isOpened = true;

        if (blockingCollider == null)
        {
            blockingCollider = GetComponent<BoxCollider>();
        }
        if (blockingCollider != null)
        {
            blockingCollider.enabled = false;
        }

        if (doorAnimator == null)
        {
            doorAnimator = GetComponentInChildren<Animator>(true);
        }
        if (doorAnimator != null)
        {
            doorAnimator.SetTrigger(OpenTriggerHash);
        }

        OnDoorOpened?.Invoke(this);
    }

    /// <summary>
    /// 精确判断世界坐标是否位于本暗门身后的短走廊内部。
    /// </summary>
    public bool ContainsWorldPointInsideRoom(Vector3 worldPos)
    {
        Vector3 lp = transform.InverseTransformPoint(worldPos);
        return Mathf.Abs(lp.x) <= roomHalfWidth + 0.08f
            && lp.z <= -0.05f
            && lp.z >= -(roomDepth + 0.20f);
    }

    public static void ResetOccupancyState()
    {
        IsPlayerInsideAnyRoom = false;
        ActivePlayerRoom = null;
        ActiveRoomCorridorAnchor = Vector3.zero;
    }

    public static void RefreshPlayerOccupancy(Transform playerTr)
    {
        if (playerTr == null)
        {
            ResetOccupancyState();
            return;
        }

        Vector3 pos = playerTr.position;
        for (int i = 0; i < allDoors.Count; i++)
        {
            SecretRoomDoor door = allDoors[i];
            if (door == null || !door.isOpened) continue;

            if (door.ContainsWorldPointInsideRoom(pos))
            {
                IsPlayerInsideAnyRoom = true;
                ActivePlayerRoom = door;
                ActiveRoomCorridorAnchor = door.CorridorOutsideAnchor;
                return;
            }
        }

        IsPlayerInsideAnyRoom = false;
        ActivePlayerRoom = null;
    }
}
