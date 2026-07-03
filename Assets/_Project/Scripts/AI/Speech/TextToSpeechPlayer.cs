using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

public class TextToSpeechPlayer : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Debug")]
    public bool logInterruptWarnings = false;

    [Header("OpenAI")]
    public string openAIKey = "*****";
    public string ttsModel  = "gpt-4o-mini-tts";
    public string voice     = "nova";

    [TextArea(3, 6)]
    public string ttsInstructions = "Speak in a warm, friendly, consistent tone. " +
                                    "Maintain a moderate and steady pitch throughout. " +
                                    "Keep energy level calm and even. ";

    [Header("Audio")]
    public AudioSource audioSource;

    [Tooltip("When interrupted, stop all TTS instances (true) or just this one (false).")]
    public bool killAllTTSOnInterrupt = true;

    // ── Static registry ───────────────────────────────────────────────────────

    private static readonly HashSet<TextToSpeechPlayer> INSTANCES = new HashSet<TextToSpeechPlayer>();

    public static void KillAllTTS()
    {
        foreach (var t in INSTANCES)
            if (t != null) t.StopSpeaking();
    }

    // ── Speech tracking ───────────────────────────────────────────────────────

    public bool IsSpeaking { get; set; } = false;

    private static string _currentSpeechText = "";
    private static string _lastSpeechText    = "";
    public static string GetCurrentSpeech() =>
        !string.IsNullOrEmpty(_currentSpeechText) ? _currentSpeechText : _lastSpeechText;
    public static void SetCurrentSpeech(string t)  { _currentSpeechText = t; _lastSpeechText = t; }
    public static void ClearCurrentSpeech()         { _currentSpeechText = ""; _lastSpeechText = ""; }

    // ── Playback completion tracking ──────────────────────────────────────────

    private Action _onPlaybackComplete;
    private bool   _playbackCompleteInvoked;

    private void StartPlaybackCompletionTracking(Action cb)
    {
        _onPlaybackComplete      = cb;
        _playbackCompleteInvoked = false;
    }

    private void CompleteCurrentPlayback()
    {
        if (_playbackCompleteInvoked) return;
        _playbackCompleteInvoked = true;
        var cb = _onPlaybackComplete;
        _onPlaybackComplete = null;
        cb?.Invoke();
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        INSTANCES.Add(this);
        if (audioSource == null) audioSource = GetComponent<AudioSource>();
        if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
        ConfigureAudioSource();
    }

    private void OnDestroy() { INSTANCES.Remove(this); }

    private void OnEnable() { StartCoroutine(GlobalAudioMonitor()); }

    private IEnumerator GlobalAudioMonitor()
    {
        while (true)
        {
            yield return new WaitForSeconds(0.1f);
        }
    }

    // ── Stop ──────────────────────────────────────────────────────────────────

    public void StopSpeaking()
    {
        if (!IsSpeaking && (audioSource == null || !audioSource.isPlaying)) return;

        if (logInterruptWarnings) Debug.LogWarning("[TTS] StopSpeaking()");
        else                      Debug.Log("[TTS] StopSpeaking()");

        IsSpeaking = false;
        CompleteCurrentPlayback();

        if (audioSource != null)
        {
            try
            {
                if (audioSource.isPlaying) audioSource.Stop();
                audioSource.time = 0f;
                audioSource.clip = null;
                ConfigureAudioSource();
            }
            catch (Exception e) { Debug.LogError("[TTS] StopSpeaking exception: " + e); }
        }
    }

    // ── Recognizer binding (no-op — interruption via NotifyTTSStarted) ────────

    public void BindRecognizer(OpenAISpeechRecognizer rec) { /* no-op */ }

    // ── Speak: fetch then play ────────────────────────────────────────────────

    public void Speak(string text, Action onComplete)
    {
        Debug.Log($"[TTS] Speak() {text?.Length ?? 0} chars");
        StartPlaybackCompletionTracking(onComplete);

        if (string.IsNullOrWhiteSpace(text))
        {
            Debug.LogWarning("[TTS] Speak() called with empty text.");
            CompleteCurrentPlayback();
            return;
        }

        SetCurrentSpeech(text);
        StartCoroutine(FetchAndPlay(text, onComplete));
    }

    private IEnumerator FetchAndPlay(string text, Action onComplete)
    {
        AudioClip clip = null;
        yield return FetchAudioClip(text, c => clip = c);

        if (clip == null)
        {
            Debug.LogError("[TTS] FetchAndPlay: clip is null, skipping playback.");
            IsSpeaking = false;
            CompleteCurrentPlayback();
            yield break;
        }

        yield return PlayClipInternal(clip);
        IsSpeaking = false;
        CompleteCurrentPlayback();
    }

    // ── FetchAudioClip: download without playing (used by AITutor prefetch) ──

    /// <summary>
    /// Downloads TTS audio and returns a ready AudioClip without playing it.
    /// AITutor calls this while the current sentence is playing so the next
    /// clip is ready the moment playback finishes, eliminating inter-sentence gaps.
    /// </summary>
    public IEnumerator FetchAudioClip(string text, Action<AudioClip> onReady)
    {
        if (string.IsNullOrWhiteSpace(text)) { onReady?.Invoke(null); yield break; }

        string key = ResolveOpenAIKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            Debug.LogError("[TTS] FetchAudioClip: no API key.");
            onReady?.Invoke(null);
            yield break;
        }

        string json = JsonUtility.ToJson(new SpeechRequest
        {
            model           = string.IsNullOrWhiteSpace(ttsModel) ? "gpt-4o-mini-tts" : ttsModel,
            input           = text,
            voice           = voice,
            response_format = "wav",
            instructions    = ttsInstructions
        });

        var req = new UnityWebRequest("https://api.openai.com/v1/audio/speech", "POST");
        req.uploadHandler   = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Authorization", "Bearer " + key);
        req.SetRequestHeader("Content-Type",  "application/json");

        yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
        bool ok = req.result == UnityWebRequest.Result.Success;
