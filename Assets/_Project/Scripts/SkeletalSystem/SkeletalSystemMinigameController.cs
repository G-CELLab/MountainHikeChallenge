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
/// lerp its position/rotation between them based on how close kneePivot's
/// current rotation is to the locked rotation each Update — see project
/// history for the exact prior implementation.
///
/// HINGE SETUP FIELDS (hingeAxisLocal, startAngleDegrees) are used ONLY to
/// define the femur's starting bent pose, computed once in Awake. Locking
/// is now checked separately via lockedYDegrees/lockedZDegrees below — see
/// the Lock Detection section.
///
/// FREE-AIM DESIGN — while held, the femur points directly at the hand,
/// full 3D, no single rotation axis constraining it. Every frame, KneePivot
/// is rotated so that the femur (a rigid child sitting at a fixed local
/// offset from the pivot — this script only ever rotates KneePivot, never
/// moves it or the femur's local offset) points exactly along the line
/// from the pivot to the hand. This trades anatomical accuracy (a real
/// knee can only flex in one plane) for a drag feel that always visually
/// follows the hand's position, in any direction, rather than only
/// responding to motion within one fixed swing plane.
///
/// Interaction flow:
///   - The student grips the tibia/fibula with one hand (it never moves —
///     holding it represents "stabilizing" it) and grips the femur with the
///     other hand. Which physical hand holds which bone doesn't matter — we
///     just require "some hand holding TibiaFibula" AND "a DIFFERENT hand
///     holding Femur" at the same time (see HandManager.isGrabbed /
///     GetHeldObjectName(), same pattern used elsewhere in the project).
///   - While the femur is held, every frame: target rotation = whatever
///     rotates the femur's fixed local offset direction to point at the
///     hand's current world position (Quaternion.FromToRotation). This is
///     computed fresh every frame from scratch — no accumulation, no
///     per-frame delta, so it can't drift.
///   - The lock check compares KneePivot's LOCAL Euler Y and Z rotations
///     independently against their own targets and tolerances (lockedYDegrees
///     ± lockYTolerance, lockedZDegrees ± lockZTolerance) — X is left
///     unconstrained. Once both are within tolerance, it snaps Y and Z
///     exactly to their locked values (X stays wherever it is), becomes
///     non-interactable, and the same MiniGameCompletionGate flow
///     NervousSystemMiniGameController uses fires.
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

    [Tooltip("A child Transform positioned where the student actually grips the bone visually (e.g. mid-shaft) — NOT the femur mesh's own pivot/origin, which may sit at one end of the bone from how it was imported and rarely matches where a hand naturally holds it. This is the point that gets aimed at the hand. Must be a descendant of KneePivot so it rotates rigidly along with the femur. If left unassigned, falls back to the femur's own transform origin (the old, less predictable behavior).")]
    [SerializeField] private Transform grabPointReference;

    [Tooltip("Used ONLY to compute the start/locked target rotations below (via the same authored-axis convention as before) — NOT used to constrain live dragging, which is now free in any direction. Verify in-scene (Local pivot mode) and flip sign if the start/locked poses look wrong.")]
    [SerializeField] private Vector3 hingeAxisLocal = Vector3.forward;

    [Tooltip("Degrees (around hingeAxisLocal, relative to KneePivot's authored rotation in the scene) representing the femur's starting BENT pose. Same meaning as before — this defines the rotation applied at scene start.")]
    [SerializeField] private float startAngleDegrees = -45f;

    [Header("Patella")]
    [Tooltip("The patella (kneecap) bone Transform. Its world position/rotation is set every frame by lerping between patellaLoosePose and patellaLockPose — NOT parented under KneePivot, since a real patella slides rather than rotating rigidly with the femur.")]
    [SerializeField] private Transform patellaBone;

    [Tooltip("Authored reference pose (position/rotation) for the patella when the knee is loose/bent — i.e. anywhere far from locked. Same convention as patellaLockPose.")]
    [SerializeField] private Transform patellaLoosePose;

    [Tooltip("Authored reference pose (position/rotation) for the patella when the knee is fully locked/straight. The patella snaps exactly here the instant the knee locks.")]
    [SerializeField] private Transform patellaLockPose;

    [Header("Lock Detection")]
    [Tooltip("Target local Euler Y rotation (degrees) for the locked/straight pose.")]
    [SerializeField] private float lockedYDegrees = 90f;

    [Tooltip("Degrees of tolerance around lockedYDegrees that counts as 'locked' for the Y axis.")]
    [SerializeField] private float lockYTolerance = 5f;

    [Tooltip("Target local Euler Z rotation (degrees) for the locked/straight pose.")]
    [SerializeField] private float lockedZDegrees = 180f;

    [Tooltip("Degrees of tolerance around lockedZDegrees that counts as 'locked' for the Z axis.")]
    [SerializeField] private float lockZTolerance = 10f;

    [Header("Natural Angle Limits")]
    [Tooltip("If dragging pushes KneePivot's local Euler Y or Z outside these min/max ranges (anatomically implausible), the femur snaps back to its starting pose and stops responding to further hand movement for the REST of this grab — the student has to let go and re-grab to try again. X is intentionally left unconstrained, matching everything else in this script. Values are in Unity's signed Euler convention (-180 to 180), so double check against the live [FreeDrag] log (debugVisualizeHandDrag) rather than guessing — the same wraparound quirk that affects lock detection applies here too.")]
    [SerializeField] private float minYDegrees = -120f;
    [SerializeField] private float maxYDegrees = 120f;
    [SerializeField] private float minZDegrees = -180f;
    [SerializeField] private float maxZDegrees = 180f;

    [Header("Feedback")]
    [SerializeField] private AudioSource lockClickSound;
    [SerializeField] private ParticleSystem lockParticles;

    [Header("Testing")]
    [Tooltip("DEV/TEST ONLY. When enabled, the lock check treats the stationary grip (tibia/fibula) as always held, so you can test dragging AND locking the knee one-handed. Leave this OFF for real playtesting/builds — the two-hand requirement is intentional gameplay, not a bug.")]
    [SerializeField] private bool debugSkipStationaryGrip = false;

    [Tooltip("DEV/TEST ONLY. Draws the pivot→hand line (red) and pivot→femur-direction line (yellow) in the Scene view each frame while the femur is held — they should overlap almost exactly. Leave OFF for real playtesting/builds.")]
    [SerializeField] private bool debugVisualizeHandDrag = false;

    [Tooltip("If the hand gets closer to the pivot than this (meters), the direction to aim at is undefined/noisy (normalizing a near-zero vector) — hold the current rotation rather than aiming at garbage. A real grab should never land this close to the joint center.")]
    [SerializeField] private float minHandDistanceFromPivot = 0.02f;

    private bool _isLocked;
    private bool _boundaryViolatedThisGrab;

    private Quaternion _baselineLocalRotation;   // KneePivot's authored rotation in the scene, as-is
    private Quaternion _startLocalRotation;      // baseline + startAngleDegrees around hingeAxisLocal

    private Transform _femurTransform;
    private bool _hasAimAnchor;
    private Vector3 _femurLocalOffsetDir; // fixed direction (in KneePivot's local space) from pivot to the aim anchor — never changes since it's rigidly attached

    private float _patellaBlendMaxDistance; // angular distance (degrees) from the start pose to the locked target, used to normalize the patella blend progress to 0..1

    private void Awake()
    {
        MiniGameCompletionGate.RegisterInteractionGate(BodySystem.Skeletal);

        if (kneePivot == null)
        {
            Debug.LogWarning("[SkeletalSystemMiniGameController] KneePivot isn't assigned — the knee will never be able to move or lock.");
            return;
        }

        _baselineLocalRotation = kneePivot.localRotation;
        Vector3 axis = hingeAxisLocal.normalized;
        _startLocalRotation = _baselineLocalRotation * Quaternion.AngleAxis(startAngleDegrees, axis);

        kneePivot.localRotation = _startLocalRotation;

        // Distance (in the same "X-agnostic" sense used every frame by
        // UpdatePatellaBlend) from the starting pose to the locked target —
        // computed once, here, so the blend has a stable denominator to
        // normalize progress against. See UpdatePatellaBlend for why X is
        // excluded from this comparison.
        Vector3 startEuler = _startLocalRotation.eulerAngles;
        Quaternion startBlendTarget = Quaternion.Euler(startEuler.x, lockedYDegrees, lockedZDegrees);
        _patellaBlendMaxDistance = Quaternion.Angle(_startLocalRotation, startBlendTarget);
        if (_patellaBlendMaxDistance < 0.01f) _patellaBlendMaxDistance = 0.01f; // guard divide-by-zero if start already equals the locked Y/Z

        _femurTransform = FindMovingBoneTransform();

        Transform aimAnchor = grabPointReference != null ? grabPointReference : _femurTransform;
        if (aimAnchor != null)
        {
            // Cache as a direction relative to KneePivot, in KneePivot's
            // local space — Transform.InverseTransformPoint(world) gives
            // the local-space position, which is exactly what we need
            // since the anchor may not be a DIRECT child of KneePivot (it
            // could be nested a few levels under the femur).
            Vector3 localPos = kneePivot.InverseTransformPoint(aimAnchor.position);
            _femurLocalOffsetDir = localPos.normalized;
            _hasAimAnchor = true;
        }
        else
        {
            Debug.LogWarning("[SkeletalSystemMiniGameController] Couldn't find the femur as a child of KneePivot by name, and no grabPointReference was assigned — dragging will do nothing.");
        }

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

        UpdateFreeDrag(stationaryHeld, femurHeld, femurSide);
        UpdatePatellaBlend();

        // Only check for the snap while BOTH hands are engaged — one on the
        // tibia/fibula (stabilizing), one on the femur (guiding it into place).
        if (stationaryHeld && femurHeld)
            TryLock();
    }

    /// <summary>
    /// Rotates KneePivot every frame — from scratch, no accumulation — so
    /// the femur points directly at the hand. No axis, no sensitivity, no
    /// per-frame rate cap: wherever the hand is, that's where the femur
    /// points, immediately.
    ///
    /// Requires BOTH the stationary grip (tibia/fibula) and the femur to be
    /// held before any aiming happens — holding the femur alone does
    /// nothing. This mirrors the two-hand requirement TryLock() already
    /// enforced for locking, but now applies to the drag itself too.
    /// </summary>
    private void UpdateFreeDrag(bool stationaryHeld, bool femurHeld, HandManager.Side side)
    {
        if (!stationaryHeld || !femurHeld)
        {
            _boundaryViolatedThisGrab = false;
            return;
        }

        if (!_hasAimAnchor || _boundaryViolatedThisGrab) return;

        var hand = HandManager.Get(side);
        if (hand == null) return;

        Vector3 toHand = hand.PalmPosition - kneePivot.position;
        float distance = toHand.magnitude;

        if (distance < minHandDistanceFromPivot)
        {
            // Direction to a point essentially AT the pivot is undefined —
            // hold the current rotation rather than aiming at noise.
            if (debugVisualizeHandDrag) DrawFreeDragDebug(hand);
            return;
        }

        Vector3 targetDir = toHand / distance;

        ApplyDirectAim(targetDir);
        CheckAngleBoundaries();

        if (debugVisualizeHandDrag) DrawFreeDragDebug(hand);
    }

    /// <summary>
    /// After aiming, checks KneePivot's local Euler Y and Z (converted to
    /// Unity's signed -180..180 convention, since raw localEulerAngles
    /// reports 0..360 and a naive comparison would break across that
    /// wraparound) against minYDegrees/maxYDegrees and
    /// minZDegrees/maxZDegrees. If either is outside its range, snaps back
    /// to the starting pose and sets _boundaryViolatedThisGrab so
    /// UpdateFreeDrag stops re-aiming for the rest of this grab — otherwise
    /// the very next frame would just aim right back at the same
    /// out-of-bounds hand position and immediately re-violate.
    /// </summary>
    private void CheckAngleBoundaries()
    {
        Vector3 euler = kneePivot.localEulerAngles;
        float signedY = ToSignedDegrees(euler.y);
        float signedZ = ToSignedDegrees(euler.z);

        bool outOfBounds = signedY < minYDegrees || signedY > maxYDegrees
                         || signedZ < minZDegrees || signedZ > maxZDegrees;

        if (!outOfBounds) return;

        Debug.LogWarning($"[SkeletalSystemMiniGameController] Angle boundary exceeded " +
                          $"(y={signedY:F1}° [{minYDegrees:F1}, {maxYDegrees:F1}], " +
                          $"z={signedZ:F1}° [{minZDegrees:F1}, {maxZDegrees:F1}]) — resetting to start pose.");

        kneePivot.localRotation = _startLocalRotation;
        _boundaryViolatedThisGrab = true;
    }

    /// <summary>Converts a raw 0..360 Euler component to Unity's signed -180..180 convention.</summary>
    private static float ToSignedDegrees(float raw) => raw > 180f ? raw - 360f : raw;

    /// <summary>
    /// Slides the patella between patellaLoosePose and patellaLockPose based
    /// on how close KneePivot's current rotation is to the locked target —
    /// restoring the design the class summary describes as having been
    /// stripped out, generalized to work with free-aim instead of a single
    /// scalar bend angle.
    ///
    /// Progress is computed the same "X-agnostic" way as
    /// _patellaBlendMaxDistance in Awake: compare the current rotation to a
    /// MOVING target that has the locked Y/Z but keeps whatever X currently
    /// is. Since X isn't part of the lock criteria, this means the
    /// resulting angular distance reflects only the Y/Z mismatch — X
    /// wandering during a normal drag doesn't distort the patella's slide
    /// position. t=0 at the starting pose, t=1 once Y/Z reach the locked
    /// target (clamped in between for anything reached via boundary resets
    /// or an unusual drag path).
    ///
    /// Runs every frame regardless of whether the femur is currently being
    /// held, so the patella reflects the leg's current bend state even
    /// after the student lets go mid-drag — not just during active dragging.
    /// </summary>
    private void UpdatePatellaBlend()
    {
        if (patellaBone == null || patellaLoosePose == null || patellaLockPose == null) return;

        Vector3 currentEuler = kneePivot.localEulerAngles;
        Quaternion currentBlendTarget = Quaternion.Euler(currentEuler.x, lockedYDegrees, lockedZDegrees);
        float currentDistance = Quaternion.Angle(kneePivot.localRotation, currentBlendTarget);

        float t = 1f - Mathf.Clamp01(currentDistance / _patellaBlendMaxDistance);

        patellaBone.position = Vector3.Lerp(patellaLoosePose.position, patellaLockPose.position, t);
        patellaBone.rotation = Quaternion.Slerp(patellaLoosePose.rotation, patellaLockPose.rotation, t);
    }

    /// <summary>
    /// Computes and applies the KneePivot world rotation that points the
    /// femur's fixed local offset direction at targetDir (world space).
    /// Separated from UpdateFreeDrag purely for clarity/testability.
    /// </summary>
    private void ApplyDirectAim(Vector3 targetDirWorld)
    {
        // We want kneePivot.rotation (world) such that:
        //   kneePivot.rotation * _femurLocalOffsetDir == targetDirWorld
        // Quaternion.FromToRotation(a, b) returns exactly the rotation that
        // satisfies rotation * a == b, so this is a direct, one-line solve
        // — no iteration, no accumulation, recomputed fresh every frame.
        kneePivot.rotation = Quaternion.FromToRotation(_femurLocalOffsetDir, targetDirWorld);
    }

    /// <summary>
    /// DEV/TEST ONLY (gated by debugVisualizeHandDrag). Draws pivot→hand
    /// (red) and pivot→femur (yellow) — they should overlap almost exactly
    /// whenever the aim solve succeeded.
    /// </summary>
    private void DrawFreeDragDebug(HandManager hand)
    {
        Debug.DrawLine(kneePivot.position, hand.PalmPosition, Color.red);
        Transform aimAnchor = grabPointReference != null ? grabPointReference : _femurTransform;
        if (aimAnchor != null)
            Debug.DrawLine(kneePivot.position, aimAnchor.position, Color.yellow);

        Vector3 euler = kneePivot.localEulerAngles;
        Debug.Log($"[FreeDrag] localEuler=({euler.x:F1}, {euler.y:F1}, {euler.z:F1})  " +
                  $"yDiff={Mathf.DeltaAngle(euler.y, lockedYDegrees):F1}  zDiff={Mathf.DeltaAngle(euler.z, lockedZDegrees):F1}");
    }

    /// <summary>
    /// Checks KneePivot's LOCAL Euler Y and Z independently against their
    /// own targets/tolerances — deliberately not a single combined angular
    /// distance, since the two axes represent different things visually
    /// (Y ≈ the main flexion swing, Z ≈ how "twisted" the bone looks) and
    /// benefit from different tolerances. X is intentionally left
    /// unconstrained here.
    /// </summary>
    private void TryLock()
    {
        Vector3 euler = kneePivot.localEulerAngles;
        float yDiff = Mathf.DeltaAngle(euler.y, lockedYDegrees);
        float zDiff = Mathf.DeltaAngle(euler.z, lockedZDegrees);

        if (Mathf.Abs(yDiff) <= lockYTolerance && Mathf.Abs(zDiff) <= lockZTolerance)
            LockKnee();
    }

    private void LockKnee()
    {
        _isLocked = true;

        // Snap Y and Z to their exact locked targets; leave X exactly where
        // it currently is rather than forcing it to some assumed value.
        Vector3 euler = kneePivot.localEulerAngles;
        euler.y = lockedYDegrees;
        euler.z = lockedZDegrees;
        kneePivot.localEulerAngles = euler;

        // Snap the patella exactly to its locked reference pose too, same
        // as the femur — no reason for it to sit at some in-between blend
        // value once the joint is actually locked.
        if (patellaBone != null && patellaLockPose != null)
        {
            patellaBone.position = patellaLockPose.position;
            patellaBone.rotation = patellaLockPose.rotation;
        }

        // Prevent the now-locked femur from being picked up again — disable
        // whatever interactable component is driving grab detection for it.
        var interactable = _femurTransform != null
            ? _femurTransform.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable>()
            : null;
        if (interactable != null) interactable.enabled = false;

        // Disabling the interactable above can suppress the normal
        // selectExited event, which would otherwise leave the "held" tint
        // stuck on — force both grip feedback components back to their base
        // color explicitly.
        _femurTransform?.GetComponent<KneeJointGripFeedback>()?.ForceReset();
        GetComponentInChildren<KneeJointGripFeedback>()?.ForceReset();

        if (lockClickSound != null) lockClickSound.Play();
        if (lockParticles != null) lockParticles.Play();

        Debug.Log("[SkeletalSystemMiniGameController] Knee joint locked — interaction gate satisfied.");
        MiniGameCompletionGate.MarkInteractionComplete(BodySystem.Skeletal);
    }

    /// <summary>
    /// The femur is a child of KneePivot rather than a separately assigned
    /// field (its transform is never touched directly by this script,
    /// only KneePivot's is) — this finds it by name, once, in Awake.
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