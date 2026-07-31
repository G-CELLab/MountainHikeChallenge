using UnityEngine;

/// <summary>
/// Lives in the Skeletal mini-game scene (Scene 1 — Trailhead), facing a
/// single knee joint made of two pieces right now: TibiaFibula (static
/// anchor — held but never moves) and Femur (the piece the student swings
/// into place).
///
/// NOTE: Patella auto-follow support has been stripped out for now to keep
/// this simpler while the core drag/lock feel is being tuned. To bring it
/// back later: re-add a Patella transform + loose/locked pose fields, and
/// lerp its position/rotation between them based on
/// Mathf.InverseLerp(startAngleDegrees, lockedAngleDegrees, _currentAngle)
/// each Update — see project history for the exact prior implementation.
///
/// HINGE DESIGN — important: the femur is NOT free-floating. It is a child
/// of KneePivot, a transform sitting at the joint center. The only thing
/// this script ever changes is KneePivot's rotation around a single
/// authored hinge axis (the knee's flexion/extension axis) — so the femur
/// physically cannot translate away from the tibia, twist sideways, or end
/// up anywhere anatomically impossible. It can only swing between "bent"
/// (the scene's starting pose) and "straight" (the locked pose), same as a
/// real hinge joint.
///
/// Interaction flow:
///   - The student grips the tibia/fibula with one hand (it never moves —
///     holding it represents "stabilizing" it) and grips the femur with the
///     other hand. Which physical hand holds which bone doesn't matter — we
///     just require "some hand holding TibiaFibula" AND "a DIFFERENT hand
///     holding Femur" at the same time (see HandManager.isGrabbed /
///     GetHeldObjectName(), same pattern used elsewhere in the project).
///   - While the femur is held, the script measures the holding hand's
///     angular position around KneePivot (projected onto the hinge's plane
///     of rotation) and converts the DELTA in that angle, since the moment
///     of grab, into a change in the pivot's flexion angle — a standard
///     "drag a hinge" technique. This means wherever the student's hand
///     currently is when they grab, the joint starts responding smoothly
///     from its current angle rather than snapping to match the hand.
///     dragSensitivity scales that delta — see its tooltip if the hand and
///     bone don't feel like they're moving at matching speed, which is
///     common when the object's scale doesn't match a human arm's natural
///     swing radius.
///   - The lock check only runs while BOTH grips are active simultaneously
///     (or always, if debugSkipStationaryGrip is on for testing). Once the
///     pivot's angle is within tolerance of the locked angle (fully
///     straight), it snaps exactly to that angle, becomes non-interactable,
///     and the same MiniGameCompletionGate flow NervousSystemMiniGameController
///     uses fires.
/// </summary>
public class SkeletalSystemMiniGameController : MonoBehaviour
{
    [Header("Grip Identification")]
    [Tooltip("Name ColliderNameResolver.ResolveName() returns for the static tibia/fibula collider — held but never moves.")]
    [SerializeField] private string stationaryGripName = "TibiaFibula";

    [Tooltip("Name ColliderNameResolver.ResolveName() returns for the femur collider — the piece that actually swings.")]
    [SerializeField] private string movingGripName = "Femur";

    [Header("Hinge Setup")]
    [Tooltip("The pivot transform sitting at the joint center. Femur (and its collider/interactable) must be a CHILD of this transform, positioned at its correct rigid offset — this script only ever rotates this transform, never moves it.")]
    [SerializeField] private Transform kneePivot;

    [Tooltip("Rotation axis for flexion/extension, in KneePivot's LOCAL space (before any runtime rotation is applied). Verify in-scene (Local pivot mode) and flip sign if the knee bends the wrong way.")]
    [SerializeField] private Vector3 hingeAxisLocal = Vector3.forward;

    [Tooltip("Pivot angle (degrees, around hingeAxisLocal) representing the knee fully LOCKED/straight. The pivot's authored rotation in the scene should already be this pose — i.e. this is normally 0.")]
    [SerializeField] private float lockedAngleDegrees = 0f;

