using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.XR.Hands;
using Unity.XR.CoreUtils;
using System;

/// <summary>
/// Manages hand tracking, fist detection, XRI selection, and shared gesture
/// primitives (pinch, palm pose, palm velocity, generic joint access) for one
/// hand. Set Side in the inspector — Left hand = Left, Right hand = Right.
///
/// Place this component directly on the "Left Hand" / "Right Hand" GameObject
/// in the XR Origin Hands rig (the one that already has the Poke Interactor
/// as a child) — that's the object EnsureInteractorReference() is designed to
/// find things from.
///
/// This is meant to be the ONE place per-hand tracking data is read from.
/// Minigame scripts should pull data from HandManager.Get(Side.Left) /
/// HandManager.Get(Side.Right) rather than each querying XRHandSubsystem
/// themselves — that keeps the subsystem lookup, fist math, and pinch math
/// in one spot instead of duplicated across seven minigame controllers.
/// </summary>
public class HandManager : MonoBehaviour
{
    public enum Side { Left, Right }

    [Header("Configuration")]
    public Side side;

    [Header("State")]
    public bool isGrabbed = false;

    [Header("Interactor Source")]
    [SerializeField] private XRPokeInteractor handInteractor;

    [Header("Fist Settings")]
    [SerializeField] private float fistThreshold = 0.08f;

    [Header("Pinch Settings")]
    [Tooltip("Max distance (meters) between thumb tip and index tip to count as a pinch.")]
    [SerializeField] private float pinchThreshold = 0.02f;

    private XRHandSubsystem handSubsystem;
    private static List<XRHandSubsystem> s_Subsystems = new List<XRHandSubsystem>();
    private XROrigin xrOrigin;

    private Transform visualPalm;
    private Vector3 lastValidWorldPos;
    private Quaternion lastValidWorldRot;
    private bool hasEverBeenTracked = false;
    private bool isManualSelecting = false;
    private float grabCooldownUntil = 0f;
    private bool releaseCycleRunning = false;

    // Tracks the last frame's grab state so we can detect the falling edge
    private bool wasGrabbed = false;

    // Tracks the last frame's pinch state so we can detect edges the same way
    private bool wasPinching = false;

    // ── Static Registry ──────────────────────────────────────────────────────

    private static readonly Dictionary<Side, HandManager> s_instances = new Dictionary<Side, HandManager>();

    /// <summary>
    /// Look up the HandManager for a given side from anywhere (minigame
    /// controllers, etc.) without needing a manually-wired Inspector reference.
    /// Returns null if that hand isn't in the scene (or not yet enabled).
    /// </summary>
    public static HandManager Get(Side side) =>
        s_instances.TryGetValue(side, out var hm) ? hm : null;

    // ── Public Events ─────────────────────────────────────────────────────────

    /// <summary>
    /// Fired on the frame isGrabbed transitions from true to false.
    /// The string argument is the name of the object that was held,
    /// resolved via ColliderNameResolver before the selection clears.
    /// Empty string if nothing identifiable was held.
    /// </summary>
    public event Action<string> OnGrabReleased;

    /// <summary>
    /// Fired on the frame IsPinching transitions from true to false.
    /// Handy for gestures like the digestive glucose-pinch that care about
    /// the moment of release, not just the held state.
    /// </summary>
    public event Action OnPinchReleased;

    // ── Public Gesture State ──────────────────────────────────────────────────

    /// <summary>True while thumb tip and index tip are within pinchThreshold.</summary>
    public bool IsPinching { get; private set; }

    /// <summary>
    /// World-space palm velocity in meters/second, computed from this frame's
    /// and last frame's tracked palm position. Zero on frames where tracking
    /// is lost. Useful for swipe/sweep gestures (respiratory, homeostasis).
    /// </summary>
    public Vector3 PalmVelocity { get; private set; }

    /// <summary>Last known good world-space palm position (falls back to last-tracked if hand drops out this frame).</summary>
    public Vector3 PalmPosition => lastValidWorldPos;

    /// <summary>Last known good world-space palm rotation.</summary>
    public Quaternion PalmRotation => lastValidWorldRot;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        s_instances[side] = this;

