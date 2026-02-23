using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Oculus.Interaction; // for Grabbable in SetHidden

[DefaultExecutionOrder(-100)]
// Bridges cloud state into the local scene: spawns seeds/pickups from
// `Cloud_Persistence` data, keeps them synced, and applies simple lock-based
// visibility rules so other clients can't interact with locked objects.
public class CloudStateSync : MonoBehaviour
{
    [Header("Prefabs (NON-network)")]
    public GameObject tomatoSeedPrefab;
    public GameObject carrotSeedPrefab;
    public GameObject tomatoPrefab;
    public GameObject carrotPrefab;

    [Header("Default seeds (when empty)")]
    // Defaults used to bootstrap the world when no seeds exist in the cloud.
    public int defaultSeedsPerType = 4;
    public Vector3 defaultTomatoSeedPos = new Vector3(-1.4f, 1f, -11.663725f);
    public Vector3 defaultCarrotSeedPos = new Vector3(-1.6f, 0.305f, -5.3f);

    [Header("Polling")]
    // How often (seconds) to poll the cloud for updates.
    public float pollInterval = 0.5f;

    // Cached cloud data for each group.
    Dictionary<string, Cloud_Persistence.PlotState> _plots = new();
    Dictionary<string, Cloud_Persistence.SeedState> _tomatoSeeds = new();
    Dictionary<string, Cloud_Persistence.SeedState> _carrotSeeds = new();
    Dictionary<string, Cloud_Persistence.TomatoState> _tomatoes = new();
    Dictionary<string, Cloud_Persistence.CarrotState> _carrots = new();

    // Locks cache (key = "group:id", value = ownerId) to control visibility.
    Dictionary<string, string> _locks = new();

    // Spawned GameObjects tracked by key and last cloud position seen.
    readonly Dictionary<string, GameObject> spawned = new(); // key(group:id) -> gos
    readonly Dictionary<string, Vector3> lastCloudPos = new(); // key(group:id) -> last pos seen

    IEnumerator Start()
    {
        yield return new WaitUntil(() => Cloud_Persistence.I != null);

        // 1) load once
        yield return LoadAll();
        Debug.Log($"[Firebase_Bootstrap] Initial LoadAll: plots={_plots.Count} tomatoSeeds={_tomatoSeeds.Count} carrotSeeds={_carrotSeeds.Count} tomatoes={_tomatoes.Count} carrots={_carrots.Count} locks={_locks.Count}");
        ApplyPlots();
        yield return EnsureDefaultSeedsIfEmpty();
        SpawnAllFromCached();

        // NEW: apply locks after initial spawn
        ApplyLocks();

        // 2) keep syncing
        StartCoroutine(PollLoop());
    }

    IEnumerator PollLoop()
    {
        while (true)
        {
            yield return LoadAll();
            Debug.Log($"[Firebase_Bootstrap] Poll LoadAll: plots={_plots.Count} tomatoSeeds={_tomatoSeeds.Count} carrotSeeds={_carrotSeeds.Count} tomatoes={_tomatoes.Count} carrots={_carrots.Count} locks={_locks.Count}");
            ApplyPlots();
            ReconcileSpawns();

            // NEW: hide/show based on locks before updating transforms
            ApplyLocks();

            UpdateExistingTransforms();
            yield return new WaitForSeconds(pollInterval);
        }
    }

    IEnumerator LoadAll()
    {
        // Load cached data for plots, seeds and pickups from cloud.
        yield return Cloud_Persistence.I.LoadAll(d => _plots = d ?? new());
        yield return Cloud_Persistence.I.LoadTomatoSeeds(d => _tomatoSeeds = d ?? new());
        yield return Cloud_Persistence.I.LoadCarrotSeeds(d => _carrotSeeds = d ?? new());
        yield return Cloud_Persistence.I.LoadTomatoes(d => _tomatoes = d ?? new());
        yield return Cloud_Persistence.I.LoadCarrots(d => _carrots = d ?? new());

        // Load simple locks map (used to hide objects owned by other clients).
        yield return Cloud_Persistence.I.LoadLocks(d => _locks = d ?? new());
    }

