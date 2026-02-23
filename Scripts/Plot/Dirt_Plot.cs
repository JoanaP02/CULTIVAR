using UnityEngine;
using System;

// Represents a single garden plot: handles planting, watering, growth,
// wilting/death timers, visuals and cloud synchronization for this plot.
public class Dirt_Plot : MonoBehaviour
{
    // Supported plant types for this plot.
    public enum PlantType : byte { Tomato = 0, Carrot = 1 }

    // Plant condition used for visuals and behavior.
    public enum PlantCondition : byte { Healthy = 0, Wilted = 1, Dead = 2 }

    [Header("Plot Identity")]
    // Unique key used for cloud persistence. Falls back to GameObject name.
    public string Key => (GetComponent<Plot_Id>()?.Id ?? name).Trim().ToLowerInvariant();

    [Header("Soil Visuals")]
    // References for swapping soil material based on wet/dry state.
    [SerializeField] MeshRenderer dirtRenderer;
    [SerializeField] Material dryMat;
    [SerializeField] Material wetMat;

    [Header("Tomato Visual Prefabs (per Stage, per Condition)")]
    // Prefabs indexed by stage for healthy/wilted/dead tomato visuals.
    [SerializeField] GameObject[] healthyPrefabs;
    [SerializeField] GameObject[] wiltedPrefabs;
    [SerializeField] GameObject[] deadPrefabs;

    [Header("Carrot Visual Prefabs")]
    // Carrot uses a simpler set of prefabs (seed and growing visual).
    [SerializeField] GameObject carrotStage0Prefab;
    [SerializeField] GameObject carrotStageGrowingPrefab;

    [Header("Carrot Materials (swap by Condition on Stages 1..3)")]
    // Materials applied to carrot renderers depending on condition.
    [SerializeField] Material carrotHealthyMat;
    [SerializeField] Material carrotWiltedMat;
    [SerializeField] Material carrotDeadMat;

    [Header("Carrot Growth Y (local)")]
    // Local Y offsets for carrot visual placement per stage.
    [SerializeField] float carrotY_stage0 = 0.5f;
    [SerializeField] float carrotY_stage1 = 0.10f;
    [SerializeField] float carrotY_stage2 = 0.2f;
    [SerializeField] float carrotY_stage3 = 0.3f;
    [SerializeField] float carrotY_stage4 = 0.4f;

    [Header("Plant Visual Offset (local)")]
    // Default local Y offset for planted visuals.
    [SerializeField] float plantedY = 0.45f;

    [Header("Growth Settings")]
    // Water needed per stage (for stages 0..3), and delay before growth.
    [SerializeField] float[] waterNeededPerStage = { 80, 120, 160, 200 }; // stage 0..3
    [SerializeField] float growDelaySeconds = 5f;
    [SerializeField] int maxStage = 4;

    [Header("Wilt & Death Settings")]
    // Time thresholds (seconds) for wilting and death when not watered.
    [SerializeField] float timeToWilt = 45f;
    [SerializeField] float timeToDie = 90f;

    [Header("Tomato Pickup Spawn")]
    // Prefab and offsets used when producing tomato pickups into the world.
    [SerializeField] GameObject tomatoPickupPrefab; 
    [SerializeField] Vector3[] tomatoSpawnOffsets =
    {
        new Vector3( 0.10f, 0.40f, 0f),
        new Vector3(-0.10f, 0.40f, 0f)
    };

    [Header("Carrot Pickup Spawn")]
    // Prefab and offsets used when producing carrot pickups into the world.
    [SerializeField] GameObject carrotPickupPrefab; 
    [SerializeField] Vector3[] carrotSpawnOffsets = 
    {
        new Vector3(0f, 0.15f, 0f),
        new Vector3(0f, 0.15f, 0f)
    };

    [Header("Seed Basket Positions")]
    // Default positions used when spawning seeds into baskets if cloud code is present.
    public Vector3 defaultTomatoSeedPos = new Vector3(-1.4f, 1f, -11.663725f);
    public Vector3 defaultCarrotSeedPos = new Vector3(-1.6f, 0.305f, -6.5f);

    [Header("Audios")]
    // Optional audio sources for plant interactions.
    [SerializeField] private AudioSource SoundRemovePlant;
    [SerializeField] private AudioSource SoundWiltedDeadPlant;
    [SerializeField] private AudioSource SoundPlant;
    [SerializeField] private AudioSource SoundGrow;



