using System.Collections;
using UnityEngine;

/// <summary>
/// Keeps a World Space Canvas parked up-and-to-the-left of the player, with
/// two deliberately simple rules:
///
///   - ROTATION never changes after the initial placement. The panel does
///     not track head rotation in any way, ever — turning your head just
///     turns your head; the panel stays exactly where and how it was
///     anchored.
///
///   - POSITION only updates while AutoWalkController.IsWalking is true —
///     i.e. only while the player is actually being moved (this project has
///     no manual locomotion, only AutoWalkController-driven movement), so
///     the panel keeps pace with the rig during a walk and otherwise just
///     sits still, full stop.
///
/// On top of that, a small constant sine-wave bob is layered onto whatever
/// the current position is, purely cosmetic, so the panel feels like it's
/// gently floating instead of glued rigidly in place.
///
/// IMPORTANT: this only ever reads headTransform (to compute the initial
/// anchor offset and to know where to translate toward while walking) and
/// AutoWalkController.IsWalking — no dependency on controllers, thumbsticks,
/// or any other input device.
///
/// Attach this to the root of the HUD Canvas (the same object BodyDashboardHUD
/// lives on, or its parent) — NOT as a child of the camera.
/// </summary>
public class HUDFollowController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Usually the XR camera. Auto-finds Camera.main if left empty.")]
    public Transform headTransform;
    [Tooltip("Auto-found via FindAnyObjectByType if left empty. The panel only actively repositions " +
             "while THIS is walking (IsWalking) — while it's null, not present in the scene, or not " +
             "walking, the panel just stays exactly where it currently is.")]
    public AutoWalkController autoWalkController;

    [Header("Placement")]
    [Tooltip("Distance from the player to the panel, in meters, computed once at startup.")]
    public float distance = 1.0f;
    [Tooltip("Vertical offset from head height, in meters. Positive sits the panel above eye line — " +
             "combined with the horizontal offset below, this is what puts it 'up and left.'")]
    public float verticalOffset = 0.15f;
    [Tooltip("Degrees to swing the panel away from the player's initial forward direction, around the " +
             "up axis, computed once at startup. Negative = left, positive = right (Unity's standard " +
             "sign for rotation around Y). E.g. -40 puts the panel up and to the left.")]
    public float horizontalOffsetDegrees = -40f;

    [Header("Follow Behavior")]
    [Tooltip("How quickly the panel eases toward the player's position while AutoWalkController is " +
             "walking. Has no effect at any other time — the panel simply does not move when not " +
             "walking, regardless of head rotation or movement.")]
    public float followSpeed = 4f;

    [Header("Hover")]
    [Tooltip("How far the panel bobs up and down, in meters. Purely cosmetic — a small idle 'floating' " +
             "motion so the panel doesn't feel glued in place. Runs continuously, walking or not.")]
    public float hoverAmplitude = 0.02f;
    [Tooltip("How fast the hover bob cycles, in radians/second.")]
    public float hoverSpeed = 1.5f;

    [Header("Startup")]
    [Tooltip("Frames to wait before checking head height, so XR tracking has had at least a couple " +
             "frames to start reporting. This alone usually isn't enough on its own — see " +
             "minPlausibleHeadHeight below for the actual safety net.")]
    public int startupDelayFrames = 3;
    [Tooltip("Minimum head Y position (meters) considered a real, tracked pose rather than the rig's " +
             "raw pre-tracking transform (often sitting near 0). SnapToHead won't fire until headTransform " +
             "is at or above this height — without this check, snapping too early could permanently lock " +
             "the panel near the floor, since position only updates while walking and wouldn't get a " +
             "chance to self-correct until the next walk starts. Set below your shortest player's actual " +
             "eye height.")]
    public float minPlausibleHeadHeight = 0.5f;
    [Tooltip("Give up waiting for a plausible head height after this many extra frames (on top of " +
             "startupDelayFrames) and snap anyway, so a genuinely unusual rig setup doesn't leave the " +
             "panel waiting forever.")]
    public int maxExtraWaitFrames = 120;

    // Fixed forever after SnapToHead — this is what makes position updates
    // translation-only. Rotation is set once in SnapToHead and never touched
    // again anywhere in this script.
    private Vector3 _anchorOffset;
    private Vector3 _basePosition;
    private bool _initialized;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        if (headTransform == null)
        {
            Camera cam = Camera.main;
            if (cam != null) headTransform = cam.transform;
        }

        if (autoWalkController == null)
            autoWalkController = FindAnyObjectByType<AutoWalkController>();

        _initialized = false;
        StartCoroutine(DelayedFirstSnap());
    }

    private IEnumerator DelayedFirstSnap()
    {
        for (int i = 0; i < Mathf.Max(1, startupDelayFrames); i++)
            yield return null;

        // Extra safety net: keep waiting (up to maxExtraWaitFrames) if the head
        // still isn't at a plausible height yet — XR tracking can take longer
        // than a couple frames to report a real pose, and since position only
        // updates while walking, a bad initial snap wouldn't get a chance to
        // self-correct until the next walk starts.
        int extraFrames = 0;
        while (headTransform != null && headTransform.position.y < minPlausibleHeadHeight
               && extraFrames < maxExtraWaitFrames)
        {
            yield return null;
            extraFrames++;
        }

        if (headTransform != null) SnapToHead();
    }

    private void Update()
    {
        if (!_initialized || headTransform == null) return;

        // Re-resolve if the scene changed underneath us (AutoWalkController
        // lives on a per-scene GameObject, not a persistent one) and we
        // don't currently have a valid reference.
        if (autoWalkController == null)
            autoWalkController = FindAnyObjectByType<AutoWalkController>();

        bool isWalking = autoWalkController != null && autoWalkController.IsWalking;

        if (isWalking)
        {
            Vector3 desiredBase = headTransform.position + _anchorOffset;
            _basePosition = Vector3.Lerp(_basePosition, desiredBase, followSpeed * Time.deltaTime);
        }
        // Not walking: _basePosition simply doesn't change. No head-rotation
        // or head-position dependency at all while stationary.

        transform.position = _basePosition + HoverOffset();
        // Rotation is intentionally never set here — it stays whatever
        // SnapToHead left it as.
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SnapToHead()
    {
        Vector3 flatForward = FlattenAndNormalize(headTransform.forward);
        Vector3 offsetDirection = Quaternion.AngleAxis(horizontalOffsetDegrees, Vector3.up) * flatForward;

        _anchorOffset = offsetDirection * distance;
        _anchorOffset.y = verticalOffset;

        _basePosition = headTransform.position + _anchorOffset;

        Vector3 lookDirection = _basePosition - headTransform.position;
        lookDirection.y = 0f;
        if (lookDirection.sqrMagnitude < 0.0001f) lookDirection = offsetDirection;
        Quaternion rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);

        transform.SetPositionAndRotation(_basePosition + HoverOffset(), rotation);
        _initialized = true;
    }

    private Vector3 HoverOffset() => Vector3.up * Mathf.Sin(Time.time * hoverSpeed) * hoverAmplitude;

    private static Vector3 FlattenAndNormalize(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 0.0001f ? Vector3.forward : v.normalized;
    }
}