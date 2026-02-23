using UnityEngine;

public class Water_Collision : MonoBehaviour
{
    // Amount of water applied to a plot per particle collision.
    [SerializeField] float waterPerHit = 0.5f;

    // Name of the layer used to detect soil collisions.
    [SerializeField] string soilLayerName = "Soil";
    int soilLayer;

    void Awake() => soilLayer = LayerMask.NameToLayer(soilLayerName);

    // Called by Unity when a particle system collides with a GameObject.
    // Expects the collided `other` to be (or be a child of) a `Dirt_Plot`.
    private void OnParticleCollision(GameObject other)
    {
        // Ignore collisions that are not with the configured soil layer.
        if (other.layer != soilLayer) return;

        // Find the parent Dirt_Plot and add water to it.
        var plot = other.GetComponentInParent<Dirt_Plot>();
        if (!plot) return;

        plot.AddWater(waterPerHit);
    }
}