    // ---------------------------
    // LOCAL CACHED STATE (mirrors cloud PlotState)
    // ---------------------------

    // Current persisted state mirrored locally.
    PlantType _plant;
    PlantCondition _cond;
    bool _occupied;
    int _stage;                 // -1 empty
    float _water;
    bool _isWet;
    long _updatedAtMs;
    long _lastWateredAtMs;
    long _wiltStartAtMs;
    long _growReadyAtMs;

    // Currently instantiated visual GameObject for this plot.
    GameObject _current;

    // Helper to get current UTC time in milliseconds.
    long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // On start, ensure visuals reflect current (possibly empty) state.
    void Start() => ApplyVisuals();

    void Update()
    {
        // Run deterministic simulation based on stored timestamps.
        if (!_occupied) return;

        // Growth trigger: advance stage when healthy, wet and growReady has passed.
        if (_cond == PlantCondition.Healthy && _isWet && _growReadyAtMs > 0 && NowMs >= _growReadyAtMs)
        {
            // Advance stage (clamped to max).
            _stage = Mathf.Min(_stage + 1, maxStage);

            if (SoundGrow) SoundGrow.Play();

            // Reset water state and scheduled grow time.
            _water = 0f;
            _isWet = false;
            _growReadyAtMs = 0;

            TouchAndSave();
            return;
        }

        // Wilt/Death logic applies only to intermediate stages (1..3).
        bool wiltableStage = _stage >= 1 && _stage <= 3;
        if (!wiltableStage) return;

        long dtWaterMs = NowMs - _lastWateredAtMs;

        // If not watered in time -> become wilted.
        if (_cond == PlantCondition.Healthy && dtWaterMs >= (long)(timeToWilt * 1000f))
        {
            _cond = PlantCondition.Wilted;
            _wiltStartAtMs = NowMs;
            TouchAndSave();
            if (SoundWiltedDeadPlant) SoundWiltedDeadPlant.Play();
            return;
        }

        // If wilted for long enough -> die.
        if (_cond == PlantCondition.Wilted && _wiltStartAtMs > 0)
        {
            long dtWiltMs = NowMs - _wiltStartAtMs;
            if (dtWiltMs >= (long)(timeToDie * 1000f))
            {
                _cond = PlantCondition.Dead;
                TouchAndSave();
                if (SoundWiltedDeadPlant) SoundWiltedDeadPlant.Play();
                return;
            }
        }
    }

    // ---------------------------
    // APPLY FROM CLOUD (CloudPoller calls this)
    // ---------------------------
    // Update local state from the cloud `PlotState`. Uses last-write-wins
    // based on `updatedAt` to avoid applying stale updates.
    public void ApplyFromCloud(Cloud_Persistence.PlotState s)
    {
        if (s == null) return;

        Debug.Log($"[Dirt_Plot] ApplyFromCloud key={Key} incoming.updatedAt={s.updatedAt} current.updatedAt={_updatedAtMs} stage={s.stage}");

        // Last-write-wins: ignore older updates.
        if (s.updatedAt < _updatedAtMs)
        {
            Debug.Log($"[Dirt_Plot] Ignored stale update for {Key}");
            return;
        }

        _updatedAtMs = s.updatedAt;

        _stage = s.stage;
        _occupied = _stage >= 0;

        _water = s.water;
        _isWet = s.isWet;

        _plant = (!string.IsNullOrEmpty(s.plantType) && s.plantType.Equals("Carrot", StringComparison.OrdinalIgnoreCase))
            ? PlantType.Carrot : PlantType.Tomato;

        _cond = (PlantCondition)Mathf.Clamp(s.condition, 0, 2);

        _lastWateredAtMs = s.lastWateredAtMs;
        _wiltStartAtMs = s.wiltStartAtMs;
        _growReadyAtMs = s.growReadyAtMs;

        ApplyVisuals();
    }

    // Apply a deletion coming from the cloud: clear visuals locally without
    // saving back to cloud. `updatedAtMs` should be the timestamp of the
    // deletion (or current time) to preserve last-write-wins ordering.
    public void ApplyDeletedFromCloud(long updatedAtMs)
    {
        if (updatedAtMs < _updatedAtMs) return;

        _updatedAtMs = updatedAtMs;

        _occupied = false;
        _stage = -1;

        _cond = PlantCondition.Healthy;
        _water = 0f;
        _isWet = false;

        _lastWateredAtMs = 0;
        _wiltStartAtMs = 0;
        _growReadyAtMs = 0;

        ApplyVisuals();
    }