    [Tooltip("Pivot angle representing the femur's starting BENT pose before the student does anything. Negative values bend one way, positive the other — set to match the imported asset's natural rest bend.")]
    [SerializeField] private float startAngleDegrees = -45f;

    [Tooltip("Degrees of tolerance around lockedAngleDegrees that counts as 'locked.'")]
    [SerializeField] private float lockAngleTolerance = 4f;

    [Header("Drag Feel")]
    [Tooltip("Multiplier applied to the hand's angular movement before it's added to the pivot angle. 1 = the pivot rotates exactly as many degrees as the hand's bearing around the pivot changes. Raise this if the bone feels sluggish/under-responsive compared to hand movement; lower it if the bone whips around too fast for small hand movements. This exists because a human arm's natural swing radius rarely matches the object's actual scale, so a literal 1:1 angular match often doesn't feel right — tune by eye/feel.")]
    [SerializeField] private float dragSensitivity = 1f;

    [Tooltip("Distance (meters) from the hinge axis line below which the hand's bearing is completely untrusted (a near-zero-length projected vector's direction is essentially noise). Keep this small — it's a floor for true degeneracy, not a general safety margin.")]
    [SerializeField] private float minHandDistanceFromAxis = 0.01f;

    [Tooltip("Distance (meters) from the hinge axis line at which the hand's bearing is FULLY trusted. Between minHandDistanceFromAxis and this value, trust fades smoothly rather than cutting on/off — this is what prevents a visible shiver/stutter if your hand's natural path happens to pass near the axis mid-swing.")]
    [SerializeField] private float axisConfidenceFadeDistance = 0.05f;

    [Tooltip("Hard cap on how many degrees the pivot angle is allowed to change in a single second, regardless of what the raw hand-angle delta says. Guards against occasional hand-tracking glitches (common right as fingers close into a fist) causing a visible snap/reversal — a real drag never needs an extreme instantaneous jump, so anything faster than this is clamped rather than trusted outright.")]
    [SerializeField] private float maxDegreesPerSecond = 720f;

    [Header("Feedback")]
    [SerializeField] private AudioSource lockClickSound;
    [SerializeField] private ParticleSystem lockParticles;

    [Header("Testing")]
    [Tooltip("DEV/TEST ONLY. When enabled, the lock check treats the stationary grip (tibia/fibula) as always held, so you can test dragging AND locking the knee one-handed. Leave this OFF for real playtesting/builds — the two-hand requirement is intentional gameplay, not a bug.")]
    [SerializeField] private bool debugSkipStationaryGrip = false;

    private bool _isLocked;
    private float _currentAngle;

    // Drag-delta bookkeeping — captured fresh each time a new grab begins,
    // so releasing and re-grabbing resumes smoothly from wherever the knee
    // currently is instead of jumping.
    private bool _wasFemurHeld;
    private bool _hasHandAngleReference;
    private float _previousHandAngle;

    private Vector3 _hingeAxisWorldCached;
    private Vector3 _planeReferenceCached;
    private Quaternion _baselineLocalRotation;

    private void Awake()
    {
        MiniGameCompletionGate.RegisterInteractionGate(BodySystem.Skeletal);

        if (kneePivot == null)
        {
            Debug.LogWarning("[SkeletalSystemMiniGameController] KneePivot isn't assigned — the knee will never be able to move or lock.");
            return;
        }

        // The pivot's authored rotation in the scene IS the locked/straight
        // pose (angle 0) — everything else is composed relative to it.
        _baselineLocalRotation = kneePivot.localRotation;

        _currentAngle = startAngleDegrees;
        ApplyPivotAngle(_currentAngle);

        RecalculatePlaneReferences();

        if (debugSkipStationaryGrip)
            Debug.LogWarning("[SkeletalSystemMiniGameController] debugSkipStationaryGrip is ON — the two-hand lock requirement is bypassed for testing. Turn this off before a real playtest/build.");
    }

