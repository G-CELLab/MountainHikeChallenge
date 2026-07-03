using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// Data-driven gesture timing system. Reads a list of GestureDefinition assets
/// and triggers gestures whose keywords appear in the AI response.
///
/// Timing is synchronized to actual audio playback: the gesture fires at the
/// estimated moment the AI speaks the matched keyword.
///
/// Multiple gestures can fire per response, gated by a cooldown timer.
/// The cooldown compares estimated fire times, not queue times, so two gestures
/// in the same response are correctly spaced by their speech positions.
///
/// Already-scheduled keywords are tracked per response so repeat ProcessResponse
/// calls (from streaming) don't log false "skipped" messages.
///
/// Talking, Thinking, and Idle are state-driven and unaffected by this system.
///
/// To add a new gesture: create a GestureDefinition asset and drag it into
/// the Gesture Definitions list in the Inspector. No code changes needed.
/// </summary>
public class GestureSynchronizer : MonoBehaviour
{
    [Header("References")]
    public TTSAnimatorDriver ttsDriver;
    public TextToSpeechPlayer ttsPlayer;

    [Header("Gesture Definitions")]
    [Tooltip("List of all available gestures. Each is a GestureDefinition ScriptableObject. " +
             "Order matters — the first keyword match wins per scan.")]
    public List<GestureDefinition> gestureDefinitions = new List<GestureDefinition>();

    [Header("Timing")]
    [Tooltip("Estimated speaking rate in characters per second (~200 wpm = 15.3 cps)")]
    public float charactersPerSecond = 15.3f;

    [Tooltip("If the calculated delay exceeds this, it is clamped to this value.")]
    public float maxGestureDelaySec = 35f;

    [Tooltip("How long to wait for audio playback to start before falling back to relative timing.")]
    public float maxWaitForAudioStartSec = 6f;

    [Header("Gesture Cooldown")]
    [Tooltip("Minimum seconds between gesture fire times. Compared against estimated playback " +
             "positions, not queue time, so gestures in the same response are spaced correctly.")]
    public float gestureCooldownSec = 3f;

    [Header("Debug")]
    public bool verboseDebug = true;
    public bool showTimingCalculations = true;

    // ── State ─────────────────────────────────────────────────────────────────

    private float _audioStartTime      = -1f;

    // Stores the estimated real-time moment the last gesture will fire.
    // Cooldown is checked against this, not against queue time.
    private float _lastGestureFireTime = -999f;

    // Tracks which keywords have already been scheduled this response so that
    // repeat ProcessResponse calls from streaming don't log false "skipped" messages.
    private readonly HashSet<string> _scheduledKeywordsThisResponse = new HashSet<string>();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (ttsDriver == null) ttsDriver = FindAnyObjectByType<TTSAnimatorDriver>();
        if (ttsPlayer == null) ttsPlayer = FindAnyObjectByType<TextToSpeechPlayer>();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Call this when a new AI response begins.
    /// Resets audio timing and clears the scheduled keyword set.
    /// Does NOT reset the cooldown — in-flight gestures from the previous
    /// response should still prevent immediate re-firing.
    /// </summary>
    public void OnResponseStart()
    {
        _audioStartTime = -1f;
        _scheduledKeywordsThisResponse.Clear();

        if (verboseDebug)
            Debug.Log("[GestureSynchronizer] 🆕 New response — audio timing reset.");
    }

    /// <summary>Call this when TTS audio playback actually begins.</summary>
    public void OnAudioPlaybackStart()
    {
        _audioStartTime = Time.realtimeSinceStartup;

        if (verboseDebug)
            Debug.Log($"[GestureSynchronizer] 🔊 Audio started at {_audioStartTime:F2}s");
    }

