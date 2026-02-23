using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

[DefaultExecutionOrder(-200)]
// Centralized cloud persistence helper for the game's realtime database.
// Responsible for loading/saving plot states, seeds, vegetables and simple locks
// using the configured `baseUrl` (Firebase RTDB). All public methods are
// implemented as coroutines and return results via callback parameters.
public class Cloud_Persistence : MonoBehaviour
{
    [SerializeField] string baseUrl =
        "baseUrl";

    public static Cloud_Persistence I { get; private set; }

    // ---------------------- DATA CLASSES ----------------------
    [Serializable]
    // Represents the persisted state for a single garden plot.
    // Fields track growth stage, watering, plant type and timing metadata
    // used for wilting/growth/death logic across networked clients.
    public class PlotState
    {
        public int stage;                 // -1 empty, 0..4 growth stages
        public float water;               // accumulated water for current stage
        public string plantType;          // "Tomato" | "Carrot"

        public int condition;             // 0 Healthy, 1 Wilted, 2 Dead
        public bool isWet;                // is the soil considered wet

        // Timestamps (ms since epoch) used for conflict resolution and timers
        public long updatedAt;            // ms - last update (last-write-wins)

        public long lastWateredAtMs;      // ms - last time watered (for wilting)
        public long wiltStartAtMs;        // ms - when wilting started (for death)

        public long growReadyAtMs;        // ms - when plot will advance to next stage
    }



    [Serializable]
    // Simple 3D position stored for seeds
    public class SeedState { public float x, y, z; }

    [Serializable]
    // Position for a spawned tomato in the world
    public class TomatoState { public float x, y, z; }

    [Serializable]
    // Position for a spawned carrot in the world
    public class CarrotState { public float x, y, z; }

    // ---------------------- INIT ----------------------
    void Awake()
    {
        if (I && I != this)
        {
            Destroy(gameObject);
            return;
        }

        // Singleton instance used by other scripts to perform cloud ops.
        I = this;
        DontDestroyOnLoad(gameObject);
        Debug.Log("[Cloud] Ready. BaseUrl=" + baseUrl);
    }

    // ---------------------- LOAD PLOTS ----------------------
    string AllUrl() => $"{baseUrl}/garden/plots.json";
    string OneUrl(string key) => $"{baseUrl}/garden/plots/{UnityWebRequest.EscapeURL(key)}.json";

    public IEnumerator LoadPlot(string key, Action<bool, PlotState> done)
    {
        // Load a single plot state by key. Callback receives success flag and PlotState.
        using var req = UnityWebRequest.Get(OneUrl(key));
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[Cloud] LoadPlot failed key={key} err={req.error}");
            done?.Invoke(false, null);
            yield break;
        }

        var raw = req.downloadHandler.text;
        if (string.IsNullOrEmpty(raw) || raw == "null")
        {
            done?.Invoke(false, null);
            yield break;
        }

