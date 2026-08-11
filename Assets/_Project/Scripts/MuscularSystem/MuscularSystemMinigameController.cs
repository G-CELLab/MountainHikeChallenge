using UnityEngine;

/// <summary>
/// Lives in the Muscular mini-game scene (Scene 2 — The Steep Incline, muscle
/// fiber portion). Drives a single muscle fiber's MuscleFiberBellyContract by
/// grabbing its two ends and pulling them together or letting them stretch
/// back out — a simple two-hand slider, not a lock-and-release mechanic.
///
/// SETUP IN SCENE:
///   - The fiber model (with MuscleFiberBellyContract on its root) needs two
///     small grippable end objects, FiberEndA and FiberEndB, positioned at
///     rest at each tip of the fiber, each with a Collider + XRBaseInteractable
///     (XRSimpleInteractable is fine — nothing needs physics here). Reuse
///     GrabPointGlow and KneeJointGripFeedback directly on each end object —
///     both are already generic (despite the "Knee" name on one of them) and
///     need no muscle-specific changes.
///   - Drag those two end Transforms into endA / endB below, and drag the
///     fiber's MuscleFiberBellyContract into fiberContract.
///   - fiberContract does NOT need driveAutomatically unchecked by hand —
///     Awake() here turns it off automatically so the two scripts never
///     fight over who's calling SetContraction().
///
/// Interaction flow:
///   - The student grabs FiberEndA with one hand and FiberEndB with the
///     other. Which physical hand holds which end doesn't matter.
///   - While both are held, each end is driven every frame to follow its
///     hand's position, but ONLY along the fiber's own long axis (the same
///     axis/center/span MuscleFiberBellyContract already computed) — exactly
///     like the knee's free-aim drag, just constrained to a line instead of
///     free 3D rotation. The perpendicular offset each end started with is
///     preserved, so it never drifts off the fiber's centerline.
///   - How far the two ends have been pulled in from their resting position,
///     averaged, maps directly to MuscleFiberBellyContract.SetContraction()
///     every frame — 0 at the resting (fully stretched) position, 1 at
///     maxPulledInFraction (fully contracted). There is no snap and no
///     forced release — the student can freely move back and forth between
///     those two limits as many times as they want, and the belly
///     bulges/shortens live the whole time.
///   - A SQUEEZE counts each time contraction rises from below
///     completionThreshold up past it — i.e. a distinct pull, not just
///     "currently contracted." After counting, contraction has to drop back
///     below squeezeReleaseThreshold before the next pull can count as a new
///     squeeze — otherwise jitter right at the threshold, or just holding at
///     max, would rack up several squeezes for one physical pull.
///   - squeezesRequired squeezes (default 3) are needed before the mini-game
///     is marked complete via MiniGameCompletionGate — but reaching that
///     doesn't change the interaction itself; grips stay live and the
///     student can keep working the fiber afterward same as before.
///   - progressRing (optional — assign a MuscleSqueezeProgressRing to use
///     it) is fed the discrete step fraction (_squeezeCount /
///     squeezesRequired) each time a squeeze registers — a clean jump per
///     completed squeeze, not a live fill that rises/falls with the current
///     pull. It pops and fades out on its own once full.
///   - Whenever fewer than both ends are held, the fiber eases back toward
///     its resting (fully stretched) pose instead of freezing in place.
/// </summary>
public class MuscularSystemMiniGameController : MonoBehaviour
{
    [Header("Grip Identification")]
    [Tooltip("Name ColliderNameResolver.ResolveName() returns for the first grippable end.")]
    [SerializeField] private string endAGripName = "FiberEndA";

    [Tooltip("Name ColliderNameResolver.ResolveName() returns for the second grippable end.")]
    [SerializeField] private string endBGripName = "FiberEndB";

    [Header("References")]
    [Tooltip("The fiber's deformer — provides the shared axis/center/span this script drags along, and receives SetContraction() every frame.")]
    [SerializeField] private MuscleFiberBellyContract fiberContract;

    [Tooltip("The grippable end object positioned at rest near one tip of the fiber.")]
    [SerializeField] private Transform endA;

    [Tooltip("The grippable end object positioned at rest near the other tip of the fiber.")]
    [SerializeField] private Transform endB;

