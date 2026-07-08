using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Persistent, scene-spanning game state — the "MiniGameSequencer" described
/// in technical_architecture.md. Lives on a GameObject in Bootstrap.unity and
/// survives every scene load via DontDestroyOnLoad.
///
/// Tracks:
///   - which scenes have already played their narration this session
///   - which mini-games have been completed (listens to MiniGameEvents.OnMiniGameComplete)
///   - how many times the player has looped back to the mountain trail
///
/// Access from anywhere via MiniGameSequencer.Instance.
///
/// Safety net: if you press Play directly on a scene other than Bootstrap
/// (very normal during development), Instance lazily creates itself on
/// first access so nothing null-refs. The "real" instance from Bootstrap
/// still wins when you do play the full flow from scene 0.
/// </summary>
[DefaultExecutionOrder(-1000)] // run before other Awakes so Instance is ready when other scripts ask for it
public class MiniGameSequencer : MonoBehaviour
{
    private static MiniGameSequencer _instance;

    public static MiniGameSequencer Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindAnyObjectByType<MiniGameSequencer>();
                if (_instance == null)
                {
                    var go = new GameObject("MiniGameSequencer (auto-created)");
                    _instance = go.AddComponent<MiniGameSequencer>();
                    Debug.LogWarning("[MiniGameSequencer] No instance found in scene — auto-created one. " +
                                     "For the full flow, make sure Bootstrap.unity (with a MiniGameSequencer " +
                                     "in it) is scene 0 and is where you press Play from.");
                }
            }
            return _instance;
        }
    }

    [Header("Debug")]
    public bool verbose = true;

    private readonly HashSet<string> _visitedScenes      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completedMiniGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public int TrailCheckpointIndex { get; private set; } = 0;

    public event Action<int>   OnTrailCheckpointAdvanced;
    public event Action<string> OnMiniGameMarkedComplete;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            if (verbose) Debug.Log("[MiniGameSequencer] Duplicate instance — destroying this one.");
            Destroy(gameObject);
            return;
        }

        _instance = this;
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

    private void HandleMiniGameComplete(string systemName)
    {
        if (string.IsNullOrEmpty(systemName)) return;
        _completedMiniGames.Add(systemName);
        if (verbose) Debug.Log($"[MiniGameSequencer] Mini-game complete: {systemName}");
        OnMiniGameMarkedComplete?.Invoke(systemName);
    }

    // ── Scene visit tracking ──────────────────────────────────────────────

    public bool HasVisited(string sceneName) =>
        !string.IsNullOrEmpty(sceneName) && _visitedScenes.Contains(sceneName);

    public void MarkVisited(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName)) return;
        _visitedScenes.Add(sceneName);
    }

    // ── Mini-game completion ──────────────────────────────────────────────

    public bool IsMiniGameComplete(string systemName) =>
        !string.IsNullOrEmpty(systemName) && _completedMiniGames.Contains(systemName);

    // ── Trail checkpoint ──────────────────────────────────────────────────

    public void AdvanceTrailCheckpoint()
    {
        TrailCheckpointIndex++;
        if (verbose) Debug.Log($"[MiniGameSequencer] Trail checkpoint advanced → {TrailCheckpointIndex}");
        OnTrailCheckpointAdvanced?.Invoke(TrailCheckpointIndex);
    }

    /// <summary>
    /// Clears all tracked state without needing to stop Play mode.
    /// Handy to wire to a debug button during playtesting.
    /// </summary>
    public void ResetAll()
    {
        _visitedScenes.Clear();
        _completedMiniGames.Clear();
        TrailCheckpointIndex = 0;
        if (verbose) Debug.Log("[MiniGameSequencer] Reset.");
    }
}