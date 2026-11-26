using UnityEngine;

public class FirstView : MonoBehaviour
{
    [SerializeField] private float sensitivity = 0.1f;

    [Header("被抓时的镜头锁定")]
    [SerializeField] private float killerFocusHeight = 0.72f;
    [SerializeField] private float focusRotateSpeed = 12f;

    private PlayerInteract input;
    private float pitch;
    private Transform focusTarget;

    private void Awake() => input = GetComponentInParent<PlayerInteract>();

    private void OnEnable()
    {
        if (input == null) return;
        input.turnAround += TurnAround;
        input.look += LookAround;
        input.OnCaught += FocusOnKiller;
    }

    private void OnDisable()
    {
        if (input == null) return;
        input.turnAround -= TurnAround;
        input.look -= LookAround;
        input.OnCaught -= FocusOnKiller;
    }

    private void TurnAround() => input.transform.Rotate(Vector3.up, 180f);

    private void LookAround(Vector2 move)
    {
        if (focusTarget != null) return;

        input.transform.Rotate(sensitivity * move.x * Vector3.up, Space.World);
        pitch = Mathf.Clamp(pitch - move.y * sensitivity, -80f, 80f);
        transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    private void FocusOnKiller(Transform killer)
    {
        focusTarget = killer;
    }

    private void LateUpdate()
    {
        if (focusTarget == null || input == null) return;

        Vector3 targetPoint = focusTarget.position + Vector3.up * killerFocusHeight;
        Vector3 dir = targetPoint - transform.position;
        if (dir.sqrMagnitude < 0.0001f) return;

        // 水平转向交给玩家根节点，俯仰角交给相机自身
        Vector3 flatDir = new Vector3(dir.x, 0f, dir.z);
        if (flatDir.sqrMagnitude > 0.0001f)
        {
            Quaternion targetYaw = Quaternion.LookRotation(flatDir.normalized, Vector3.up);
            input.transform.rotation = Quaternion.Slerp(
                input.transform.rotation, targetYaw, focusRotateSpeed * Time.deltaTime);
        }

        float horizontalDist = flatDir.magnitude;
        float targetPitch = -Mathf.Atan2(dir.y, Mathf.Max(0.0001f, horizontalDist)) * Mathf.Rad2Deg;
        pitch = Mathf.Lerp(pitch, Mathf.Clamp(targetPitch, -80f, 80f), focusRotateSpeed * Time.deltaTime);
        transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }
}