    [Tooltip("OPTIONAL. Visual feedback for the squeeze gate below — fills a slice per completed squeeze, live-fills the current attempt in real time, and pops/fades out on its own once full. Leave unassigned if you don't want a ring for this fiber.")]
    [SerializeField] private MuscleSqueezeProgressRing progressRing;

    [Header("Contraction Mapping")]
    [Tooltip("Fraction (0-1) of the resting half-span the ends can be pulled IN toward center — this is the MAX contraction limit. 0.5 = ends can be pulled in as far as halfway to the centerline, no further. Lower = a shorter, easier pull; higher = requires a bigger pull to reach full contraction.")]
    [Range(0.1f, 0.9f)]
    [SerializeField] private float maxPulledInFraction = 0.55f;

    [Tooltip("Contraction value (0-1) a single pull must reach to count as one squeeze.")]
    [Range(0.5f, 1f)]
    [SerializeField] private float completionThreshold = 0.9f;

    [Header("Squeeze Gate")]
    [Tooltip("Number of distinct squeezes (contraction rising past completionThreshold) needed to mark the mini-game complete.")]
    [Range(1, 10)]
    [SerializeField] private int squeezesRequired = 3;

    [Tooltip("After a squeeze is counted, contraction must drop back below this before the next pull can count as a new squeeze. Keeps one long hold — or jitter right at the threshold — from racking up multiple squeezes.")]
    [Range(0f, 0.9f)]
    [SerializeField] private float squeezeReleaseThreshold = 0.4f;

    [Header("Relax-Back (when not both hands are holding)")]
    [Tooltip("How quickly (fraction of full contraction range per second) the fiber eases back toward fully-stretched (0) when released. Higher = snappier relax.")]
    [SerializeField] private float relaxSpeed = 2.5f;

    [Header("Debug")]
    [SerializeField] private bool verbose = true;
    [Tooltip("Draws the fiber's axis line in the Scene view. Leave OFF for real playtesting/builds.")]
    [SerializeField] private bool debugVisualize = false;

    private bool _hasCompletedOnce;
    private int _squeezeCount;
    private bool _isAboveThreshold; // true while the current pull is still counted as "in" the squeeze it triggered

    private Vector3 _localAxisDir;      // fiberContract's local axis, cached once
    private float _axisCenterLocal;
    private float _axisHalfSpanLocal;
    private float _minOffsetMagnitude;  // |offset from center| at MAX contraction (pulled all the way in)

    // Each end's fixed perpendicular offset from the axis, in fiberContract's
    // local space — preserved every frame so an end only ever slides along
    // the fiber's centerline, never drifts sideways off it.
    private Vector3 _perpendicularA;
    private Vector3 _perpendicularB;

    // Which side of center each end starts on (+1 or -1 along _localAxisDir).
    private float _signA;
    private float _signB;

    private float _currentContraction;

    private void Awake()
    {
        MiniGameCompletionGate.RegisterInteractionGate(BodySystem.Muscular);

        if (fiberContract == null || endA == null || endB == null)
        {
            Debug.LogWarning("[MuscularSystemMiniGameController] fiberContract/endA/endB not fully assigned — the muscle mini-game will not function.");
            return;
        }

        // Hand-driven control owns SetContraction() from here on — the
        // deformer's own automatic sine-wave test loop must not also be
        // writing to it at the same time.
        fiberContract.SetDriveAutomatically(false);

        _localAxisDir = fiberContract.LocalAxisDirection;
        _axisCenterLocal = fiberContract.AxisCenterLocal;
        _axisHalfSpanLocal = fiberContract.AxisHalfSpanLocal;
        _minOffsetMagnitude = _axisHalfSpanLocal * (1f - maxPulledInFraction);

        CacheEndGeometry(endA, out _perpendicularA, out _signA);
        CacheEndGeometry(endB, out _perpendicularB, out _signB);
    }

    /// <summary>
    /// Decomposes an end's resting local position (relative to
    /// fiberContract's transform) into a fixed perpendicular offset and
    /// which side of center it starts on, exactly once at Awake.
    /// </summary>
    private void CacheEndGeometry(Transform end, out Vector3 perpendicular, out float sign)
    {
        Vector3 localPos = fiberContract.transform.InverseTransformPoint(end.position);
        float axisValue = Vector3.Dot(localPos, _localAxisDir);
        perpendicular = localPos - axisValue * _localAxisDir;

        float offsetFromCenter = axisValue - _axisCenterLocal;
        sign = offsetFromCenter >= 0f ? 1f : -1f;
    }

