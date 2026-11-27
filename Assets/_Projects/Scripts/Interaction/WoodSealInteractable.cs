using System.Collections;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;

/// <summary>
/// 安全屋入口木头封条（WoodSeal）交互、双向碰撞封锁、碎裂动画与猴子撞碎控制器：
/// 1. 开局默认封锁（带实体碰撞箱阻挡玩家从任意一侧穿行），玩家复活时自动重置为封锁状态；
/// 2. 玩家从正反两侧对准封条时均显示屏幕中心白色手势 UI（UI_ServiceBell_InteractHand.png），点击/按交互键立刻碎裂；
/// 3. 猴子寻路与 NavMesh 完全无视本封条（位于 Default 层且标记 NavMeshModifier.ignoreFromBuild = true）；
/// 4. 若封条尚未解封且任意活跃猴子/首领经过接触到封条，封条立刻向行进方向碎裂；
/// 5. 碎裂瞬间立即关闭碰撞箱（碎裂动画期间无任何碰撞阻挡），播放木板炸裂飞溅动画后完全消失。
/// </summary>
public class WoodSealInteractable : MonoBehaviour
{
    private static readonly List<WoodSealInteractable> allSeals = new();
    private static AudioClip synthesizedWoodBreakClip;

    private struct ShatterPiece
    {
        public Transform tr;
        public Vector3 velocity;
        public Vector3 angularVel;
        public Vector3 initialScale;
    }

    [Header("交互与猴子撞碎参数")]
    [SerializeField] private float interactDistance = 1.65f;
    [SerializeField] private float monkeyBreachHalfDepth = 0.56f;
    [SerializeField] private float monkeyBreachHalfWidth = 0.62f;

    [Header("碎裂动画参数")]
    [SerializeField] private float shatterDuration = 0.72f;
    [SerializeField] private float shatterImpulse = 2.65f;
    [SerializeField] private float shatterGravity = 9.2f;
    [SerializeField, Range(0f, 1f)] private float breakSfxVolume = 0.92f;

    private BoxCollider blockingCollider;
    private GameObject visualRoot;
    private Material woodMaterial;
    private Material bracketMaterial;
    private AudioSource audioSource;

    private Transform shatterContainer;
    private readonly List<ShatterPiece> shatterPieces = new(16);
    private Coroutine shatterRoutine;
    private bool isBroken;

    public bool IsBroken => isBroken;
    public bool CanInteract => !isBroken;
    public float InteractDistance => interactDistance;

    public static void ResetAllSeals()
    {
        for (int i = 0; i < allSeals.Count; i++)
        {
            WoodSealInteractable seal = allSeals[i];
            if (seal != null)
            {
                seal.ResetToSealed();
            }
        }
    }

    private void Awake()
    {
        // 确保整个封条层级位于 Default 层（0），绝对不进入 Wall 层（7），确保猴子视野射线与寻路图完全无视封条
        SetLayerRecursive(gameObject, 0);

        // 确保 NavMesh 烘焙完全忽略封条，猴子寻路直接穿过
        NavMeshModifier navMod = GetComponent<NavMeshModifier>();
        if (navMod == null)
        {
            navMod = gameObject.AddComponent<NavMeshModifier>();
        }
        navMod.ignoreFromBuild = true;
        navMod.applyToChildren = true;

        // 获取或自动补齐实体阻挡碰撞箱（覆盖整个门洞宽 0.98m × 高 0.98m × 厚 0.22m，阻挡玩家两侧穿行）
        blockingCollider = GetComponent<BoxCollider>();
        if (blockingCollider == null)
        {
            blockingCollider = gameObject.AddComponent<BoxCollider>();
            blockingCollider.center = new Vector3(0.01f, 0.36f, -0.015f);
            blockingCollider.size = new Vector3(0.98f, 0.98f, 0.22f);
        }
        blockingCollider.isTrigger = false;

        // 定位 WoodSeal 模型子节点并抓取原木材质用于碎裂木片
        if (transform.childCount > 0)
        {
            visualRoot = transform.GetChild(0).gameObject;
        }
        CacheMaterials();

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }
        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.spatialBlend = 0.65f;
        audioSource.dopplerLevel = 0f;
        audioSource.rolloffMode = AudioRolloffMode.Linear;
        audioSource.minDistance = 500f;
        audioSource.maxDistance = 1000f;

