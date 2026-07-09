using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Single source of truth for overall game/hike state — separate from
/// AnatomyTutorSession, which owns scene *narrative* context for the AI tutor.
///
/// GameManager owns:
///   - Which systems have been completed (for HUD + gating "what's next")
///   - Overall mountain progress (0 = trailhead, 1 = summit) for the HUD bar
///   - The current high-level hike state (hiking / transitioning / in a mini-game / summit)
///
/// GameManager does NOT duplicate scene narrative data — it calls into
/// AnatomyTutorSession.SetScene()/SetProgressSummary() so the AI tutor's
/// context stays in sync with whatever the HUD is showing.
///
/// Listens to MiniGameEvents.OnMiniGameComplete so individual mini-game
/// scripts (HeartPumpInteraction, DiaphragmInteraction, etc.) don't need to
/// know about GameManager at all — they just fire the existing shared event.
/// </summary>
public enum HikeState
{
    Hiking,        // walking the trail, first-person hiker view
    Transitioning, // portal/fade between mountain and inside-the-body views
    MiniGame,      // actively inside an organ-system interaction
    Summit         // final scene / homeostasis wrap-up
}

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Mountain Progress")]
    [Range(0f, 1f)]
    [Tooltip("0 = trailhead, 1 = summit. Drive this from AutoWalkController or manually for testing.")]
    [SerializeField] private float mountainProgress = 0f;

    [Header("Debug")]
    public bool verbose = true;

    // ── Canonical scene order ─────────────────────────────────────────────
    // Derived directly from the AnatomySceneId enum's declaration order
    // instead of a second, hand-maintained list. The enum is already declared
    // in the intended narrative order (Trailhead → Nervous → Skeletal →
    // SteepIncline → Circulatory → Respiratory → Digestive → Summit), and
    // keeping only one copy of that order means adding a new scene to the
    // enum can never silently leave GetNextScene()/HUD highlighting out of
    // sync again, the way the old hard-coded list did.
    //
    // If the intended flow ever needs to reorder scenes without reordering
    // the enum itself, replace this with an explicit list again — just keep
    // it as the ONLY list, not a second one that can drift.
    public static readonly AnatomySceneId[] SceneOrder =
        (AnatomySceneId[])Enum.GetValues(typeof(AnatomySceneId));

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly HashSet<BodySystem> _completedSystems = new HashSet<BodySystem>();

    public HikeState CurrentHikeState { get; private set; } = HikeState.Hiking;
    public AnatomySceneId CurrentScene => AnatomyTutorSession.Current.SceneId;
    public float MountainProgress => mountainProgress;
    public IReadOnlyCollection<BodySystem> CompletedSystems => _completedSystems;

    // ── Events (HUD, narrator glue, etc. subscribe to these) ────────────────────

    public event Action<float> OnMountainProgressChanged;
    public event Action<BodySystem> OnSystemCompleted;
    public event Action<AnatomySceneId> OnSceneChanged;
    public event Action<HikeState> OnHikeStateChanged;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[GameManager] Duplicate instance found — destroying the new one.");
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        MiniGameEvents.OnMiniGameComplete += HandleMiniGameComplete;
    }

    private void OnDisable()
    {
        MiniGameEvents.OnMiniGameComplete -= HandleMiniGameComplete;
    }

    // ── MiniGameEvents glue ───────────────────────────────────────────────────

    /// <summary>
    /// Mini-game scripts already call MiniGameEvents.TriggerMiniGameComplete("Circulatory")
    /// etc. per the shared contract — GameManager listens for that instead of every
    /// interaction script needing a direct reference to GameManager.
    /// </summary>
    private void HandleMiniGameComplete(string systemName)
    {
        if (Enum.TryParse(systemName, ignoreCase: true, out BodySystem system))
        {
            MarkSystemComplete(system);
        }
        else if (verbose)
        {
            Debug.LogWarning($"[GameManager] OnMiniGameComplete fired with unrecognized system name: '{systemName}'");
        }
    }

    // ── Public API — systems ─────────────────────────────────────────────────

    public void MarkSystemComplete(BodySystem system)
    {
        if (!_completedSystems.Add(system)) return; // already completed, no-op

        if (verbose) Debug.Log($"[GameManager] ✅ System completed: {system}");

        OnSystemCompleted?.Invoke(system);
        RefreshTutorProgressSummary();
    }

    public bool IsSystemComplete(BodySystem system) => _completedSystems.Contains(system);

    /// <summary>
    /// String overload for callers that only have a system name on hand (e.g.
    /// MiniGameSequencer, mini-game scripts checking "is Circulatory done?").
    /// GameManager is the single owner of completed-system state — nothing else
    /// should keep its own copy of this set.
    /// </summary>
    public bool IsSystemComplete(string systemName) =>
        Enum.TryParse(systemName, ignoreCase: true, out BodySystem system) && IsSystemComplete(system);

    // ── Public API — mountain progress ───────────────────────────────────────

    public void SetMountainProgress(float progress01)
    {
        mountainProgress = Mathf.Clamp01(progress01);
        OnMountainProgressChanged?.Invoke(mountainProgress);
    }

    // ── Public API — hike state ──────────────────────────────────────────────

    public void SetHikeState(HikeState state)
    {
        if (CurrentHikeState == state) return;
        CurrentHikeState = state;
        if (verbose) Debug.Log($"[GameManager] Hike state → {state}");
        OnHikeStateChanged?.Invoke(state);
    }

    // ── Public API — scene / sequencing ──────────────────────────────────────

    /// <summary>
    /// Moves the AI tutor's scene context to sceneId. Does not itself change
    /// the loaded Unity scene — call this from SceneTransitionManager once the
    /// new scene has finished loading.
    /// </summary>
    public void GoToScene(AnatomySceneId sceneId, string progressSummaryOverride = null)
    {
        AnatomyTutorSession.SetScene(sceneId, progressSummaryOverride);
        OnSceneChanged?.Invoke(sceneId);
    }

    /// <summary>Returns the next scene in the canonical order, or null if already at the end.</summary>
    public AnatomySceneId? GetNextScene()
    {
        int idx = Array.IndexOf(SceneOrder, CurrentScene);
        if (idx < 0 || idx >= SceneOrder.Length - 1) return null;
        return SceneOrder[idx + 1];
    }

    /// <summary>
    /// Maps a scene to the body system(s) it primarily teaches, for HUD highlighting.
    /// Nervous and Skeletal now have their own dedicated scenes (split out of the
    /// combined Trailhead intro), so each highlights just its own system; Trailhead
    /// itself and SteepIncline are narrative/transition beats that don't own a
    /// specific system indicator.
    /// </summary>
    public static IEnumerable<BodySystem> SystemsForScene(AnatomySceneId scene)
    {
        switch (scene)
        {
            case AnatomySceneId.Nervous:     return new[] { BodySystem.Nervous };
            case AnatomySceneId.Skeletal:    return new[] { BodySystem.Skeletal };
            case AnatomySceneId.Circulatory: return new[] { BodySystem.Circulatory };
            case AnatomySceneId.Respiratory: return new[] { BodySystem.Respiratory };
            case AnatomySceneId.Digestive:   return new[] { BodySystem.Digestive };
            default:                         return Array.Empty<BodySystem>();
        }
    }

    // ── Tutor sync ────────────────────────────────────────────────────────────

    private void RefreshTutorProgressSummary()
    {
        string summary = _completedSystems.Count == 0
            ? "No systems completed yet."
            : $"Completed so far: {string.Join(", ", _completedSystems.Select(s => s.ToString()))}.";

        AnatomyTutorSession.SetProgressSummary(summary);
    }

    // ── Editor/testing helpers ───────────────────────────────────────────────

    /// <summary>Resets all progress — handy for repeated playtesting without restarting.</summary>
    [ContextMenu("Reset Progress")]
    public void ResetProgress()
    {
        _completedSystems.Clear();
        mountainProgress = 0f;
        CurrentHikeState = HikeState.Hiking;
        AnatomyTutorSession.ResetToDefault();
        OnMountainProgressChanged?.Invoke(0f);
        Debug.Log("[GameManager] Progress reset.");
    }
}