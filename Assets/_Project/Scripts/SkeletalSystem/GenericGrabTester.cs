using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;


/// <summary>
/// Minimal, console-only diagnostic for testing the VR grab pipeline on a
/// plain test object (a cube, a sphere, anything) — completely separate from
/// the femur/tibia bones, so it isolates whether a problem is in the
/// interaction system itself (Interaction Manager, Poke Interactor, XR
/// Interaction Group) versus something specific to the bone objects (mesh
/// collider shape, the Static flag, ColliderNameResolver name matching).
///
/// Reports both layers to the Console:
///   1. LOW-LEVEL (XRI): hoverEntered / selectEntered straight from this
///      object's own XRSimpleInteractable — tells you whether the Poke
///      Interactor is physically reaching this collider at all.
///   2. HIGH-LEVEL (HandManager): polls HandManager.Get(side).isGrabbed /
///      GetHeldObjectName() every frame — the same mechanism the real
///      mini-game controller uses.
///
/// Setup for the test cube:
///   1. Create a Cube (GameObject > 3D Object > Cube).
///   2. Make sure it has a Collider with "Is Trigger" checked (a Box
///      Collider works fine — doesn't need to be a Capsule).
///   3. Add a Rigidbody, Is Kinematic checked, Use Gravity off (matches how
///      the femur/tibia are set up).
///   4. Add an XR Simple Interactable component.
///   5. Add this script.
///   6. Do NOT mark the cube "Static" in the Inspector header.
///
/// No Inspector configuration needed on this script itself — just add it
/// and watch the Console while grabbing the cube in VR.
/// </summary>
[RequireComponent(typeof(XRBaseInteractable))]
public class GenericGrabTester : MonoBehaviour
{
    private XRBaseInteractable _interactable;
    private string _resolvedName;

    private bool _isHeld;
    private HandManager.Side? _holdingSide;

    private void Awake()
    {
        _interactable = GetComponent<XRBaseInteractable>();
        _resolvedName = ColliderNameResolver.ResolveName(transform);
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

    private void OnHoverEntered(HoverEnterEventArgs args) =>
        Debug.Log($"[GenericGrabTester] [LOW-LEVEL] '{_resolvedName}' HOVER ENTERED by {args.interactorObject}.");

    private void OnHoverExited(HoverExitEventArgs args) =>
        Debug.Log($"[GenericGrabTester] [LOW-LEVEL] '{_resolvedName}' hover exited.");

    private void OnSelectEntered(SelectEnterEventArgs args) =>
        Debug.Log($"[GenericGrabTester] [LOW-LEVEL] '{_resolvedName}' XRI SELECT ENTERED by {args.interactorObject}.");

    private void OnSelectExited(SelectExitEventArgs args) =>
        Debug.Log($"[GenericGrabTester] [LOW-LEVEL] '{_resolvedName}' XRI select exited.");

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

        if (heldThisFrame != _isHeld)
        {
            _isHeld = heldThisFrame;
            _holdingSide = sideThisFrame;

            string state = _isHeld ? $"GRABBED by {sideThisFrame}" : "RELEASED";
            Debug.Log($"[GenericGrabTester] [HIGH-LEVEL] '{_resolvedName}' {state}");
        }
    }
}