    void ApplyPlots()
    {
        var plotsInScene = FindObjectsByType<Dirt_Plot>(FindObjectsSortMode.None);
        int applied = 0;
        foreach (var p in plotsInScene)
        {
            if (p == null) continue;
                if (_plots.TryGetValue(p.Key, out var s))
                {
                    if (s != null)
                    {
                        p.ApplyFromCloud(s);
                        applied++;
                    }
                    else
                    {
                        // Explicit null value for this key in cloud: treat as deletion.
                        p.ApplyDeletedFromCloud(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    }
                }
                else
                {
                    // Plot missing from cloud entirely: also treat as deletion.
                    p.ApplyDeletedFromCloud(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
        }

        Debug.Log($"[Firebase_Bootstrap] ApplyPlots: scenePlots={plotsInScene.Length} applied={applied}");
    }

    IEnumerator EnsureDefaultSeedsIfEmpty()
    {
        bool emptySeeds = (_tomatoSeeds.Count == 0) && (_carrotSeeds.Count == 0);

        if (!emptySeeds) yield break;

        bool gotLock = false;
        string owner = SystemInfo.deviceUniqueIdentifier;
        yield return Cloud_Persistence.I.TryAcquireLock("init_seeds", owner, ok => gotLock = ok);

        if (!gotLock) yield break;

        // reload to be safe (someone else might have created between checks)
        yield return Cloud_Persistence.I.LoadTomatoSeeds(d => _tomatoSeeds = d ?? new());
        yield return Cloud_Persistence.I.LoadCarrotSeeds(d => _carrotSeeds = d ?? new());

        if (_tomatoSeeds.Count != 0 || _carrotSeeds.Count != 0) yield break;

        for (int i = 0; i < defaultSeedsPerType; i++)
        {
            string idT = Guid.NewGuid().ToString();
            yield return Cloud_Persistence.I.AddTomatoSeed(idT, defaultTomatoSeedPos);

            string idC = Guid.NewGuid().ToString();
            yield return Cloud_Persistence.I.AddCarrotSeed(idC, defaultCarrotSeedPos);
        }

        // refresh caches after creating
        yield return Cloud_Persistence.I.LoadTomatoSeeds(d => _tomatoSeeds = d ?? new());
        yield return Cloud_Persistence.I.LoadCarrotSeeds(d => _carrotSeeds = d ?? new());
    }

    void SpawnAllFromCached()
    {
        // seeds
        foreach (var kv in _tomatoSeeds) SpawnIfMissing("seed_tomato", kv.Key, tomatoSeedPrefab, kv.Value.x, kv.Value.y, kv.Value.z);
        foreach (var kv in _carrotSeeds) SpawnIfMissing("seed_carrot", kv.Key, carrotSeedPrefab, kv.Value.x, kv.Value.y, kv.Value.z);

        // pickups
        foreach (var kv in _tomatoes) SpawnIfMissing("tomatoes", kv.Key, tomatoPrefab, kv.Value.x, kv.Value.y, kv.Value.z);
        foreach (var kv in _carrots) SpawnIfMissing("carrots", kv.Key, carrotPrefab, kv.Value.x, kv.Value.y, kv.Value.z);
    }

    void ReconcileSpawns()
    {
        // build set of all ids that should exist
        var should = new HashSet<string>();

        foreach (var kv in _tomatoSeeds) should.Add(Key("seed_tomato", kv.Key));
        foreach (var kv in _carrotSeeds) should.Add(Key("seed_carrot", kv.Key));
        foreach (var kv in _tomatoes) should.Add(Key("tomatoes", kv.Key));
        foreach (var kv in _carrots) should.Add(Key("carrots", kv.Key));

        // delete local objects that no longer exist in firebase
        var toRemove = new List<string>();
        foreach (var kv in spawned)
            if (!should.Contains(kv.Key))
                toRemove.Add(kv.Key);

        foreach (var k in toRemove)
        {
            Destroy(spawned[k]);
            spawned.Remove(k);
        }

        // spawn missing
        SpawnAllFromCached();
    }

    void SpawnIfMissing(string group, string id, GameObject prefab, float x, float y, float z)
    {
        if (!prefab) return;

        string k = Key(group, id);
        if (spawned.ContainsKey(k)) return;

        Vector3 pos = new Vector3(x, y, z);
        pos = SnapToTerrain(pos);
        var go = Instantiate(prefab, pos, Quaternion.identity);

        // Attach IdHolder so `BasketTrigger` and other systems can identify it.
        var h = go.GetComponent<IdHolder>();
        if (!h) h = go.AddComponent<IdHolder>();
        h.id = id;
        h.isTomatoSeed = (group == "seed_tomato");
        h.isCarrotSeed = (group == "seed_carrot");
        h.isTomato = (group == "tomatoes");
        h.isCarrot = (group == "carrots");

        // Ensure spawned object behaves dynamically (not static) so physics works.
        StartCoroutine(ForceDynamic(go));

        spawned[k] = go;

        // If the object is currently locked by another client, hide it immediately.
        ApplyLockToOne(k, go);
    }

    IEnumerator ForceDynamic(GameObject go)
    {
        // run now + after 2 frames to override Building Blocks init
        SetDynamic(go);
        yield return null;
        SetDynamic(go);
        yield return null;
        SetDynamic(go);
    }

    static void SetDynamic(GameObject go)
    {
        if (!go) return;
        var rb = go.GetComponent<Rigidbody>();
        if (!rb) return;

        rb.isKinematic = false;
        rb.useGravity = true;
        rb.WakeUp();
    }

    void UpdateExistingTransforms()
    {
        // seeds
        foreach (var kv in _tomatoSeeds)
            UpdatePosIfExists("seed_tomato", kv.Key, kv.Value.x, kv.Value.y, kv.Value.z);

        foreach (var kv in _carrotSeeds)
            UpdatePosIfExists("seed_carrot", kv.Key, kv.Value.x, kv.Value.y, kv.Value.z);

        // pickups
        foreach (var kv in _tomatoes)
            UpdatePosIfExists("tomatoes", kv.Key, kv.Value.x, kv.Value.y, kv.Value.z);

        foreach (var kv in _carrots)
            UpdatePosIfExists("carrots", kv.Key, kv.Value.x, kv.Value.y, kv.Value.z);
    }

    void UpdatePosIfExists(string group, string id, float x, float y, float z)
    {
        string k = Key(group, id);
        if (!spawned.TryGetValue(k, out var go) || !go) return;

        Vector3 cloudPos = new Vector3(x, y, z);

        // 1) Só faz update se o Firebase mudou desde o último poll
        if (lastCloudPos.TryGetValue(k, out var prev) && (cloudPos - prev).sqrMagnitude < 0.0001f)
            return;

        lastCloudPos[k] = cloudPos;

        // 2) Não sobrepor física se o objeto estiver a mexer (evita "nunca assentar")
        var rb = go.GetComponent<Rigidbody>();
        if (rb && !rb.isKinematic && rb.linearVelocity.sqrMagnitude > 0.01f)
            return;

        go.transform.position = cloudPos;
    }

    // ---------------------- LOCK VISIBILITY (NEW) ----------------------

    void ApplyLocks()
    {
        string me = SystemInfo.deviceUniqueIdentifier;

        foreach (var kv in spawned)
        {
            string key = kv.Key; // "group:id"
            GameObject go = kv.Value;
            if (!go) continue;

            string owner = null;
            bool hasLock = _locks != null && _locks.TryGetValue(key, out owner) && !string.IsNullOrEmpty(owner);
            bool lockedByOther = hasLock && owner != me;

            SetHidden(go, lockedByOther);
        }
    }


    void ApplyLockToOne(string key, GameObject go)
    {
        if (!go) return;

        string me = SystemInfo.deviceUniqueIdentifier;

        string owner = null;
        bool hasLock = _locks != null && _locks.TryGetValue(key, out owner) && !string.IsNullOrEmpty(owner);
        bool lockedByOther = hasLock && owner != me;

        SetHidden(go, lockedByOther);
    }


    static void SetHidden(GameObject go, bool hide)
    {
        // Toggle renderers (invisible/visible)
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            r.enabled = !hide;

        // Impedir grab
        foreach (var g in go.GetComponentsInChildren<Oculus.Interaction.Grabbable>(true))
            g.enabled = !hide;

        // Freeze physics while hidden to avoid falling/sliding.
        var rb = go.GetComponent<Rigidbody>();
        if (rb)
        {
            if (hide)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
                rb.useGravity = false;
            }
            else
            {
                rb.isKinematic = false;
                rb.useGravity = true;
                rb.WakeUp();
            }
        }
    }

    // Ensure spawned objects are slightly above terrain height to avoid clipping.
    static Vector3 SnapToTerrain(Vector3 pos)
    {
        var t = Terrain.activeTerrain;
        if (t == null || t.terrainData == null)
            return pos;

        float terrainY = t.SampleHeight(pos) + t.GetPosition().y;

        const float offset = 0.02f;

        if (pos.y < terrainY + offset)
            pos.y = terrainY + offset;

        return pos;
    }

    static string Key(string group, string id) => $"{group}:{id}";
}
