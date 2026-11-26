using System;
using UnityEngine;

[RequireComponent(typeof(BoxCollider))]
[RequireComponent(typeof(Rigidbody))]
public class ElevatorExitTrigger : MonoBehaviour
{
    private BoxCollider box;
    private Rigidbody rb;

    public event Action<PlayerInteract> OnPlayerExited;

    private void Reset()
    {
        ConfigurePhysics();
    }

    private void Awake()
    {
        ConfigurePhysics();
    }

    private void ConfigurePhysics()
    {
        box = GetComponent<BoxCollider>();
        if (box != null)
        {
            box.isTrigger = true;
        }

        rb = GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody>();
        }
        rb.isKinematic = true;
        rb.useGravity = false;
    }

    public void SetActive(bool active)
    {
        if (box != null)
        {
            box.enabled = active;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        TryNotifyExit(other);
    }

    private void OnTriggerStay(Collider other)
    {
        TryNotifyExit(other);
    }

    private void TryNotifyExit(Collider other)
    {
        if (!other.TryGetComponent(out PlayerInteract player)) return;
        if (!player.IsAlive) return;

        OnPlayerExited?.Invoke(player);
    }
}