    private void Update()
    {
        if (_isLocked || kneePivot == null) return;

        bool stationaryHeld = false;
        bool femurHeld = false;
        HandManager.Side femurSide = default;

        foreach (HandManager.Side side in new[] { HandManager.Side.Left, HandManager.Side.Right })
        {
            var hand = HandManager.Get(side);
            if (hand == null || !hand.isGrabbed) continue;

            string held = hand.GetHeldObjectName();
            if (held == stationaryGripName) stationaryHeld = true;
            if (held == movingGripName) { femurHeld = true; femurSide = side; }
        }

        // Testing convenience only — see the Testing header tooltip. Real
        // gameplay always requires an actual hand on the tibia/fibula.
        if (debugSkipStationaryGrip) stationaryHeld = true;

        UpdateHingeDrag(femurHeld, femurSide);

        // Only check for the snap while BOTH hands are engaged — one on the
        // tibia/fibula (stabilizing), one on the femur (guiding it into place).
        if (stationaryHeld && femurHeld)
            TryLock();

        _wasFemurHeld = femurHeld;
    }

    /// <summary>
    /// Converts the holding hand's angular position around KneePivot into a
    /// change in the hinge angle, using a delta from the moment of grab
    /// rather than an absolute mapping — this is what makes it feel like
    /// dragging a hinge rather than teleporting the joint to match the hand.
    /// dragSensitivity scales the raw angular delta; see its tooltip.
    /// </summary>
    private void UpdateHingeDrag(bool femurHeld, HandManager.Side side)
    {
        if (!femurHeld)
        {
            _hasHandAngleReference = false;
            return;
        }

        var hand = HandManager.Get(side);
        if (hand == null) return;

        bool valid = TryGetHandAngleAroundPivot(hand.PalmPosition, out float handAngleNow, out _, out float confidence);
        if (!valid)
        {
            // True degeneracy only (inside minHandDistanceFromAxis) — even
            // the confidence-faded blend below can't safely use this
            // reading, so hold position exactly.
            return;
        }

        if (!_hasHandAngleReference)
        {
            // Don't establish a fresh reference from a low-confidence
            // reading, or the first real delta inherits a bad baseline.
            if (confidence < 0.5f) return;

            RecalculatePlaneReferences();
            _previousHandAngle = handAngleNow;
            _hasHandAngleReference = true;
            return; // no meaningful delta on the very first valid frame
        }

        // Incremental (previous-frame-to-now) rather than delta-from-grab-
        // start — avoids any long-term drift and keeps the confidence fade
        // below reacting to the hand's current position each frame.
        float rawDeltaThisFrame = Mathf.DeltaAngle(_previousHandAngle, handAngleNow);
        _previousHandAngle = handAngleNow;

        float correctedDelta = rawDeltaThisFrame * dragSensitivity;
        float target = _currentAngle + correctedDelta;

        float lo = Mathf.Min(startAngleDegrees, lockedAngleDegrees);
        float hi = Mathf.Max(startAngleDegrees, lockedAngleDegrees);
        target = Mathf.Clamp(target, lo, hi);

        // Fade the target smoothly toward "no change" as confidence drops,
        // rather than snapping between trusted/untrusted — this is what
        // eliminates the shiver if the hand's path grazes near the axis.
        float blendedTarget = Mathf.Lerp(_currentAngle, target, confidence);

        // Rate-limit how far _currentAngle is allowed to move in a single
        // frame on top of that, regardless of what the blended target says —
        // this is what actually rejects a one-frame tracking glitch, rather
        // than just reducing its likelihood.
        float maxStepThisFrame = maxDegreesPerSecond * Time.deltaTime;
        _currentAngle = Mathf.MoveTowards(_currentAngle, blendedTarget, maxStepThisFrame);

        ApplyPivotAngle(_currentAngle);
    }

