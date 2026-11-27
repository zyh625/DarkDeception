using UnityEngine;

/// <summary>
/// 挂在第一关雕像上：
/// 收集完所有普通水晶（进入 ActivateStatue 阶段）后，雕像变为可交互状态；
/// 当玩家视角中心对准雕像时由 SecretRoomManager 显示白色手势 UI（UI_ServiceBell_InteractHand.png），
/// 点击/按下交互键后触发 LevelOne.ActivateStatue()：消除全部首领并打开逃生电梯门。
/// </summary>
public class StatueInteractTrigger : MonoBehaviour
{
    [SerializeField] private LevelOne levelOne;
    [SerializeField] private float interactDistance = 2.0f;

    private Light readyGlowLight;

    public bool CanInteract => levelOne != null && levelOne.ObjectiveState == LevelObjectiveState.ActivateStatue;
    public float InteractDistance => interactDistance;

    private void Awake()
    {
        if (levelOne == null)
        {
            levelOne = FindAnyObjectByType<LevelOne>();
        }

        // 若雕像及其子物体未带实体碰撞体，自动补齐一个非触发 CapsuleCollider 供中心视线射线精准命中
        Collider existingCol = GetComponentInChildren<Collider>(true);
        if (existingCol == null)
        {
            CapsuleCollider cap = gameObject.AddComponent<CapsuleCollider>();
            cap.center = new Vector3(0f, 0.36f, 0f);
            cap.radius = 0.34f;
            cap.height = 0.92f;
            cap.isTrigger = false;
        }

        // 雕像可交互时在周边点亮微弱暖金光晕提示玩家前来交互
        GameObject glowGo = new GameObject("StatueReadyGlow");
        glowGo.transform.SetParent(transform, false);
        glowGo.transform.localPosition = new Vector3(0f, 0.52f, 0f);
        readyGlowLight = glowGo.AddComponent<Light>();
        readyGlowLight.type = LightType.Point;
        readyGlowLight.color = new Color(1.00f, 0.80f, 0.35f, 1f);
        readyGlowLight.range = 2.6f;
        readyGlowLight.intensity = 0f;
        readyGlowLight.shadows = LightShadows.None;
        readyGlowLight.enabled = false;
    }

    private void Update()
    {
        if (readyGlowLight == null) return;
        bool ready = CanInteract;
        if (readyGlowLight.enabled != ready)
        {
            readyGlowLight.enabled = ready;
        }
        if (ready)
        {
            readyGlowLight.intensity = 1.25f + 0.45f * Mathf.Sin(Time.time * 3.8f);
        }
    }

    /// <summary>
    /// 玩家对准雕像并点击/按下交互键时由 SecretRoomManager 调用。
    /// </summary>
    public void Interact()
    {
        if (!CanInteract) return;
        HapticFeedback.RequestShake(0.008f, 0.22f);
        levelOne.ActivateStatue();
    }
}
