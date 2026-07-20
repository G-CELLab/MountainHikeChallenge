using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Reusable "light travels along the spine" effect. Spawns a copy of
/// pulsePrefab (a small glowing orb — ideally with a TrailRenderer and/or
/// Light for the streak look) and moves it through a chain of world-space
/// points at a constant speed, then destroys it and reports back.
///
/// Each call spawns its own instance, so firing several pulses back-to-back
/// (a student touching neurons quickly) doesn't cancel or interrupt earlier
/// ones — they all travel independently.
/// </summary>
public class SpineSignalPulse : MonoBehaviour
{
    [Tooltip("Small glowing object that travels along the path — ideally has a TrailRenderer and/or Light for the streak look.")]
    [SerializeField] private GameObject pulsePrefab;

    [Tooltip("Default units/second the pulse travels, used when FirePulse doesn't override it.")]
    [SerializeField] private float defaultSpeed = 4f;

    /// <summary>
    /// Moves a pulse instance through the given world-space path.
    /// scaleMultiplier lets the final "everything at once" pulse look bigger
    /// and brighter than the small per-neuron ones without needing a second prefab.
    /// onArrive fires the moment the pulse reaches the last point, right
    /// before the instance is destroyed — good place to hook completion logic
    /// so it lines up with the visual rather than firing instantly.
    /// </summary>
    public void FirePulse(Vector3[] path, float? speedOverride = null, float scaleMultiplier = 1f, Action onArrive = null)
    {
        if (pulsePrefab == null)
        {
            Debug.LogWarning("[SpineSignalPulse] No pulsePrefab assigned — skipping the visual, but still calling onArrive so gameplay isn't blocked.");
            onArrive?.Invoke();
            return;
        }

        if (path == null || path.Length < 2)
        {
            onArrive?.Invoke();
            return;
        }

        StartCoroutine(TravelRoutine(path, speedOverride ?? defaultSpeed, scaleMultiplier, onArrive));
    }

    private IEnumerator TravelRoutine(Vector3[] path, float speed, float scaleMultiplier, Action onArrive)
    {
        var pulse = Instantiate(pulsePrefab, path[0], Quaternion.identity);
        pulse.transform.localScale *= scaleMultiplier;

        for (int i = 0; i < path.Length - 1; i++)
        {
            Vector3 from = path[i];
            Vector3 to = path[i + 1];
            float segmentLength = Vector3.Distance(from, to);
            float duration = segmentLength / Mathf.Max(0.01f, speed);
            float t = 0f;

            while (t < duration)
            {
                t += Time.deltaTime;
                pulse.transform.position = Vector3.Lerp(from, to, Mathf.Clamp01(t / duration));
                yield return null;
            }
            pulse.transform.position = to;
        }

        onArrive?.Invoke();
        Destroy(pulse);
    }
}