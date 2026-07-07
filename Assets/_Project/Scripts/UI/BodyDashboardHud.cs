using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Drives the World Space Canvas HUD from GameManager events.
/// Per technical_architecture.md this must be World Space, not Screen Space,
/// since Screen Space Canvases don't render correctly in VR.
///
/// Mountain progress is shown via mountainProgressFillImage — an Image with
/// Image Type = Filled, whose fillAmount is driven directly. This matches
/// the ATP-style progress bar prefab used in the project rather than a
/// Unity Slider component.
///
/// This script only reacts to GameManager — it has no game logic of its own.
/// </summary>
public class BodyDashboardHUD : MonoBehaviour
{
    [System.Serializable]
    public class SystemIndicator
    {
        public BodySystem system;
        public Image icon;

        [Header("Colors")]
        public Color lockedColor = new Color(0.4f, 0.4f, 0.4f);
        public Color activeColor = new Color(1f, 0.85f, 0.2f);
        public Color completeColor = new Color(0.25f, 0.8f, 0.35f);
    }

    [Header("Mountain Progress")]
    [Tooltip("An Image with Image Type = Filled. Its fillAmount is set directly — " +
             "used for the ATP-style progress bar prefab.")]
    public Image mountainProgressFillImage;
    public TMP_Text progressLabel;

    [Header("System Indicators")]
    [Tooltip("One entry per BodySystem. Build 6 colored circle Images in the " +
             "Canvas (Nervous, Skeletal, Muscular, Circulatory, Respiratory, Digestive) " +
             "and drag each into a slot here.")]
    public List<SystemIndicator> systemIndicators = new List<SystemIndicator>();

    [Header("Debug")]
    public bool verbose = true;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        if (GameManager.Instance == null)
        {
            if (verbose) Debug.LogWarning("[BodyDashboardHUD] No GameManager in scene yet — HUD will not update.");
            return;
        }

        GameManager.Instance.OnMountainProgressChanged += HandleProgressChanged;
        GameManager.Instance.OnSystemCompleted += HandleSystemCompleted;
        GameManager.Instance.OnSceneChanged += HandleSceneChanged;

        RefreshAll();
    }

    private void OnDisable()
    {
        if (GameManager.Instance == null) return;

        GameManager.Instance.OnMountainProgressChanged -= HandleProgressChanged;
        GameManager.Instance.OnSystemCompleted -= HandleSystemCompleted;
        GameManager.Instance.OnSceneChanged -= HandleSceneChanged;
    }

    // ── Full refresh (used on enable, in case the HUD spawns mid-game) ──────────

    private void RefreshAll()
    {
        HandleProgressChanged(GameManager.Instance.MountainProgress);

        foreach (var indicator in systemIndicators)
        {
            Color color = GameManager.Instance.IsSystemComplete(indicator.system)
                ? indicator.completeColor
                : indicator.lockedColor;
            ApplyColor(indicator, color);
        }

        HandleSceneChanged(GameManager.Instance.CurrentScene);
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void HandleProgressChanged(float progress01)
    {
        if (mountainProgressFillImage != null) mountainProgressFillImage.fillAmount = progress01;
        if (progressLabel != null) progressLabel.text = $"{Mathf.RoundToInt(progress01 * 100f)}% to summit";
    }

    private void HandleSystemCompleted(BodySystem system)
    {
        var indicator = systemIndicators.Find(i => i.system == system);
        if (indicator == null) return;

        ApplyColor(indicator, indicator.completeColor);
        if (verbose) Debug.Log($"[BodyDashboardHUD] {system} indicator → complete");
    }

    /// <summary>
    /// Highlights whichever system(s) the current scene is teaching, as long
    /// as they aren't already marked complete (complete always wins visually).
    /// </summary>
    private void HandleSceneChanged(AnatomySceneId scene)
    {
        var activeSystems = new HashSet<BodySystem>(GameManager.SystemsForScene(scene));

        foreach (var indicator in systemIndicators)
        {
            if (GameManager.Instance.IsSystemComplete(indicator.system))
            {
                ApplyColor(indicator, indicator.completeColor);
            }
            else if (activeSystems.Contains(indicator.system))
            {
                ApplyColor(indicator, indicator.activeColor);
            }
            else
            {
                ApplyColor(indicator, indicator.lockedColor);
            }
        }
    }

    private static void ApplyColor(SystemIndicator indicator, Color color)
    {
        if (indicator.icon != null) indicator.icon.color = color;
    }
}