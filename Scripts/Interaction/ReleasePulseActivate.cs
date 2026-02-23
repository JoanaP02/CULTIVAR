using UnityEngine;
using Oculus.Interaction;

// Emits a one-frame pulse when the source ActiveState transitions from
// true -> false (button release). Useful for triggering actions on release
// rather than while pressed.
public class ReleasePulseActiveState : MonoBehaviour, IActiveState
{
    // Drag an ActiveState provider here (e.g. an OVRButtonActiveState).
    [SerializeField] private MonoBehaviour _sourceActiveState;
    private IActiveState _source;

    // Previous frame value of the source Active flag.
    private bool _prev;

    // True for one frame when a falling edge (release) is detected.
    private bool _pulse;

    // IActiveState implementation: returns true only on the release frame.
    public bool Active => _pulse;

    private void Awake()
    {
        _source = _sourceActiveState as IActiveState;
        if (_source == null)
        {
            Debug.LogError($"{name}: Source ActiveState invalid. It must implement IActiveState.");
        }
    }

    private void Update()
    {
        if (_source == null) return;

        bool cur = _source.Active;

        // Detect falling edge: previously true, now false -> a release occurred.
        _pulse = (_prev && !cur);

        if (_pulse) Debug.Log("RELEASE PULSE!");

        // Store current for next frame comparison.
        _prev = cur;
    }
}
