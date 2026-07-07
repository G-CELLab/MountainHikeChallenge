using System.Collections;
using UnityEngine;

/// <summary>
/// Keeps a World Space Canvas floating in front of the player, using
/// smoothed "lazy follow" rather than rigid camera-parenting.
///
/// Rigidly parenting UI directly to the camera is a common VR comfort
/// complaint — it feels glued to your face and moves with every micro head
/// movement. This instead only starts catching up once the player has
/// turned far enough that the panel would otherwise leave their view, then
/// eases toward the new position/rotation rather than snapping.
///
/// IMPORTANT: this only ever reads headTransform (the XR camera). It has no
/// dependency on controllers, thumbsticks, or any input device — head
/// rotation alone (physical neck movement in a real headset) is what drives
/// repositioning. That means it works identically for a hand-tracking-only,
/// auto-walked setup with no locomotion input at all.
///
/// Attach this to the root of the HUD Canvas (the same object BodyDashboardHUD
/// lives on, or its parent) — NOT as a child of the camera.
/// </summary>
public class HUDFollowController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Usually the XR camera. Auto-finds Camera.main if left empty.")]
    public Transform headTransform;

    [Header("Placement")]
    [Tooltip("Distance in front of the head, in meters.")]
    public float distance = 1.2f;
    [Tooltip("Vertical offset from head height, in meters. Negative sits the panel slightly below eye line, like a real HUD.")]
    public float verticalOffset = -0.25f;

    [Header("Follow Behavior")]
    [Tooltip("Degrees the head must turn away before the panel starts repositioning. Higher = panel stays put through more head movement.")]
    public float followAngleThreshold = 25f;
    [Tooltip("How quickly the panel eases to its new spot once triggered. Higher = snappier, lower = floatier.")]
    public float followSpeed = 4f;
    [Tooltip("Ignore head pitch (looking up/down) when orienting the panel, so it always stays upright instead of tilting with the camera.")]
    public bool keepUpright = true;

    [Header("Startup")]
    [Tooltip("Frames to wait before the first snap, so XR tracking has reported a real head position/rotation instead of the rig's raw pre-tracking transform (often (0,0,0) locally, which is what was causing the panel to appear below the floor on scene start).")]
    public int startupDelayFrames = 3;

    private Vector3 _targetPosition;
    private Quaternion _targetRotation;
    private bool _initialized;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        if (headTransform == null)
        {
            Camera cam = Camera.main;
            if (cam != null) headTransform = cam.transform;
        }

        _initialized = false;
        StartCoroutine(DelayedFirstSnap());
    }

    private IEnumerator DelayedFirstSnap()
    {
        // Wait a few frames so XR tracking has had a chance to report a real
        // pose before we snap to it. Snapping on frame 0 risks reading the
        // rig's raw pre-tracking transform.
        for (int i = 0; i < Mathf.Max(1, startupDelayFrames); i++)
            yield return null;

        if (headTransform != null) SnapToHead();
    }

    private void LateUpdate()
    {
        if (headTransform == null || !_initialized) return;

        Vector3 desiredPosition = ComputeDesiredPosition();
        Quaternion desiredRotation = ComputeDesiredRotation(desiredPosition);

        float angleFromCurrentTarget = Quaternion.Angle(transform.rotation, desiredRotation);

        if (angleFromCurrentTarget > followAngleThreshold)
        {
            _targetPosition = desiredPosition;
            _targetRotation = desiredRotation;
        }

        transform.position = Vector3.Lerp(transform.position, _targetPosition, followSpeed * Time.deltaTime);
        transform.rotation = Quaternion.Slerp(transform.rotation, _targetRotation, followSpeed * Time.deltaTime);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SnapToHead()
    {
        _targetPosition = ComputeDesiredPosition();
        _targetRotation = ComputeDesiredRotation(_targetPosition);
        transform.SetPositionAndRotation(_targetPosition, _targetRotation);
        _initialized = true;
    }

    private Vector3 ComputeDesiredPosition()
    {
        Vector3 forwardFlat = headTransform.forward;
        forwardFlat.y = 0f;
        if (forwardFlat.sqrMagnitude < 0.0001f) forwardFlat = Vector3.forward;
        forwardFlat.Normalize();

        Vector3 position = headTransform.position + forwardFlat * distance;
        position.y = headTransform.position.y + verticalOffset;
        return position;
    }

    private Quaternion ComputeDesiredRotation(Vector3 panelPosition)
    {
        Vector3 lookDirection = panelPosition - headTransform.position;
        if (keepUpright) lookDirection.y = 0f;
        if (lookDirection.sqrMagnitude < 0.0001f) lookDirection = headTransform.forward;
        return Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
    }
}