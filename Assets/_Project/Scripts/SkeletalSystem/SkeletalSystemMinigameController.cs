using System.Collections;
using UnityEngine;

/// <summary>
/// Lives in the Skeletal mini-game scene (Scene 1 — Trailhead), facing a
/// single knee joint made of two pieces right now: TibiaFibula (static
/// anchor — held but never moves) and Femur (the piece the student swings
/// into place).
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
///     exactly to their locked values (X stays wherever it is).
///   - LOCKING IS REPEATABLE. Each time the knee locks, the femur's current
///     grab is force-released (so the student has to physically re-grab
///     it) and the interactable is re-enabled a frame later so it CAN be
///     re-grabbed, dragged away, and locked again. The lock sound/particles
///     play every time this happens. The MiniGameCompletionGate flow
///     NervousSystemMiniGameController uses only fires once, on the very
///     first lock — later re-locks don't re-fire it.
///   - Once locked, a new lock is only accepted again after the pose has
///     actually swung away from the locked target by more than tolerance +
///     relockDeadzoneDegrees on Y or Z — see _isArmedForLock in TryLock.
///     Without this, an instant auto-regrab (trigger still held when the
///     interactable re-enables) would immediately re-lock in place several
///     times in a row before the student has moved anything.
///   - Once the knee has locked at least one time, it can no longer be
///     dragged PAST the locked angle in the extending direction (that
///     would be hyperextension) — see ClampBeyondLockedIfNeeded. It can
///     still be dragged the other way, back through the start pose and
///     into a squat (if patellaSquatPose is assigned), same as always.
///   - CheckAngleBoundaries uses a wraparound-safe arc check (ArcExcessDegrees)
///     rather than a naive signed-degree min/max comparison, because a
///     locked/max value sitting at or near the ±180° seam (very common —
///     "knee straight" is often authored as Z=180°) would otherwise turn a
///     1° overshoot into an apparent ~194° violation the instant the angle
///     wraps from +179° to -179°. boundaryGraceDegrees adds a small buffer
///     on top of that so minor jitter right at the edge doesn't instantly
///     reset the pose either.
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

    [Tooltip("OPTIONAL. Authored reference pose (position/rotation) for the patella when the knee bends PAST the starting pose in the opposite direction from locked — e.g. a squat. If left unassigned, bending that way just holds at patellaLoosePose (the original behavior).")]
    [SerializeField] private Transform patellaSquatPose;

    [Tooltip("Degrees of rotation past the starting pose, in the squat direction, that correspond to patellaSquatPose being fully reached (blend t=1). Tune to match how far the knee can realistically bend into a squat. Only used if patellaSquatPose is assigned.")]
    [SerializeField] private float squatBlendMaxDegrees = 45f;

    [Header("Lock Detection")]
    [Tooltip("Target local Euler Y rotation (degrees) for the locked/straight pose.")]
    [SerializeField] private float lockedYDegrees = 90f;

    [Tooltip("Degrees of tolerance around lockedYDegrees that counts as 'locked' for the Y axis.")]
    [SerializeField] private float lockYTolerance = 5f;

    [Tooltip("Target local Euler Z rotation (degrees) for the locked/straight pose.")]
    [SerializeField] private float lockedZDegrees = 180f;

    [Tooltip("Degrees of tolerance around lockedZDegrees that counts as 'locked' for the Z axis.")]
    [SerializeField] private float lockZTolerance = 10f;

    [Tooltip("After a lock, Y or Z must swing at least (its own tolerance + this many degrees) away from the locked target before the system will accept another lock. Prevents an instant auto-regrab (controller trigger still held when the interactable re-enables) from immediately re-locking in place several times in a row before the student has actually moved anything.")]
    [SerializeField] private float relockDeadzoneDegrees = 15f;

    [Header("Natural Angle Limits")]
    [Tooltip("If dragging pushes KneePivot's local Euler Y or Z outside these min/max ranges (anatomically implausible), the femur snaps back to its starting pose and stops responding to further hand movement for the REST of this grab — the student has to let go and re-grab to try again. X is intentionally left unconstrained, matching everything else in this script. Values are in Unity's signed Euler convention (-180 to 180). The check itself (ArcExcessDegrees) is wraparound-safe, so a limit sitting at/near ±180° (very common when the locked/straight pose is authored as 180°) won't misfire the way a naive comparison would.")]
    [SerializeField] private float minYDegrees = -120f;
    [SerializeField] private float maxYDegrees = 120f;
    [SerializeField] private float minZDegrees = -180f;
    [SerializeField] private float maxZDegrees = 180f;

    [Tooltip("Extra degrees of slack allowed past minYDegrees/maxYDegrees/minZDegrees/maxZDegrees before the anatomical-plausibility reset actually fires — a small buffer so tiny overshoot or jitter right at the edge (e.g. right after a lock sitting at that same edge) doesn't instantly reset the pose.")]
    [SerializeField] private float boundaryGraceDegrees = 3f;

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

    // True only for the one-frame window right after a lock, while we force
    // the grab to release and wait to re-enable the interactable. Update()
    // does nothing while this is true. This is NOT a permanent "done"
    // state — it clears itself once the femur is grabbable again.
    private bool _isFrozenAfterLock;

    // Gates TryLock. Set false the instant a lock happens; only set back to
    // true once Y or Z has swung past tolerance + relockDeadzoneDegrees away
    // from the locked target. Starts true so the very first lock isn't
    // blocked (the start pose is already far from locked by construction).
    private bool _isArmedForLock = true;

    // True forever after the very first lock. Once true:
    //   - MarkInteractionComplete is never called again on subsequent locks.
    //   - ClampBeyondLockedIfNeeded starts enforcing the "no hyperextension
    //     past locked" limit on every drag.
    private bool _hasLockedAtLeastOnce;

    // True forever after the minigame's one-time completion has fired.
    private bool _hasCompletedOnce;

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
        if (kneePivot == null) return;

        // Waiting out the one-frame force-release window from a lock — see
        // ReleaseGripAndReenable. Nothing to do until that clears.
        if (_isFrozenAfterLock) return;

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

        // Only worth enforcing the "no hyperextension past locked" limit if
        // the wide anatomical-plausibility reset above didn't already fire
        // this frame (that reset already sends things back to the start
        // pose, which makes clamping to locked redundant/conflicting).
        if (!_boundaryViolatedThisGrab)
            ClampBeyondLockedIfNeeded();

        if (debugVisualizeHandDrag) DrawFreeDragDebug(hand);
    }

    /// <summary>
    /// After aiming, checks KneePivot's local Euler Y and Z against
    /// minYDegrees/maxYDegrees and minZDegrees/maxZDegrees using
    /// ArcExcessDegrees (wraparound-safe — see class summary), with
    /// boundaryGraceDegrees of slack added on top. If either is outside its
    /// range beyond that grace, snaps back to the starting pose and sets
    /// _boundaryViolatedThisGrab so UpdateFreeDrag stops re-aiming for the
    /// rest of this grab — otherwise the very next frame would just aim
    /// right back at the same out-of-bounds hand position and immediately
    /// re-violate.
    /// </summary>
    private void CheckAngleBoundaries()
    {
        Vector3 euler = kneePivot.localEulerAngles;
        float signedY = ToSignedDegrees(euler.y);
        float signedZ = ToSignedDegrees(euler.z);

        float yExcess = ArcExcessDegrees(signedY, minYDegrees, maxYDegrees, out bool yBelowMin);
        float zExcess = ArcExcessDegrees(signedZ, minZDegrees, maxZDegrees, out bool zBelowMin);

        bool yViolated = yExcess > boundaryGraceDegrees;
        bool zViolated = zExcess > boundaryGraceDegrees;

        if (!yViolated && !zViolated) return;

        // Only report the axis/axes that actually caused this, with how far
        // past the limit (beyond the grace buffer) they went.
        var reasons = new System.Collections.Generic.List<string>();
        if (yViolated) reasons.Add(yBelowMin
            ? $"Y={signedY:F1}° is below min {minYDegrees:F1}° (by {yExcess:F1}°, grace {boundaryGraceDegrees:F1}°)"
            : $"Y={signedY:F1}° is above max {maxYDegrees:F1}° (by {yExcess:F1}°, grace {boundaryGraceDegrees:F1}°)");
        if (zViolated) reasons.Add(zBelowMin
            ? $"Z={signedZ:F1}° is below min {minZDegrees:F1}° (by {zExcess:F1}°, grace {boundaryGraceDegrees:F1}°)"
            : $"Z={signedZ:F1}° is above max {maxZDegrees:F1}° (by {zExcess:F1}°, grace {boundaryGraceDegrees:F1}°)");

        Debug.LogWarning($"[SkeletalSystemMiniGameController] Angle boundary exceeded — " +
                          $"{string.Join("; ", reasons)} — resetting to start pose.");

        kneePivot.localRotation = _startLocalRotation;
        _boundaryViolatedThisGrab = true;
    }

    /// <summary>
    /// Wraparound-safe replacement for a naive "signedValue &lt; min ||
    /// signedValue &gt; max" check. Naive comparison breaks the instant the
    /// tracked angle crosses the ±180° seam — e.g. sitting right at
    /// Z=180° (a very common "leg straight" value) and drifting 1° further
    /// reads as -179°, which a naive check sees as ~194° below a min of
    /// 15° instead of ~1° past max. This walks the angle around the circle
    /// starting at min and measures how far outside the [min,max] arc it
    /// landed, picking whichever edge (min or max) is actually closer so
    /// the reported excess/direction reflects the real, small deviation
    /// rather than an artifact of where the wraparound happens to fall.
    /// Returns a value &lt;= 0 if inside the allowed arc (not violated),
    /// otherwise the number of degrees past whichever edge is closer.
    /// </summary>
    private static float ArcExcessDegrees(float signedValue, float min, float max, out bool belowMin)
    {
        float rangeSize = max - min;
        float arcPosition = Mathf.Repeat(signedValue - min, 360f); // 0 at min, increasing toward max

        if (arcPosition <= rangeSize)
        {
            belowMin = false;
            return arcPosition - rangeSize; // <= 0
        }

        float distancePastMax = arcPosition - rangeSize;
        float distanceBelowMin = 360f - arcPosition;

        belowMin = distanceBelowMin < distancePastMax;
        return belowMin ? distanceBelowMin : distancePastMax;
    }

    /// <summary>
    /// Once the knee has locked at least one time, this prevents dragging
    /// it PAST the locked angle any further in the extending direction
    /// (hyperextension) — the locked pose becomes a hard stop from then on.
    /// Bending the other way, back through the start pose and into a squat,
    /// is untouched by this and still works exactly as before.
    ///
    /// Checked independently per axis (Y and Z), same convention as
    /// TryLock/CheckAngleBoundaries: figure out which direction "locked" is
    /// from the start pose on that axis, then see if the current value has
    /// gone further than locked in that same direction. If so, clamp that
    /// axis back to exactly the locked value rather than resetting the
    /// whole pose — this should feel like a physical stop, not a snap-away.
    /// </summary>
    private void ClampBeyondLockedIfNeeded()
    {
        if (!_hasLockedAtLeastOnce) return;

        Vector3 startEuler = _startLocalRotation.eulerAngles;
        Vector3 euler = kneePivot.localEulerAngles;

        float startToLockY = Mathf.DeltaAngle(startEuler.y, lockedYDegrees);
        float startToLockZ = Mathf.DeltaAngle(startEuler.z, lockedZDegrees);

        float lockToCurrentY = Mathf.DeltaAngle(lockedYDegrees, euler.y);
        float lockToCurrentZ = Mathf.DeltaAngle(lockedZDegrees, euler.z);

        bool overExtendedY = Mathf.Abs(startToLockY) > 0.01f
                           && Mathf.Sign(lockToCurrentY) == Mathf.Sign(startToLockY)
                           && Mathf.Abs(lockToCurrentY) > 0.01f;

        bool overExtendedZ = Mathf.Abs(startToLockZ) > 0.01f
                           && Mathf.Sign(lockToCurrentZ) == Mathf.Sign(startToLockZ)
                           && Mathf.Abs(lockToCurrentZ) > 0.01f;

        if (!overExtendedY && !overExtendedZ) return;

        var reasons = new System.Collections.Generic.List<string>();
        if (overExtendedY) reasons.Add($"Y={euler.y:F1}° went {lockToCurrentY:F1}° past locked {lockedYDegrees:F1}°");
        if (overExtendedZ) reasons.Add($"Z={euler.z:F1}° went {lockToCurrentZ:F1}° past locked {lockedZDegrees:F1}°");

        Debug.Log($"[SkeletalSystemMiniGameController] Post-lock hyperextension stop — " +
                   $"{string.Join("; ", reasons)} — clamping to locked value.");

        if (overExtendedY) euler.y = lockedYDegrees;
        if (overExtendedZ) euler.z = lockedZDegrees;
        kneePivot.localEulerAngles = euler;
    }

    /// <summary>Converts a raw 0..360 Euler component to Unity's signed -180..180 convention.</summary>
    private static float ToSignedDegrees(float raw) => raw > 180f ? raw - 360f : raw;

    /// <summary>
    /// Slides the patella between patellaLoosePose and patellaLockPose based
    /// on how close KneePivot's current rotation is to the locked target,
    /// generalized to work with free-aim instead of a single scalar bend
    /// angle.
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
    ///
    /// If patellaSquatPose is assigned, bending PAST the starting pose in
    /// the direction opposite of locked (e.g. squatting rather than
    /// straightening) blends toward that pose instead, using
    /// squatBlendMaxDegrees as its own normalization range. Direction is
    /// determined via a 2D (Y,Z) dot product against the start→locked
    /// direction, not a single hardcoded axis, since different knee rigs
    /// may have their flex live mostly in Y, mostly in Z, or a mix of
    /// both. If patellaSquatPose is left unassigned, bending that way just
    /// holds at patellaLoosePose, matching the original behavior.
    /// </summary>
    private void UpdatePatellaBlend()
    {
        if (patellaBone == null || patellaLoosePose == null || patellaLockPose == null) return;

        // Figure out which side of the starting pose we're currently on:
        // bending TOWARD locked (original loose→lock blend), or bending
        // PAST the start in the opposite direction (toward a squat).
        // Rather than assuming a single flex axis, treat (Y,Z) as a 2D
        // vector relative to the start pose and compare directions via dot
        // product — negative means current movement points the opposite
        // way from the start→locked direction, i.e. it's a squat, no
        // matter whether this particular rig's flex lives mostly in Y, Z,
        // or a mix of both.
        Vector3 startEuler = _startLocalRotation.eulerAngles;
        Vector2 startToLock = new Vector2(
            Mathf.DeltaAngle(startEuler.y, lockedYDegrees),
            Mathf.DeltaAngle(startEuler.z, lockedZDegrees));

        Vector3 currentEuler = kneePivot.localEulerAngles;
        Vector2 startToCurrent = new Vector2(
            Mathf.DeltaAngle(startEuler.y, currentEuler.y),
            Mathf.DeltaAngle(startEuler.z, currentEuler.z));

        bool bendingTowardSquat = patellaSquatPose != null
                                && startToCurrent.sqrMagnitude > 0.0001f
                                && Vector2.Dot(startToLock, startToCurrent) < 0f;

        if (bendingTowardSquat)
        {
            float squatT = Mathf.Clamp01(startToCurrent.magnitude / squatBlendMaxDegrees);
            patellaBone.position = Vector3.Lerp(patellaLoosePose.position, patellaSquatPose.position, squatT);
            patellaBone.rotation = Quaternion.Slerp(patellaLoosePose.rotation, patellaSquatPose.rotation, squatT);
            return;
        }

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
                  $"yDiff={Mathf.DeltaAngle(euler.y, lockedYDegrees):F1}  zDiff={Mathf.DeltaAngle(euler.z, lockedZDegrees):F1}  " +
                  $"armed={_isArmedForLock}");
    }

    /// <summary>
    /// Checks KneePivot's LOCAL Euler Y and Z independently against their
    /// own targets/tolerances — deliberately not a single combined angular
    /// distance, since the two axes represent different things visually
    /// (Y ≈ the main flexion swing, Z ≈ how "twisted" the bone looks) and
    /// benefit from different tolerances. X is intentionally left
    /// unconstrained here.
    ///
    /// Gated by _isArmedForLock: right after a lock, this is false, and
    /// stays false until Y or Z has swung past tolerance +
    /// relockDeadzoneDegrees away from the locked target — only then is a
    /// new lock accepted. Without this, an instant auto-regrab (trigger
    /// still down when the interactable re-enables after the previous
    /// lock) would see the pose still within tolerance and immediately
    /// re-lock, over and over, before the student has moved anything.
    /// </summary>
    private void TryLock()
    {
        Vector3 euler = kneePivot.localEulerAngles;
        float yDiff = Mathf.DeltaAngle(euler.y, lockedYDegrees);
        float zDiff = Mathf.DeltaAngle(euler.z, lockedZDegrees);

        if (!_isArmedForLock)
        {
            if (Mathf.Abs(yDiff) > lockYTolerance + relockDeadzoneDegrees ||
                Mathf.Abs(zDiff) > lockZTolerance + relockDeadzoneDegrees)
            {
                _isArmedForLock = true;
            }
            return;
        }

        if (Mathf.Abs(yDiff) <= lockYTolerance && Mathf.Abs(zDiff) <= lockZTolerance)
            LockKnee();
    }

    /// <summary>
    /// Snaps the femur (and patella) into the locked pose, fires feedback,
    /// and marks the minigame complete — but only the FIRST time this ever
    /// happens. Every time (including repeats), it force-releases whoever's
    /// currently holding the femur and makes it re-grabbable a moment
    /// later, via ReleaseGripAndReenable, so the student can let go, pull
    /// the leg back down, and lock it again to review the motion.
    /// </summary>
    private void LockKnee()
    {
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

        bool isFirstLock = !_hasLockedAtLeastOnce;
        _hasLockedAtLeastOnce = true;
        _isArmedForLock = false; // must swing away past the deadzone before another lock is accepted

        if (isFirstLock && !_hasCompletedOnce)
        {
            _hasCompletedOnce = true;
            MiniGameCompletionGate.MarkInteractionComplete(BodySystem.Skeletal);
            Debug.Log("[SkeletalSystemMiniGameController] Knee joint locked for the first time — interaction gate satisfied.");
        }
        else
        {
            Debug.Log("[SkeletalSystemMiniGameController] Knee joint re-locked.");
        }

        // Feedback plays every time the knee locks, first time or not.
        if (lockClickSound != null) lockClickSound.Play();
        if (lockParticles != null) lockParticles.Play();

        StartCoroutine(ReleaseGripAndReenable());
    }

    /// <summary>
    /// Forces whoever is currently holding the femur to drop it (by
    /// disabling its interactable, which the XR Interaction Toolkit treats
    /// as a select-exit), waits one frame for that to actually process,
    /// then re-enables the interactable so the femur can be freely
    /// re-grabbed and dragged again. _isFrozenAfterLock blocks Update()'s
    /// drag/lock logic for that single-frame gap so nothing tries to read
    /// hand state while the release is still in flight.
    /// </summary>
    private IEnumerator ReleaseGripAndReenable()
    {
        _isFrozenAfterLock = true;

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

        yield return null;

        if (interactable != null) interactable.enabled = true;

        _boundaryViolatedThisGrab = false; // a fresh grab should get a clean slate
        _isFrozenAfterLock = false;
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