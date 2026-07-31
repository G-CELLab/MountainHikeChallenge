using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Drop-in diagnostic script for verifying two-hand VR grab detection on a
/// single bone (femur_bone, tibia_bone, etc.) — the SAME script/component can
/// be added to either object with zero per-object configuration.
///
/// Reports TWO separate layers, since a silent failure could be in either:
///
///   1. LOW-LEVEL (XRI): hoverEntered/selectEntered straight from this
///      object's own XRSimpleInteractable — the same events
///      KneeJointGripFeedback listens to. This tells you whether the Poke
///      Interactor is even physically reaching this collider at all,
///      completely independent of the fist gesture.
///
///   2. HIGH-LEVEL (HandManager): polls HandManager.Get(side).isGrabbed /
///      GetHeldObjectName() every frame — the exact mechanism
///      SkeletalSystemMiniGameController itself uses to decide
///      "stationaryHeld" / "femurHeld". This only ever succeeds if #1 is
///      already working AND the fist gesture is active at the right moment
///      AND the resolved name matches.
///
/// If you see LOW-LEVEL hover logs but never HIGH-LEVEL grabbed logs, the
/// problem is gesture timing/cooldown, not proximity. If you never see
/// LOW-LEVEL hover logs at all, the Poke Interactor isn't reaching the
/// collider — check collider size/placement, interaction layers, and the
/// Poke Interactor's poke depth/radius settings.
///
/// Setup: just add this component to femur_bone and to tibia_bone (or any
/// other grippable bone). It reads its own name via ColliderNameResolver at
/// Awake, so there's nothing to wire up in the Inspector.
/// </summary>
[RequireComponent(typeof(XRBaseInteractable))]
public class GrabFeatureTester : MonoBehaviour
{
    [Header("Debug Output")]
    [SerializeField] private bool logToConsole = true;
    [SerializeField] private bool showOnScreenGUI = true;

    [Header("On-Screen GUI")]
    [Tooltip("Vertical slot this instance's status box uses on screen, so multiple testers (e.g. femur + tibia) stack instead of overlapping. Give femur 0, tibia 1, etc.")]
    [SerializeField] private int guiSlot = 0;

    private string _resolvedName;
    private bool _isHeld;
    private HandManager.Side? _holdingSide;
    private bool _isHovered;
    private bool _isXRISelected;

    private XRBaseInteractable _interactable;

    /// <summary>True on any frame HandManager reports this object as held by either hand.</summary>
    public bool IsHeld => _isHeld;

    /// <summary>Which hand is currently holding this object, if any.</summary>
    public HandManager.Side? HoldingSide => _holdingSide;

    private void Awake()
    {
        _resolvedName = ColliderNameResolver.ResolveName(transform);
        _interactable = GetComponent<XRBaseInteractable>();
    }

    private void OnEnable()
    {
        _interactable.hoverEntered.AddListener(OnHoverEntered);
        _interactable.hoverExited.AddListener(OnHoverExited);
        _interactable.selectEntered.AddListener(OnSelectEntered);
        _interactable.selectExited.AddListener(OnSelectExited);
    }

    private void OnDisable()
    {
        _interactable.hoverEntered.RemoveListener(OnHoverEntered);
        _interactable.hoverExited.RemoveListener(OnHoverExited);
        _interactable.selectEntered.RemoveListener(OnSelectEntered);
        _interactable.selectExited.RemoveListener(OnSelectExited);
    }

    private void OnHoverEntered(HoverEnterEventArgs args)
    {
        _isHovered = true;
        if (logToConsole)
            Debug.Log($"[GrabFeatureTester] [LOW-LEVEL] '{_resolvedName}' HOVER ENTERED by {args.interactorObject}.");
    }

    private void OnHoverExited(HoverExitEventArgs args)
    {
        _isHovered = false;
        if (logToConsole)
            Debug.Log($"[GrabFeatureTester] [LOW-LEVEL] '{_resolvedName}' hover exited.");
    }

    private void OnSelectEntered(SelectEnterEventArgs args)
    {
        _isXRISelected = true;
        if (logToConsole)
            Debug.Log($"[GrabFeatureTester] [LOW-LEVEL] '{_resolvedName}' XRI SELECT ENTERED by {args.interactorObject}.");
    }

    private void OnSelectExited(SelectExitEventArgs args)
    {
        _isXRISelected = false;
        if (logToConsole)
            Debug.Log($"[GrabFeatureTester] [LOW-LEVEL] '{_resolvedName}' XRI select exited.");
    }

    private void Update()
    {
        bool heldThisFrame = false;
        HandManager.Side? sideThisFrame = null;

        foreach (HandManager.Side side in new[] { HandManager.Side.Left, HandManager.Side.Right })
        {
            var hand = HandManager.Get(side);
            if (hand == null || !hand.isGrabbed) continue;

            if (hand.GetHeldObjectName() == _resolvedName)
            {
                heldThisFrame = true;
                sideThisFrame = side;
                break;
            }
        }

        // Only log/update on the actual transition, not every frame, so the
        // Console stays readable during a longer test session.
        if (heldThisFrame != _isHeld)
        {
            _isHeld = heldThisFrame;
            _holdingSide = sideThisFrame;

            if (logToConsole)
            {
                string state = _isHeld ? $"GRABBED by {sideThisFrame}" : "RELEASED";
                Debug.Log($"[GrabFeatureTester] [HIGH-LEVEL] '{_resolvedName}' {state}");
            }
        }
    }

    private void OnGUI()
    {
        if (!showOnScreenGUI) return;

        var rect = new Rect(10, 10 + guiSlot * 50, 340, 45);

        GUI.color = _isHeld ? Color.green : (_isHovered ? Color.yellow : Color.gray);
        string label =
            $"{_resolvedName}\n" +
            $"hover:{_isHovered}  xriSelect:{_isXRISelected}  grabbed:{_isHeld}{(_holdingSide.HasValue ? $" ({_holdingSide})" : "")}";
        GUI.Box(rect, label);
        GUI.color = Color.white;
    }
}