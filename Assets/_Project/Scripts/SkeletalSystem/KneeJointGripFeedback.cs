using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Attach to any grippable bone (Femur, TibiaFibula) alongside its
/// interactable component — works with either XRSimpleInteractable or
/// XRGrabInteractable (or any other XRBaseInteractable subclass), since it
/// only depends on the shared hover/select events on the base class. Gives a
/// subtle color tint on hover (inviting the student to grab it) and a
/// stronger tint while actually held — the knee scene's equivalent of the
/// glow NeuronVisual uses to signal interactivity, since a static bone model
/// otherwise gives no hint about where hands are supposed to go.
///
/// Uses a MaterialPropertyBlock rather than touching material.color
/// directly, so this never creates a material instance per bone (cheap,
/// and safe to use on shared materials without side effects elsewhere).
/// Assumes the target renderer's shader exposes "_Color" (Unity's Standard
/// shader property name) — swap the property name in the inspector if this
/// project uses URP/Lit's "_BaseColor" instead.
/// </summary>
[RequireComponent(typeof(XRBaseInteractable))]
public class KneeJointGripFeedback : MonoBehaviour
{
    [SerializeField] private Renderer targetRenderer;
    [SerializeField] private string colorPropertyName = "_Color";

    [SerializeField] private Color hoverTint = new Color(1f, 0.95f, 0.6f, 1f);
    [SerializeField] private Color heldTint = new Color(1f, 0.85f, 0.2f, 1f);
    [SerializeField] private float tintStrength = 0.35f; // 0 = no visible change, 1 = fully replaced by tint

    private XRBaseInteractable _interactable;
    private MaterialPropertyBlock _propBlock;
    private Color _baseColor;
    private bool _initialized;

    private void Awake()
    {
        _interactable = GetComponent<XRBaseInteractable>();
        _propBlock = new MaterialPropertyBlock();

        if (targetRenderer == null)
            targetRenderer = GetComponentInChildren<Renderer>();

        if (targetRenderer != null && targetRenderer.sharedMaterial != null &&
            targetRenderer.sharedMaterial.HasProperty(colorPropertyName))
        {
            _baseColor = targetRenderer.sharedMaterial.GetColor(colorPropertyName);
            _initialized = true;
        }
        else
        {
            Debug.LogWarning($"[KneeJointGripFeedback] No renderer/color property found on '{name}' — " +
                              "grip highlight will be skipped, interaction still works normally.");
        }
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

        ApplyColor(_baseColor);
    }

    private void OnHoverEntered(UnityEngine.XR.Interaction.Toolkit.HoverEnterEventArgs args) => ApplyTint(hoverTint);
    private void OnHoverExited(UnityEngine.XR.Interaction.Toolkit.HoverExitEventArgs args) => ApplyColor(_baseColor);
    private void OnSelectEntered(UnityEngine.XR.Interaction.Toolkit.SelectEnterEventArgs args) => ApplyTint(heldTint);
    private void OnSelectExited(UnityEngine.XR.Interaction.Toolkit.SelectExitEventArgs args) => ApplyColor(_baseColor);

    private void ApplyTint(Color tint) => ApplyColor(Color.Lerp(_baseColor, tint, tintStrength));

    private void ApplyColor(Color color)
    {
        if (!_initialized || targetRenderer == null) return;

        targetRenderer.GetPropertyBlock(_propBlock);
        _propBlock.SetColor(colorPropertyName, color);
        targetRenderer.SetPropertyBlock(_propBlock);
    }

    /// <summary>
    /// Called by KneeJointMiniGameController once the joint locks, so the
    /// tint doesn't get stuck on "held" after selectExited stops firing
    /// (the interactable is disabled at lock time, which can suppress the
    /// normal exit event).
    /// </summary>
    public void ForceReset() => ApplyColor(_baseColor);
}