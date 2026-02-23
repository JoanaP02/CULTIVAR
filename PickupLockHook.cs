using System.Collections;
using UnityEngine;
using Oculus.Interaction; // Grabbable, PointerEvent, PointerEventType

public class PickupLockHook : MonoBehaviour
{
    // References
    Grabbable grabbable;
    IdHolder h;

    // Local identifier for this device/client and a flag for lock ownership.
    string myOwner;
    bool iOwnLock;

    // Selection state and coroutine used to delay acquiring locks for short grabs.
    bool isSelected;
    Coroutine lockRoutine;

    // Delay before attempting to claim a lock (helps avoid transient grabs).
    [SerializeField] float lockDelay = 0.12f;

    void Awake()
    {
        myOwner = SystemInfo.deviceUniqueIdentifier;

        grabbable = GetComponent<Grabbable>();
        h = GetComponent<IdHolder>();

        if (!grabbable) Debug.LogError($"[PickupLockHook] Missing Grabbable on {name}");
        if (!h) Debug.LogError($"[PickupLockHook] Missing IdHolder on {name}");

        // Subscribe to pointer events to detect select/unselect (grab/release).
        grabbable.WhenPointerEventRaised += OnPointerEvent;
    }

    void OnDisable()
    {
        // If this GameObject is disabled while owning a lock, release it
        // to avoid leaving stale locks in the cloud.
        if (iOwnLock && Cloud_Persistence.I && h && !string.IsNullOrEmpty(h.id))
        {
            StartCoroutine(ReleaseLockOnly());
        }
    }

    void OnDestroy()
    {
        if (grabbable) grabbable.WhenPointerEventRaised -= OnPointerEvent;
    }

    void OnPointerEvent(PointerEvent evt)
    {
        // Debug.Log($"[PickupLockHook] evt={evt.Type} on {name}");

        if (!Cloud_Persistence.I) return;
        if (!h) return;
        if (string.IsNullOrEmpty(h.id)) return;

        if (evt.Type == PointerEventType.Select)
        {
            // Pointer selected (begin grab). Start delayed lock attempt.
            isSelected = true;

            // If a previous routine exists, stop it then start a fresh one.
            if (lockRoutine != null) StopCoroutine(lockRoutine);
            lockRoutine = StartCoroutine(DelayedTryLock());
        }
        else if (evt.Type == PointerEventType.Unselect || evt.Type == PointerEventType.Cancel)
        {
            // Pointer released or cancelled. Cancel pending lock attempt and
            // release any owned lock while saving final position.
            isSelected = false;

            if (lockRoutine != null)
            {
                StopCoroutine(lockRoutine);
                lockRoutine = null;
            }

            StartCoroutine(ReleaseLockAndSave());
        }
    }

    IEnumerator DelayedTryLock()
    {
        yield return new WaitForSeconds(lockDelay);

        // se entretanto largou, não faz nada
        // If the pointer was released during the delay, abort.
        if (!isSelected) yield break;

        // If we already own the lock, nothing to do.
        if (iOwnLock) yield break;

        bool ok = false;
        // Try to acquire a simple lock entry in the cloud; callback returns success.
        yield return Cloud_Persistence.I.TryAcquireLock(LockKey(), myOwner, v => ok = v);

        if (!ok)
        {
            // Failed to acquire lock -> force immediate ungrab so the local user
            // doesn't hold an object they can't modify in the cloud.
            yield return ForceUngrabNow();
            yield break;
        }

        iOwnLock = true;
    }

    string Group()
    {
        if (h.isTomatoSeed) return "seed_tomato";
        if (h.isCarrotSeed) return "seed_carrot";
        if (h.isTomato) return "tomatoes";
        if (h.isCarrot) return "carrots";
        return "unknown";
    }

    string LockKey() => $"{Group()}:{h.id}";

    IEnumerator ReleaseLockAndSave()
    {
        if (!iOwnLock) yield break;

        // 1) Save final position to cloud (snap to terrain to avoid clipping).
        Vector3 p = transform.position;
        p = SnapToTerrain(p);

        if (h.isTomato) yield return Cloud_Persistence.I.AddTomato(h.id, p);
        else if (h.isCarrot) yield return Cloud_Persistence.I.AddCarrot(h.id, p);
        else if (h.isTomatoSeed) yield return Cloud_Persistence.I.AddTomatoSeed(h.id, p);
        else if (h.isCarrotSeed) yield return Cloud_Persistence.I.AddCarrotSeed(h.id, p);

        // 2) Remove lock entry from cloud.
        yield return Cloud_Persistence.I.ReleaseLock(LockKey());

        iOwnLock = false;
    }

    IEnumerator ReleaseLockOnly()
    {
        // Para casos em que o objeto é desativado e não queremos salvar posição
        yield return Cloud_Persistence.I.ReleaseLock(LockKey());
        iOwnLock = false;
    }

    IEnumerator ForceUngrabNow()
    {
        grabbable.enabled = false;
        yield return null;
        grabbable.enabled = true;
    }

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
}