        ResetToSealed();
    }

    private void OnEnable()
    {
        if (!allSeals.Contains(this))
        {
            allSeals.Add(this);
        }
    }

    private void OnDisable()
    {
        allSeals.Remove(this);
    }

    private void Update()
    {
        if (isBroken) return;

        // 猴子经过且封条尚未解封时：接触到封条瞬间直接撞碎封条（无阻挡延迟）
        IReadOnlyList<MonkeyAI> activeEnemies = MonkeyAI.ActiveMonkeys;
        for (int i = 0; i < activeEnemies.Count; i++)
        {
            MonkeyAI mk = activeEnemies[i];
            if (mk == null || !mk.gameObject.activeInHierarchy) continue;
            if (mk.IsAsleepInSecretRoom || mk.IsTimeFrozen) continue;
            if (mk.CurrentState == MonkeyAI.MonkeyState.Executing || mk.CurrentState == MonkeyAI.MonkeyState.Frozen) continue;

            Vector3 local = transform.InverseTransformPoint(mk.transform.position);
            if (Mathf.Abs(local.z) <= monkeyBreachHalfDepth &&
                Mathf.Abs(local.x - 0.01f) <= monkeyBreachHalfWidth &&
                local.y >= -0.65f && local.y <= 1.15f)
            {
                Vector3 breachDir = mk.transform.forward;
                breachDir.y = 0f;
                if (breachDir.sqrMagnitude < 0.01f)
                {
                    breachDir = transform.position - mk.transform.position;
                    breachDir.y = 0f;
                }
                Shatter(breachDir.sqrMagnitude > 0.001f ? breachDir.normalized : transform.forward, byMonkey: true);
                break;
            }
        }
    }

    /// <summary>
    /// 玩家在封条任意一侧点击/按下交互键打开封条。
    /// </summary>
    public void BreakByPlayer(Vector3 playerWorldPos)
    {
        if (isBroken) return;
        Vector3 pushDir = transform.position - playerWorldPos;
        pushDir.y = 0f;
        if (pushDir.sqrMagnitude < 0.001f) pushDir = transform.forward;
        else pushDir.Normalize();

        Shatter(pushDir, byMonkey: false);
    }

    /// <summary>
    /// 复活时由 LevelOne 调用：恢复封条模型与碰撞箱阻挡。
    /// </summary>
    public void ResetToSealed()
    {
        if (shatterRoutine != null)
        {
            StopCoroutine(shatterRoutine);
            shatterRoutine = null;
        }
        ClearShatterPieces();

        isBroken = false;
        if (visualRoot != null)
        {
            visualRoot.SetActive(true);
        }
        if (blockingCollider != null)
        {
            blockingCollider.enabled = true;
        }
    }

    private void Shatter(Vector3 pushWorldDir, bool byMonkey)
    {
        if (isBroken) return;
        isBroken = true;

        // 关键规则：碎裂瞬间碰撞箱立刻失效，仅纯粹播放木头碎裂动画随后消失
        if (blockingCollider != null)
        {
            blockingCollider.enabled = false;
        }

        if (visualRoot != null)
        {
            visualRoot.SetActive(false);
        }

        PlayBreakAudio();
        HapticFeedback.RequestShake(byMonkey ? 0.010f : 0.0075f, 0.22f);

        SpawnShatterPieces(pushWorldDir, byMonkey);
        if (shatterRoutine != null) StopCoroutine(shatterRoutine);
        shatterRoutine = StartCoroutine(ShatterAnimRoutine());
    }

    private void SpawnShatterPieces(Vector3 pushWorldDir, bool byMonkey)
    {
        ClearShatterPieces();

        GameObject root = new GameObject("WoodSealShatterPieces");
        root.transform.SetParent(transform, false);
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        shatterContainer = root.transform;

        // 将 3 根横向木板分别裂解成 4 块不规则飞溅木条（共 12 块木条 + 2 块飞脱碎屑，零碰撞体）
        float[] boardLocalYs = { 0.195f, 0.365f, 0.540f };
        float[] segXs = { -0.22f, -0.07f, 0.09f, 0.24f };
        float speedScale = byMonkey ? 1.28f : 1.0f;

        Vector3 pushNorm = pushWorldDir.sqrMagnitude > 0.001f ? pushWorldDir.normalized : transform.forward;
        // 统一对齐到穿过门洞的法线方向，使木条明显向另一侧炸开
        float dotFwd = Vector3.Dot(pushNorm, transform.forward);
        Vector3 mainBlastDir = (dotFwd >= 0f ? transform.forward : -transform.forward);

        for (int b = 0; b < boardLocalYs.Length; b++)
        {
            for (int s = 0; s < segXs.Length; s++)
            {
                GameObject chunk = GameObject.CreatePrimitive(PrimitiveType.Cube);
                chunk.name = $"Splinter_{b}_{s}";
                Collider c = chunk.GetComponent<Collider>();
                if (c != null) Destroy(c);

                chunk.transform.SetParent(shatterContainer, false);
                float tiltZ = (b == 1 ? 6f : -4f) + Random.Range(-10f, 10f);
                chunk.transform.localPosition = new Vector3(
                    segXs[s] + Random.Range(-0.025f, 0.025f),
                    boardLocalYs[b] + Random.Range(-0.015f, 0.015f),
                    Random.Range(-0.02f, 0.02f));
                chunk.transform.localRotation = Quaternion.Euler(Random.Range(-8f, 8f), Random.Range(-8f, 8f), tiltZ);

                Vector3 sc = new Vector3(
                    Random.Range(0.14f, 0.22f),
                    Random.Range(0.055f, 0.095f),
                    Random.Range(0.028f, 0.048f));
                chunk.transform.localScale = sc;

                MeshRenderer mr = chunk.GetComponent<MeshRenderer>();
                if (mr != null && woodMaterial != null)
                {
                    mr.sharedMaterial = woodMaterial;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }

                // 炸裂初速度：主要沿破门方向喷射 + 横向散开 + 轻微上扬弧线
                Vector3 spreadRight = transform.right * (segXs[s] * 3.8f + Random.Range(-0.55f, 0.55f));
                Vector3 vel = mainBlastDir * (shatterImpulse * speedScale * Random.Range(0.75f, 1.32f))
                    + spreadRight
                    + Vector3.up * Random.Range(0.85f, 2.25f);
                Vector3 angVel = new Vector3(
                    Random.Range(-560f, 560f),
                    Random.Range(-520f, 520f),
                    Random.Range(-640f, 640f));

                shatterPieces.Add(new ShatterPiece
                {
                    tr = chunk.transform,
                    velocity = vel,
                    angularVel = angVel,
                    initialScale = sc
                });
            }
        }

        // 额外补充 2 块崩落的固定铁扣/碎木块增强打击感
        for (int k = 0; k < 2; k++)
        {
            GameObject bit = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bit.name = $"BracketBit_{k}";
            Collider c = bit.GetComponent<Collider>();
            if (c != null) Destroy(c);

            bit.transform.SetParent(shatterContainer, false);
            float sx = k == 0 ? -0.28f : 0.28f;
            bit.transform.localPosition = new Vector3(sx, 0.36f, 0f);
            Vector3 sc = new Vector3(0.055f, 0.075f, 0.04f);
            bit.transform.localScale = sc;

            MeshRenderer mr = bit.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                Material m = bracketMaterial != null ? bracketMaterial : woodMaterial;
                if (m != null) mr.sharedMaterial = m;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            shatterPieces.Add(new ShatterPiece
            {
                tr = bit.transform,
                velocity = mainBlastDir * (shatterImpulse * 0.7f) + transform.right * (sx * 2.2f) + Vector3.up * Random.Range(0.6f, 1.6f),
                angularVel = new Vector3(Random.Range(-680f, 680f), Random.Range(-680f, 680f), Random.Range(-680f, 680f)),
                initialScale = sc
            });
        }
    }

    private IEnumerator ShatterAnimRoutine()
    {
        float dur = Mathf.Max(0.25f, shatterDuration);
        float elapsed = 0f;

        while (elapsed < dur)
        {
            float dt = Time.deltaTime;
            elapsed += dt;
            float norm = Mathf.Clamp01(elapsed / dur);
            // 后 38% 迅速缩小消失，干净利落不残留地面杂物
            float scaleMul = norm > 0.62f ? Mathf.Clamp01(1f - (norm - 0.62f) / 0.38f) : 1f;

            for (int i = 0; i < shatterPieces.Count; i++)
            {
                ShatterPiece p = shatterPieces[i];
                if (p.tr == null) continue;

                p.velocity.y -= shatterGravity * dt;
                Vector3 nextWorld = p.tr.position + p.velocity * dt;
                if (nextWorld.y < 0.03f)
                {
                    nextWorld.y = 0.03f;
                    p.velocity.y = Mathf.Abs(p.velocity.y) * 0.25f;
                    p.velocity.x *= 0.72f;
                    p.velocity.z *= 0.72f;
                }
                p.tr.position = nextWorld;
                p.tr.Rotate(p.angularVel * dt, Space.Self);
                p.tr.localScale = p.initialScale * scaleMul;
                shatterPieces[i] = p;
            }

            yield return null;
        }

        ClearShatterPieces();
        shatterRoutine = null;
    }

    private void ClearShatterPieces()
    {
        shatterPieces.Clear();
        if (shatterContainer != null)
        {
            Destroy(shatterContainer.gameObject);
            shatterContainer = null;
        }
    }

    private void CacheMaterials()
    {
        MeshRenderer[] renderers = GetComponentsInChildren<MeshRenderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            MeshRenderer r = renderers[i];
            if (r == null || r.sharedMaterial == null) continue;
            string n = r.gameObject.name;
            if (woodMaterial == null && n.Contains("Board"))
            {
                woodMaterial = r.sharedMaterial;
            }
            else if (bracketMaterial == null && n.Contains("Bracket"))
            {
                bracketMaterial = r.sharedMaterial;
            }
        }
        if (woodMaterial == null && renderers.Length > 0)
        {
            woodMaterial = renderers[0].sharedMaterial;
        }
    }

    private void PlayBreakAudio()
    {
        if (audioSource == null) return;
        float sfxVol = GameSettings.SfxVolume;
        if (sfxVol <= 0.001f) return;

        float distAtten = AudioManager.ComputeStraightLineDistanceAttenuation(transform.position, 1.8f, 20.0f);
        if (distAtten <= 0.01f) return;

        AudioClip clip = GetOrCreateWoodBreakClip();
        if (clip == null) return;

        audioSource.pitch = Random.Range(0.92f, 1.08f);
        audioSource.PlayOneShot(clip, Mathf.Clamp01(breakSfxVolume * distAtten * sfxVol));
    }

    /// <summary>
    /// 程序化合成清脆厚重的木板断裂崩碎声（瞬态木裂爆音 + 多重木屑飞溅脆响），无需外部音频依赖。
    /// </summary>
    private static AudioClip GetOrCreateWoodBreakClip()
    {
        if (synthesizedWoodBreakClip != null) return synthesizedWoodBreakClip;

        const int sampleRate = 22050;
        const float duration = 0.52f;
        int totalSamples = Mathf.CeilToInt(sampleRate * duration);
        float[] samples = new float[totalSamples];

        System.Random rng = new System.Random(719283);
        float[] crackTimes = { 0.00f, 0.035f, 0.085f, 0.145f, 0.22f };

        for (int i = 0; i < totalSamples; i++)
        {
            float t = (float)i / sampleRate;
            float acc = 0f;
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);

            // 1. 低频闷响撞击（门板断裂冲击）
            float thud = Mathf.Sin(2f * Mathf.PI * 115f * t) * Mathf.Exp(-t * 22f) * 0.65f;
            acc += thud;

            // 2. 多次干脆木纹劈裂瞬态爆音
            for (int c = 0; c < crackTimes.Length; c++)
            {
                float dt = t - crackTimes[c];
                if (dt < 0f || dt > 0.18f) continue;
                float gain = c == 0 ? 0.95f : (0.58f - c * 0.08f);
                float env = Mathf.Exp(-dt * (c == 0 ? 34f : 48f));
                float woodRes = Mathf.Sin(2f * Mathf.PI * (420f + c * 155f) * dt) * 0.45f
                              + Mathf.Sin(2f * Mathf.PI * (960f + c * 210f) * dt) * 0.25f;
                acc += (noise * 0.72f + woodRes) * env * gain;
            }

            float tail = t > 0.36f ? Mathf.Clamp01((duration - t) / 0.16f) : 1f;
            samples[i] = Mathf.Clamp(acc * 0.68f * tail, -0.95f, 0.95f);
        }

        synthesizedWoodBreakClip = AudioClip.Create("SynthesizedWoodSealShatter", totalSamples, 1, sampleRate, false);
        synthesizedWoodBreakClip.SetData(samples, 0);
        return synthesizedWoodBreakClip;
    }

    private static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform ch in go.transform)
        {
            SetLayerRecursive(ch.gameObject, layer);
        }
    }
}
