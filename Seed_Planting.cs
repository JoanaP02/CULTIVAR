using UnityEngine;

public class Seed_Planting : MonoBehaviour
{
    // Which plant type this seed will create when planted.
    [SerializeField] Dirt_Plot.PlantType plantType;

    // Name of the physics layer considered 'soil' for planting collisions.
    [SerializeField] string soilLayerName = "Soil";

    // Resolved soil layer index and whether this seed has already been used.
    int soilLayer;
    bool used;

    // Local components cached for disabling physics and colliders after planting.
    Rigidbody rb;
    Collider[] cols;
    IdHolder h;

    void Awake()
    {
        soilLayer = LayerMask.NameToLayer(soilLayerName);
        rb = GetComponent<Rigidbody>();
        cols = GetComponentsInChildren<Collider>(true);
        h = GetComponent<IdHolder>();
    }

    void OnCollisionEnter(Collision c)
    {
        // Ignore if already planted or collision not with soil layer.
        if (used) return;
        if (c.collider.gameObject.layer != soilLayer) return;

        // Find the parent `Dirt_Plot` and attempt to plant.
        var plot = c.collider.GetComponentInParent<Dirt_Plot>();
        if (!plot) return;

        // `OnSeedContact` returns true if planting succeeded (plot was empty).
        if (!plot.OnSeedContact(plantType)) return;

        // From this point the seed was consumed by the plot.
        used = true;

        // Disable physics/collisions immediately to avoid interacting with other plots.
        if (rb)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            rb.detectCollisions = false;
        }
        if (cols != null)
        {
            foreach (var col in cols) col.enabled = false;
        }

        // Remove the seed entry from cloud persistence (source of truth).
        if (Cloud_Persistence.I && h && !string.IsNullOrEmpty(h.id))
        {
            if (h.isTomatoSeed) Cloud_Persistence.I.StartCoroutine(Cloud_Persistence.I.DeleteTomatoSeed(h.id));
            else if (h.isCarrotSeed) Cloud_Persistence.I.StartCoroutine(Cloud_Persistence.I.DeleteCarrotSeed(h.id));
        }

        // Destroy local seed GameObject now that planting is done.
        Destroy(gameObject);
    }
}
