using UnityEngine;

public class Plant_Harvest : MonoBehaviour
{
    // --------------------------------------------------------------------
    // TRIGGER (hand touches the plant)
    // --------------------------------------------------------------------
    void OnTriggerEnter(Collider other)
    {
        // Only react to hand colliders
        if (other.gameObject.layer != LayerMask.NameToLayer("Hands"))
            return;

        // Find the plot script on the parent (the plant is a child of the plot)
        var plot = GetComponentInParent<Dirt_Plot>();
        if (!plot) return;

        // Ask the plot to harvest (plot checks if it is the correct stage)
        plot.TryHarvest();
    }
}
