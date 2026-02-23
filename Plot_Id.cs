using UnityEngine;

public class Plot_Id : MonoBehaviour
{
    public string Id
    {
        get
        {
            string parent = transform.parent != null ? transform.parent.name.ToLowerInvariant() : "root";
            string self = gameObject.name.ToLowerInvariant();
            return $"{parent}_{self}";
        }
    }
}
