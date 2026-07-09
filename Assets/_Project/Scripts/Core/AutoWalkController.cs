using System.Collections;
using System.Collections.Generic;
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
/// Path selection — for scenes visited at ONLY one checkpoint (Circulatory,
/// Respiratory, Digestive, Summit), just fill in `waypoints` and ignore
/// `checkpointPaths` entirely; that's the whole setup.
///
/// For a scene like _MountainTrail that's revisited at several trail
/// checkpoints (each landing the player further up the mountain, per
/// PlayerSpawnPoint/PlayerEndPoint's own checkpoint numbering), add one
/// `checkpointPaths` entry per checkpoint instead: its own ordered chain of
/// intermediate waypoints ending with that checkpoint's own PlayerEndPoint
/// dragged in as the last element. BeginAutoWalk() picks whichever chain
/// matches MiniGameSequencer.Instance.TrailCheckpointIndex at the moment
/// it's called; `waypoints` is only used as a fallback if no entry matches.
///
/// Reports overall progress (0-1 across the whole waypoint chain) to
/// GameManager as it walks, so the HUD progress bar moves smoothly instead
/// of jumping at scene boundaries.
/// </summary>
public class AutoWalkController : MonoBehaviour
{
    [System.Serializable]
    public class CheckpointPath
    {
        [Tooltip("Which MiniGameSequencer.TrailCheckpointIndex this path is for.")]
        public int checkpointIndex;

        [Tooltip("Ordered waypoints for this checkpoint's segment. Drag that checkpoint's own " +
                 "PlayerEndPoint in as the LAST element, same as the single-path case below.")]
        public Transform[] waypoints;
    }

    [Header("References")]
    [Tooltip("The XR rig root (XR Origin or equivalent) that should move. " +
             "Leave empty — the rig lives in the persistent _Bootstrap scene, so it " +
             "can't be assigned here via the Inspector. It's auto-resolved at runtime " +
             "from PlayerRigPositioner.rigTransform instead. Only fill this in manually " +
             "if you're testing this scene in isolation with a local rig stand-in.")]
    public Transform rigToMove;

    [Header("Path")]
    [Tooltip("One entry per trail checkpoint, for scenes revisited at several checkpoints " +
             "(e.g. _MountainTrail). Leave empty if this scene only ever walks one segment — " +
             "'Waypoints' below covers that case on its own.")]
    public List<CheckpointPath> checkpointPaths = new List<CheckpointPath>();

    [Tooltip("Fallback path used when no entry in Checkpoint Paths matches the current checkpoint " +
             "(or when Checkpoint Paths is left empty entirely — the common case for scenes with " +
             "only one walk segment). Horizontal direction only — see Ground Following below for " +
             "how height/slopes are handled.")]
    public Transform[] waypoints;
    public float moveSpeed = 1.5f;
    public float rotationSpeed = 4f;
    public float arrivalThreshold = 0.15f;

    [Header("Ground Following")]
    [Tooltip("XR Origin's CharacterController, auto-resolved from rigToMove if left empty. " +
             "Movement is sent through CharacterController.Move() rather than set directly on the " +
             "Transform, so Unity's own capsule-vs-terrain collision sweep is what follows the slope " +
             "— the same mechanism that handles it for normal player movement. No raycasting involved.")]
    public CharacterController characterController;

    [Tooltip("Small constant downward speed fed into every Move() call so the capsule stays pressed " +
             "against the ground (including on a downhill slope, where horizontal motion alone would " +
             "drift the rig above the surface). Not real gravity/falling — just enough to keep contact.")]
    public float groundedPushSpeed = 2f;

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
    private Transform[] _activeWaypoints;

    // ── Public API ────────────────────────────────────────────────────────────

