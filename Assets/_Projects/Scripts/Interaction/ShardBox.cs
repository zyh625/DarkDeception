using UnityEngine;

public class ShardBox : MonoBehaviour
{
    private BoxCollider box;
    private ShardAnimation shardAnimation;
    private bool havePicked = false;
    
    private void Awake()
    {
        box = GetComponent<BoxCollider>();
        shardAnimation = GetComponentInChildren<ShardAnimation>();
    }
    private void OnEnable()
    {
        shardAnimation.disappear += Miss;
        havePicked = false;
    }
    private void OnDisable()
    {
        shardAnimation.disappear -= Miss;
    }
    private void OnTriggerEnter(Collider other)
    {
        if (havePicked) return;
        if(other.TryGetComponent<PlayerInteract>(out PlayerInteract player))
        {
            shardAnimation.PickUp();
            player.PickUp();
            havePicked = false;
        }
    }
    private void Miss() => gameObject.SetActive(false);
}
