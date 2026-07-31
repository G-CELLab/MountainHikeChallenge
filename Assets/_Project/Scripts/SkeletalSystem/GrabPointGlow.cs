using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Auto-generates a small pulsing glow marker directly from this object's
/// own CapsuleCollider — no manual mesh creation, positioning, or material
/// setup needed. Add this alongside the existing Capsule Collider and
/// XR Grab Interactable on the bone (e.g. femur_bone) and it builds a
/// translucent capsule matching the collider's own center/radius/height/
/// direction, with a runtime material that already has Emission AND
/// transparency enabled (this is a fresh material instance created in
/// code, so there's no "toggle Surface Type/Emission in the Inspector"
/// step — this script owns the material outright and sets it up itself).
///
/// Pulses continuously while idle, fades out while the bone is actually
/// held, and can be permanently hidden once the knee locks via Hide().
/// </summary>
[RequireComponent(typeof(CapsuleCollider))]
public class GrabPointGlow : MonoBehaviour
{
    [Header("Marker Shape")]
    [Tooltip("Shader used for the generated marker. Falls back to Standard if this shader isn't found in the project.")]
    [SerializeField] private string shaderName = "Universal Render Pipeline/Lit";

    [Tooltip("Marker size relative to the collider's own radius/height. 1 = exactly matches the collider's grab zone; slightly above 1 makes it a bit more visible without misrepresenting the actual grab tolerance.")]
    [SerializeField] private float visualSizeMultiplier = 1.15f;

    [Header("Glow")]
    [SerializeField] private string emissionColorProperty = "_EmissionColor";
    [SerializeField] private Color glowColor = new Color(0.3f, 0.9f, 1f, 1f);
    [Tooltip("Emission intensity at the dimmest point of the pulse.")]
    [SerializeField] private float minIntensity = 0.4f;
    [Tooltip("Emission intensity at the brightest point of the pulse.")]
    [SerializeField] private float maxIntensity = 2.5f;
    [Tooltip("Full pulse cycles per second.")]
    [SerializeField] private float pulseSpeed = 1f;
    [Tooltip("How quickly the glow fades in/out when grab state changes (seconds to fully fade).")]
    [SerializeField] private float fadeDuration = 0.2f;

    [Tooltip("Base transparency of the marker surface itself, separate from the emission glow — this is what makes it read as a soft translucent indicator rather than a solid opaque ball. 0 = fully invisible surface (glow only), 1 = fully opaque surface.")]
    [Range(0f, 1f)]
    [SerializeField] private float surfaceOpacity = 0.25f;

    private CapsuleCollider _capsule;
    private XRBaseInteractable _interactable;
    private Material _material; // runtime instance, safe — this object is created fresh in code, never shared
    private Renderer _markerRenderer;

    private bool _isHeld;
    private bool _hiddenPermanently;
    private float _currentFade = 1f; // 0 = fully hidden, 1 = fully visible/pulsing

    private void Awake()
    {
        _capsule = GetComponent<CapsuleCollider>();
        _interactable = GetComponent<XRBaseInteractable>();

        BuildMarker();
    }