    public void BeginAutoWalk()
    {
        ResolveRigIfNeeded();

        _activeWaypoints = ResolveActiveWaypoints();

        if (rigToMove == null || _activeWaypoints == null || _activeWaypoints.Length == 0)
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

    // ── Path resolution ───────────────────────────────────────────────────────

    /// <summary>
    /// Picks the waypoint chain for the current trail checkpoint. Falls back
    /// to the flat `waypoints` field if checkpointPaths is empty or has no
    /// entry for the current checkpoint — this is what keeps single-segment
    /// scenes (Circulatory, Respiratory, Digestive, Summit) simple: they never
    /// need to touch checkpointPaths at all.
    /// </summary>
    private Transform[] ResolveActiveWaypoints()
    {
        int checkpoint = MiniGameSequencer.Instance.TrailCheckpointIndex;

        foreach (var path in checkpointPaths)
        {
            if (path.checkpointIndex == checkpoint)
            {
                if (path.waypoints == null || path.waypoints.Length == 0)
                {
                    Debug.LogWarning($"[AutoWalkController] Checkpoint Paths entry for checkpoint " +
                                      $"{checkpoint} has no waypoints assigned — falling back to Waypoints.");
                    break;
                }
                if (verbose)
                    Debug.Log($"[AutoWalkController] Using Checkpoint Paths entry for checkpoint {checkpoint}.");
                return path.waypoints;
            }
        }

        return waypoints;
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
        if (rigToMove == null)
        {
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

        if (characterController == null && rigToMove != null)
        {
            characterController = rigToMove.GetComponent<CharacterController>();
            if (characterController == null && verbose)
            {
                Debug.LogWarning("[AutoWalkController] No CharacterController found on rigToMove — " +
                                  "falling back to setting Transform.position directly, which won't " +
                                  "follow slopes. Add a CharacterController to the XR Origin for proper " +
                                  "terrain-following movement.");
            }
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

        for (int i = 0; i < _activeWaypoints.Length; i++)
        {
            Transform target = _activeWaypoints[i];

            while (FlatDistance(rigToMove.position, target.position) > arrivalThreshold)
            {
                Vector3 direction = FlatDirection(rigToMove.position, target.position);

                if (direction.sqrMagnitude > 0.0001f)
                {
                    Quaternion targetRotation = Quaternion.LookRotation(direction, Vector3.up);
                    rigToMove.rotation = Quaternion.Slerp(rigToMove.rotation, targetRotation, rotationSpeed * Time.deltaTime);
                }

                float step = moveSpeed * Time.deltaTime;
                Vector3 motion = direction * step;

                if (characterController != null && characterController.enabled)
                {
                    // CharacterController.Move() sweeps the capsule against the terrain
                    // collider itself and slides along whatever slope it hits — the small
                    // constant downward component keeps the capsule pressed against the
                    // ground (including on downhill stretches) so it doesn't hover.
                    motion.y = -groundedPushSpeed * Time.deltaTime;
                    characterController.Move(motion);
                }
                else
                {
                    // No CharacterController to sweep collision for us — best we can do is
                    // move on the flat plane and leave height alone. Fine for flat ground,
                    // not for a slope; the warning in ResolveRigIfNeeded already flags this.
                    rigToMove.position += motion;
                }

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
        if (_activeWaypoints.Length == 0) return 0f;

        float length = FlatDistance(rigToMove.position, _activeWaypoints[0].position);
        for (int i = 0; i < _activeWaypoints.Length - 1; i++)
            length += FlatDistance(_activeWaypoints[i].position, _activeWaypoints[i + 1].position);

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
        DrawChain(waypoints, Color.cyan);

        if (checkpointPaths != null)
        {
            Gizmos.color = Color.magenta;
            foreach (var path in checkpointPaths)
                DrawChain(path.waypoints, Color.magenta);
        }
    }

    private static void DrawChain(Transform[] chain, Color color)
    {
        if (chain == null || chain.Length == 0) return;

        Gizmos.color = color;
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i] == null) continue;
            Gizmos.DrawSphere(chain[i].position, 0.2f);
            if (i > 0 && chain[i - 1] != null)
                Gizmos.DrawLine(chain[i - 1].position, chain[i].position);
        }
    }
#endif
}