    // ---------------------------
    // PLANT by seed contact
    // ---------------------------
    // Called when a seed touches this plot. Returns true if planting succeeded.
    public bool OnSeedContact(PlantType type)
    {
        // Only plant if the plot is currently empty.
        if (_occupied) return false;

        _plant = type;
        _occupied = true;
        _stage = 0;

        _cond = PlantCondition.Healthy;
        _water = 0f;
        _isWet = false;
        _growReadyAtMs = 0;

        _lastWateredAtMs = NowMs;
        _wiltStartAtMs = 0;

        TouchAndSave();
        return true;
    }

    // ---------------------------
    // WATER
    // ---------------------------
    // Add water to the plot. Handles recovery from wilt, wet/dry state
    // and scheduling growth when water threshold is reached.
    public void AddWater(float amount)
    {
        if (!_occupied || _stage < 0 || _stage > maxStage) return;
        if (_cond == PlantCondition.Dead) return;
        if (_plant == PlantType.Carrot && _stage == maxStage) return;

        _lastWateredAtMs = NowMs;

        // If wilted and watered -> recover to healthy.
        if (_cond == PlantCondition.Wilted)
        {
            _cond = PlantCondition.Healthy;
            _wiltStartAtMs = 0;
            TouchAndSave();
            return;
        }

        _water += amount;

        // Determine water needed for this stage (only stages 0..3 have needs)
        int wi = Mathf.Clamp(_stage, 0, waterNeededPerStage.Length - 1);
        float need = waterNeededPerStage[wi];

        // If reached threshold and not already wet -> mark wet and schedule growth.
        if (!_isWet && _stage >= 0 && _stage <= 3 && _water >= need)
        {
            _isWet = true;
            _growReadyAtMs = NowMs + (long)(growDelaySeconds * 1000f);
        }

        TouchAndSave();
    }

    // ---------------------------
    // REMOVE (optional)
    // ---------------------------
    // Remove plant state (make plot empty) and persist.
    public void RemovePlant()
    {
        _occupied = false;
        _stage = -1;
        _cond = PlantCondition.Healthy;

        _water = 0f;
        _isWet = false;
        _growReadyAtMs = 0;

        _lastWateredAtMs = 0;
        _wiltStartAtMs = 0;

        TouchAndSave();
    }

    // ---------------------------
    // Save
    // ---------------------------
    // Update `updatedAt` timestamp, refresh visuals and save to cloud.
    void TouchAndSave()
    {
        _updatedAtMs = NowMs;
        ApplyVisuals();
        SaveToCloud();
    }

    // Build a `PlotState` and send it to the cloud persistence singleton.
    void SaveToCloud()
    {
        if (!Cloud_Persistence.I) return;

        var s = new Cloud_Persistence.PlotState
        {
            stage = _stage,
            water = _water,
            plantType = _plant.ToString(),
            condition = (int)_cond,
            isWet = _isWet,
            updatedAt = _updatedAtMs,
            lastWateredAtMs = _lastWateredAtMs,
            wiltStartAtMs = _wiltStartAtMs,
            growReadyAtMs = _growReadyAtMs
        };

        Cloud_Persistence.I.StartCoroutine(Cloud_Persistence.I.Put(Key, s));
    }

