using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Diagnostic-turned-functional touch script — attach to a simple test
/// object OR a neuron (anything with a trigger Collider and, optionally, an
/// XR Simple Interactable) to isolate/exercise the interaction pipeline:
///
///   1. OnTriggerEnter  -> physical trigger collider contact is happening at all
///      (this fires regardless of XRI, so it proves colliders/layers/physics
///       are set up correctly, independent of the interaction toolkit)
///   2. OnHoverEntered  -> XRI's interactor sees this object as a valid target
///      (requires: shared Interaction Manager, matching Interaction Layer Mask)
///   3. OnSelectEntered -> the actual "poke/select" registered
///      (requires: everything above PLUS Require Poke Filter satisfied, or
///       unchecked, and the interactor's poke depth actually reaching the
///       collider)
///
/// On a "touch" (either the raw trigger contact OR an XRI select — whichever
/// gets there first), this now calls NeuronVisual.Discharge() if one is
/// present on the object, so a neuron flashes/shrinks away instead of just
/// vanishing. If there's no NeuronVisual (e.g. still using the plain test
/// cube), it falls back to the old SetActive(false) behavior so this script
/// keeps working as a pipeline diagnostic too.
///
/// Read the Console top-to-bottom: whichever numbered log is the LAST one
/// to appear tells us exactly where the pipeline stops.
/// </summary>
[RequireComponent(typeof(XRSimpleInteractable))]
public class PokeTestLogger : MonoBehaviour
{
    private XRSimpleInteractable _interactable;
    private NeuronVisual _neuronVisual;

    private void Awake()
    {
        _interactable = GetComponent<XRSimpleInteractable>();
        _neuronVisual = GetComponent<NeuronVisual>(); // null is fine — Activate() handles it
    }

    private void OnEnable()
    {
        _interactable.hoverEntered.AddListener(OnHoverEntered);
        _interactable.hoverExited.AddListener(OnHoverExited);
        _interactable.selectEntered.AddListener(OnSelectEntered);
    }

    private void OnDisable()
    {
        _interactable.hoverEntered.RemoveListener(OnHoverEntered);
        _interactable.hoverExited.RemoveListener(OnHoverExited);
        _interactable.selectEntered.RemoveListener(OnSelectEntered);
    }

    // Fires on raw physics trigger contact — proves the Collider/Rigidbody/
    // layer setup itself works, completely independent of XRI. This is also
    // currently the reliable "was I touched" signal for neurons.
    private void OnTriggerEnter(Collider other)
    {
        Debug.Log($"[PokeTest] 1. OnTriggerEnter — physical contact from '{other.name}' " +
                   $"(layer: {LayerMask.LayerToName(other.gameObject.layer)})");

        Activate();
    }

    private void OnTriggerExit(Collider other)
    {
        Debug.Log($"[PokeTest] 1b. OnTriggerExit — contact with '{other.name}' ended");
    }

    private void OnHoverEntered(HoverEnterEventArgs args)
    {
        Debug.Log($"[PokeTest] 2. OnHoverEntered — XRI sees this as a valid hover target, " +
                   $"interactor: '{args.interactorObject.transform.name}'");
    }

    private void OnHoverExited(HoverExitEventArgs args)
    {
        Debug.Log($"[PokeTest] 2b. OnHoverExited — interactor '{args.interactorObject.transform.name}' left hover");
    }

    private void OnSelectEntered(SelectEnterEventArgs args)
    {
        Debug.Log($"[PokeTest] 3. OnSelectEntered — SELECT REGISTERED from " +
                   $"'{args.interactorObject.transform.name}' — full pipeline works!");

        Activate();
    }

    // Shared "this thing was touched" entry point for both detection paths.
    // Safe to call twice (e.g. trigger fires, then select also fires) —
    // NeuronVisual.Discharge() already guards against double-firing itself,
    // and SetActive(false) is idempotent.
    private void Activate()
    {
        if (_neuronVisual != null)
            _neuronVisual.Discharge();
        else
            gameObject.SetActive(false);
    }
}