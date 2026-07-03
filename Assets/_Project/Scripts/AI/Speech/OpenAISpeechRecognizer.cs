using System;
using System.IO;
using System.Collections;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Captures mic audio, detects speech via VAD, transcribes via Whisper,
/// then fires OnTranscriptReady for AITutor to handle.
///
/// Changes from the original:
///   - GPTConnector dependency removed entirely
///   - NotifyTTSStarted() is now a direct public method (no reflection)
///   - ProcessUtterance is now awaited (yield return) to prevent overlapping requests
///   - Barge-in / interrupt logic stripped (AITutor handles response gating)
///   - OnSpeechStarted event added — fires as soon as VAD detects speech begin
///     so AITutor can stop TTS immediately without waiting for the transcript
///   - Permission check now polls until granted rather than failing immediately
///     on first launch when the user hasn't responded to the dialog yet
/// </summary>
public class OpenAISpeechRecognizer : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("OpenAI")]
    public string openAIKey = "";
    [Tooltip("OpenAI transcription model")]
    public string transcriptionModel = "gpt-4o-mini-transcribe";

    [Header("Record Settings")]
    public int   sampleRate       = 16000;
    public int   maxRecordTime    = 30;
    public float minRecordTime    = 0.6f;
    public float preRollSeconds   = 0.2f;
    [Tooltip("Cooldown after dispatching an utterance before listening again (seconds)")]
    public float recordCooldownSec = 0.3f;

    [Header("Voice Activity Detection")]
    [Tooltip("RMS volume threshold to start capturing")]
    public float startThreshold  = 0.02f;
    [Tooltip("How long volume must stay above startThreshold before we commit (seconds)")]
    public float startHoldTime   = 0.15f;
    [Tooltip("Volume threshold considered silence")]
    public float stopThreshold   = 0.015f;
    [Tooltip("Silence duration before we consider the utterance finished (seconds)")]
    public float stopSilenceTime = 0.6f;

    [Header("TTS Self-Interrupt Protection")]
    [Tooltip("Window after TTS starts during which mic input is ignored (prevents echo triggering VAD)")]
    public float ttsProtectionSec = 0.8f;

    [Header("Permission")]
    [Tooltip("How long to wait for the user to respond to the microphone permission dialog (seconds)")]
    public float permissionTimeoutSec = 30f;

    [Header("Debug")]
    public bool showDebugOverlay = true;
    public bool verboseDebug     = true;
    public float debugLogInterval = 0.25f;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired immediately when VAD detects speech has started.
    /// AITutor subscribes to this to stop TTS right away, before the
    /// transcript is ready. Does NOT fire during the TTS protection window.
    /// </summary>
    public event Action OnSpeechStarted;

    /// <summary>Fired with the Whisper transcript once an utterance is processed.</summary>
    public event Action<string> OnTranscriptReady;

    /// <summary>
    /// Called by AITutor (or TextToSpeechPlayer) when TTS playback begins,
    /// so we can open the protection window against echo-triggered VAD.
    /// </summary>
    public void NotifyTTSStarted()
    {
        _ttsStartTime = Time.realtimeSinceStartup;
        D("[Recognizer] TTS protection window opened");
    }

    // ── Private state ─────────────────────────────────────────────────────────

    private string    _micName;
    private AudioClip _micClip;
    private bool      _isRunning;
    private float     _ttsStartTime   = -999f;
    private float     _nextLogTime    = 0f;
    private float     _globalMax      = 0f;
    private float     _lastBatchMax   = 0f;
    private GUIStyle  _guiStyle;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        D("[Recognizer] Start()");
        StartCoroutine(InitAndLoop());
    }

    private void OnDisable()
    {
        _isRunning = false;

        if (!string.IsNullOrWhiteSpace(_micName) && Microphone.IsRecording(_micName))
            Microphone.End(_micName);
    }

    private void OnGUI()
    {
        if (!showDebugOverlay) return;
        if (_guiStyle == null)
        {
            _guiStyle = new GUIStyle(GUI.skin.box) { fontSize = 14 };
            _guiStyle.normal.textColor = Color.white;
        }
        GUI.Box(new Rect(10, 10, 260, 50),
            $"Mic peak: {_lastBatchMax:F4}\nGlobal max: {_globalMax:F4}", _guiStyle);
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    private IEnumerator InitAndLoop()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Use Unity's Android permission API. Poll until the user grants
        // microphone permission (the OS dialog is asynchronous on first run).
        float permElapsed = 0f;
        while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
        {
            if (permElapsed >= permissionTimeoutSec)
            {
                Debug.LogError($"[Recognizer] Microphone permission not granted after {permissionTimeoutSec}s — speech recognition disabled.");
                enabled = false;
                yield break;
            }
            permElapsed += Time.deltaTime;
            yield return null;
        }
        Debug.Log("[Recognizer] Microphone permission granted.");
#endif

        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("[Recognizer] No microphone detected.");
            yield break;
        }

        Debug.Log($"[Recognizer] Found {Microphone.devices.Length} microphone(s):");
        for (int i = 0; i < Microphone.devices.Length; i++)
            Debug.Log($"[Recognizer]   [{i}] {Microphone.devices[i]}");

        _micName = Microphone.devices[0];
        Debug.Log($"[Recognizer] ✔ Using mic: '{_micName}' | sampleRate={sampleRate} maxRecordTime={maxRecordTime}s");

        yield return RecordingLoop();
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    private IEnumerator RecordingLoop()
    {
        _isRunning = true;
        while (_isRunning)
        {
            string wavPath = null;
            yield return RecordUtteranceAndSave(p => wavPath = p);

            if (!string.IsNullOrEmpty(wavPath))
            {
                yield return ProcessUtterance(wavPath);
            }

            yield return new WaitForSeconds(recordCooldownSec);
        }
    }

    // ── Utterance processing ──────────────────────────────────────────────────

    private IEnumerator ProcessUtterance(string wavPath)
    {
        // Quick size check
        try
        {
            long size = new FileInfo(wavPath).Length;
            if (size < 3900) // < ~120ms of 16kHz 16-bit mono
            {
                Debug.LogWarning($"[Recognizer] Utterance too short ({size} bytes), skipping.");
                yield break;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Recognizer] Size check failed: {e.Message}");
        }

        // Transcribe
        string transcript = null;
        yield return Transcribe(wavPath, t => transcript = t);

        if (!string.IsNullOrWhiteSpace(transcript))
        {
            D($"[Recognizer] Transcript: {transcript}");
            OnTranscriptReady?.Invoke(transcript);
        }
        else
        {
            D("[Recognizer] Transcription returned empty.");
        }

        // Clean up the temporary WAV file to avoid filling persistent storage.
        try
        {
            if (!string.IsNullOrEmpty(wavPath) && File.Exists(wavPath))
                File.Delete(wavPath);
        }
        catch (Exception e)
        {
            D($"[Recognizer] Failed to delete temp wav: {e.Message}");
        }
    }

    // ── VAD + recording ───────────────────────────────────────────────────────

    private IEnumerator RecordUtteranceAndSave(Action<string> onSaved)
    {
        _globalMax = 0f;
        _lastBatchMax = 0f;

        _micClip = Microphone.Start(_micName, true, maxRecordTime, sampleRate);
        yield return new WaitUntil(() => Microphone.GetPosition(_micName) > 0);

        int   channels    = _micClip.channels;
        int   lastSample  = 0;
        bool  started     = false;
        float aboveTimer  = 0f;
        float silenceTimer = 0f;
        float recordedTime = 0f;
        float totalTime    = 0f;

        int          preRollMax = Mathf.CeilToInt(preRollSeconds * sampleRate);
        var          preRoll    = new System.Collections.Generic.Queue<float>(preRollMax);
        var          capture    = new System.Collections.Generic.List<float>(sampleRate * 10);

        while (true)
        {
            int cur = Microphone.GetPosition(_micName);
            if (cur < 0) { yield return null; continue; }

            int delta = cur - lastSample;
            if (delta < 0) delta += _micClip.samples;
            if (delta == 0) { ThrottledLog($"[VAD] waiting... peak={_lastBatchMax:F4}"); yield return null; continue; }

            // Read new samples
            float[] raw = new float[delta * channels];
            int     wrap = _micClip.samples - lastSample;
            if (delta <= wrap)
            {
                _micClip.GetData(raw, lastSample);
            }
            else
            {
                float[] a = new float[wrap * channels];
                float[] b = new float[(delta - wrap) * channels];
                _micClip.GetData(a, lastSample);
                _micClip.GetData(b, 0);
                Buffer.BlockCopy(a, 0, raw, 0, a.Length * sizeof(float));
                Buffer.BlockCopy(b, 0, raw, a.Length * sizeof(float), b.Length * sizeof(float));
            }

            float batchMax   = 0f;
            int   frames     = raw.Length / channels;

            for (int f = 0; f < frames; f++)
            {
                float mono = 0f;
                for (int c = 0; c < channels; c++) mono += raw[f * channels + c];
                mono /= channels;
                float abs = Mathf.Abs(mono);
                if (abs > batchMax) batchMax = abs;

                if (!started)
                {
                    if (preRoll.Count >= preRollMax) preRoll.Dequeue();
                    preRoll.Enqueue(mono);
                }
                else
                {
                    capture.Add(mono);
                    recordedTime += 1f / sampleRate;
                }
            }

            float batchDur = frames / (float)sampleRate;
            totalTime    += batchDur;
            _lastBatchMax = batchMax;
            if (batchMax > _globalMax) _globalMax = batchMax;

            // VAD start
            if (!started)
            {
                if (batchMax > startThreshold)
                {
                    bool inProtection = (Time.realtimeSinceStartup - _ttsStartTime) < ttsProtectionSec;
                    if (inProtection)
                    {
                        ThrottledLog("[VAD] TTS protection active, ignoring peak");
                        aboveTimer = 0f;
                    }
                    else
                    {
                        aboveTimer += batchDur;
                        if (aboveTimer >= startHoldTime)
                        {
                            started = true;
                            while (preRoll.Count > 0)
                            {
                                capture.Add(preRoll.Dequeue());
                                recordedTime += 1f / sampleRate;
                            }
                            silenceTimer = 0f;
                            D("[VAD] Speech started");

                            // Fire immediately so AITutor can stop TTS now,
                            // before waiting for the full transcript.
                            OnSpeechStarted?.Invoke();
                        }
                    }
                }
                else { aboveTimer = 0f; }
            }
            else
            {
                // VAD stop
                if (batchMax < stopThreshold)
                {
                    silenceTimer += batchDur;
                    if (recordedTime >= minRecordTime && silenceTimer >= stopSilenceTime)
                    {
                        D("[VAD] Speech ended");
                        break;
                    }
                }
                else { silenceTimer = 0f; }
            }

            if (totalTime >= maxRecordTime) break;

            lastSample = cur;
            yield return null;
        }

        Microphone.End(_micName);

        // Pre-roll salvage for short utterances
        if (capture.Count == 0 && preRoll.Count > 0 && _globalMax > startThreshold)
        {
            D("[VAD] Salvaging pre-roll buffer");
            while (preRoll.Count > 0) capture.Add(preRoll.Dequeue());
        }

        if (capture.Count == 0)
        {
            Debug.LogWarning($"[Recognizer] No speech captured. Peak={_globalMax:F4}, threshold={startThreshold}");
            onSaved?.Invoke(null);
            yield break;
        }

        // Write WAV
        var clip = AudioClip.Create("utterance", capture.Count, 1, sampleRate, false);
        clip.SetData(capture.ToArray(), 0);
        string path = Path.Combine(Application.persistentDataPath, "temp_speech.wav");
        try
        {
            byte[] wav = WavUtility.FromAudioClip(clip);
            File.WriteAllBytes(path, wav);
            D($"[Recognizer] Saved {wav.Length} bytes → {path}");
            onSaved?.Invoke(path);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Recognizer] WAV write failed: {e.Message}");
            onSaved?.Invoke(null);
        }
    }

    // ── Whisper transcription ─────────────────────────────────────────────────

    private IEnumerator Transcribe(string filePath, Action<string> onComplete)
    {
        string key = ResolveApiKey();
        if (string.IsNullOrEmpty(key))
        {
            Debug.LogError("[Recognizer] No API key.");
            onComplete?.Invoke(null);
            yield break;
        }

        byte[] audio;
        try { audio = File.ReadAllBytes(filePath); }
        catch (Exception e)
        {
            Debug.LogError($"[Recognizer] Read failed: {e.Message}");
            onComplete?.Invoke(null);
            yield break;
        }

        var form = new WWWForm();
        form.AddBinaryData("file", audio, "speech.wav", "audio/wav");
        form.AddField("model", string.IsNullOrWhiteSpace(transcriptionModel)
            ? "gpt-4o-mini-transcribe" : transcriptionModel);

        var req = UnityWebRequest.Post("https://api.openai.com/v1/audio/transcriptions", form);
        req.SetRequestHeader("Authorization", "Bearer " + key);
        req.timeout = 20;

        yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
        bool ok = req.result == UnityWebRequest.Result.Success;
#else
        bool ok = !req.isNetworkError && !req.isHttpError;
#endif
        if (!ok)
        {
            Debug.LogError($"[Recognizer] Transcription failed: {req.error}");
            onComplete?.Invoke(null);
            yield break;
        }

        string text = ExtractTranscript(req.downloadHandler.text);
        onComplete?.Invoke(string.IsNullOrWhiteSpace(text) ? null : text.Trim());
    }

    private static string ExtractTranscript(string json)
    {
        try
        {
            var parsed = JsonUtility.FromJson<TranscriptResponse>(json);
            if (parsed != null && !string.IsNullOrWhiteSpace(parsed.text))
                return parsed.text;
        }
        catch { /* fall through */ }

        var m = Regex.Match(json ?? "", "\"text\"\\s*:\\s*\"(?<t>(?:\\\\.|[^\"])*)\"");
        return m.Success ? Regex.Unescape(m.Groups["t"].Value) : null;
    }

    private string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(openAIKey)) return openAIKey;
        string env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void D(string msg) { if (verboseDebug) Debug.Log(msg); }

    private void ThrottledLog(string msg)
    {
        if (!verboseDebug) return;
        if (Time.realtimeSinceStartup < _nextLogTime) return;
        _nextLogTime = Time.realtimeSinceStartup + Mathf.Max(0.05f, debugLogInterval);
        Debug.Log(msg);
    }

    [Serializable] private class TranscriptResponse { public string text; }
}