    /// <summary>
    /// Process the accumulated AI response text and schedule gestures whose
    /// keywords are found, subject to the cooldown gate.
    ///
    /// Cooldown is based on estimated fire time (queue time + speech delay),
    /// not raw queue time. This means two keywords in the same streamed response
    /// are correctly treated as far apart if their speech positions are far apart.
    ///
    /// Keywords already scheduled this response are silently skipped so that
    /// repeat calls from streaming don't produce misleading "skipped" log spam.
    ///
    /// Safe to call repeatedly as tokens stream in.
    /// Returns true if a gesture was scheduled this call.
    /// </summary>
    public bool ProcessResponse(string accumulatedText)
    {
        if (string.IsNullOrWhiteSpace(accumulatedText)) return false;

        string normalized = NormalizeText(accumulatedText);

        foreach (GestureDefinition def in gestureDefinitions)
        {
            if (def == null || def.keywords == null || def.keywords.Length == 0) continue;

            foreach (string keyword in def.keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword)) continue;

                int keywordIndex = GetWholeWordPosition(normalized, keyword.ToLowerInvariant());
                if (keywordIndex < 0) continue;

                // Already scheduled this keyword this response — skip silently
                if (_scheduledKeywordsThisResponse.Contains(keyword)) continue;

                // Calculate when this gesture would actually fire in real time
                float delay             = CalculateDelay(normalized, keywordIndex, def);
                float estimatedFireTime = Time.realtimeSinceStartup + delay;

                // Cooldown: compare estimated fire times, not queue times
                float timeSinceLastFire = estimatedFireTime - _lastGestureFireTime;
                if (timeSinceLastFire < gestureCooldownSec)
                {
                    if (verboseDebug)
                        Debug.Log($"[GestureSynchronizer] ⏳ '{def.gestureName}' skipped — " +
                                  $"estimated fire is only {timeSinceLastFire:F2}s after last gesture " +
                                  $"(cooldown: {gestureCooldownSec:F1}s).");
                    continue;
                }

                // Good to go — mark keyword, store fire time, and schedule
                _scheduledKeywordsThisResponse.Add(keyword);
                _lastGestureFireTime = estimatedFireTime;
                StartCoroutine(TriggerAfterDelay(def, keyword, delay));

                if (verboseDebug)
                    Debug.Log($"[GestureSynchronizer] {def.logEmoji} Matched '{keyword}' " +
                              $"for gesture '{def.gestureName}' — scheduled in {delay:F2}s " +
                              $"(estimated fire at +{delay:F2}s)");

                break;
            }
        }

        return false;
    }

    /// <summary>
    /// True if the cooldown window around the last scheduled gesture is still active.
    /// </summary>
    public bool IsGestureOnCooldown =>
        (Time.realtimeSinceStartup - _lastGestureFireTime) < gestureCooldownSec;

    // ── Timing ────────────────────────────────────────────────────────────────

    private float CalculateDelay(string normalizedText, int keywordIndex, GestureDefinition def)
    {
        if (keywordIndex < 0 || keywordIndex >= normalizedText.Length)
        {
            float fallback = Mathf.Max(0f, def.fallbackDelaySeconds - Mathf.Max(0f, def.advanceSeconds));
            if (showTimingCalculations)
                Debug.LogWarning($"[GestureSynchronizer] ⚠️ Invalid keyword index for '{def.gestureName}', " +
                                 $"using fallback {fallback:F2}s");
            return fallback;
        }

        float rawTime    = keywordIndex / Mathf.Max(1f, charactersPerSecond);
        float scaledTime = rawTime * Mathf.Clamp(def.timeScale, 0.2f, 2.0f);
        float totalDelay = Mathf.Clamp(scaledTime - Mathf.Max(0f, def.advanceSeconds), 0f, maxGestureDelaySec);

        if (showTimingCalculations)
            Debug.Log($"[GestureSynchronizer] 📊 '{def.gestureName}' timing:" +
                      $"\n  Chars before keyword : {keywordIndex}" +
                      $"\n  Raw speak time       : {rawTime:F2}s" +
                      $"\n  Time scale           : {def.timeScale:F2}" +
                      $"\n  Advance              : {def.advanceSeconds:F2}s" +
                      $"\n  Final delay          : {totalDelay:F2}s");

        return totalDelay;
    }

    // ── Coroutine ─────────────────────────────────────────────────────────────

    private IEnumerator TriggerAfterDelay(GestureDefinition def, string matchedKeyword, float delaySec)
    {
        float waitStart = Time.realtimeSinceStartup;

        if (_audioStartTime <= 0f)
        {
            // Audio hasn't started yet — wait for it, then apply the full delay
            float waited = 0f;
            while (_audioStartTime <= 0f && waited < maxWaitForAudioStartSec)
            {
                yield return null;
                waited += Time.unscaledDeltaTime;
            }

            if (_audioStartTime <= 0f && verboseDebug)
                Debug.LogWarning($"[GestureSynchronizer] ⚠️ Audio start not detected within " +
                                 $"{maxWaitForAudioStartSec:F1}s — using relative wait.");

            yield return new WaitForSecondsRealtime(delaySec);
        }
        else
        {
            // Audio already playing — subtract elapsed time so gesture lands on time
            float elapsed   = Time.realtimeSinceStartup - _audioStartTime;
            float remaining = Mathf.Max(0f, delaySec - elapsed);
            yield return new WaitForSecondsRealtime(remaining);
        }

        if (ttsDriver == null)
        {
            Debug.LogWarning($"[GestureSynchronizer] ❌ Cannot trigger '{def.gestureName}' — TTSAnimatorDriver is null.");
            yield break;
        }

        ttsDriver.TriggerGesture(def);

        if (verboseDebug)
        {
            float actualWait = Time.realtimeSinceStartup - waitStart;
            Debug.Log($"[GestureSynchronizer] {def.logEmoji} Triggered '{def.gestureName}' " +
                      $"(keyword: '{matchedKeyword}') after {actualWait:F2}s actual wait.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int GetWholeWordPosition(string text, string keyword)
    {
        string pattern = @"\b" + Regex.Escape(keyword) + @"\b";
        Match  match   = Regex.Match(text, pattern);
        return match.Success ? match.Index : -1;
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.ToLowerInvariant();
        text = Regex.Replace(text, @"[^\w\s]", " ");
        text = Regex.Replace(text, @"\s+", " ");
        return text.Trim();
    }

    // ── Inspector helpers ─────────────────────────────────────────────────────

    public void SetCharactersPerSecond(float cps)
    {
        charactersPerSecond = Mathf.Clamp(cps, 5f, 30f);
        if (verboseDebug)
            Debug.Log($"[GestureSynchronizer] ⚙️ Characters per second → {charactersPerSecond:F1}");
    }
}