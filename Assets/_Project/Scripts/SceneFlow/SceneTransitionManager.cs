using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Handles moving between scenes (MountainTrail → MiniGame_Circulatory → etc.).
/// This is the file referenced in technical_architecture.md that didn't exist
/// yet — it fills the "portal" moment described in scene_design.md: fade out,
/// load the next scene, tell GameManager/AnatomyTutorSession where we are,
/// fade back in.
///
/// Usage: drop one of these in Bootstrap.unity (DontDestroyOnLoad, like
/// GameManager), reference a full-screen CanvasGroup for the fade, and call
/// TransitionToScene(...) from a portal trigger, a "mini-game complete" event
/// handler, or a button — instead of hand-wiring SceneManager.LoadScene calls
/// all over the project.
/// </summary>
public class SceneTransitionManager : MonoBehaviour
{
    public static SceneTransitionManager Instance { get; private set; }

    [Header("Fade")]
    [Tooltip("Full-screen CanvasGroup on a persistent Canvas, alpha 0 = fully visible scene, 1 = fully black.")]
    public CanvasGroup fadeCanvasGroup;
    public float fadeDuration = 1f;

    [Header("Debug")]
    public bool verbose = true;

    private bool _isTransitioning;
    public bool IsTransitioning => _isTransitioning;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fades out, loads unitySceneName, updates GameManager + AnatomyTutorSession
    /// to targetScene, then fades back in.
    /// </summary>
    public void TransitionToScene(AnatomySceneId targetScene, string unitySceneName)
    {
        if (_isTransitioning)
        {
            if (verbose) Debug.LogWarning("[SceneTransitionManager] Transition already in progress — ignoring request.");
            return;
        }

        StartCoroutine(TransitionRoutine(targetScene, unitySceneName));
    }

    /// <summary>
    /// Convenience overload — advances to whatever GameManager.GetNextScene()
    /// says is next, using the provided scene-name lookup.
    /// </summary>
    public void TransitionToNextScene(System.Func<AnatomySceneId, string> sceneNameLookup)
    {
        var next = GameManager.Instance != null ? GameManager.Instance.GetNextScene() : null;
        if (next == null)
        {
            if (verbose) Debug.Log("[SceneTransitionManager] No next scene — already at the end of the sequence.");
            return;
        }

        string sceneName = sceneNameLookup?.Invoke(next.Value);
        if (string.IsNullOrEmpty(sceneName))
        {
            Debug.LogError($"[SceneTransitionManager] No Unity scene name provided for {next.Value}.");
            return;
        }

        TransitionToScene(next.Value, sceneName);
    }

    // ── Routine ───────────────────────────────────────────────────────────────

    private IEnumerator TransitionRoutine(AnatomySceneId targetScene, string unitySceneName)
    {
        _isTransitioning = true;
        GameManager.Instance?.SetHikeState(HikeState.Transitioning);
        if (verbose) Debug.Log($"[SceneTransitionManager] Transitioning → {targetScene} ('{unitySceneName}')");

        yield return Fade(1f);

        AsyncOperation load = SceneManager.LoadSceneAsync(unitySceneName, LoadSceneMode.Single);
        if (load == null)
        {
            Debug.LogError($"[SceneTransitionManager] Failed to start loading scene '{unitySceneName}' — check it's in Build Settings.");
            _isTransitioning = false;
            yield return Fade(0f);
            yield break;
        }

        while (!load.isDone) yield return null;

        GameManager.Instance?.GoToScene(targetScene);
        GameManager.Instance?.SetHikeState(
            targetScene == AnatomySceneId.Summit ? HikeState.Summit : HikeState.MiniGame);

        yield return Fade(0f);

        _isTransitioning = false;
        if (verbose) Debug.Log($"[SceneTransitionManager] Transition complete — now in {targetScene}.");
    }

    private IEnumerator Fade(float targetAlpha)
    {
        if (fadeCanvasGroup == null) yield break;

        float startAlpha = fadeCanvasGroup.alpha;
        float t = 0f;

        while (t < fadeDuration)
        {
            t += Time.deltaTime;
            fadeCanvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, t / fadeDuration);
            yield return null;
        }

        fadeCanvasGroup.alpha = targetAlpha;
    }
}