        try
        {
            var s = JsonUtility.FromJson<PlotState>(raw);
            done?.Invoke(s != null, s);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Cloud] LoadPlot parse error key={key} msg={e.Message} raw={raw}");
            done?.Invoke(false, null);
        }
    }

    public IEnumerator LoadAll(Action<Dictionary<string, PlotState>> done)
    {
        // Load all plot states. Returns a dictionary mapping keys -> PlotState.
        using var req = UnityWebRequest.Get(AllUrl());
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            done?.Invoke(new());
            yield break;
        }

        var raw = req.downloadHandler.text;
        if (string.IsNullOrEmpty(raw) || raw == "null")
        {
            done?.Invoke(new());
            yield break;
        }

        var dict = new Dictionary<string, PlotState>();

        try
        {
            string[] blocks = raw.Trim('{', '}')
                .Split(new[] { "}," }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var b in blocks)
            {
                string[] parts = b.Split(new[] { ':' }, 2);
                if (parts.Length < 2) continue;

                string key = parts[0].Trim().Trim('"');
                string json = parts[1].Trim();
                    if (!json.EndsWith("}")) json += "}";

                    // Skip explicit null entries (deleted children) so the
                    // returned dictionary does not contain keys with null values.
                    if (json.Trim().Equals("null}", StringComparison.OrdinalIgnoreCase))
                        continue;

                    dict[key] = JsonUtility.FromJson<PlotState>(json);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[Cloud][Parse Plots] " + e.Message);
        }

        Debug.Log($"[Cloud] LoadAll parsed plots={dict.Count}");
        done?.Invoke(dict);
    }

    public IEnumerator Put(string key, PlotState s)
    {
        // Persist a PlotState for `key`. Updates `updatedAt` to current UTC ms.
        s.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string url = OneUrl(key);
        string json = JsonUtility.ToJson(s);

        using var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPUT);
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();
    }

    // ---------------------- LOAD SEED_TOMATO ----------------------
    public IEnumerator LoadTomatoSeeds(Action<Dictionary<string, SeedState>> done)
    {
        // Convenience wrapper to load all tomato seed entries.
        yield return LoadGeneric("/seed_tomato.json", done);
    }

    // ---------------------- LOAD SEED_CARROT ----------------------
    public IEnumerator LoadCarrotSeeds(Action<Dictionary<string, SeedState>> done)
    {
        // Convenience wrapper to load all carrot seed entries.
        yield return LoadGeneric("/seed_carrot.json", done);
    }

    // ---------------------- LOAD TOMATOES ----------------------
    public IEnumerator LoadTomatoes(Action<Dictionary<string, TomatoState>> done)
    {
        // Load all tomato instances (vegetables) from the cloud.
        yield return LoadGeneric("/tomatoes.json", done);
    }

    // ---------------------- LOAD CARROTS ----------------------
    public IEnumerator LoadCarrots(Action<Dictionary<string, CarrotState>> done)
    {
        // Load all carrot instances (vegetables) from the cloud.
        yield return LoadGeneric("/carrots.json", done);
    }

    // ---------------------- GENERIC LOADER ----------------------
    IEnumerator LoadGeneric<T>(string path, Action<Dictionary<string, T>> done)
    {
        // Generic loader that fetches a JSON map at `path` and parses each entry
        // into a Dictionary<string,T> where T is a serializable state class.
        string url = baseUrl + path;

        using var req = UnityWebRequest.Get(url);
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            done?.Invoke(new());
            yield break;
        }

        var raw = req.downloadHandler.text;
        if (string.IsNullOrEmpty(raw) || raw == "null")
        {
            done?.Invoke(new());
            yield break;
        }

        var dict = new Dictionary<string, T>();

        try
        {
            string[] blocks = raw.Trim('{', '}')
                .Split(new[] { "}," }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var b in blocks)
            {
                string[] parts = b.Split(new[] { ':' }, 2);
                if (parts.Length < 2) continue;

                string key = parts[0].Trim().Trim('"');
                string json = parts[1].Trim();
                if (!json.EndsWith("}")) json += "}";

                dict[key] = JsonUtility.FromJson<T>(json);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[Cloud][Parse Generic] " + e.Message);
        }

        done?.Invoke(dict);
    }

    // ---------------------- SAVE SEEDS ----------------------
    public IEnumerator AddTomatoSeed(string id, Vector3 pos)
    {
        // Add/overwrite a tomato seed entry at id with world position `pos`.
        string url = baseUrl + "/seed_tomato/" + id + ".json";
        var s = new SeedState { x = pos.x, y = pos.y, z = pos.z };

        using var req = UnityWebRequest.Put(url, JsonUtility.ToJson(s));
        req.SetRequestHeader("Content-Type", "application/json");
        yield return req.SendWebRequest();
    }

    public IEnumerator AddCarrotSeed(string id, Vector3 pos)
    {
        // Add/overwrite a carrot seed entry at id with world position `pos`.
        string url = baseUrl + "/seed_carrot/" + id + ".json";
        var s = new SeedState { x = pos.x, y = pos.y, z = pos.z };

        using var req = UnityWebRequest.Put(url, JsonUtility.ToJson(s));
        req.SetRequestHeader("Content-Type", "application/json");
        yield return req.SendWebRequest();
    }

    // ---------------------- SAVE VEGETABLES ----------------------
    public IEnumerator AddTomato(string id, Vector3 pos)
    {
        // Add/overwrite a tomato vegetable entry at id with world position `pos`.
        string url = baseUrl + "/tomatoes/" + id + ".json";
        var t = new TomatoState { x = pos.x, y = pos.y, z = pos.z };

        using var req = UnityWebRequest.Put(url, JsonUtility.ToJson(t));
        req.SetRequestHeader("Content-Type", "application/json");
        yield return req.SendWebRequest();
    }

    public IEnumerator AddCarrot(string id, Vector3 pos)
    {
        // Add/overwrite a carrot vegetable entry at id with world position `pos`.
        string url = baseUrl + "/carrots/" + id + ".json";
        var c = new CarrotState { x = pos.x, y = pos.y, z = pos.z };

        using var req = UnityWebRequest.Put(url, JsonUtility.ToJson(c));
        req.SetRequestHeader("Content-Type", "application/json");
        yield return req.SendWebRequest();
    }

    // ---------------------- DELETE ----------------------
    public IEnumerator DeleteTomatoSeed(string id)
    {
        // Delete a tomato seed entry from the cloud by id.
        using var req = UnityWebRequest.Delete(baseUrl + "/seed_tomato/" + id + ".json");
        yield return req.SendWebRequest();
    }

    public IEnumerator DeleteCarrotSeed(string id)
    {
        // Delete a carrot seed entry from the cloud by id.
        using var req = UnityWebRequest.Delete(baseUrl + "/seed_carrot/" + id + ".json");
        yield return req.SendWebRequest();
    }

    public IEnumerator DeleteTomato(string id)
    {
        // Delete a tomato vegetable entry from the cloud by id.
        using var req = UnityWebRequest.Delete(baseUrl + "/tomatoes/" + id + ".json");
        yield return req.SendWebRequest();
    }

    public IEnumerator DeleteCarrot(string id)
    {
        // Delete a carrot vegetable entry from the cloud by id.
        using var req = UnityWebRequest.Delete(baseUrl + "/carrots/" + id + ".json");
        yield return req.SendWebRequest();
    }

    public IEnumerator DeletePlot(string key)
    {
        // Delete a garden plot state by key.
        using var req = UnityWebRequest.Delete(baseUrl + "/garden/plots/" + UnityWebRequest.EscapeURL(key) + ".json");
        yield return req.SendWebRequest();
    }

    public IEnumerator TryAcquireLock(string lockKey, string owner, System.Action<bool> done)
    {
        // Attempt to claim a simple string lock in the DB. Returns true if claim succeeded.
        // lockKey ex: "basket_tomato_<id>"
        string url = $"{baseUrl}/locks/{UnityWebRequest.EscapeURL(lockKey)}.json";

        // if value is null => lock is free; we'll attempt to write owner
        using var get = UnityWebRequest.Get(url);
        yield return get.SendWebRequest();

        if (get.result != UnityWebRequest.Result.Success)
        {
            done?.Invoke(false);
            yield break;
        }

        string raw = get.downloadHandler.text;
        if (!string.IsNullOrEmpty(raw) && raw != "null")
        {
            done?.Invoke(false);
            yield break;
        }

        // claim the lock by writing the owner string
        using var put = UnityWebRequest.Put(url, $"\"{owner}\"");
        put.SetRequestHeader("Content-Type", "application/json");
        yield return put.SendWebRequest();

        done?.Invoke(put.result == UnityWebRequest.Result.Success);
    }

    // ---------------------- LOCKS (NEW) ----------------------
    public IEnumerator ReleaseLock(string lockKey)
    {
        // Release (delete) a simple lock entry by key.
        string url = $"{baseUrl}/locks/{UnityWebRequest.EscapeURL(lockKey)}.json";
        using var req = UnityWebRequest.Delete(url);
        yield return req.SendWebRequest();
    }

    public IEnumerator LoadLocks(System.Action<Dictionary<string, string>> done)
    {
        // Load all lock entries as a map lockKey -> owner.
        using var req = UnityWebRequest.Get($"{baseUrl}/locks.json");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            done?.Invoke(new());
            yield break;
        }

        string raw = req.downloadHandler.text;
        if (string.IsNullOrEmpty(raw) || raw == "null")
        {
            done?.Invoke(new());
            yield break;
        }

        // raw expected: {"tomatoes:ID":"owner","seed_tomato:ID":"owner",...}
        var dict = new Dictionary<string, string>();

        try
        {
            raw = raw.Trim().Trim('{', '}').Trim();
            if (raw.Length == 0) { done?.Invoke(dict); yield break; }

            // split por ","
            var pairs = raw.Split(new[] { "\"," }, System.StringSplitOptions.RemoveEmptyEntries);

            foreach (var p in pairs)
            {
                string s = p;
                if (!s.EndsWith("\"")) s += "\"";

                int colon = s.IndexOf("\":");
                if (colon < 0) continue;

                string key = s.Substring(0, colon).Trim().Trim('"');
                string val = s.Substring(colon + 2).Trim(); // depois do ":
                val = val.Trim().Trim('"');

                dict[key] = val;
            }
        }
        catch
        {
            // se falhar parsing, devolve vazio para não rebentar gameplay
            dict = new Dictionary<string, string>();
        }

        done?.Invoke(dict);
    }


}
