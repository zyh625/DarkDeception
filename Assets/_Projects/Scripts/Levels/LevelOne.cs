using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class LevelOne : MonoBehaviour
{
    [SerializeField]private PlayerInteract playerInteract;
    private int curShard = 0;

    [SerializeField] private Transform shard;
    [SerializeField] private Transform[] shardBorn;
    private List<Transform> shards = new();
    private int maxShard = 0;
    private PlayerInteract player;
    [SerializeField] private Transform playerBorn;
    private void Awake()
    {
        foreach (Transform t in shardBorn)
        {
            Transform tt = Instantiate(shard);
            tt.position = t.position;
            shards.Append(tt);
            maxShard++;
        }
        player = Instantiate(playerInteract);
        player.transform.position = playerBorn.position;
    }
    private void OnEnable()
    {
        player.pickUp += PickUpShard;
    }
    private void OnDisable()
    {
        player.pickUp -= PickUpShard;
    }

    private void PickUpShard()
    {
        curShard++;
        if (curShard >= maxShard)
        {
            OpenExit();
        }
    }
    private void OpenExit()
    {

    }
}
