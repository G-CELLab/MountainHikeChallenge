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
/// the summit). If startDelaySeconds is set, the rig waits that long after
/// BeginAutoWalk() is called before actually moving — useful for letting a
/// scene/guide intro or narration beat finish before the walk kicks off.
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

        [Header("Mountain Progress Reporting (this segment only)")]
        [Tooltip("This segment's own slice of the overall 0-1 climb — e.g. checkpoint 0 might be " +
                 "0.0-0.2, checkpoint 1 might be 0.2-0.4, and so on up to 1.0 at the summit. Each " +
                 "checkpoint needs ITS OWN range here; the component-level Progress Range Start/End " +
                 "fields further down are only used as a fallback for the flat 'Waypoints' list " +
                 "(single-segment scenes), not for anything in this list.")]
        [Range(0f, 1f)] public float progressRangeStart = 0f;
        [Range(0f, 1f)] public float progressRangeEnd = 1f;

        [Header("Required Systems (this segment only)")]
        [Tooltip("If set, this segment's walk won't actually start moving until every system listed " +
                 "here is marked complete on GameManager — same idea as the component-level Required " +
                 "Systems Before Walk field further down, but scoped to THIS checkpoint only. Each " +
                 "checkpoint needs its OWN requirement here (e.g. checkpoint 1's walk — the one " +
                 "leaving Skeletal — should require Skeletal, not whatever system comes next; " +
                 "requiring a system that hasn't happened yet deadlocks this segment forever). The " +
                 "component-level field below is only consulted as a fallback for the flat " +
                 "'Waypoints' single-segment case, never for a checkpointPaths entry.")]
        public List<BodySystem> requiredSystemsBeforeWalk = new List<BodySystem>();
    }

    [Header("References")]
    [Tooltip("The XR rig root (XR Origin or equivalent) that should move. " +
             "Leave empty — the rig lives in the persistent _Bootstrap scene, so it " +
             "can't be assigned here via the Inspector. It's auto-resolved at runtime " +
             "from PlayerRigPositioner.rigTransform instead. Only fill this in manually " +
             "if you're testing this scene in isolation with a local rig stand-in.")]
    public Transform rigToMove;

    [Header("Start Delay")]
    [Tooltip("Seconds to wait after BeginAutoWalk() is called before the rig actually starts " +
             "moving. Lets a scene intro, guide spawn animation, or narration beat finish first. " +
             "0 = start immediately, same as before.")]
    public float startDelaySeconds = 0f;

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

    [Tooltip("FALLBACK ONLY — used when no checkpointPaths entry matched the current checkpoint (the " +
             "flat 'Waypoints' single-segment case above). For a multi-checkpoint scene, set each " +
             "checkpoint's own requirement on its CheckpointPath entry instead — this list is NOT " +
             "shared across checkpoints, so putting a value here has no effect once checkpointPaths " +
             "has a matching entry for the current checkpoint. If set, BeginAutoWalk() can still be " +
             "called right away (e.g. on scene load or from a trigger volume), but actual movement " +
             "won't start until every system listed here is marked complete on GameManager. Leave " +
             "empty for a walk that should start as soon as BeginAutoWalk() is called.")]
    public List<BodySystem> requiredSystemsBeforeWalk = new List<BodySystem>();

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
    [Tooltip("If set, walking this path updates GameManager's mountain progress across its own range " +
             "for this segment — see each CheckpointPath entry's own Progress Range Start/End above " +
             "when checkpointPaths is in use. The Progress Range Start/End fields directly below are " +
             "only consulted as a fallback when no checkpointPaths entry matched (i.e. the flat " +
             "'Waypoints' single-segment case).")]
    public bool reportProgressToGameManager = true;
    [Tooltip("Fallback range, used only when checkpointPaths has no entry for the current checkpoint " +
             "(so the flat Waypoints field above is what's actually walking).")]
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
    private CheckpointPath _activeCheckpointPath;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Read-only check: would BeginAutoWalk() actually walk anywhere right now,
    /// for the CURRENT trail checkpoint? True only if there's a non-empty
    /// waypoint chain for this checkpoint AND (if that checkpoint has any
    /// requiredSystemsBeforeWalk) every one of those systems is already
    /// complete. Doesn't start anything or touch any internal state — this
    /// exists so SceneFlowController can decide whether to wait for
    /// OnWalkCompleted without guessing or needing a manually-set flag.
    ///
    /// Checking required systems synchronously (rather than waiting) works
    /// because a checkpoint's requirement is always something that finishes
    /// BEFORE the player ever arrives back at that checkpoint (that's the
    /// whole point of the gate) — so by the time this scene has loaded and
    /// SceneFlowController is asking, the answer is already knowable. A
    /// checkpoint whose requirement can't possibly be done yet (e.g.
    /// checkpoint 0 requiring Nervous+Skeletal, which haven't happened on
    /// this first visit) correctly reports false — no walk to wait for.
    /// </summary>
    public bool WillWalkAtCurrentCheckpoint()
    {
        int checkpoint = MiniGameSequencer.Instance.TrailCheckpointIndex;

        Transform[] activeWaypoints = null;
        List<BodySystem> requiredSystems = null;

        foreach (var path in checkpointPaths)
        {
            if (path.checkpointIndex == checkpoint)
            {
                activeWaypoints = path.waypoints;
                requiredSystems = path.requiredSystemsBeforeWalk;
                break;
            }
        }

        // Fallback to the flat single-segment case if no checkpointPaths entry matched.
        if (activeWaypoints == null)
        {
            activeWaypoints = waypoints;
            requiredSystems = requiredSystemsBeforeWalk;
        }

        if (activeWaypoints == null || activeWaypoints.Length == 0) return false;

        if (requiredSystems != null && requiredSystems.Count > 0)
        {
            if (GameManager.Instance == null) return false;
            foreach (var system in requiredSystems)
            {
                if (!GameManager.Instance.IsSystemComplete(system))
                    return false;
            }
        }

        return true;
    }

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
        _walkRoutine = StartCoroutine(DelayedWalkRoutine());
    }

    public void StopAutoWalk()
    {
        if (_walkRoutine != null) StopCoroutine(_walkRoutine);
        IsWalking = false;
    }

    // ── Start delay wrapper ───────────────────────────────────────────────────

    /// <summary>
    /// Waits startDelaySeconds (if any) before handing off to the real
    /// WalkRoutine(). Kept as a thin wrapper so WalkRoutine() itself stays
    /// focused purely on movement — the delay is a separate concern.
    /// </summary>
    private IEnumerator DelayedWalkRoutine()
    {
        if (startDelaySeconds > 0f)
        {
            if (verbose)
                Debug.Log($"[AutoWalkController] Waiting {startDelaySeconds:F1}s before starting walk.");
            yield return new WaitForSecondsRealtime(startDelaySeconds);
        }

        // Per-checkpoint requirement when a checkpointPaths entry matched (the
        // usual _MountainTrail case); the component-level list is only a
        // fallback for the flat single-segment Waypoints case.
        List<BodySystem> activeRequiredSystems =
            _activeCheckpointPath != null ? _activeCheckpointPath.requiredSystemsBeforeWalk : requiredSystemsBeforeWalk;

        if (activeRequiredSystems != null && activeRequiredSystems.Count > 0)
        {
            if (verbose)
                Debug.Log($"[AutoWalkController] Waiting for required systems before walking: " +
                          $"{string.Join(", ", activeRequiredSystems)}");

            yield return new WaitUntil(() => AllRequiredSystemsComplete(activeRequiredSystems));

            if (verbose)
                Debug.Log("[AutoWalkController] Required systems complete — proceeding with walk.");
        }

        yield return WalkRoutine();
    }

    private bool AllRequiredSystemsComplete(List<BodySystem> requiredSystems)
    {
        if (GameManager.Instance == null) return false;

        foreach (var system in requiredSystems)
        {
            if (!GameManager.Instance.IsSystemComplete(system))
                return false;
        }

        return true;
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
                _activeCheckpointPath = path;
                return path.waypoints;
            }
        }

        _activeCheckpointPath = null;
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

        // Per-checkpoint segment uses its own progressRangeStart/End; the flat
        // Waypoints fallback case (no matching checkpointPaths entry) uses the
        // component-level fields instead. Resolving both into local floats here
        // means the rest of this routine doesn't need to care which case it's in.
        float rangeStart = _activeCheckpointPath != null ? _activeCheckpointPath.progressRangeStart : progressRangeStart;
        float rangeEnd = _activeCheckpointPath != null ? _activeCheckpointPath.progressRangeEnd : progressRangeEnd;

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
                    float globalProgress = Mathf.Lerp(rangeStart, rangeEnd, localProgress);
                    GameManager.Instance.SetMountainProgress(globalProgress);
                }

                yield return null;
            }

            OnWaypointReached?.Invoke(i);
            if (verbose) Debug.Log($"[AutoWalkController] Reached waypoint {i} ({target.name})");
        }

        if (reportProgressToGameManager && GameManager.Instance != null)
            GameManager.Instance.SetMountainProgress(rangeEnd);

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