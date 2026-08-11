using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A small floating world-space ring that fills clockwise as progress goes
/// 0 → 1, then pops and fades out once it hits 1 — the visual for
/// MuscularSystemMiniGameController's 3-squeeze gate, but generic enough to
/// drive from any "N discrete steps" interaction that wants the same cue.
///
/// Builds its own Canvas/Image hierarchy and a soft-edged donut sprite
/// entirely in code at Awake — same "no manual asset setup" approach
/// GrabPointGlow uses for its glow marker. Drop this on an empty child
/// GameObject positioned roughly where you want the ring to float (e.g. just
/// above the muscle fiber), leave everything at defaults, done.
///
/// Usage: call SetProgress(0..1) every frame (or whenever progress changes)
/// from the driving script. Once fraction reaches ~1, this plays its own
/// pop+fade animation and disables itself — SetProgress calls after that are
/// ignored until ResetRing() is called (e.g. if you ever wire up a full
/// mini-game replay/reset flow).
/// </summary>
public class MuscleSqueezeProgressRing : MonoBehaviour
{
    [Header("Placement")]
    [Tooltip("Where the ring is centered. Defaults to this GameObject's own transform if left unassigned.")]
    [SerializeField] private Transform anchor;

    [Tooltip("Local offset from the anchor, in meters — e.g. floating just above the muscle fiber.")]
    [SerializeField] private Vector3 localOffset = new Vector3(0f, 0.15f, 0f);

    [Tooltip("Ring diameter in meters.")]
    [SerializeField] private float diameterMeters = 2.4f;

    [Tooltip("If true, the ring rotates every frame to face the main camera — readable from any angle the student walks/looks from. This rotation is applied FIRST, then rotationOffsetEuler on top of it.")]
    [SerializeField] private bool faceCamera = true;

    [Tooltip("Extra rotation applied on top of the camera-facing look rotation (or used as the ring's fixed local rotation if faceCamera is off). Defaults to a 90° spin so the ring reads right without needing a custom mesh orientation — tweak per-axis here if it still looks off in your scene.")]
    [SerializeField] private Vector3 rotationOffsetEuler = new Vector3(0f, 0f, 90f);

    [Header("Colors")]
    [SerializeField] private Color fillColor = new Color(0.35f, 0.9f, 0.4f, 1f);
    [SerializeField] private Color backgroundColor = new Color(1f, 1f, 1f, 0.18f);

    [Header("Ring Shape")]
    [Tooltip("Inner radius as a fraction of the outer radius — 0 = solid disc (pac-man fill), closer to 1 = thin ring. 0.6-0.75 reads as a clean progress ring.")]
    [Range(0f, 0.9f)]
    [SerializeField] private float innerRadiusFraction = 0.68f;

    [Tooltip("Texture resolution the donut sprite is drawn at. 128 is plenty for a UI element this small.")]
    [SerializeField] private int textureResolution = 128;

    [Header("Completion Animation")]
    [SerializeField] private float popDuration = 0.15f;
    [SerializeField] private float popScale = 1.35f;
    [SerializeField] private float fadeDuration = 0.5f;

    private Transform _canvasTransform;
    private CanvasGroup _canvasGroup;
    private Image _fillImage;
    private Camera _mainCamera;
    private Vector3 _baseScale;
    private bool _hiddenAfterCompletion;

    private void Awake()
    {
        BuildRing();
    }

    private void BuildRing()
    {
        Transform parent = anchor != null ? anchor : transform;

        var canvasGO = new GameObject("SqueezeProgressRing (auto-generated)");
        canvasGO.transform.SetParent(parent, false);
        canvasGO.transform.localPosition = localOffset;
        _canvasTransform = canvasGO.transform;

        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        var canvasRect = canvasGO.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(200f, 200f);
        // 200 UI units == diameterMeters meters, so authoring stays in meters.
        _baseScale = Vector3.one * (diameterMeters / 200f);
        canvasGO.transform.localScale = _baseScale;

        _canvasGroup = canvasGO.AddComponent<CanvasGroup>();

        Sprite donut = BuildDonutSprite();

        var bgGO = new GameObject("Background");
        bgGO.transform.SetParent(canvasGO.transform, false);
        var bgRect = bgGO.AddComponent<RectTransform>();
        StretchFull(bgRect);
        var bgImage = bgGO.AddComponent<Image>();
        bgImage.sprite = donut;
        bgImage.color = backgroundColor;
        bgImage.raycastTarget = false;

        var fillGO = new GameObject("Fill");
        fillGO.transform.SetParent(canvasGO.transform, false);
        var fillRect = fillGO.AddComponent<RectTransform>();
        StretchFull(fillRect);
        _fillImage = fillGO.AddComponent<Image>();
        _fillImage.sprite = donut;
        _fillImage.color = fillColor;
        _fillImage.type = Image.Type.Filled;
        _fillImage.fillMethod = Image.FillMethod.Radial360;
        _fillImage.fillOrigin = (int)Image.Origin360.Top;
        _fillImage.fillClockwise = true;
        _fillImage.fillAmount = 0f;
        _fillImage.raycastTarget = false;
    }

