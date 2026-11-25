using System;
using UnityEngine;

public class ShardAnimation : MonoBehaviour
{
    private Animator animator;
    public event Action disappear;
    private void Awake() => animator = GetComponent<Animator>();
    public void PickUp() => animator.SetTrigger("PickUp");

    private void Disappear() => disappear?.Invoke();
}
