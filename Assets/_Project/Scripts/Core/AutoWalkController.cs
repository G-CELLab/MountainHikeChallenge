using System.Collections;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Waypoint-based auto-walk for the XR rig along the mountain trail.
///
/// Drop an empty GameObject with this script on it into MountainTrail.unity,
/// assign a chain of empty Transforms as waypoints, and either drag the XR
/// rig's root transform into rigToMove OR leave it empty — since the rig
/// lives in the persistent _Bootstrap scene (a different scene file than
/// this component), it can't actually be wired via drag-and-drop here;
/// leaving it null lets this auto-resolve it at runtime via
/// PlayerRigPositioner, which DOES hold a valid reference (it lives
/// alongside the rig in _Bootstrap).
///
/// Call BeginAutoWalk() from a trigger volume, a button, or
/// SceneTransitionManager to walk the student hands-off through a trail
/// segment (e.g. the easy walk before the incline, or the final stretch to
/// the summit).
///
/// Reports overall progress (0-1 across the whole waypoint chain) to
/// GameManager as it walks, so the HUD progress bar moves smoothly instead
/// of jumping at scene boundaries.
/// </summary>
public class AutoWalkController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The XR rig root (XR Origin or equivalent) that should move. " +
             "Leave empty — the rig lives in the persistent _Bootstrap scene, so it " +
             "can't be assigned here via the Inspector. It's auto-resolved at runtime " +
             "from PlayerRigPositioner.rigTransform instead. Only fill this in manually " +
             "if you're testing this scene in isolation with a local rig stand-in.")]
    public Transform rigToMove;

    [Header("Path")]
    [Tooltip("Ordered waypoints. Y is ignored for movement/arrival checks — assumes terrain height is handled separately (e.g. CharacterController + gravity).")]
    public Transform[] waypoints;
    public float moveSpeed = 1.5f;
    public float rotationSpeed = 4f;
    public float arrivalThreshold = 0.15f;

    [Header("Mountain Progress Reporting")]
    [Tooltip("If set, walking this path updates GameManager's mountain progress across this sub-range (e.g. 0.0-0.4 for the first trail segment).")]
    public bool reportProgressToGameManager = true;
    [Range(0f, 1f)] public float progressRangeStart = 0f;
    [Range(0f, 1f)] public float progressRangeEnd = 1f;

    [Header("Events")]
    public UnityEvent OnWalkStarted;
    public UnityEvent OnWalkCompleted;
    public UnityEvent<int> OnWaypointReached;

    [Header("Debug")]
    public bool verbose = true;

    public bool IsWalking { get; private set; }

    private Coroutine _walkRoutine;
    private float _totalPathLength;

    // ── Public API ────────────────────────────────────────────────────────────

    public void BeginAutoWalk()
    {
        ResolveRigIfNeeded();

        if (rigToMove == null || waypoints == null || waypoints.Length == 0)
        {
            Debug.LogWarning("[AutoWalkController] Missing rig or waypoints — cannot start auto-walk.");
            return;
        }

        if (_walkRoutine != null) StopCoroutine(_walkRoutine);
        _walkRoutine = StartCoroutine(WalkRoutine());
    }

    public void StopAutoWalk()
    {
        if (_walkRoutine != null) StopCoroutine(_walkRoutine);
        IsWalking = false;
    }

    // ── Rig resolution ────────────────────────────────────────────────────────

    /// <summary>
    /// The XR rig lives in the persistent _Bootstrap scene, which isn't the
    /// same scene file as this component, so it can't be wired via the
    /// Inspector here. PlayerRigPositioner DOES hold a valid reference to it
    /// (it lives alongside the rig in _Bootstrap), so borrow that instead of
    /// requiring a second, impossible-to-set-up reference on this script.
    /// </summary>
    private void ResolveRigIfNeeded()
    {
        if (rigToMove != null) return;

        var positioner = FindAnyObjectByType<PlayerRigPositioner>();
        if (positioner != null && positioner.rigTransform != null)
        {
            rigToMove = positioner.rigTransform;
            if (verbose) Debug.Log("[AutoWalkController] Resolved rigToMove from PlayerRigPositioner.");
        }
        else if (verbose)
        {
            Debug.LogWarning("[AutoWalkController] Could not resolve rigToMove — no PlayerRigPositioner " +
                              "found, or its rigTransform is unassigned.");
        }
    }

    // ── Walk routine ──────────────────────────────────────────────────────────

    private IEnumerator WalkRoutine()
    {
        IsWalking = true;
        OnWalkStarted?.Invoke();
        if (verbose) Debug.Log("[AutoWalkController] 🥾 Auto-walk started.");

        _totalPathLength = ComputeTotalPathLength();
        float distanceCovered = 0f;

        for (int i = 0; i < waypoints.Length; i++)
        {
            Transform target = waypoints[i];

            while (FlatDistance(rigToMove.position, target.position) > arrivalThreshold)
            {
                Vector3 direction = FlatDirection(rigToMove.position, target.position);

                if (direction.sqrMagnitude > 0.0001f)
                {
                    Quaternion targetRotation = Quaternion.LookRotation(direction, Vector3.up);
                    rigToMove.rotation = Quaternion.Slerp(rigToMove.rotation, targetRotation, rotationSpeed * Time.deltaTime);
                }

                float step = moveSpeed * Time.deltaTime;
                rigToMove.position += direction * step;
                distanceCovered += step;

                if (reportProgressToGameManager && GameManager.Instance != null && _totalPathLength > 0f)
                {
                    float localProgress = Mathf.Clamp01(distanceCovered / _totalPathLength);
                    float globalProgress = Mathf.Lerp(progressRangeStart, progressRangeEnd, localProgress);
                    GameManager.Instance.SetMountainProgress(globalProgress);
                }

                yield return null;
            }

            OnWaypointReached?.Invoke(i);
            if (verbose) Debug.Log($"[AutoWalkController] Reached waypoint {i} ({target.name})");
        }

        if (reportProgressToGameManager && GameManager.Instance != null)
            GameManager.Instance.SetMountainProgress(progressRangeEnd);

        IsWalking = false;
        OnWalkCompleted?.Invoke();
        if (verbose) Debug.Log("[AutoWalkController] ✅ Auto-walk completed.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private float ComputeTotalPathLength()
    {
        if (waypoints.Length == 0) return 0f;

        float length = FlatDistance(rigToMove.position, waypoints[0].position);
        for (int i = 0; i < waypoints.Length - 1; i++)
            length += FlatDistance(waypoints[i].position, waypoints[i + 1].position);

        return length;
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static Vector3 FlatDirection(Vector3 from, Vector3 to)
    {
        from.y = 0f;
        to.y = 0f;
        return (to - from).normalized;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (waypoints == null || waypoints.Length == 0) return;

        Gizmos.color = Color.cyan;
        for (int i = 0; i < waypoints.Length; i++)
        {
            if (waypoints[i] == null) continue;
            Gizmos.DrawSphere(waypoints[i].position, 0.2f);
            if (i > 0 && waypoints[i - 1] != null)
                Gizmos.DrawLine(waypoints[i - 1].position, waypoints[i].position);
        }
    }
#endif
}