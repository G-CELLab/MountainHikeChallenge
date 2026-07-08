using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Persistent, scene-spanning driver for the auto-narration flow. Lives once
/// on a GameObject in _Bootstrap (DontDestroyOnLoad), alongside GameManager,
/// SceneTransitionManager, and the AI agent.
///
/// Flow steps are keyed by (scene name, trail checkpoint) rather than scene
/// name alone. This matters because a scene like _MountainTrail is visited
/// more than once — the first arrival needs different narration and a
/// different "next scene" than a later return trip. Keying by scene name
/// alone can only ever describe one visit, which causes the flow to loop
/// forever the moment a repeat visit reuses the same "go to X next" step.
///
/// The checkpoint used for lookup is captured BEFORE this scene's step runs
/// (and before any advanceTrailCheckpointOnStart bump), so it lines up with
/// whatever PlayerRigPositioner/AIGuidePositioner used to pick a spawn point
/// for this same load — both systems agree on "which visit is this."
///
/// On every scene load:
///   1. Looks up (scene.name, currentCheckpoint) in `flow`.
///   2. If found: applies AnatomyTutorSession context, speaks the cached
///      NarrationLines entry for that step's sceneId, waits for it to
///      finish, raises OnSceneNarrationCompleted, then (if nextSceneName is
///      set) loads the next scene after a short beat.
///   3. If not found: does nothing for this scene load — either it's a
///      scene that doesn't narrate (e.g. _Bootstrap), or you haven't wired
///      up what happens at this checkpoint yet.
/// </summary>
public class SceneNarrationController : MonoBehaviour
{
    [Serializable]
    public class SceneFlowStep
    {
        [Tooltip("Must exactly match the Unity scene name (as it appears in Build Settings).")]
        public string sceneName;

        [Tooltip("Which trail checkpoint this step applies to. 0 = the first time this scene " +
                 "loads, 1 = the next visit, etc. Matches MiniGameSequencer's TrailCheckpointIndex " +
                 "at the moment the scene loads — this is what lets the SAME scene have different " +
                 "narration/next-scene behavior across repeat visits instead of looping.")]
        public int checkpointIndex = 0;

        [Tooltip("Which cached NarrationLines entry to speak in this scene, via NarrationLines.GetSceneNarration(sceneId).")]
        public AnatomySceneId sceneId;

        [Tooltip("Scene to load once narration completes. Leave empty if this scene handles " +
                 "its own transition instead (e.g. the mountain trail's own trigger volumes), " +
                 "or if you simply haven't built what comes next yet.")]
        public string nextSceneName;

        [Tooltip("Extra safety net: skip narration if this exact (scene, checkpoint) combo has " +
                 "already played once this session. Shouldn't normally trigger in a forward-only " +
                 "flow, but guards against odd double-loads during testing.")]
        public bool onlyNarrateOnFirstVisit = true;

        [Tooltip("Bump MiniGameSequencer.Instance.TrailCheckpointIndex when this step completes, " +
                 "so the NEXT time this scene is visited, it's treated as a later checkpoint.")]
        public bool advanceTrailCheckpointOnStart = false;

        [Tooltip("Small pause before narration starts, so the guide has a moment to finish spawning in.")]
        public float delayBeforeNarrationStart = 0.5f;

        [Tooltip("Beat after narration completes before loading the next scene.")]
        public float delayAfterNarrationBeforeTransition = 1.5f;
    }

    public static SceneNarrationController Instance { get; private set; }

    /// <summary>
    /// Fires after a scene's narration completes (or is skipped as a repeat visit),
    /// with the scene name that just finished. Subscribe from any per-scene script
    /// that needs to react — no Inspector wiring required.
    /// </summary>
    public static event Action<string> OnSceneNarrationCompleted;

    [Header("Scene Flow")]
    [Tooltip("One entry per (scene, checkpoint) combination that should auto-narrate. " +
             "Configured here, in one place — no per-scene setup needed anywhere else.")]
    public List<SceneFlowStep> flow = new List<SceneFlowStep>();

    [Header("References")]
    [Tooltip("Auto-found via FindAnyObjectByType if left empty. Since AITutor is also " +
             "persistent, this only needs to resolve once.")]
    public AITutor aiTutor;

    [Header("Debug")]
    public bool verbose = true;

    private static bool _persisted = false;
    private Dictionary<(string sceneName, int checkpoint), SceneFlowStep> _flowLookup;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (_persisted)
        {
            if (verbose) Debug.Log("[SceneNarrationController] Duplicate detected — destroying this GameObject only.");
            Destroy(gameObject);
            return;
        }
        _persisted = true;
        Instance = this;
        DontDestroyOnLoad(transform.root.gameObject);

