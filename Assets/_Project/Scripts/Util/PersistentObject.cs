using UnityEngine;

/// <summary>
/// Makes this GameObject persist across scene loads, exactly like GameManager,
/// SceneTransitionManager, and the AI guide already do — and guards against
/// ending up with two of it active at once.
///
/// Use this on the XR Origin (XR Rig) root and on XR Interaction Manager,
/// both moved into _Bootstrap. Once tagged persistent here, other _Bootstrap
/// scripts (like PlayerRigPositioner) can hold a normal, permanent Inspector
/// reference to this same object, since it never gets destroyed or replaced.
///
/// Duplicate guard: if you ever Play directly from a scene that still has
/// its own leftover copy of this object (e.g. testing MountainTrail on its
/// own without going through _Bootstrap first), this destroys whichever
/// instance ISN'T the persistent one — so you never end up with two XR
/// Origins / two Interaction Managers processing input at the same time.
/// </summary>
public class PersistentObject : MonoBehaviour
{
    [Tooltip("Label used in the duplicate-destroyed warning log, purely for readability (e.g. 'XR Origin', 'XR Interaction Manager').")]
    public string debugLabel = "Persistent Object";

    private static readonly System.Collections.Generic.Dictionary<string, GameObject> _persistentByLabel
        = new System.Collections.Generic.Dictionary<string, GameObject>();

    private void Awake()
    {
        if (_persistentByLabel.TryGetValue(debugLabel, out GameObject existing) && existing != gameObject)
        {
            Debug.LogWarning($"[PersistentObject] Duplicate '{debugLabel}' found on '{gameObject.name}' — " +
                              $"destroying it. The persistent one from -1_Bootstrap wins.");
            Destroy(gameObject);
            return;
        }

        _persistentByLabel[debugLabel] = gameObject;
        DontDestroyOnLoad(gameObject);
    }
}