    private void BuildMarker()
    {
        var marker = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        marker.name = "GrabPointMarker (auto-generated)";

        // Purely visual — remove the primitive's default CapsuleCollider so
        // it can't interfere with the bone's own grab collider at the same
        // spot.
        Destroy(marker.GetComponent<Collider>());

        marker.transform.SetParent(transform, false);
        marker.transform.localPosition = _capsule.center;

        // Unity's default Capsule primitive is 2 units tall (spanning
        // local Y from -1 to 1) with a 0.5 radius (1 unit diameter), long
        // axis along local Y. Scale it to match the collider's actual
        // radius/height, then rotate so its long axis matches whichever
        // local axis the collider uses (CapsuleCollider.direction: 0 = X,
        // 1 = Y, 2 = Z).
        float diameter = _capsule.radius * 2f * visualSizeMultiplier;

        // A capsule's total height can't be shorter than its own diameter
        // (Unity clamps this internally for the real collider too) — match
        // that here so the marker never looks squashed/invalid.
        float effectiveHeight = Mathf.Max(_capsule.height, _capsule.radius * 2f) * visualSizeMultiplier;

        marker.transform.localScale = new Vector3(diameter, effectiveHeight * 0.5f, diameter);

        marker.transform.localRotation = _capsule.direction switch
        {
            0 => Quaternion.Euler(0f, 0f, 90f), // collider's long axis is local X
            2 => Quaternion.Euler(90f, 0f, 0f), // collider's long axis is local Z
            _ => Quaternion.identity,            // collider's long axis is local Y (default)
        };

        _markerRenderer = marker.GetComponent<Renderer>();
        _markerRenderer.shadowCastingMode = ShadowCastingMode.Off;
        _markerRenderer.receiveShadows = false;

        Shader shader = Shader.Find(shaderName);
        if (shader == null)
        {
            Debug.LogWarning($"[GrabPointGlow] Shader '{shaderName}' not found — falling back to Standard.");
            shader = Shader.Find("Standard");
        }

        _material = new Material(shader);
        SetupTransparency(_material);
        _material.EnableKeyword("_EMISSION");
        _material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        _markerRenderer.material = _material;
    }

    /// <summary>
    /// Configures the runtime material for alpha-blended transparency. This
    /// replicates what URP's Lit Inspector does under the hood when you
    /// switch its "Surface Type" dropdown to Transparent — since we're
    /// building this material in code rather than authoring it in the
    /// Editor, we have to set these properties/keywords ourselves.
    /// </summary>
    private void SetupTransparency(Material mat)
    {
        Color baseColor = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;
        baseColor.a = surfaceOpacity;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseColor);

        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f); // 0 = Opaque, 1 = Transparent
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = (int)RenderQueue.Transparent;
    }

    private void OnEnable()
    {
        if (_interactable == null)
            _interactable = GetComponent<XRBaseInteractable>();

        if (_interactable != null)
        {
            _interactable.selectEntered.AddListener(OnSelectEntered);
            _interactable.selectExited.AddListener(OnSelectExited);
        }
    }

    private void OnDisable()
    {
        if (_interactable != null)
        {
            _interactable.selectEntered.RemoveListener(OnSelectEntered);
            _interactable.selectExited.RemoveListener(OnSelectExited);
        }
    }

    private void OnSelectEntered(UnityEngine.XR.Interaction.Toolkit.SelectEnterEventArgs args) => _isHeld = true;
    private void OnSelectExited(UnityEngine.XR.Interaction.Toolkit.SelectExitEventArgs args) => _isHeld = false;

    /// <summary>Permanently turns the glow off — call this once the knee locks, same moment KneeJointGripFeedback.ForceReset() is called.</summary>
    public void Hide() => _hiddenPermanently = true;

    private void Update()
    {
        if (_material == null) return;

        float targetFade = (_isHeld || _hiddenPermanently) ? 0f : 1f;
        _currentFade = fadeDuration > 0f
            ? Mathf.MoveTowards(_currentFade, targetFade, Time.deltaTime / fadeDuration)
            : targetFade;

        if (_currentFade <= 0f)
        {
            _material.SetColor(emissionColorProperty, Color.black);
            return;
        }

        float pulse = Mathf.Lerp(minIntensity, maxIntensity, (Mathf.Sin(Time.time * pulseSpeed * Mathf.PI * 2f) + 1f) * 0.5f);
        _material.SetColor(emissionColorProperty, glowColor * pulse * _currentFade);
    }

    private void OnDestroy()
    {
        // We created this material instance ourselves — clean it up rather
        // than leaking it when the object is destroyed.
        if (_material != null) Destroy(_material);
    }
}