    // ---------------------------
    // VISUALS
    // ---------------------------
    // Instantiate and position the correct visual for the current plant,
    // stage and condition. Also swap soil material based on wet/dry.
    void ApplyVisuals()
    {
        if (_current) Destroy(_current);

        if (dirtRenderer)
            dirtRenderer.material = _isWet ? wetMat : dryMat;

        if (!_occupied || _stage < 0) return;

        // CARROT visuals (simpler staged offsets + material swap)
        if (_plant == PlantType.Carrot)
        {
            GameObject prefab = null;
            float y = plantedY;

            if (_stage == 0) { prefab = carrotStage0Prefab; y = carrotY_stage0; }
            else if (_stage == 1) { prefab = carrotStageGrowingPrefab; y = carrotY_stage1; }
            else if (_stage == 2) { prefab = carrotStageGrowingPrefab; y = carrotY_stage2; }
            else if (_stage == 3) { prefab = carrotStageGrowingPrefab; y = carrotY_stage3; }
            else if (_stage == maxStage) { prefab = carrotStageGrowingPrefab; y = carrotY_stage4; }

            if (prefab != null)
            {
                _current = Instantiate(prefab, transform);
                _current.transform.localPosition = new Vector3(0f, y, 0f);

                if (_stage >= 1)
                    ApplyCarrotMaterialToCurrent();
            }
            return;
        }

        // TOMATO visuals (choose prefab by condition and stage)
        if (_stage > maxStage) return;

        GameObject tomatoVis = null;

        if (_cond == PlantCondition.Healthy && _stage < healthyPrefabs.Length)
            tomatoVis = healthyPrefabs[_stage];
        else if (_cond == PlantCondition.Wilted && _stage < wiltedPrefabs.Length)
            tomatoVis = wiltedPrefabs[_stage];
        else if (_cond == PlantCondition.Dead && _stage < deadPrefabs.Length)
            tomatoVis = deadPrefabs[_stage];

        if (tomatoVis != null)
        {
            _current = Instantiate(tomatoVis, transform);
            _current.transform.localPosition = new Vector3(0f, plantedY, 0f);
        }
    }


    // Apply the carrot material matching the current condition to all child renderers.
    void ApplyCarrotMaterialToCurrent()
    {
        if (_current == null) return;

        Material target = _cond switch
        {
            PlantCondition.Wilted => carrotWiltedMat,
            PlantCondition.Dead => carrotDeadMat,
            _ => carrotHealthyMat
        };

        if (target == null) return;

        var renderers = _current.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
            r.sharedMaterial = target;
    }

    // If the plant is beyond stage 0, spawn a seed into the basket (cloud)
    // and remove the plant locally.
    public void RemoveIfAboveStage0()
    {
        if (!_occupied) return;
        if (_stage <= 0) return;
        
        if (Cloud_Persistence.I)
        {
            string id = Guid.NewGuid().ToString();

            if (_plant == PlantType.Tomato)
                Cloud_Persistence.I.StartCoroutine(
                    Cloud_Persistence.I.AddTomatoSeed(id, defaultTomatoSeedPos));
            else
                Cloud_Persistence.I.StartCoroutine(
                    Cloud_Persistence.I.AddCarrotSeed(id, defaultCarrotSeedPos));
        }

        RemovePlant();
        if (SoundRemovePlant) SoundRemovePlant.Play();

    }

    // Attempt to harvest the plant if it is fully grown. Spawns vegetables
    // into the world via cloud persistence and updates/removes the plant.
    public void TryHarvest()
    {
        if (!_occupied) return;
        if (_stage != maxStage) return;

        if (_plant == PlantType.Tomato)
            HarvestTomatoes();
        else
            HarvestCarrots();
    }

    
    void HarvestTomatoes()
    {
        if (!Cloud_Persistence.I) return;

        // 1) escrever 2 tomates no Firebase (offsets em torno do plot)
        foreach (var offset in tomatoSpawnOffsets)
        {
            Vector3 spawnPos = transform.position + offset;
            string id = Guid.NewGuid().ToString();
            Cloud_Persistence.I.StartCoroutine(Cloud_Persistence.I.AddTomato(id, spawnPos));
            if (SoundPlant) SoundPlant.Play();

        }

        // 2) voltar a planta para stage anterior e marcar dead (como a tua lógica antiga)
        _stage = maxStage - 1;          // ex: 4 -> 3
        _cond = PlantCondition.Dead;

        _water = 0f;
        _isWet = false;
        _growReadyAtMs = 0;

        TouchAndSave();
    }

    void HarvestCarrots()
    {
        if (!Cloud_Persistence.I) return;

        foreach (var offset in carrotSpawnOffsets)
        {
            Vector3 spawnPos = transform.position + offset;
            string id = Guid.NewGuid().ToString();
            Cloud_Persistence.I.StartCoroutine(Cloud_Persistence.I.AddCarrot(id, spawnPos));
            if (SoundPlant) SoundPlant.Play();
        }

        RemovePlant(); 

        Cloud_Persistence.I.StartCoroutine(Cloud_Persistence.I.DeletePlot(Key));
    }

}
