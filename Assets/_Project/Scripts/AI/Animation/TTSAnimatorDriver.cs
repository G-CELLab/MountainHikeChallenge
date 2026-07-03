using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives the character animator based on TTS state and gesture events.
///
/// Responsibilities:
///   - Sets isSpeaking bool while audio is playing OR while TTS player
///     has IsSpeaking = true (covers pre-fetch window before audio starts)
///   - Fires animator Triggers for gestures via TriggerGesture(GestureDefinition)
///
/// The character returns to Idle automatically via the Animator when not speaking.
/// Gesture animations are fired externally by GestureSynchronizer.
/// </summary>
public class TTSAnimatorDriver : MonoBehaviour
{
    [Header("References")]
    public TextToSpeechPlayer tts;
    public Animator animator;

    [Header("Thinking Gesture")]
    [Tooltip("Trigger parameter name for the thinking animation.")]
    public string thinkingTriggerParam = "Thinking";

    [Tooltip("Trigger for transitioning directly from Idle to Talking, bypassing Thinking. " +
             "Add a matching trigger in the Animator with a transition Idle → Talking. " +
             "Used by narration so the agent goes straight to talking without the thinking pose.")]
    public string startTalkingTriggerParam = "StartTalking";

    [Header("Animator Parameters")]
    [Tooltip("Bool set to true while TTS audio is playing.")]
    public string isSpeakingParam = "isSpeaking";

    [Header("Timing")]
    [Tooltip("Minimum seconds between identical gesture triggers. Prevents accidental double-fires.")]
    public float minRepeatTriggerIntervalSec = 0.75f;

    [Header("Debug")]
    public bool verbose = true;

    // ── Cached hashes ─────────────────────────────────────────────────────────

    private int  _isSpeakingHash;
    private bool _hasIsSpeaking;

    private readonly Dictionary<int, float> _lastTriggerTimeByHash = new Dictionary<int, float>();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (!tts)      tts      = FindAnyObjectByType<TextToSpeechPlayer>();
        if (!animator) animator = FindCompatibleAnimator();

        if (!animator)
        {
            Debug.LogError("[TTSAnimatorDriver] ❌ No Animator found.");
            enabled = false;
            return;
        }

        _isSpeakingHash = Animator.StringToHash(isSpeakingParam);
        _hasIsSpeaking  = HasBoolParam(isSpeakingParam);

