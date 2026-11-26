using UnityEngine;

[RequireComponent(typeof(BoxCollider))]
public class ShardBox : MonoBehaviour
{
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

        player.PickUp(transform.position);
        gameObject.SetActive(false);
    }
}
