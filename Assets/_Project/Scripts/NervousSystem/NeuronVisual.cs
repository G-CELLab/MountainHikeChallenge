using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Handles a single neuron's visual life cycle:
///   - Fades/scales into view the moment it's enabled (a soft "pop" with a
///     little overshoot, not a flat linear grow).
///   - Floats gently and pulses its glow while idle, waiting to be touched.
///   - On Discharge() (called by NervousSystemInteraction when touched),
///     flashes bright, shrinks away, and disables itself.
///
/// Fully self-contained — the controller doesn't need to manage fade-in
/// timing at all, it just calls Discharge() when a neuron is touched.
///
/// Glow/pulse requires a Renderer using a shader with an emission channel
/// (URP Lit with Emission enabled works well) and Bloom enabled in your URP
/// Volume — without Bloom the emission color changes but won't visually
/// "glow." The float/scale animation works regardless of shader.
/// </summary>
[DisallowMultipleComponent]
public class NeuronVisual : MonoBehaviour
{
    [Header("Idle Animation")]
    [SerializeField] private float bobAmplitude = 0.03f;
    [SerializeField] private float bobSpeed = 1.2f;
    [SerializeField] private float fadeInDuration = 1.2f;

    [Header("Glow Pulse")]
    [Tooltip("Leave empty to skip emission pulsing — float/fade still works without it.")]
    [SerializeField] private Renderer glowRenderer;
    [SerializeField] private Color emissionColor = new Color(0.3f, 0.8f, 1f);
    [SerializeField] private float pulseMinIntensity = 0.5f;
    [SerializeField] private float pulseMaxIntensity = 2.5f;
    [SerializeField] private float pulseSpeed = 1.5f;

    [Header("Discharge (on touch)")]
    [SerializeField] private float dischargeFlashIntensity = 6f;
    [SerializeField] private float dischargeDuration = 0.5f;

    private static readonly int EmissionColorID = Shader.PropertyToID("_EmissionColor");
    private MaterialPropertyBlock _mpb;
    private Vector3 _baseLocalPos;
    private Vector3 _targetScale;
    private float _timeSeed;
    private bool _discharged;

    /// <summary>
    /// Fires once this neuron's discharge animation has FINISHED (flash
    /// faded, scaled to zero, object disabled) — not at the moment of
    /// touch. Anything that needs to know "this neuron is really done,
    /// visually" (e.g. a mini-game controller counting all 6 neurons
    /// before allowing the scene to move on) should subscribe to this
    /// rather than hooking Discharge() itself, which only marks the START
    /// of the animation.
    /// </summary>
    public event Action<NeuronVisual> OnDischarged;

    private void Awake()
    {
        _mpb = new MaterialPropertyBlock();
        _baseLocalPos = transform.localPosition;
        _targetScale = transform.localScale; // capture the Inspector-set size BEFORE OnEnable zeroes it for the fade-in
        _timeSeed = UnityEngine.Random.Range(0f, 100f); // desyncs neurons so they don't all bob/pulse in lockstep
    }

    private void OnEnable()
    {
        _discharged = false;
        transform.localScale = Vector3.zero;
        StopAllCoroutines();
        StartCoroutine(FadeInRoutine());
    }

    private void Update()
    {
        if (_discharged) return;

        float bob = Mathf.Sin((Time.time + _timeSeed) * bobSpeed) * bobAmplitude;
        transform.localPosition = _baseLocalPos + Vector3.up * bob;

        if (glowRenderer != null)
        {
            float t = (Mathf.Sin((Time.time + _timeSeed) * pulseSpeed) + 1f) * 0.5f;
            ApplyEmission(Mathf.Lerp(pulseMinIntensity, pulseMaxIntensity, t));
        }
    }

    private void ApplyEmission(float intensity)
    {
        glowRenderer.GetPropertyBlock(_mpb);
        _mpb.SetColor(EmissionColorID, emissionColor * intensity);
        glowRenderer.SetPropertyBlock(_mpb);
    }

    private IEnumerator FadeInRoutine()
    {
        float t = 0f;

        while (t < fadeInDuration)
        {
            t += Time.deltaTime;
            transform.localScale = Vector3.Lerp(Vector3.zero, _targetScale, EaseOutBack(Mathf.Clamp01(t / fadeInDuration)));
            yield return null;
        }
        transform.localScale = _targetScale;
    }

    // Slight overshoot so the neuron feels like it "pops" into existence.
    private static float EaseOutBack(float x)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        return 1f + c3 * Mathf.Pow(x - 1f, 3f) + c1 * Mathf.Pow(x - 1f, 2f);
    }

    /// <summary>Call when this neuron is touched. Flashes bright, shrinks away, then disables the object.</summary>
    public void Discharge()
    {
        if (_discharged) return;
        _discharged = true;
        StopAllCoroutines();
        StartCoroutine(DischargeRoutine());
    }

    private IEnumerator DischargeRoutine()
    {
        float t = 0f;
        Vector3 startScale = transform.localScale;

        // Timeline is deliberately front-loaded: the neuron stays full-size AND
        // fully blown-out/bloomed (until holdEnd) for most of the duration, so
        // it reads as pure light rather than solid geometry the whole time it's
        // visible. Scale only collapses — fast, with an accelerating ease —
        // once brightness has already started falling, so there's no window
        // where it's simultaneously dim AND a visible-sized sphere (that's the
        // exact moment it stops looking like light and starts looking like a ball).
        const float flashRampEnd = 0.15f;
        const float holdEnd = 0.6f;

        while (t < dischargeDuration)
        {
            t += Time.deltaTime;
            float p = Mathf.Clamp01(t / dischargeDuration);

            if (glowRenderer != null)
            {
                float flash;
                if (p < flashRampEnd)
                    flash = Mathf.Lerp(pulseMaxIntensity, dischargeFlashIntensity, p / flashRampEnd);
                else if (p < holdEnd)
                    flash = dischargeFlashIntensity; // hold fully blown-out
                else
                    flash = Mathf.Lerp(dischargeFlashIntensity, 0f, (p - holdEnd) / (1f - holdEnd));

                ApplyEmission(flash);
            }

            // Scale doesn't move at all until holdEnd, then snaps to zero fast
            // (cubic ease-in) in sync with the brightness fall-off above.
            float scaleT = p < holdEnd ? 0f : (p - holdEnd) / (1f - holdEnd);
            float easedScaleT = scaleT * scaleT * scaleT;
            transform.localScale = Vector3.Lerp(startScale, Vector3.zero, easedScaleT);

            yield return null;
        }

        gameObject.SetActive(false);
        OnDischarged?.Invoke(this);
    }
}