        BuildLookup();
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += HandleSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
    }

    private void BuildLookup()
    {
        _flowLookup = new Dictionary<(string, int), SceneFlowStep>();
        foreach (var step in flow)
        {
            if (string.IsNullOrWhiteSpace(step.sceneName)) continue;

            var key = (step.sceneName, step.checkpointIndex);
            if (_flowLookup.ContainsKey(key))
            {
                Debug.LogWarning($"[SceneNarrationController] Duplicate flow entry for scene " +
                                  $"'{step.sceneName}' at checkpoint {step.checkpointIndex} — using the first one.");
                continue;
            }

            _flowLookup[key] = step;
        }
    }

    // ── Scene load hook ───────────────────────────────────────────────────────

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (_flowLookup == null) BuildLookup(); // safety net if OnEnable ever raced Awake

        // Captured BEFORE this step can advance the checkpoint, so this lookup
        // agrees with whatever PlayerRigPositioner/AIGuidePositioner used for
        // this same scene load.
        int checkpoint = MiniGameSequencer.Instance.TrailCheckpointIndex;

        if (!_flowLookup.TryGetValue((scene.name, checkpoint), out SceneFlowStep step))
        {
            if (verbose)
                Debug.Log($"[SceneNarrationController] No flow entry for '{scene.name}' at checkpoint {checkpoint} — skipping.");
            return;
        }

        if (aiTutor == null) aiTutor = FindAnyObjectByType<AITutor>();

        StartCoroutine(RunSequence(step, scene.name));
    }

    // ── Sequence ──────────────────────────────────────────────────────────────

    private IEnumerator RunSequence(SceneFlowStep step, string sceneName)
    {
        AnatomyTutorSession.SetScene(step.sceneId);

        // Scoped by scene+checkpoint, not scene name alone — a later visit to the
        // same scene name is a different step with its own visited-state.
        string visitKey = $"{sceneName}@checkpoint{step.checkpointIndex}";
        bool alreadyVisited = MiniGameSequencer.Instance.HasVisited(visitKey);

        if (step.onlyNarrateOnFirstVisit && alreadyVisited)
        {
            if (verbose)
                Debug.Log($"[SceneNarrationController] '{visitKey}' already visited — skipping narration.");

            HandleNarrationComplete(step, sceneName);
            yield break;
        }

        if (step.delayBeforeNarrationStart > 0f)
            yield return new WaitForSecondsRealtime(step.delayBeforeNarrationStart);

        if (aiTutor == null)
        {
            Debug.LogWarning("[SceneNarrationController] No AITutor found — skipping narration, " +
                              "firing completion immediately so the flow doesn't stall.");
            HandleNarrationComplete(step, sceneName);
            yield break;
        }

        string line = NarrationLines.GetSceneNarration(step.sceneId);

        if (string.IsNullOrWhiteSpace(line))
        {
            Debug.LogWarning("[SceneNarrationController] Narration line is empty — skipping.");
            HandleNarrationComplete(step, sceneName);
            yield break;
        }

        // Subscribe once, unsubscribe as soon as it fires.
        UnityEngine.Events.UnityAction<string> handler = null;
        handler = (spokenText) =>
        {
            aiTutor.OnNarrationCompleted.RemoveListener(handler);
            MiniGameSequencer.Instance.MarkVisited(visitKey);

            // Advance the checkpoint AFTER narration completes, so the next
            // load of this scene is treated as a later visit. Doing this here
            // (not at the top of the coroutine) means anything that reads the
            // checkpoint mid-narration still sees "this" visit's value.
            if (step.advanceTrailCheckpointOnStart)
                MiniGameSequencer.Instance.AdvanceTrailCheckpoint();

            HandleNarrationComplete(step, sceneName);
        };
        aiTutor.OnNarrationCompleted.AddListener(handler);

        if (verbose) Debug.Log($"[SceneNarrationController] Speaking narration for '{visitKey}': {line}");
        aiTutor.SpeakNarration(line);
    }

    private void HandleNarrationComplete(SceneFlowStep step, string sceneName)
    {
        OnSceneNarrationCompleted?.Invoke(sceneName);

        if (!string.IsNullOrWhiteSpace(step.nextSceneName))
            StartCoroutine(TransitionAfterDelay(step.nextSceneName, step.delayAfterNarrationBeforeTransition));
        else if (verbose)
            Debug.Log($"[SceneNarrationController] '{sceneName}' at checkpoint {step.checkpointIndex} has no next scene — staying put.");
    }

    private IEnumerator TransitionAfterDelay(string nextSceneName, float delay)
    {
        if (delay > 0f)
            yield return new WaitForSecondsRealtime(delay);

        if (verbose) Debug.Log($"[SceneNarrationController] Loading next scene: '{nextSceneName}'");
        SceneManager.LoadScene(nextSceneName);
    }
}