    /// <summary>
    /// Signed angle (degrees) of the hand's position around the pivot,
    /// measured within the plane perpendicular to the hinge axis, plus the
    /// hand's distance from the axis (used only for the confidence fade
    /// below) and a 0-1 confidence value based on that same distance.
    /// Confidence is 0 below minHandDistanceFromAxis (true
    /// degeneracy — angleDegrees is meaningless and the call returns false),
    /// ramps linearly to 1 by axisConfidenceFadeDistance, and is 1 beyond
    /// that. Only ever used for DIFFERENCES between two calls, so the exact
    /// zero-reference direction doesn't matter as long as it's consistent
    /// between frames — it's recalculated in RecalculatePlaneReferences()
    /// whenever the pivot itself might have moved (scene start, each new
    /// grab).
    /// </summary>
    private bool TryGetHandAngleAroundPivot(Vector3 handWorldPos, out float angleDegrees, out float distance, out float confidence)
    {
        Vector3 toHand = handWorldPos - kneePivot.position;
        Vector3 projected = Vector3.ProjectOnPlane(toHand, _hingeAxisWorldCached);
        distance = projected.magnitude;

        if (distance < minHandDistanceFromAxis)
        {
            angleDegrees = 0f;
            confidence = 0f;
            return false;
        }

        angleDegrees = Vector3.SignedAngle(_planeReferenceCached, projected, _hingeAxisWorldCached);
        confidence = Mathf.Clamp01(Mathf.InverseLerp(minHandDistanceFromAxis, axisConfidenceFadeDistance, distance));
        return true;
    }

    /// <summary>
    /// Refreshes the cached world-space hinge axis and an arbitrary-but-
    /// consistent reference direction in its rotation plane. Rotating a
    /// transform around its own local axis doesn't change that axis's world
    /// direction, so this only needs recalculating when the pivot's PARENT
    /// might have moved (or, defensively, right before each new grab).
    /// </summary>
    private void RecalculatePlaneReferences()
    {
        _hingeAxisWorldCached = kneePivot.parent != null
            ? kneePivot.parent.TransformDirection(hingeAxisLocal.normalized)
            : hingeAxisLocal.normalized;

        // Pick a reference vector guaranteed not parallel to the hinge axis.
        Vector3 candidate = Vector3.Cross(_hingeAxisWorldCached, Vector3.up);
        if (candidate.sqrMagnitude < 0.001f)
            candidate = Vector3.Cross(_hingeAxisWorldCached, Vector3.forward);

        _planeReferenceCached = candidate.normalized;
    }

    private void ApplyPivotAngle(float angleDegrees)
    {
        kneePivot.localRotation = _baselineLocalRotation * Quaternion.AngleAxis(angleDegrees, hingeAxisLocal.normalized);
    }

    private void TryLock()
    {
        if (Mathf.Abs(Mathf.DeltaAngle(_currentAngle, lockedAngleDegrees)) <= lockAngleTolerance)
            LockKnee();
    }

    private void LockKnee()
    {
        _isLocked = true;
        _currentAngle = lockedAngleDegrees;
        ApplyPivotAngle(_currentAngle);

        // Prevent the now-locked femur from being picked up again — disable
        // whatever interactable component is driving grab detection for it.
        var femurTransform = FindMovingBoneTransform();
        var interactable = femurTransform != null
            ? femurTransform.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable>()
            : null;
        if (interactable != null) interactable.enabled = false;

        // Disabling the interactable above can suppress the normal
        // selectExited event, which would otherwise leave the "held" tint
        // stuck on — force both grip feedback components back to their base
        // color explicitly.
        femurTransform?.GetComponent<KneeJointGripFeedback>()?.ForceReset();
        GetComponentInChildren<KneeJointGripFeedback>()?.ForceReset();

        if (lockClickSound != null) lockClickSound.Play();
        if (lockParticles != null) lockParticles.Play();

        Debug.Log("[SkeletalSystemMiniGameController] Knee joint locked — interaction gate satisfied.");
        MiniGameCompletionGate.MarkInteractionComplete(BodySystem.Skeletal);
    }

    /// <summary>
    /// The femur is a child of KneePivot rather than a separately assigned
    /// field (the whole point of the hinge design is that its transform is
    /// never touched directly) — this just finds it by name for the
    /// one-time interactable-disable step at lock time.
    /// </summary>
    private Transform FindMovingBoneTransform()
    {
        if (kneePivot == null) return null;

        foreach (Transform child in kneePivot)
        {
            if (ColliderNameResolver.ResolveName(child) == movingGripName)
                return child;
        }
        return null;
    }
}