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
///      finish, raises OnSceneFlowCompleted, then (if nextSceneName is
///      set) loads the next scene after a short beat.
///   3. If not found: does nothing for this scene load — either it's a
///      scene that doesn't narrate (e.g. _Bootstrap), or you haven't wired
///      up what happens at this checkpoint yet.
/// </summary>
public class SceneFlowController : MonoBehaviour
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

        [Tooltip("AnatomySceneId of nextSceneName — only used if nextSceneName is set. Passed to " +
                 "SceneTransitionManager so it can fade and set the correct HikeState (e.g. MiniGame " +
                 "vs Summit) for the scene we're heading INTO, same as any other transition in the game.")]
        public AnatomySceneId nextSceneId;

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

        [Tooltip("TESTING ONLY — check to mute just this step's narration line. Scene transition, " +
                 "checkpoint advancement, and visited-tracking still happen exactly as normal — only " +
                 "the spoken audio (and the tutor's gestures tied to it) are skipped. Leave unchecked " +
                 "for anything you're not actively speeding through.")]
        public bool skipNarrationForTesting = false;

        [Tooltip("If checked, the automatic transition to nextSceneName WAITS for " +
                 "MiniGameEvents.OnMiniGameComplete to fire with a system name matching this step's " +
                 "sceneId (e.g. sceneId = Nervous fires on MiniGameEvents.TriggerMiniGameComplete(\"Nervous\")) " +
                 "before proceeding — narration finishing (or being skipped for testing) is no longer " +
                 "enough by itself to advance. Check this for any scene where the player must actually " +
                 "finish the gesture minigame before moving on. Leave unchecked for narration-only scenes " +
                 "with no minigame (Trailhead, Summit, etc.).")]
        public bool waitForMiniGameCompletion = false;
    }

    public static SceneFlowController Instance { get; private set; }

    /// <summary>
    /// Fires after a scene's flow completes (or is skipped as a repeat visit,
    /// or muted for testing), with the scene name that just finished.
    /// Subscribe from any per-scene script that needs to react — no Inspector
    /// wiring required.
    /// </summary>
    public static event Action<string> OnSceneFlowCompleted;

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

    [Header("Testing")]
    [Tooltip("TESTING ONLY — master switch to mute ALL narration across every scene at once. " +
             "Scene transitions, checkpoint advancement, and visited-tracking still happen exactly " +
             "as normal for every step — only the spoken audio/gestures are skipped. Faster than " +
             "unchecking every step individually when you just want to blast through the whole flow. " +
             "Turn this back off before a real playtest or build.")]
    [SerializeField] private bool skipAllNarrationForTesting = false;

    [Tooltip("TESTING ONLY — when narration is being skipped (by either toggle above), also suppress " +
             "the automatic transition to nextSceneName, so you stay in this scene as long as you want " +
             "to actually test its minigame instead of being auto-advanced away after ~1-2 seconds. " +
             "Uncheck this if you specifically want to test the full auto-flow at high speed instead " +
             "(e.g. verifying checkpoint/transition logic end-to-end without sitting through audio).")]
    [SerializeField] private bool stayInSceneWhenSkippingForTesting = true;

    [Tooltip("TESTING ONLY — for a step with waitForMiniGameCompletion checked, when narration is " +
             "skipped for testing this is how long to wait for the REAL MiniGameEvents.OnMiniGameComplete " +
             "before giving up and advancing anyway. Lets you either actually play the minigame during a " +
             "quick test (transition still fires the instant you finish it) or just wait out the timeout " +
             "to blast past it. Real (non-testing) playthroughs always wait for genuine completion — " +
             "this timeout never applies outside the testing-skip path.")]
    [SerializeField] private float testingMiniGameTimeoutSeconds = 3f;

    private static bool _persisted = false;
    private Dictionary<(string sceneName, int checkpoint), SceneFlowStep> _flowLookup;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (_persisted)
        {
            if (verbose) Debug.Log("[SceneFlowController] Duplicate detected — destroying this GameObject only.");
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
                Debug.LogWarning($"[SceneFlowController] Duplicate flow entry for scene " +
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
                Debug.Log($"[SceneFlowController] No flow entry for '{scene.name}' at checkpoint {checkpoint} — skipping.");
            return;
        }

        if (aiTutor == null) aiTutor = FindAnyObjectByType<AITutor>();

        StartCoroutine(RunSequence(step, scene.name));
    }

    // ── Sequence ──────────────────────────────────────────────────────────────

    private IEnumerator RunSequence(SceneFlowStep step, string sceneName)
    {
        // Route through GameManager so OnSceneChanged fires (BodyDashboardHud's
        // system-indicator highlighting depends on that event). Falls back to
        // setting the session directly only if GameManager isn't present yet —
        // e.g. testing this scene in isolation without Bootstrap.
        if (GameManager.Instance != null)
            GameManager.Instance.GoToScene(step.sceneId);
        else
            AnatomyTutorSession.SetScene(step.sceneId);

        // Scoped by scene+checkpoint, not scene name alone — a later visit to the
        // same scene name is a different step with its own visited-state.
        string visitKey = $"{sceneName}@checkpoint{step.checkpointIndex}";
        bool alreadyVisited = MiniGameSequencer.Instance.HasVisited(visitKey);

        if (step.onlyNarrateOnFirstVisit && alreadyVisited)
        {
            if (verbose)
                Debug.Log($"[SceneFlowController] '{visitKey}' already visited — skipping flow.");

            HandleFlowComplete(step, sceneName);
            yield break;
        }

        bool skipForTesting = skipAllNarrationForTesting || step.skipNarrationForTesting;
        if (skipForTesting)
        {
            if (verbose)
                Debug.Log($"[SceneFlowController] 🔇 Narration skipped for testing: '{visitKey}'.");

            // Same bookkeeping the real completion handler does — testing
            // should still progress checkpoints/visited-state correctly, only
            // the actual audio/gesture playback is what's being skipped.
            MiniGameSequencer.Instance.MarkVisited(visitKey);
            if (step.advanceTrailCheckpointOnStart)
            {
                // Wait a frame before bumping. SceneFlowController is just one
                // of several independent subscribers to SceneManager.sceneLoaded
                // (PlayerRigPositioner, AIGuidePositioner, etc.), and Unity
                // doesn't guarantee they all run before this coroutine does. The
                // real (non-testing) narration path never hits this problem
                // because the advance already happens seconds later, well after
                // every other sceneLoaded subscriber for THIS load has finished
                // — this just gives the testing-skip path that same guarantee
                // instead of bumping the checkpoint mid-dispatch and stranding
                // whichever subscriber happens to run after this one (e.g. the
                // AI guide positioner reading a checkpoint no spawn point in
                // this scene actually matches yet).
                yield return null;
                MiniGameSequencer.Instance.AdvanceTrailCheckpoint();
            }

            if (step.waitForMiniGameCompletion)
            {
                // Still give the real minigame-complete event a chance to fire
                // (so you can actually play it during a quick test), but don't
                // hang forever if you don't — bail out after the testing
                // timeout and advance anyway.
                HandleFlowComplete(step, sceneName, testingMiniGameTimeoutSeconds);
            }
            else if (stayInSceneWhenSkippingForTesting)
            {
                // Deliberately does NOT call HandleFlowComplete here — that's
                // what queues the automatic transition to nextSceneName. Still
                // fire the completion event so anything listening (HUD, etc.)
                // knows this step is "done," it just doesn't chain onward.
                if (verbose)
                    Debug.Log($"[SceneFlowController] Staying in '{sceneName}' for testing — auto-transition suppressed.");
                OnSceneFlowCompleted?.Invoke(sceneName);
            }
            else
            {
                HandleFlowComplete(step, sceneName);
            }

            yield break;
        }

        if (step.delayBeforeNarrationStart > 0f)
            yield return new WaitForSecondsRealtime(step.delayBeforeNarrationStart);

        if (aiTutor == null)
        {
            Debug.LogWarning("[SceneFlowController] No AITutor found — skipping narration, " +
                              "firing completion immediately so the flow doesn't stall.");
            HandleFlowComplete(step, sceneName);
            yield break;
        }

        string line = NarrationLines.GetSceneNarration(step.sceneId);

        if (string.IsNullOrWhiteSpace(line))
        {
            Debug.LogWarning("[SceneFlowController] Narration line is empty — skipping.");
            HandleFlowComplete(step, sceneName);
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

            HandleFlowComplete(step, sceneName);
        };
        aiTutor.OnNarrationCompleted.AddListener(handler);

        if (verbose) Debug.Log($"[SceneFlowController] Speaking narration for '{visitKey}': {line}");
        aiTutor.SpeakNarration(line);
    }

    private void HandleFlowComplete(SceneFlowStep step, string sceneName, float miniGameTimeoutSeconds = 0f)
    {
        OnSceneFlowCompleted?.Invoke(sceneName);

        if (string.IsNullOrWhiteSpace(step.nextSceneName))
        {
            if (verbose)
                Debug.Log($"[SceneFlowController] '{sceneName}' at checkpoint {step.checkpointIndex} has no next scene — staying put.");
            return;
        }

        if (step.waitForMiniGameCompletion)
            StartCoroutine(WaitForMiniGameThenTransition(step, miniGameTimeoutSeconds));
        else
            StartCoroutine(TransitionAfterDelay(step));
    }

    /// <summary>
    /// Blocks the transition until MiniGameEvents.OnMiniGameComplete fires
    /// with a system name matching step.sceneId — e.g. sceneId = Nervous
    /// waits specifically for TriggerMiniGameComplete("Nervous"), which is
    /// exactly what NervousSystemInteraction fires once every neuron's been
    /// touched. This is what actually stops the scene from changing before
    /// the player has finished the task, instead of a fixed timer that has
    /// no idea whether the minigame is done.
    /// </summary>
    private IEnumerator WaitForMiniGameThenTransition(SceneFlowStep step, float timeoutSeconds = 0f)
    {
        string expectedSystem = step.sceneId.ToString();
        bool completed = false;

        Action<string> handler = null;
        handler = (system) =>
        {
            if (system == expectedSystem)
            {
                completed = true;
                MiniGameEvents.OnMiniGameComplete -= handler;
            }
        };
        MiniGameEvents.OnMiniGameComplete += handler;

        if (verbose)
        {
            string timeoutNote = timeoutSeconds > 0f ? $" (testing timeout: {timeoutSeconds}s)" : "";
            Debug.Log($"[SceneFlowController] Waiting for minigame completion ('{expectedSystem}') before transitioning to '{step.nextSceneName}'{timeoutNote}...");
        }

        // timeoutSeconds <= 0 means "real playthrough" — wait indefinitely for
        // the actual MiniGameEvents.OnMiniGameComplete, same as before. Only
        // the testing-skip path passes a positive timeout.
        float elapsed = 0f;
        while (!completed)
        {
            if (timeoutSeconds > 0f)
            {
                elapsed += Time.deltaTime;
                if (elapsed >= timeoutSeconds)
                {
                    MiniGameEvents.OnMiniGameComplete -= handler;
                    if (verbose)
                        Debug.Log($"[SceneFlowController] ⏱ Testing timeout ({timeoutSeconds}s) reached for '{expectedSystem}' — advancing without real completion.");
                    break;
                }
            }
            yield return null;
        }

        if (completed && verbose)
            Debug.Log($"[SceneFlowController] ✅ Minigame '{expectedSystem}' complete — proceeding to transition.");

        yield return TransitionAfterDelay(step);
    }

    private IEnumerator TransitionAfterDelay(SceneFlowStep step)
    {
        if (step.delayAfterNarrationBeforeTransition > 0f)
            yield return new WaitForSecondsRealtime(step.delayAfterNarrationBeforeTransition);

        if (verbose) Debug.Log($"[SceneFlowController] Loading next scene: '{step.nextSceneName}'");

        // Hand off to SceneTransitionManager so this transition gets the same
        // fade + HikeState handling as every other scene change in the game,
        // instead of a second, silent SceneManager.LoadScene path that skips
        // both. Falls back to a bare load only if no SceneTransitionManager
        // exists yet (e.g. quick single-scene testing).
        if (SceneTransitionManager.Instance != null)
            SceneTransitionManager.Instance.TransitionToScene(step.nextSceneId, step.nextSceneName);
        else
            SceneManager.LoadScene(step.nextSceneName);
    }
}