        // Static events, so no Instance-null-check ordering risk — force-release
        // any held object the moment a minigame ends (success or failure) so a
        // stale grab never carries over into the next scene/phase.
        MiniGameEvents.OnMiniGameComplete += HandleMiniGameEnded;
        MiniGameEvents.OnMiniGameFailed  += HandleMiniGameEnded;
    }

    private void OnDisable()
    {
        if (s_instances.TryGetValue(side, out var current) && current == this)
            s_instances.Remove(side);

        MiniGameEvents.OnMiniGameComplete -= HandleMiniGameEnded;
        MiniGameEvents.OnMiniGameFailed  -= HandleMiniGameEnded;
    }

    private void HandleMiniGameEnded(string systemName) => ForceRelease();

    void Start()
    {
        xrOrigin = UnityEngine.Object.FindAnyObjectByType<XROrigin>();
        EnsureInteractorReference();
        ConfigureInteractorInput();
        FindVisualPalm();

        lastValidWorldPos = transform.position;
        lastValidWorldRot = transform.rotation;
    }

    void Update()
    {
        EnsureInteractorReference();

        Vector3 previousPos = lastValidWorldPos;
        bool trackedThisFrame = false;

        if (TryGetPalmWorldPose(out Vector3 pPos, out Quaternion pRot))
        {
            lastValidWorldPos   = pPos;
            lastValidWorldRot   = pRot;
            hasEverBeenTracked  = true;
            trackedThisFrame    = true;
        }
        else if (visualPalm != null && visualPalm.gameObject.activeInHierarchy)
        {
            lastValidWorldPos   = visualPalm.position;
            lastValidWorldRot   = visualPalm.rotation;
            hasEverBeenTracked  = true;
            trackedThisFrame    = true;
        }

        // Palm velocity — only meaningful when we actually got a fresh reading
        // this frame; zero it out on tracking-loss frames rather than reporting
        // a stale/garbage velocity.
        PalmVelocity = (trackedThisFrame && Time.deltaTime > 0f)
            ? (lastValidWorldPos - previousPos) / Time.deltaTime
            : Vector3.zero;

        if (hasEverBeenTracked && handInteractor != null)
        {
            handInteractor.transform.position = lastValidWorldPos;
            handInteractor.transform.rotation = lastValidWorldRot;
        }

        bool gestureActive = EvaluateClosedFist();

        // Detect grab release — fire event before UpdateXRISelection clears
        // the interactor's selection so GetHeldObjectName() still works.
        if (wasGrabbed && !gestureActive)
            FireGrabReleased();

        wasGrabbed = gestureActive;

        UpdateXRISelection(gestureActive);
        isGrabbed = gestureActive;

        // Pinch is independent of the fist/grab XRI selection logic above —
        // it doesn't drive interactor selection, it's just a state minigame
        // scripts can poll or subscribe to directly.
        bool pinchActive = EvaluatePinch();
        IsPinching = pinchActive;

        if (wasPinching && !pinchActive)
            OnPinchReleased?.Invoke();

        wasPinching = pinchActive;
    }

    // ── Force Release ─────────────────────────────────────────────────────────

    private void ForceRelease()
    {
        if (handInteractor == null) return;

        isManualSelecting = false;
        grabCooldownUntil = Time.time + 0.5f;

        if (HasCurrentSelection())
        {
            try { handInteractor.EndManualInteraction(); }
            catch (System.Exception) { }

            try
            {
                var mgr = handInteractor.interactionManager;
                if (mgr != null)
                    mgr.CancelInteractorSelection(
                        (UnityEngine.XR.Interaction.Toolkit.Interactors.IXRSelectInteractor)handInteractor);
            }
            catch (System.Exception) { }
        }

        if (!releaseCycleRunning)
            StartCoroutine(CycleInteractorEnabled());
    }

    private IEnumerator CycleInteractorEnabled()
    {
        releaseCycleRunning = true;
        handInteractor.enabled = false;
        yield return null;
        handInteractor.enabled = true;
        releaseCycleRunning = false;
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    private void ConfigureInteractorInput()
    {
        if (handInteractor == null) return;

        // XRPokeInteractor has no selectInput / input-reader setup to disable
        // (unlike NearFarInteractor, it doesn't extend XRBaseInputInteractor) —
        // its native select behavior comes purely from physical poke depth
        // against an XRPokeFilter, not from an input action. We still clear
        // attachTransform since HandManager drives selection manually via
        // StartManualInteraction/EndManualInteraction based on the fist gesture,
        // not via the poke depth itself.
        handInteractor.attachTransform = null;

        // Poke depth is a physical touch — if physics collision poke-selects an
        // object on its own (independent of the fist gesture), that will race
        // against the manual grab logic below. Disabling physics-layer overlap
        // here would defeat the point of using a poke interactor at all, so
        // this is left enabled: it's on the scene/prefab setup (poke filter
        // depth, physics layers) to make sure only intended surfaces are
        // poke-selectable, and on this script's UpdateXRISelection() to react
        // to whichever selection state currently exists.
    }

    private void FindVisualPalm()
    {
        // Legacy fallback from a previous hand-model naming scheme (L_Palm /
        // R_Palm). The current XR Origin Hands rig's visuals
        // (LeftHandQuestVisual / LeftHandAndroidXRVisual, etc.) won't have a
        // child with this exact name, so this will simply find nothing and
        // visualPalm stays null — that's fine, TryGetPalmWorldPose() from
        // XRHandSubsystem is the real source of truth and doesn't depend on
        // this at all. Only worth wiring up again if hand tracking itself
        // drops out and you want a fallback pose from the rendered hand mesh.
        if (transform.parent == null) return;

        string palmName = side == Side.Left ? "L_Palm" : "R_Palm";
        foreach (Transform child in transform.parent.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == palmName && child.gameObject.activeInHierarchy)
            {
                visualPalm = child;
                return;
            }
        }
    }

    private void EnsureInteractorReference()
    {
        if (handInteractor != null && handInteractor.gameObject.activeInHierarchy) return;
        handInteractor = GetComponentInChildren<XRPokeInteractor>(true);
        if (handInteractor == null && transform.parent != null)
            handInteractor = transform.parent.GetComponentInChildren<XRPokeInteractor>(true);
    }

    // ── XRI Selection ─────────────────────────────────────────────────────────

    private void UpdateXRISelection(bool active)
    {
        if (handInteractor == null || !handInteractor.enabled || !handInteractor.gameObject.activeInHierarchy)
            return;

        if (!active)
        {
            if (isManualSelecting || HasCurrentSelection())
                ForceRelease();
            return;
        }

        if (Time.time < grabCooldownUntil)
        {
            if (HasCurrentSelection())
                ForceRelease();
            return;
        }

        if (isManualSelecting && !HasCurrentSelection())
        {
            isManualSelecting = false;
            grabCooldownUntil = Time.time + 0.5f;
            return;
        }

        if (!isManualSelecting && HasCurrentSelection())
        {
            ForceRelease();
            return;
        }

        if (!isManualSelecting)
        {
            var targets = handInteractor.interactablesHovered;
            if (targets != null && targets.Count > 0)
            {
                var target = targets[0] as UnityEngine.XR.Interaction.Toolkit.Interactables.IXRSelectInteractable;
                if (target != null && target.IsSelectableBy(handInteractor))
                {
                    handInteractor.StartManualInteraction(target);
                    isManualSelecting = true;
                }
            }
        }
    }

    private bool HasCurrentSelection()
    {
        return handInteractor != null
            && handInteractor.interactablesSelected != null
            && handInteractor.interactablesSelected.Count > 0;
    }

    // ── Grab Release ──────────────────────────────────────────────────────────

    private void FireGrabReleased()
    {
        string heldName = GetHeldObjectName();
        OnGrabReleased?.Invoke(heldName);
    }

    /// <summary>
    /// Returns the ColliderNameResolver display name of the currently held
    /// object, or "" if nothing is held or the name cannot be resolved.
    /// Safe to call from OnGrabReleased listeners — fires before selection clears.
    /// </summary>
    public string GetHeldObjectName()
    {
        if (handInteractor == null) return "";

        var selected = handInteractor.interactablesSelected;
        if (selected == null || selected.Count == 0) return "";

        var interactable = selected[0];
        if (interactable == null) return "";

        Transform t = (interactable as UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable)?.transform
                   ?? (interactable as Component)?.transform;

        if (t == null) return "";

        return ColliderNameResolver.ResolveName(t);
    }

    /// <summary>
    /// Returns the world-space position of the currently held object,
    /// or Vector3.zero if nothing is held.
    /// </summary>
    public Vector3 GetHeldObjectPosition()
    {
        if (handInteractor == null) return Vector3.zero;

        var selected = handInteractor.interactablesSelected;
        if (selected == null || selected.Count == 0) return Vector3.zero;

        var interactable = selected[0];
        Transform t = (interactable as UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable)?.transform
                   ?? (interactable as Component)?.transform;

        return t != null ? t.position : Vector3.zero;
    }

    /// <summary>
    /// Returns the Bounds of the currently held object's first collider,
    /// or an empty Bounds at Vector3.zero if nothing is held.
    /// </summary>
    public Bounds GetHeldObjectBounds()
    {
        if (handInteractor == null) return new Bounds();

        var selected = handInteractor.interactablesSelected;
        if (selected == null || selected.Count == 0) return new Bounds();

        var interactable = selected[0];
        Transform t = (interactable as UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable)?.transform
                   ?? (interactable as Component)?.transform;

        if (t == null) return new Bounds();

        Collider col = t.GetComponentInChildren<Collider>();
        return col != null ? col.bounds : new Bounds(t.position, Vector3.one * 0.1f);
    }

    // ── Hand Tracking ─────────────────────────────────────────────────────────

    /// <summary>
    /// Public wrapper so minigame scripts can read this hand's palm pose
    /// directly (respiratory diaphragm push, homeostasis sweep, etc.)
    /// without duplicating the XRHandSubsystem lookup themselves.
    /// Prefer the PalmPosition/PalmRotation properties for the "last known
    /// good" pose (handles tracking dropout gracefully) — use this method
    /// directly only if you specifically need to know whether the hand is
    /// tracked THIS exact frame.
    /// </summary>
    public bool TryGetPalmWorldPose(out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero;
        rot = Quaternion.identity;

        if (handSubsystem == null || !handSubsystem.running) TryFindHandSubsystem();
        if (handSubsystem == null || !handSubsystem.running) return false;

        XRHand hand = side == Side.Left ? handSubsystem.leftHand : handSubsystem.rightHand;
        if (!hand.isTracked) return false;

        var palm = hand.GetJoint(XRHandJointID.Palm);
        if (!palm.TryGetPose(out var pose)) return false;

        if (xrOrigin != null)
        {
            pos = xrOrigin.transform.TransformPoint(pose.position);
            rot = xrOrigin.transform.rotation * pose.rotation;
        }
        else
        {
            pos = pose.position;
            rot = pose.rotation;
        }

        return true;
    }

    /// <summary>
    /// Generic joint access for gestures that need more than the palm —
    /// skeletal/muscular's two-point grab and digestive's knead-along-a-path
    /// both want specific finger/wrist joints. World space, same
    /// XROrigin-relative transform as TryGetPalmWorldPose().
    /// </summary>
    public bool TryGetJointWorldPose(XRHandJointID jointID, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero;
        rot = Quaternion.identity;

        if (handSubsystem == null || !handSubsystem.running) TryFindHandSubsystem();
        if (handSubsystem == null || !handSubsystem.running) return false;

        XRHand hand = side == Side.Left ? handSubsystem.leftHand : handSubsystem.rightHand;
        if (!hand.isTracked) return false;

        var joint = hand.GetJoint(jointID);
        if (!joint.TryGetPose(out var pose)) return false;

        if (xrOrigin != null)
        {
            pos = xrOrigin.transform.TransformPoint(pose.position);
            rot = xrOrigin.transform.rotation * pose.rotation;
        }
        else
        {
            pos = pose.position;
            rot = pose.rotation;
        }

        return true;
    }

    private bool EvaluateClosedFist()
    {
        if (handSubsystem == null || !handSubsystem.running) TryFindHandSubsystem();
        if (handSubsystem == null || !handSubsystem.running) return false;

        XRHand hand = side == Side.Left ? handSubsystem.leftHand : handSubsystem.rightHand;
        if (!hand.isTracked) return false;

        var palm = hand.GetJoint(XRHandJointID.Palm);
        if (!palm.TryGetPose(out var pPose)) return false;

        int curled = 0;
        XRHandJointID[] tips =
        {
            XRHandJointID.IndexTip, XRHandJointID.MiddleTip,
            XRHandJointID.RingTip,  XRHandJointID.LittleTip
        };

        foreach (var id in tips)
        {
            if (hand.GetJoint(id).TryGetPose(out var tipPose))
                if (Vector3.Distance(pPose.position, tipPose.position) <= fistThreshold)
                    curled++;
        }

        return curled >= 3;
    }

    /// <summary>
    /// Thumb-tip-to-index-tip distance check. Local-space joint positions are
    /// used (not world) since we only care about the distance between two
    /// joints on the same hand — no need to go through XROrigin transforms
    /// just to measure a relative distance.
    /// </summary>
    private bool EvaluatePinch()
    {
        if (handSubsystem == null || !handSubsystem.running) TryFindHandSubsystem();
        if (handSubsystem == null || !handSubsystem.running) return false;

        XRHand hand = side == Side.Left ? handSubsystem.leftHand : handSubsystem.rightHand;
        if (!hand.isTracked) return false;

        if (!hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumbPose)) return false;
        if (!hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var indexPose)) return false;

        return Vector3.Distance(thumbPose.position, indexPose.position) <= pinchThreshold;
    }

    private void TryFindHandSubsystem()
    {
        SubsystemManager.GetSubsystems(s_Subsystems);
        foreach (var s in s_Subsystems)
        {
            if (s.running) { handSubsystem = s; return; }
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void SetInteractionEnabled(bool enabled)
    {
        if (handInteractor != null)
            handInteractor.enabled = enabled;
    }
}