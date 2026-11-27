using UnityEngine;

public enum ShardColorType
{
    NormalPurple = 0,
    Red = 1,
    Yellow = 2
}

[RequireComponent(typeof(BoxCollider))]
public class ShardBox : MonoBehaviour
{
    [SerializeField] private ShardColorType shardType = ShardColorType.NormalPurple;

    public ShardColorType ShardType => shardType;

    private void Awake()
    {
        BoxCollider box = GetComponent<BoxCollider>();
        if (box != null)
        {
            box.isTrigger = true;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.TryGetComponent(out PlayerInteract player)) return;
        if (!player.IsAlive) return;

        player.PickUp(transform.position, shardType);
        gameObject.SetActive(false);
    }
}
