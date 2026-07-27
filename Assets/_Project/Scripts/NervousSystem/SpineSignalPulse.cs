using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Reusable "light travels along the spine" effect. Spawns a copy of
/// pulsePrefab (a small glowing orb — ideally with a TrailRenderer and/or
/// Light for the streak look) and moves it along a path, then destroys it
/// and reports back.
///
/// Two ways to define the path:
///   1. FirePulse(Vector3[] path, ...) — original API, raw world-space
///      points. Kept as-is for any existing callers.
///   2. FirePulse(Transform[] waypoints, ...) — NEW, recommended for the
///      full-body nervous system model. The model isn't rigged (no bones
///      to sample automatically), so instead: hand-place a handful of
///      empty GameObjects as children of the spine mesh, following its
///      actual curve from neck to tailbone in the Scene view, and pass
///      them in here. Because they're parented under the spine, they
///      move/scale with it automatically — no re-tuning if you nudge the
///      model later. See SpineWaypointPath.cs for a small helper that
///      draws gizmo lines between them so placement is easy to check.
///
/// Both paths are smoothed through a Catmull-Rom spline (see
/// useSplineSmoothing) so the pulse hugs the spine's actual curve instead
/// of cutting straight lines between a handful of waypoints — this matters
/// far more on the new asset's realistic S-curve than it did on the old
/// simple placeholder spine.
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

    [Header("Curve Smoothing (new full-body model)")]
    [Tooltip("Smooth the path through a Catmull-Rom spline instead of straight-line segments between waypoints. Strongly recommended for the new model's curved spine — only turn off if you're feeding in an already-dense/smooth path.")]
    [SerializeField] private bool useSplineSmoothing = true;

    [Tooltip("How many points to sample along the spline per segment between waypoints. Higher = smoother curve, slightly more expensive. 10-20 is plenty for a spine with 4-8 waypoints.")]
    [SerializeField] private int splineSamplesPerSegment = 12;

    /// <summary>
    /// Original API: moves a pulse instance through raw world-space points.
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

        Vector3[] finalPath = useSplineSmoothing ? BuildSpline(path) : path;
        StartCoroutine(TravelRoutine(finalPath, speedOverride ?? defaultSpeed, scaleMultiplier, onArrive));
    }

    /// <summary>
    /// New API for the full-body nervous system model: pass in a handful of
    /// empty-GameObject markers placed by hand along the spine's actual
    /// curve (neck → tailbone). Reads their CURRENT world positions at fire
    /// time, so this stays correct even if the model gets repositioned,
    /// rescaled, or rotated later — as long as the markers are parented
    /// under it.
    /// </summary>
    public void FirePulse(Transform[] waypoints, float? speedOverride = null, float scaleMultiplier = 1f, Action onArrive = null)
    {
        if (waypoints == null || waypoints.Length < 2)
        {
            Debug.LogWarning("[SpineSignalPulse] Need at least 2 waypoints to fire a pulse.");
            onArrive?.Invoke();
            return;
        }

        var points = new Vector3[waypoints.Length];
        for (int i = 0; i < waypoints.Length; i++)
        {
            if (waypoints[i] == null)
            {
                Debug.LogWarning($"[SpineSignalPulse] Waypoint at index {i} is null — skipping this pulse.");
                onArrive?.Invoke();
                return;
            }
            points[i] = waypoints[i].position;
        }

        FirePulse(points, speedOverride, scaleMultiplier, onArrive);
    }

    /// <summary>
    /// Convenience overload: fire directly from a SpineWaypointPath component
    /// (see that file) instead of pulling its .Waypoints array out yourself.
    /// </summary>
    public void FirePulse(SpineWaypointPath path, float? speedOverride = null, float scaleMultiplier = 1f, Action onArrive = null)
    {
        if (path == null)
        {
            Debug.LogWarning("[SpineSignalPulse] SpineWaypointPath reference is null.");
            onArrive?.Invoke();
            return;
        }
        FirePulse(path.Waypoints, speedOverride, scaleMultiplier, onArrive);
    }

    /// <summary>
    /// Runs a set of control points through a clamped Catmull-Rom spline,
    /// returning a denser array of points that smoothly curves through all
    /// of the originals (rather than cutting straight lines between them).
    /// "Clamped" means the first/last control points are duplicated as
    /// phantom points so the curve doesn't overshoot past the actual ends
    /// of the spine — without that, Catmull-Rom can bow outward before the
    /// first point and after the last one.
    /// </summary>
    private Vector3[] BuildSpline(Vector3[] controlPoints)
    {
        if (controlPoints.Length < 3)
            return controlPoints; // nothing to smooth with only 2 points — a straight line is already correct

        var result = new List<Vector3>();
        int count = controlPoints.Length;

        for (int i = 0; i < count - 1; i++)
        {
            Vector3 p0 = controlPoints[Mathf.Max(i - 1, 0)];
            Vector3 p1 = controlPoints[i];
            Vector3 p2 = controlPoints[i + 1];
            Vector3 p3 = controlPoints[Mathf.Min(i + 2, count - 1)];

            // Every segment samples t in [0, 1) so its start point isn't
            // duplicated with the previous segment's end — except the very
            // last segment, which also includes t = 1 so the path actually
            // reaches the final waypoint.
            bool isLastSegment = i == count - 2;
            int samples = isLastSegment ? splineSamplesPerSegment + 1 : splineSamplesPerSegment;

            for (int s = 0; s < samples; s++)
            {
                float t = s / (float)splineSamplesPerSegment;
                result.Add(CatmullRom(p0, p1, p2, p3, t));
            }
        }

        return result.ToArray();
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;

        return 0.5f * (
            2f * p1 +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
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