    private void Update()
    {
        if (fiberContract == null || endA == null || endB == null) return;

        bool aHeld = false, bHeld = false;
        HandManager.Side aSide = default, bSide = default;

        foreach (HandManager.Side side in new[] { HandManager.Side.Left, HandManager.Side.Right })
        {
            var hand = HandManager.Get(side);
            if (hand == null || !hand.isGrabbed) continue;

            string held = hand.GetHeldObjectName();
            if (held == endAGripName) { aHeld = true; aSide = side; }
            if (held == endBGripName) { bHeld = true; bSide = side; }
        }

        if (aHeld && bHeld)
        {
            DragEnd(endA, HandManager.Get(aSide), _perpendicularA, _signA, out float offsetA);
            DragEnd(endB, HandManager.Get(bSide), _perpendicularB, _signB, out float offsetB);

            float avgOffsetMagnitude = (Mathf.Abs(offsetA) + Mathf.Abs(offsetB)) * 0.5f;
            _currentContraction = Mathf.Clamp01(Mathf.InverseLerp(_axisHalfSpanLocal, _minOffsetMagnitude, avgOffsetMagnitude));

            fiberContract.SetContraction(_currentContraction);
        }
        else
        {
            // Not both hands holding — ease back toward the resting,
            // fully-stretched pose rather than freezing in place.
            _currentContraction = Mathf.MoveTowards(_currentContraction, 0f, relaxSpeed * Time.deltaTime);
            fiberContract.SetContraction(_currentContraction);

            SnapEndTowardRest(endA, _perpendicularA, _signA, relaxSpeed * Time.deltaTime);
            SnapEndTowardRest(endB, _perpendicularB, _signB, relaxSpeed * Time.deltaTime);
        }

        // Runs every frame regardless of hold state — a rising edge past
        // completionThreshold can only actually happen while both hands are
        // driving contraction up, but the re-arm (dropping back below
        // squeezeReleaseThreshold) needs to keep being checked while
        // relaxing after a release too, or letting go mid-squeeze would
        // leave the gate permanently armed-off and no further squeeze could
        // ever be counted.
        UpdateSqueezeTracking();
        UpdateProgressRing();
    }

    /// <summary>
    /// Moves one end directly to wherever its holding hand currently is,
    /// projected onto the fiber's axis and clamped between the MAX
    /// contraction offset and the resting offset — the end can never be
    /// pulled in further than maxPulledInFraction, or stretched out further
    /// than where it started. Outputs the resulting signed offset from
    /// center (in local units) so the caller can feed it into the
    /// contraction mapping.
    /// </summary>
    private void DragEnd(Transform end, HandManager hand, Vector3 perpendicular, float sign, out float signedOffset)
    {
        Vector3 localHandPos = fiberContract.transform.InverseTransformPoint(hand.PalmPosition);
        float axisValue = Vector3.Dot(localHandPos, _localAxisDir);
        float rawOffset = axisValue - _axisCenterLocal;

        // Clamp magnitude to [_minOffsetMagnitude, _axisHalfSpanLocal],
        // preserving this end's own side of center — a hand pulled past the
        // max-contraction point or flung outward beyond the fiber's resting
        // length just holds at whichever limit it hit.
        float magnitude = Mathf.Clamp(Mathf.Abs(rawOffset), _minOffsetMagnitude, _axisHalfSpanLocal);
        signedOffset = sign * magnitude;

        Vector3 newLocalPos = perpendicular + (_axisCenterLocal + signedOffset) * _localAxisDir;
        end.position = fiberContract.transform.TransformPoint(newLocalPos);
    }