#else
        bool ok = !req.isNetworkError && !req.isHttpError;
#endif
        if (!ok)
        {
            Debug.LogError($"[TTS] API error: {req.error} (HTTP {req.responseCode})");
            if (req.downloadHandler != null) Debug.LogError("[TTS] Detail: " + req.downloadHandler.text);
            onReady?.Invoke(null);
            yield break;
        }

        byte[] wavData = req.downloadHandler.data;
        AudioClip clip = WavUtility.ToAudioClip(wavData);
        if (clip == null)
        {
            Debug.LogError("[TTS] Failed to decode WAV from bytes");
            onReady?.Invoke(null);
            yield break;
        }

        Debug.Log($"[TTS] Decoded {wavData.Length} bytes in memory");
        Debug.Log($"[TTS] Speaking: {text}");
        onReady?.Invoke(clip);
    }

    // ── PlayClip: play a pre-fetched clip (used by AITutor prefetch queue) ────

    /// <summary>
    /// Play an already-fetched AudioClip and invoke onDone when finished.
    /// Fully self-contained — does NOT touch the shared _onPlaybackComplete slot
    /// so it cannot interfere with Speak() callbacks running on the same component.
    /// </summary>
    public void PlayClip(AudioClip clip, Action onDone)
    {
        if (clip == null) { onDone?.Invoke(); return; }
        StartCoroutine(PlayClipSelfContained(clip, onDone));
    }

    private IEnumerator PlayClipSelfContained(AudioClip clip, Action onDone)
    {
        if (audioSource == null)
            audioSource = gameObject.GetComponent<AudioSource>()
                       ?? gameObject.AddComponent<AudioSource>();

        ConfigureAudioSource();
        audioSource.clip = clip;
        IsSpeaking = true;
        audioSource.Play();

        float expected      = clip.length;
        float start         = Time.realtimeSinceStartup;
        float lastPlayingAt = start;
        const float grace   = 0.05f;

        while (true)
        {
            if (audioSource == null) break;
            if (audioSource.isPlaying)
            {
                lastPlayingAt = Time.realtimeSinceStartup;
            }
            else
            {
                float gap     = Time.realtimeSinceStartup - lastPlayingAt;
                float elapsed = Time.realtimeSinceStartup - start;
                if (gap >= grace && elapsed >= expected * 0.8f) break;
            }
            yield return null;
        }

        Debug.Log("[TTS] PlayClip done, invoking onDone callback.");
        onDone?.Invoke();
    }

    // ── Model audio base64 playback ───────────────────────────────────────────

    public void PlayModelAudioBase64(string base64Data, string format, Action onComplete)
    {
        StartPlaybackCompletionTracking(onComplete);
        StartCoroutine(PlayModelAudioBase64Co(base64Data, format, onComplete));
    }

    public void PlayModelAudioBase64(string base64Data, string format, string speechText, Action onComplete)
    {
        SetCurrentSpeech(speechText);
        StartPlaybackCompletionTracking(onComplete);
        StartCoroutine(PlayModelAudioBase64Co(base64Data, format, onComplete));
    }

    private IEnumerator PlayModelAudioBase64Co(string base64Data, string format, Action onComplete)
    {
        if (string.IsNullOrEmpty(base64Data)) { CompleteCurrentPlayback(); yield break; }

        string ext  = (format ?? "").ToLowerInvariant() == "wav" ? "wav" : "mp3";
        string path = Path.Combine(Application.persistentDataPath, "gpt_audio_reply." + ext);

        try { File.WriteAllBytes(path, Convert.FromBase64String(base64Data)); }
        catch (Exception e)
        {
            Debug.LogError("[TTS] Write model audio failed: " + e.Message);
            CompleteCurrentPlayback();
            yield break;
        }

        AudioType at = ext == "wav" ? AudioType.WAV : AudioType.MPEG;
        using (var www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, at))
        {
            yield return www.SendWebRequest();
#if UNITY_2020_2_OR_NEWER
            bool ok = www.result == UnityWebRequest.Result.Success;
#else
            bool ok = !www.isNetworkError && !www.isHttpError;
#endif
            if (!ok)
            {
                Debug.LogError("[TTS] Load model audio failed: " + www.error);
                CompleteCurrentPlayback();
                yield break;
            }
            yield return PlayClipInternal(DownloadHandlerAudioClip.GetContent(www));
        }

        IsSpeaking = false;
        onComplete?.Invoke();
    }

    // ── Internal clip playback with grace-period robustness ──────────────────

    private IEnumerator PlayClipInternal(AudioClip clip)
    {
        if (audioSource == null)
            audioSource = gameObject.GetComponent<AudioSource>()
                       ?? gameObject.AddComponent<AudioSource>();

        ConfigureAudioSource();
        audioSource.clip = clip;
        IsSpeaking = true;
        audioSource.Play();

        float expected      = clip != null ? clip.length : 0f;
        float start         = Time.realtimeSinceStartup;
        float lastPlayingAt = start;
        const float grace   = 0.5f;

        while (true)
        {
            if (audioSource == null) break;
            if (audioSource.isPlaying)
            {
                lastPlayingAt = Time.realtimeSinceStartup;
            }
            else
            {
                float gap     = Time.realtimeSinceStartup - lastPlayingAt;
                float elapsed = Time.realtimeSinceStartup - start;
                if (gap >= grace && elapsed >= expected * 0.8f) break;
            }
            yield return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ConfigureAudioSource()
    {
        if (audioSource == null) return;
        audioSource.loop         = false;
        audioSource.playOnAwake  = false;
        audioSource.spatialBlend = 0f;
        audioSource.dopplerLevel = 0f;
        audioSource.mute         = false;
        if (audioSource.volume <= 0f) audioSource.volume = 1f;
    }

    private string ResolveOpenAIKey()
    {
        if (IsLikelyRealKey(openAIKey)) return openAIKey.Trim();

        string env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (IsLikelyRealKey(env)) return env.Trim();

        var gen = FindAnyObjectByType<AIResponseGenerator>();
        if (gen != null && IsLikelyRealKey(gen.OpenAIKey)) return gen.OpenAIKey.Trim();

        return null;
    }

    private static bool IsLikelyRealKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        string t = key.Trim();
        return t.Length >= 20
            && !t.Contains("*")
            && !t.Equals("placeholder", StringComparison.OrdinalIgnoreCase)
            && t.StartsWith("sk-", StringComparison.OrdinalIgnoreCase);
    }

    // ── Serializable types ────────────────────────────────────────────────────

    [Serializable]
    public class SpeechRequest
    {
        public string model;
        public string input;
        public string voice;
        public string response_format;
        public string instructions;
    }
}