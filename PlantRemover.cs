using UnityEngine;

public class PlantRemover : MonoBehaviour
{
    [SerializeField] string removalToolTag = "RemovalTool";
    bool used;

    private void OnTriggerEnter(Collider other)
    {
        if (used) return;
        if (!other.CompareTag(removalToolTag)) return;

        Dirt_Plot plot = GetComponentInParent<Dirt_Plot>();
        if (!plot) return;

        used = true;
        plot.RemoveIfAboveStage0();
    }
}
