using System;
using UnityEngine;

public class PlayerInteract : MonoBehaviour
{
    public event Action pickUp;
    public void PickUp() => pickUp?.Invoke();
}