        if (verbose)
        {
            LogCheck(isSpeakingParam, _hasIsSpeaking, "Bool");
            Debug.Log($"[TTSAnimatorDriver] Using Animator '{animator.name}' / " +
                      $"controller '{GetControllerName(animator)}'");
        }
    }

    private void OnValidate()
    {
        if (animator) _isSpeakingHash = Animator.StringToHash(isSpeakingParam);
    }

    private void Update()
    {
        if (!animator) return;

        // Consider speaking if audio is actively playing OR if the TTS player
        // has flagged IsSpeaking = true (covers the pre-fetch window where
        // clips are being downloaded but not yet playing through the audio source).
        bool audioPlaying = tts != null && tts.audioSource != null && tts.audioSource.isPlaying;
        bool ttsFlagged   = tts != null && tts.IsSpeaking;
        bool speaking     = audioPlaying || ttsFlagged;

        if (_hasIsSpeaking) animator.SetBool(_isSpeakingHash, speaking);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires the animator Trigger defined in a GestureDefinition.
    /// This is the single entry point for all gesture triggers.
    /// </summary>
    public void TriggerGesture(GestureDefinition def)
    {
        if (def == null)
        {
            Debug.LogWarning("[TTSAnimatorDriver] TriggerGesture called with null definition.");
            return;
        }

        if (string.IsNullOrWhiteSpace(def.animatorTriggerName))
        {
            Debug.LogWarning($"[TTSAnimatorDriver] GestureDefinition '{def.gestureName}' has no " +
                             $"animatorTriggerName set.");
            return;
        }

        int hash = Animator.StringToHash(def.animatorTriggerName);

        if (!HasTriggerParam(def.animatorTriggerName))
        {
            if (verbose)
                Debug.LogWarning($"[TTSAnimatorDriver] ❗ Animator missing Trigger: " +
                                 $"'{def.animatorTriggerName}' (gesture: '{def.gestureName}')");
            return;
        }

        // Debounce — ignore if the same trigger fired very recently
        float now = Time.realtimeSinceStartup;
        if (minRepeatTriggerIntervalSec > 0f && _lastTriggerTimeByHash.TryGetValue(hash, out float lastTime))
        {
            float elapsed = now - lastTime;
            if (elapsed >= 0f && elapsed < minRepeatTriggerIntervalSec)
            {
                if (verbose)
                    Debug.Log($"[TTSAnimatorDriver] ⏭️ Ignored duplicate: '{def.animatorTriggerName}' " +
                              $"({elapsed:F2}s < {minRepeatTriggerIntervalSec:F2}s)");
                return;
            }
        }

        animator.ResetTrigger(hash);
        animator.SetTrigger(hash);
        _lastTriggerTimeByHash[hash] = now;

        MainLogger.LogAIGestureEvent(def.gestureName.ToUpperInvariant());

        if (verbose)
            Debug.Log($"[TTSAnimatorDriver] {def.logEmoji} Trigger fired: '{def.animatorTriggerName}' " +
                      $"(gesture: '{def.gestureName}')");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool HasBoolParam(string paramName)
    {
        if (animator == null || string.IsNullOrEmpty(paramName)) return false;
        foreach (var p in animator.parameters)
            if (p.type == AnimatorControllerParameterType.Bool && p.name == paramName)
                return true;
        return false;
    }

    private bool HasTriggerParam(string paramName)
    {
        if (animator == null || string.IsNullOrEmpty(paramName)) return false;
        foreach (var p in animator.parameters)
            if (p.type == AnimatorControllerParameterType.Trigger && p.name == paramName)
                return true;
        return false;
    }

    // ── Animator discovery ────────────────────────────────────────────────────

    private Animator FindCompatibleAnimator()
    {
        var candidates = new List<Animator>();

        var local = GetComponent<Animator>();
        if (local) candidates.Add(local);

        var parent = GetComponentInParent<Animator>(true);
        if (parent && !candidates.Contains(parent)) candidates.Add(parent);

        foreach (var child in GetComponentsInChildren<Animator>(true))
            if (!candidates.Contains(child)) candidates.Add(child);

        Animator fallback = null;
        foreach (var candidate in candidates)
        {
            if (!candidate) continue;
            if (fallback == null) fallback = candidate;
            if (CandidateLooksCompatible(candidate)) return candidate;
        }

        return fallback;
    }

    private bool CandidateLooksCompatible(Animator candidate)
    {
        if (!candidate || !candidate.runtimeAnimatorController) return false;
        foreach (var p in candidate.parameters)
            if (p.type == AnimatorControllerParameterType.Bool && p.name == isSpeakingParam)
                return true;
        return false;
    }

    private static string GetControllerName(Animator target)
    {
        if (!target || !target.runtimeAnimatorController) return "<none>";
        return target.runtimeAnimatorController.name;
    }

    private static void LogCheck(string name, bool ok, string type)
    {
        if (ok) Debug.Log($"[TTSAnimatorDriver] ✅ {type} '{name}'");
        else    Debug.LogWarning($"[TTSAnimatorDriver] ❌ Missing {type} '{name}'");
    }

    // ── Thinking Animation Logic ──────────────────────────────────────────────

    private bool _thinkingActive = false;

    public void TriggerThinking()
    {
        if (!animator) return;
        if (!HasTriggerParam(thinkingTriggerParam)) return;
        animator.ResetTrigger(thinkingTriggerParam);
        animator.SetTrigger(thinkingTriggerParam);
        _thinkingActive = true;
        if (verbose) Debug.Log("[TTSAnimatorDriver] 🤔 Thinking triggered.");
    }

    public void CancelThinking()
    {
        if (!animator || !_thinkingActive) return;
        animator.ResetTrigger(thinkingTriggerParam);
        _thinkingActive = false;
        if (verbose) Debug.Log("[TTSAnimatorDriver] ✅ Thinking cancelled.");
    }

    /// <summary>
    /// Transitions directly from Idle to Talking without going through Thinking.
    /// Call this for narration where no "processing" period exists.
    /// Requires a "StartTalking" trigger in the Animator with an Idle → Talking transition.
    /// </summary>
    public void TriggerStartTalking()
    {
        if (!animator) return;
        if (!HasTriggerParam(startTalkingTriggerParam))
        {
            if (verbose)
                Debug.LogWarning($"[TTSAnimatorDriver] ❗ Animator missing Trigger: '{startTalkingTriggerParam}' — add it with an Idle → Talking transition.");
            return;
        }
        animator.ResetTrigger(startTalkingTriggerParam);
        animator.SetTrigger(startTalkingTriggerParam);
        if (verbose) Debug.Log("[TTSAnimatorDriver] 🗣️ StartTalking triggered.");
    }
}