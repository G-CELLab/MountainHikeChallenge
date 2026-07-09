using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Central AI orchestrator for the VR anatomy tutor.
///
/// Pipeline:
///   OnTranscriptReceived(text)
///     → AnatomyTutorSession.Current (live scene snapshot)
///     → RAGIndex.Retrieve()
///     → AIResponseGenerator.GenerateStreamingResponse()
///         → per sentence: EnqueueTTS() → PrefetchNext() → DrainTTSQueue()
///     → GestureSynchronizer.ProcessResponse() (keyword-driven, no fallback)
///
/// Interruption:
///   OnSpeechStarted fires as soon as VAD detects the user speaking.
///   This immediately stops TTS and goes to idle — before the transcript arrives.
///   When the transcript arrives via OnTranscriptReceived, the query is sent
///   and the thinking animation triggers as normal.
///   Narration is NOT interruptible — _isNarrating blocks all interrupt paths.
///
/// Narration:
///   SpeakNarration() plays a fixed pre-written string directly through TTS
///   without going through OpenAI. Narration strings live in NarrationLines.cs.
///   NOT interruptible by user speech — narration must complete fully so the
///   user has the opportunity to see all gestures during the study.
///   If the user speaks during narration, their query is queued and answered
///   immediately after narration completes.
///   OnNarrationCompleted fires once a narration line finishes playing
///   (not fired if interrupted) — SceneNarrationController listens to this
///   to know when it's safe to transition to the next scene.
///
/// Logging:
///   Both EnqueueTTS() and the narration path call
///   TextToSpeechPlayer.SetCurrentSpeech() and AnatomyTutorSession.RecordTutorSpeech()
///   with the text about to be spoken, so CombinedLogger's AI_Speech column
///   (which reads straight from AnatomyTutorSession) captures every response
///   and narration line — not just ones routed through TextToSpeechPlayer.Speak().
/// </summary>
[RequireComponent(typeof(AIResponseGenerator))]
[AddComponentMenu("AI/AI Tutor")]
public class AITutor : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("References")]
    [SerializeField] private TextToSpeechPlayer     ttsPlayer;
    [SerializeField] private TTSAnimatorDriver      animatorDriver;
    [SerializeField] private OpenAISpeechRecognizer speechRecognizer;
    [SerializeField] private GestureSynchronizer    gestureSynchronizer;

    [Header("RAG Settings")]
    [Tooltip("Path relative to Assets folder. e.g. 'Scripts/AI/knowledge_base'")]
    [SerializeField] private string knowledgeBaseFolder = "Scripts/AI/knowledge_base";
    [SerializeField] private int    ragTopK             = 3;
    [SerializeField] private int    ragMaxContextChars  = 2500;
    [SerializeField] private float  ragMinScore         = 0.08f;
    [SerializeField] private bool   logRagRetrieval     = true;

    [Header("Tutor Settings")]
    [Tooltip("Cooldown after a response completes before accepting the next transcript (seconds)")]
    [SerializeField] private float responseCooldownSec = 0.5f;

    [Tooltip("How long to wait for the interrupted coroutine to release the gate before forcing a new query (seconds)")]
    [SerializeField] private float interruptTimeoutSec = 1.5f;

    [Header("Events")]
    public UnityEvent<string> OnResponseStarted    = new UnityEvent<string>();
    public UnityEvent<string> OnResponseCompleted  = new UnityEvent<string>();
    public UnityEvent<string> OnErrorOccurred      = new UnityEvent<string>();
    public UnityEvent<string> OnNarrationCompleted = new UnityEvent<string>();

    // ── Private state ─────────────────────────────────────────────────────────

    private AIResponseGenerator _generator;
    private RAGIndex            _ragIndex;

    private readonly Dictionary<string, AudioClip> _narrationCache = new Dictionary<string, AudioClip>();

    private bool  _isProcessing   = false;
    private bool  _isNarrating    = false;  // true only during narration — blocks all interrupts
    private bool  _interrupted    = false;
    private float _lastResponseAt = -999f;
    private bool _ttsBusy          = false;
    private int  _prefetchInFlight = 0;
    private int  _enqueueOrder     = 0;
    private int  _playbackOrder    = 0;
    private readonly Queue<string> _pendingNarrations = new Queue<string>();

    private readonly SortedDictionary<int, AudioClip> _orderedClipQueue
        = new SortedDictionary<int, AudioClip>();

    private static bool _persisted = false;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (_persisted)
        {
            Debug.LogWarning("[AITutor] Duplicate AITutor detected — destroying this instance.");
            Destroy(transform.root.gameObject);
            return;
        }
        _persisted = true;
        DontDestroyOnLoad(transform.root.gameObject);

        _generator  = GetComponent<AIResponseGenerator>();

        if (ttsPlayer           == null) ttsPlayer           = FindAnyObjectByType<TextToSpeechPlayer>();
        if (animatorDriver      == null) animatorDriver      = FindAnyObjectByType<TTSAnimatorDriver>();
        if (speechRecognizer    == null) speechRecognizer    = FindAnyObjectByType<OpenAISpeechRecognizer>();
        if (gestureSynchronizer == null) gestureSynchronizer = FindAnyObjectByType<GestureSynchronizer>();

        BuildRAGIndex();
        StartCoroutine(WarmUpOnStart());
        StartCoroutine(PrefetchAllNarrations());
    }

    private void OnEnable()
    {
        if (speechRecognizer != null)
        {
            speechRecognizer.OnTranscriptReady += OnTranscriptReceived;
            speechRecognizer.OnSpeechStarted   += OnUserSpeechStarted;
        }
    }

    private void OnDisable()
    {
        if (speechRecognizer != null)
        {
            speechRecognizer.OnTranscriptReady -= OnTranscriptReceived;
            speechRecognizer.OnSpeechStarted   -= OnUserSpeechStarted;
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// No-op kept for API compatibility — AnatomyTutorSession is a static
    /// class and is always live, so there is nothing to pull/refresh from.
    /// </summary>
    public void RefreshSceneState()
    {
        Debug.Log($"[AITutor] Scene state: {AnatomyTutorSession.Current}");
    }

    public AnatomyTutorSceneSnapshot GetSceneState() => AnatomyTutorSession.Current;

    public void ProcessUserQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        if (_isProcessing)
        {
            Debug.Log("[AITutor] Queuing new query after speech-start interrupt.");
            StartCoroutine(InterruptThenQuery(query));
            return;
        }

        if (Time.realtimeSinceStartup - _lastResponseAt < responseCooldownSec)
        {
            Debug.Log("[AITutor] In cooldown — query ignored.");
            return;
        }

        StartCoroutine(RunQueryCoroutine(query));
    }

    public bool IsProcessing => _isProcessing;

    public bool IsNarrating => _isNarrating;

    /// <summary>
    /// Speaks a fixed narration string directly through TTS without going
    /// through OpenAI. Narration strings live in NarrationLines.cs.
    /// NOT interruptible — narration plays to completion regardless of user speech.
    /// Skipped silently if the agent is already processing.
    /// </summary>
    public void SpeakNarration(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (_isProcessing)
        {
            Debug.Log("[AITutor] Narration queued — will play after current response.");
            _pendingNarrations.Enqueue(text);
            return;
        }

        StartCoroutine(NarrationCoroutine(text));
    }

    // ── Scene narration triggers ────────────────────────────────────────────

    public void TriggerTrailheadNarration()   => SpeakNarration(NarrationLines.Trailhead);
    public void TriggerCirculatoryNarration() => SpeakNarration(NarrationLines.Circulatory);
    public void TriggerRespiratoryNarration() => SpeakNarration(NarrationLines.Respiratory);
    public void TriggerDigestiveNarration()   => SpeakNarration(NarrationLines.Digestive);
    public void TriggerSummitNarration()      => SpeakNarration(NarrationLines.Summit);

    /// <summary>
    /// Immediately stops TTS playback and signals all coroutines to exit.
    /// The processing gate is released by the finally block in the active coroutine.
    /// Blocked during narration — narration must complete fully.
    /// </summary>
    public void Interrupt()
    {
        if (!_isProcessing) return;

        if (_isNarrating)
        {
            Debug.Log("[AITutor] 🛑 Interrupt blocked — narration in progress.");
            return;
        }

        Debug.Log("[AITutor] 🛑 Interrupt called.");
        _interrupted = true;

        if (ttsPlayer != null)
        {
            if (ttsPlayer.killAllTTSOnInterrupt)
                TextToSpeechPlayer.KillAllTTS();
            else
                ttsPlayer.StopSpeaking();
        }

        _orderedClipQueue.Clear();
        _ttsBusy = false;
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private void OnUserSpeechStarted()
    {
        if (!_isProcessing) return;

        if (_isNarrating)
        {
            Debug.Log("[AITutor] 🎤 Speech detected during narration — ignored.");
            return;
        }

        Debug.Log("[AITutor] 🎤 Speech detected — stopping TTS immediately.");

        if (ttsPlayer != null)
        {
            if (ttsPlayer.killAllTTSOnInterrupt)
                TextToSpeechPlayer.KillAllTTS();
            else
                ttsPlayer.StopSpeaking();
        }

        animatorDriver?.CancelThinking();

        _interrupted = true;
        _orderedClipQueue.Clear();
        _ttsBusy = false;
    }

    private void OnTranscriptReceived(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return;
        if (transcript.Trim().Length < 4) return;
        
        Debug.Log($"[AITutor] Transcript received: {transcript}");
        
        // Record user speech before processing
        AnatomyTutorSession.RecordUserSpeech(transcript);
        
        ProcessUserQuery(transcript);
    }

    private IEnumerator InterruptThenQuery(string query)
    {
        if (_isNarrating)
        {
            Debug.Log("[AITutor] Query dropped — asked during narration.");
            yield break;
        }

        float timeout = Time.realtimeSinceStartup + interruptTimeoutSec;
        while (_isProcessing && Time.realtimeSinceStartup < timeout)
            yield return null;

        if (_isProcessing)
        {
            Debug.LogWarning("[AITutor] Interrupt timeout — forcing gate release.");
            _isProcessing = false;
        }

        yield return new WaitForSecondsRealtime(0.1f);

        Debug.Log($"[AITutor] Starting new query after interrupt: {query}");
        StartCoroutine(RunQueryCoroutine(query));
    }

    // ── Narration coroutine ───────────────────────────────────────────────────

    private IEnumerator NarrationCoroutine(string text)
    {
        _isProcessing = true;
        _isNarrating  = true;
        _interrupted  = false;
        bool narrationCompleted = false;

        gestureSynchronizer?.OnResponseStart();
        speechRecognizer?.NotifyTTSStarted();

        Debug.Log($"[AITutor] 📢 Narration: {text}");
        AnatomyTutorSession.RecordTutorSpeech(text);

        if (!_interrupted)
        {
            if (ttsPlayer != null)
            {
                ttsPlayer.IsSpeaking = true;
                TextToSpeechPlayer.SetCurrentSpeech(text);
            }
            animatorDriver?.TriggerStartTalking();
            EnqueueTTSNarration(text);
            gestureSynchronizer?.ProcessResponse(text);
        }

        try
        {
            if (!_interrupted)
            {
                float ttsTimeout = Time.realtimeSinceStartup + 60f;
                while (_ttsBusy && Time.realtimeSinceStartup < ttsTimeout)
                {
                    if (_interrupted) break;
                    yield return null;
                }
            }

            narrationCompleted = !_interrupted;
        }
        finally
        {
            _isNarrating      = false;
            _isProcessing     = false;
            _ttsBusy          = false;
            _prefetchInFlight = 0;
            _enqueueOrder     = 0;
            _playbackOrder    = 0;
            _orderedClipQueue.Clear();
            _lastResponseAt   = Time.realtimeSinceStartup;

            if (_interrupted)
                Debug.Log("[AITutor] Narration interrupted — gate released.");
            else
                Debug.Log("[AITutor] Narration complete — gate released.");
        }
        
        // ── drain pending narrations ──────────────────────────────────────────
        if (!_interrupted && _pendingNarrations.Count > 0)
            SpeakNarration(_pendingNarrations.Dequeue());

        if (narrationCompleted)
            OnNarrationCompleted.Invoke(text);
    }

    private void EnqueueTTSNarration(string text)
    {
        if (_narrationCache.TryGetValue(text, out AudioClip cached))
        {
            // Play immediately from cache — no network wait
            int order = _enqueueOrder++;
            _orderedClipQueue[order] = cached;
            if (!_ttsBusy)
            {
                _ttsBusy = true;
                StartCoroutine(DrainTTSQueue());
            }
        }
        else
        {
            // Fallback to normal fetch if not cached yet
            EnqueueTTS(text);
        }
    }

    // ── Query coroutine ───────────────────────────────────────────────────────

    private IEnumerator RunQueryCoroutine(string query)
    {
        _isProcessing = true;
        _interrupted  = false;
        string fullResponse = "";
        bool   ttsCompleted = false;

        OnResponseStarted.Invoke(query);
        gestureSynchronizer?.OnResponseStart();
        animatorDriver?.TriggerThinking();

        try
        {
            AnatomyTutorSceneSnapshot sceneSnapshot = AnatomyTutorSession.Current;

            string ragQuery = $"{sceneSnapshot.Phase} {query}";
            float  bestScore;
            string bestSource;
            _ragIndex.BuildContextPrompt(
                ragQuery, sceneSnapshot.Phase, ragTopK, ragMaxContextChars,
                out bestScore, out bestSource);

            if (logRagRetrieval)
                Debug.Log($"[AITutor] RAG: query='{query}' bestScore={bestScore:0.000} source='{bestSource}'");

            if (bestScore < ragMinScore)
                Debug.LogWarning($"[AITutor] RAG confidence low ({bestScore:0.000} < {ragMinScore}).");

            List<RAGIndex.Hit> hits = _ragIndex.Retrieve(ragQuery, ragTopK);

            speechRecognizer?.NotifyTTSStarted();

            yield return _generator.GenerateStreamingResponse(
                query,
                hits,
                sceneSnapshot,
                onSentenceReady: sentence =>
                {
                    if (_interrupted) return;

                    animatorDriver?.CancelThinking();
                    EnqueueTTS(sentence);
                    fullResponse += (fullResponse.Length > 0 ? " " : "") + sentence;

                    gestureSynchronizer?.ProcessResponse(fullResponse);
                },
                onComplete: full =>
                {
                    if (_interrupted) return;

                    if (string.IsNullOrWhiteSpace(fullResponse))
                        fullResponse = full;

                    gestureSynchronizer?.ProcessResponse(fullResponse);
                }
            );

            if (!_interrupted)
            {
                float ttsTimeout = Time.realtimeSinceStartup + 30f;
                while (_ttsBusy && Time.realtimeSinceStartup < ttsTimeout)
                {
                    if (_interrupted) break;
                    yield return null;
                }

                if (_ttsBusy && !_interrupted)
                    Debug.LogWarning("[AITutor] TTS drain timed out after 30s.");
            }

            ttsCompleted = !_interrupted;
        }
        finally
        {
            _isProcessing     = false;
            _ttsBusy          = false;
            _prefetchInFlight = 0;
            _enqueueOrder     = 0;
            _playbackOrder    = 0;
            _orderedClipQueue.Clear();
            _lastResponseAt   = Time.realtimeSinceStartup;

            if (_interrupted)
                Debug.Log("[AITutor] Response interrupted — gate released.");
            else
                Debug.Log("[AITutor] Processing gate released.");
        }

        if (ttsCompleted && _pendingNarrations.Count > 0)
            SpeakNarration(_pendingNarrations.Dequeue());

        if (ttsCompleted)
            OnResponseCompleted.Invoke(fullResponse);
    }

    // ── TTS sentence queue ────────────────────────────────────────────────────

    private void EnqueueTTS(string sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence)) return;
        if (_interrupted) return;

        if (ttsPlayer != null)
        {
            TextToSpeechPlayer.SetCurrentSpeech(sentence);
            AnatomyTutorSession.RecordTutorSpeech(sentence);
        }

        int order = _enqueueOrder++;
        StartCoroutine(PrefetchNext(sentence, order));
        if (!_ttsBusy)
        {
            _ttsBusy = true;
            StartCoroutine(DrainTTSQueue());
        }
    }

    private IEnumerator PrefetchNext(string sentence, int order)
    {
        if (ttsPlayer == null) yield break;
        _prefetchInFlight++;
        AudioClip clip = null;
        yield return ttsPlayer.FetchAudioClip(sentence, c => clip = c);
        _prefetchInFlight--;

        if (!_interrupted)
            _orderedClipQueue[order] = clip;
    }

    private IEnumerator DrainTTSQueue()
    {
        while (true)
        {
            if (_interrupted) goto done;

            if (_prefetchInFlight == 0 && !_orderedClipQueue.ContainsKey(_playbackOrder))
                break;

            float waitStart = Time.realtimeSinceStartup;
            while (!_orderedClipQueue.ContainsKey(_playbackOrder))
            {
                if (_interrupted) goto done;
                if (_prefetchInFlight == 0 && _orderedClipQueue.Count == 0)
                    goto done;

                if (Time.realtimeSinceStartup - waitStart > 10f)
                {
                    Debug.LogWarning($"[AITutor] Timed out waiting for clip {_playbackOrder} (10s).");
                    goto done;
                }
                yield return null;
            }

            if (_interrupted) goto done;

            AudioClip clip = _orderedClipQueue[_playbackOrder];
            _orderedClipQueue.Remove(_playbackOrder);
            _playbackOrder++;

            bool isFirstClip = _playbackOrder == 1;

            bool playDone = false;
            if (clip != null && ttsPlayer != null)
            {
                ttsPlayer.PlayClip(clip, () => playDone = true);

                if (isFirstClip)
                    gestureSynchronizer?.OnAudioPlaybackStart();
            }
            else
            {
                playDone = true;
            }

            float playStart = Time.realtimeSinceStartup;
            while (!playDone)
            {
                if (_interrupted) goto done;

                if (_orderedClipQueue.ContainsKey(_playbackOrder) && ttsPlayer?.audioSource != null)
                {
                    float elapsed   = Time.realtimeSinceStartup - playStart;
                    float remaining = ttsPlayer.audioSource.clip != null
                        ? ttsPlayer.audioSource.clip.length - ttsPlayer.audioSource.time
                        : 0f;
                    if (elapsed > 0.3f && remaining < 0.15f) break;
                }

                if (Time.realtimeSinceStartup - playStart > 40f)
                {
                    Debug.LogWarning("[AITutor] Playback timed out (40s).");
                    break;
                }
                yield return null;
            }

            bool isLastClip = _prefetchInFlight == 0 && _orderedClipQueue.Count == 0;
            if (isLastClip)
            {
                if (ttsPlayer != null) ttsPlayer.IsSpeaking = false;
                Debug.Log("[AITutor] All sentences played — IsSpeaking = false.");
            }
        }

        done:
        if (ttsPlayer != null) ttsPlayer.IsSpeaking = false;
        _ttsBusy = false;
    }

    // ── RAG index setup ───────────────────────────────────────────────────────

    private void BuildRAGIndex()
    {
        _ragIndex = new RAGIndex { ChunkSize = 1500, ChunkOverlap = 80 };
        _ragIndex.Rebuild(ResolveKBPath());
    }

    private string ResolveKBPath()
    {
        string candidate = knowledgeBaseFolder ?? "";

        if (!Path.IsPathRooted(candidate))
            candidate = Path.GetFullPath(Path.Combine(Application.dataPath, candidate));

        if (Directory.Exists(candidate))
        {
            Debug.Log($"[AITutor] Knowledge base resolved: {candidate}");
            return candidate;
        }

        Debug.LogError(
            $"[AITutor] Knowledge base NOT found at: '{candidate}'\n" +
            $"Set 'Knowledge Base Folder' in the Inspector, e.g. 'Scripts/AI/knowledge_base'.");
        return candidate;
    }

    private IEnumerator WarmUpOnStart()
    {
        yield return new WaitForSeconds(5f);
        yield return _generator.WarmUp();
    }

    private IEnumerator PrefetchAllNarrations()
    {
        string[] lines = new[]
        {
            NarrationLines.Trailhead,
            NarrationLines.Nervous,
            NarrationLines.Skeletal,
            NarrationLines.SteepIncline,
            NarrationLines.Circulatory,
            NarrationLines.Respiratory,
            NarrationLines.Digestive,
            NarrationLines.Summit,
            NarrationLines.Homeostasis,
        };

        foreach (string line in lines)
        {
            yield return ttsPlayer.FetchAudioClip(line, clip =>
            {
                if (clip != null) _narrationCache[line] = clip;
            });
            Debug.Log($"[AITutor] Prefetched narration: {line.Substring(0, Mathf.Min(40, line.Length))}...");
        }

        Debug.Log("[AITutor] All narrations prefetched.");
    }
}