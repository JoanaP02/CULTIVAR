using UnityEngine;
using System;
using System.Collections;

// Handles objects entering a basket trigger and swaps harvested plants
// for their corresponding seeds in cloud storage.
public class BasketTrigger : MonoBehaviour
{
    public enum BasketType { Tomato, Carrot }

    [Header("Basket Settings")]
    // Which kind of plant this basket accepts.
    public BasketType basketType = BasketType.Tomato;
    // Optional transform where the seed will be spawned; if null, uses the object's position.
    public Transform spawnPoint;

    private void OnTriggerEnter(Collider other)
    {
        var idh = other.GetComponent<IdHolder>();
        if (idh == null) return;

        // Only accept the matching plant type for this basket.
        if (basketType == BasketType.Tomato)
        {
            if (!idh.isTomato) return;
            // Begin coroutine to replace the tomato with a tomato seed.
            StartCoroutine(DoSwapTomatoToSeed(other.gameObject, idh.id));
        }
        else // Carrot
        {
            if (!idh.isCarrot) return;
            // Begin coroutine to replace the carrot with a carrot seed.
            StartCoroutine(DoSwapCarrotToSeed(other.gameObject, idh.id));
        }
    }

    IEnumerator DoSwapTomatoToSeed(GameObject tomatoGo, string tomatoId)
    {
        // Determine where to place the new seed (spawnPoint if provided).
        Vector3 pos = spawnPoint ? spawnPoint.position : tomatoGo.transform.position;

        // Remove the tomato from cloud persistence first.
        yield return Cloud_Persistence.I.DeleteTomato(tomatoId);

        // Remove the local GameObject from the scene.
        Destroy(tomatoGo);

        // Create a new id for the seed and add it to cloud persistence at `pos`.
        string newId = Guid.NewGuid().ToString();
        yield return Cloud_Persistence.I.AddTomatoSeed(newId, pos);
    }

    IEnumerator DoSwapCarrotToSeed(GameObject carrotGo, string carrotId)
    {
        // Determine where to place the new seed (spawnPoint if provided).
        Vector3 pos = spawnPoint ? spawnPoint.position : carrotGo.transform.position;

        // Remove the carrot from cloud persistence first.
        yield return Cloud_Persistence.I.DeleteCarrot(carrotId);

        // Remove the local GameObject from the scene.
        Destroy(carrotGo);

        // Create a new id for the seed and add it to cloud persistence at `pos`.
        string newId = Guid.NewGuid().ToString();
        yield return Cloud_Persistence.I.AddCarrotSeed(newId, pos);
    }
}