    /// <summary>
    /// Eases one end back toward its original resting axis position this
    /// frame. contractionFractionThisFrame is relaxSpeed * Time.deltaTime —
    /// scaled here by the fiber's own half-span so relaxSpeed reads the same
    /// ("roughly this fraction of the fiber relaxes per second") regardless
    /// of how big or small a given fiber model is.
    /// </summary>
    private void SnapEndTowardRest(Transform end, Vector3 perpendicular, float sign, float contractionFractionThisFrame)
    {
        Vector3 currentLocalPos = fiberContract.transform.InverseTransformPoint(end.position);
        float currentAxisValue = Vector3.Dot(currentLocalPos, _localAxisDir);

        float restAxisValue = _axisCenterLocal + sign * _axisHalfSpanLocal;
        float axisUnitsThisFrame = contractionFractionThisFrame * _axisHalfSpanLocal;
        float newAxisValue = Mathf.MoveTowards(currentAxisValue, restAxisValue, axisUnitsThisFrame);

        Vector3 newLocalPos = perpendicular + newAxisValue * _localAxisDir;
        end.position = fiberContract.transform.TransformPoint(newLocalPos);
    }

    /// <summary>
    /// Counts one squeeze each time contraction rises past completionThreshold
    /// from below — a rising edge, not "currently above." Once counted, the
    /// pull has to relax back below squeezeReleaseThreshold before the next
    /// one can count, so a single sustained hold (or jitter right at the
    /// threshold) can't rack up multiple squeezes. Marks the mini-game
    /// complete the moment squeezesRequired is reached, but doesn't stop
    /// counting or touch the grips in any way after that.
    ///
    /// Deliberately does NOT push a live count to AnatomyTutorSession — Carla
    /// isn't watching this counter frame-by-frame, and having her narrate an
    /// exact tally ("that's your second squeeze") reads as if she's tracking
    /// something she isn't. She should encourage the 3-squeeze goal and take
    /// the student's own word for their progress instead; see the Muscular
    /// scene's CurrentObjective text in AnatomyTutorSession for that framing.
    /// _squeezeCount here only drives the gate and the visual ring — it's not
    /// AI-facing.
    /// </summary>
    private void UpdateSqueezeTracking()
    {
        if (!_isAboveThreshold && _currentContraction >= completionThreshold)
        {
            _isAboveThreshold = true;

            if (_squeezeCount < squeezesRequired)
            {
                _squeezeCount++;
                if (verbose) Debug.Log($"[MuscularSystemMiniGameController] Squeeze {_squeezeCount}/{squeezesRequired} registered.");

                if (_squeezeCount >= squeezesRequired && !_hasCompletedOnce)
                {
                    _hasCompletedOnce = true;
                    MiniGameCompletionGate.MarkInteractionComplete(BodySystem.Muscular);
                    if (verbose) Debug.Log("[MuscularSystemMiniGameController] All squeezes complete — interaction gate satisfied.");
                }
            }
        }
        else if (_isAboveThreshold && _currentContraction <= squeezeReleaseThreshold)
        {
            _isAboveThreshold = false; // armed again — next pull past the threshold counts as a new squeeze
        }
    }

    /// <summary>
    /// Feeds progressRing the discrete step fraction — _squeezeCount /
    /// squeezesRequired, nothing smoothed or live in between. Deliberately
    /// NOT blended with in-progress contraction: an in-between fill that
    /// rises while squeezing and falls back while releasing reads as the
    /// ring "breathing" with every pull, which is confusing next to a gate
    /// that's actually counting discrete completed squeezes — and letting
    /// live contraction approach 1.0 on its own risked nudging the OVERALL
    /// fraction past this script's completion trigger before a real
    /// squeeze had actually been counted. Jumps cleanly to 0 → 1/3 → 2/3 → 1
    /// exactly when each squeeze registers, full stop.
    /// </summary>
    private void UpdateProgressRing()
    {
        if (progressRing == null) return;

        float fraction = (float)_squeezeCount / squeezesRequired;
        progressRing.SetProgress(fraction);
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!debugVisualize || fiberContract == null) return;

        Vector3 centerWorld = fiberContract.transform.TransformPoint(_axisCenterLocal * _localAxisDir);
        Vector3 endWorldPos = fiberContract.transform.TransformPoint((_axisCenterLocal + _axisHalfSpanLocal) * _localAxisDir);
        Vector3 endWorldNeg = fiberContract.transform.TransformPoint((_axisCenterLocal - _axisHalfSpanLocal) * _localAxisDir);

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(endWorldNeg, endWorldPos);
        Gizmos.color = Color.cyan;
        Gizmos.DrawSphere(centerWorld, 0.02f);
    }
#endif
}