    private static void StretchFull(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    /// <summary>
    /// Draws a soft-edged donut (filled circle with a transparent hole in the
    /// middle) into a square texture, antialiased by fading alpha over a
    /// ~1.5px band at both the outer and inner edges rather than a hard cutoff.
    /// </summary>
    private Sprite BuildDonutSprite()
    {
        int res = Mathf.Max(16, textureResolution);
        var tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        float outerRadius = res * 0.5f - 1f;
        float innerRadius = outerRadius * innerRadiusFraction;
        float edgeSoftness = 1.5f;
        Vector2 center = new Vector2(res * 0.5f, res * 0.5f);

        var pixels = new Color32[res * res];
        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);

                float outerAlpha = Mathf.Clamp01((outerRadius - dist) / edgeSoftness);
                float innerAlpha = Mathf.Clamp01((dist - innerRadius) / edgeSoftness);
                float alpha = Mathf.Min(outerAlpha, innerAlpha);

                pixels[y * res + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply();

        return Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0.5f, 0.5f), res);
    }

    private void Update()
    {
        if (_canvasTransform == null) return;

        if (faceCamera)
        {
            if (_mainCamera == null) _mainCamera = Camera.main;
            if (_mainCamera == null) return;

            Quaternion lookRotation = Quaternion.LookRotation(
                _canvasTransform.position - _mainCamera.transform.position);
            _canvasTransform.rotation = lookRotation * Quaternion.Euler(rotationOffsetEuler);
        }
        else
        {
            _canvasTransform.localRotation = Quaternion.Euler(rotationOffsetEuler);
        }
    }

    private Coroutine _completionRoutine;

    private void OnDestroy()
    {
        // Belt-and-suspenders — Unity normally stops a MonoBehaviour's own
        // coroutines automatically when it's destroyed, but being explicit
        // here costs nothing and rules it out as a cause if this object
        // (or its canvas child) ever gets torn down mid-animation, e.g. by
        // a scene transition firing right as the ring is completing.
        StopAllCoroutines();
    }

    /// <summary>
    /// Sets the ring's fill 0..1. Ignored once the completion animation has
    /// started (call ResetRing() first if you need to show it again).
    /// </summary>
    public void SetProgress(float fraction01)
    {
        if (_hiddenAfterCompletion || _fillImage == null) return;

        float fraction = Mathf.Clamp01(fraction01);
        _fillImage.fillAmount = fraction;

        if (fraction >= 0.999f)
        {
            _hiddenAfterCompletion = true;
            _completionRoutine = StartCoroutine(PlayCompletionAndHide());
        }
    }

    /// <summary>Shows the ring again at 0 progress — for a future full mini-game reset/replay flow.</summary>
    public void ResetRing()
    {
        if (_completionRoutine != null)
        {
            StopCoroutine(_completionRoutine);
            _completionRoutine = null;
        }

        _hiddenAfterCompletion = false;
        if (_canvasTransform != null) _canvasTransform.gameObject.SetActive(true);
        if (_canvasGroup != null) _canvasGroup.alpha = 1f;
        if (_canvasTransform != null) _canvasTransform.localScale = _baseScale;
        if (_fillImage != null) _fillImage.fillAmount = 0f;
    }

    /// <summary>
    /// Pops then fades the ring out. Re-checks _canvasTransform/_canvasGroup
    /// for null at the top of every loop iteration (not just once at the
    /// start) — this is a multi-frame coroutine, and if the ring's
    /// GameObject is destroyed by something else (e.g. a scene unload)
    /// partway through, Unity can still resume this coroutine for one more
    /// frame before it's cleaned up. Bailing out cleanly there instead of
    /// touching a destroyed Transform is what actually fixes the
    /// MissingReferenceException, independent of whatever destroyed it.
    /// </summary>
    private IEnumerator PlayCompletionAndHide()
    {
        float t = 0f;
        while (t < popDuration)
        {
            if (_canvasTransform == null) yield break;

            t += Time.deltaTime;
            float scale = Mathf.Lerp(1f, popScale, t / popDuration);
            _canvasTransform.localScale = _baseScale * scale;
            yield return null;
        }

        t = 0f;
        while (t < fadeDuration)
        {
            if (_canvasTransform == null || _canvasGroup == null) yield break;

            t += Time.deltaTime;
            float p = t / fadeDuration;
            _canvasGroup.alpha = Mathf.Lerp(1f, 0f, p);
            _canvasTransform.localScale = _baseScale * Mathf.Lerp(popScale, 0.6f, p);
            yield return null;
        }

        if (_canvasTransform != null) _canvasTransform.gameObject.SetActive(false);
        _completionRoutine = null;
    }
}