using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Sends a RAG-augmented prompt to OpenAI Chat Completions (streaming).
/// Responsible only for: system template, payload construction, and streaming.
/// All scene context comes from AnatomyTutorSceneSnapshot (AnatomyTutorSession.Current).
/// All knowledge comes from RAG.
/// Falls back to a local anatomy tutor response when no API key is configured.
/// </summary>
public class AIResponseGenerator : MonoBehaviour
{
    [Header("OpenAI")]
    [SerializeField] private string openaiApiKey   = "";
    [SerializeField] private string openaiModel    = "gpt-4o-mini";
    [SerializeField] private float  temperature    = 0.2f;
    [SerializeField] private int    maxTokens      = 150;
    [SerializeField] private int    timeoutSeconds = 30;

    [Header("Streaming TTS")]
    [Tooltip("Fire onSentenceReady as soon as the first sentence boundary appears in the stream.")]
    [SerializeField] private bool streamingSentences      = true;
    [Tooltip("Minimum characters before we treat an early chunk as a complete sentence.")]
    [SerializeField] private int  minCharsBeforeEarlyFire = 40;

    [Header("Debug")]
    [SerializeField] private bool logRequests = true;
    [SerializeField] private bool logLatency  = true;

    private const string ENDPOINT = "https://api.openai.com/v1/chat/completions";

    private static bool _sslInitialized = false;

    private static void EnsureSSLInitialized()
    {
        if (_sslInitialized) return;
        _sslInitialized = true;
        
        // Allow self-signed certificates and certificate chain issues on mobile
        // This is necessary for some Android devices with incomplete CA certificates
        ServicePointManager.ServerCertificateValidationCallback = 
            (RemoteCertificateValidationCallback)Delegate.Combine(
                ServicePointManager.ServerCertificateValidationCallback,
                new RemoteCertificateValidationCallback(
                    (sender, certificate, chain, sslPolicyErrors) => {
                        // Accept all certificates for OpenAI endpoint
                        // In production, you may want to validate specific certificates
                        if (sender is HttpWebRequest req && 
                            req.RequestUri.Host.Contains("openai.com"))
                        {
                            return true;
                        }
                        return sslPolicyErrors == SslPolicyErrors.None;
                    }));
    }

    private const string SYSTEM_TEMPLATE =
        "You are Carla, a warm and encouraging guide inside the Mountain Hike Challenge VR experience — " +
        "a middle school anatomy lesson where the learner climbs Mount Timpanogos on \"the Summit Challenge\" " +
        "while their own body's systems work together to get them to the top. " +
        "You already introduced yourself by name at the very start of the hike, so don't re-introduce yourself " +
        "again in later replies — just continue speaking as Carla, in first person, present tense, as if you're " +
        "standing there with the learner watching the same thing they are. " +
        "Answer anatomy questions clearly and accurately, and use the knowledge base excerpts for scene guidance, " +
        "vocabulary, and known misconceptions. If a learner's question reflects one of the common misconceptions " +
        "in the knowledge base (e.g. the heart 'making' blood, body systems working one at a time instead of " +
        "together, digestion only happening in the stomach, or bones being lifeless), gently correct it using the " +
        "knowledge base's explanation rather than just answering the surface question. " +
        "If asked what to do, guide the learner using the knowledge base for the current anatomy scene. " +
        "Keep replies to 1-2 sentences. Never invent instructions not in the knowledge base. " +
        "Always connect the current system to at least one other body system — the throughline of this whole " +
        "experience is that body systems work together to maintain homeostasis, not in isolation.";

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Stream a response for userQuery given RAG chunks and scene state.
    /// onSentenceReady — called once per sentence as tokens arrive (for early TTS).
    /// onComplete      — called once with the full assembled response text.
    ///
    /// Socratic dialogue mode (mini-games): pass phaseInstructions,
    /// dialogueHistoryBlock and onAssessmentParsed to turn on the
    /// [[ASSESS:...]] control-tag protocol — see SocraticDialogueController.
    /// Leave them null/default for the original free-form Q&A behavior used
    /// outside mini-game scenes (Trailhead, SteepIncline, etc.) — nothing
    /// about that path changes.
    /// </summary>
    public IEnumerator GenerateStreamingResponse(
        string                    userQuery,
        List<RAGIndex.Hit>        ragHits,
        AnatomyTutorSceneSnapshot sceneState,
        Action<string>            onSentenceReady,
        Action<string>            onComplete,
        string                    phaseInstructions       = null,
        string                    dialogueHistoryBlock    = null,
        string                    crossSystemMemoryBlock  = null,
        bool                      requireAssessmentTag    = false,
        Action<StudentUnderstanding, string> onAssessmentParsed = null)
    {
        var t0 = DateTime.Now;

        string systemPrompt = BuildSystemPrompt(ragHits, phaseInstructions, requireAssessmentTag);
        string userMessage  = BuildUserMessage(userQuery, sceneState, dialogueHistoryBlock, crossSystemMemoryBlock);

        if (logRequests)
        {
            Debug.Log($"[AIResponseGenerator] System ({systemPrompt.Length} chars):\n{systemPrompt}");
            Debug.Log($"[AIResponseGenerator] User: {userMessage.Substring(0, Mathf.Min(300, userMessage.Length))}");
        }

        string apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Debug.LogWarning("[AIResponseGenerator] No API key found. Using local fallback response.");
            string localFallback = BuildLocalFallbackResponse(userQuery, sceneState, ragHits);

            if (requireAssessmentTag)
            {
                // No live model to classify with — default to Partial so the
                // FSM still advances predictably instead of stalling offline.
                onAssessmentParsed?.Invoke(StudentUnderstanding.Partial, null);
            }

            yield return EmitLocalFallback(localFallback, onSentenceReady, onComplete);
            yield break;
        }

        EnsureSSLInitialized();
        string payload = BuildPayload(systemPrompt, userMessage);
        yield return RunStreamingRequest(
            payload, apiKey, onSentenceReady, onComplete,
            requireAssessmentTag, onAssessmentParsed);

        if (logLatency)
            Debug.Log($"[AIResponseGenerator] Total time: {(DateTime.Now - t0).TotalMilliseconds:F0}ms");
    }

    // ── Prompt builders ───────────────────────────────────────────────────────

    /// <summary>
    /// System prompt = core template + RAG knowledge base excerpts + (in
    /// mini-game Socratic dialogue mode) the current phase's instructions
    /// and the assessment-tag protocol.
    /// </summary>
    private static string BuildSystemPrompt(
        List<RAGIndex.Hit> hits,
        string             phaseInstructions    = null,
        bool               requireAssessmentTag = false)
    {
        var sb = new StringBuilder(SYSTEM_TEMPLATE);

        if (hits != null && hits.Count > 0)
        {
            sb.Append("\n\nKNOWLEDGE BASE:\n");
            for (int i = 0; i < hits.Count; i++)
                sb.Append($"[{i + 1}] {hits[i].Chunk.Text}\n\n");
            sb.Append("Prefer the knowledge base over generic answers.");
        }

        if (!string.IsNullOrWhiteSpace(phaseInstructions))
        {
            // This override has to come first and be forceful: the base
            // SYSTEM_TEMPLATE above explicitly tells the model to "answer
            // anatomy questions clearly," "if asked what to do, guide the
            // learner," and "gently correct" misconceptions using the
            // explanation — all three directly cause the model to just
            // state the answer the moment a student asks "what am I
            // supposed to do?" or seems confused, which defeats the entire
            // point of Socratic mode. Without this override, that base
            // instruction wins far more often than the phase instructions do.
            sb.Append("\n\nSOCRATIC DIALOGUE MODE (this refines, not replaces, the persona above): You're having " +
                      "a guided conversation, like a responsive teacher — not running a quiz that withholds " +
                      "information. It's good to affirm what the student gets right (\"You're on the right " +
                      "track!\"), name or label things for them (e.g. what an object in the scene is called), and " +
                      "share a piece of the mechanism conversationally when it helps them keep moving, especially " +
                      "if they're unsure or only partly right. The point isn't secrecy, it's pacing: build the " +
                      "full explanation together across several turns rather than handing them the whole " +
                      "mechanism in one go, and don't just state the complete answer to the phase's core question " +
                      "outright — always follow up with something that keeps them engaged: a related question, " +
                      "or an invitation to try touching/interacting with something specific in the simulation. " +
                      "The DIALOGUE MODE notes below say more about this phase specifically.");

            sb.Append("\n\n").Append(phaseInstructions);
        }

        if (requireAssessmentTag)
        {
            sb.Append("\n\n").Append(SocraticDialogueController.ASSESSMENT_TAG_INSTRUCTION);
        }

        return sb.ToString();
    }

    /// <summary>
    /// User message = scene context + (in Socratic mode) this mini-game's
    /// dialogue history and any cross-mini-game memory + the learner's query.
    /// </summary>
    private static string BuildUserMessage(
        string                    query,
        AnatomyTutorSceneSnapshot state,
        string                    dialogueHistoryBlock   = null,
        string                    crossSystemMemoryBlock = null)
    {
        string context = state != null ? state.ToContextString() : "Scene context unavailable.";
        var sb = new StringBuilder(context);

        if (!string.IsNullOrWhiteSpace(crossSystemMemoryBlock))
            sb.Append("\n\n").Append(crossSystemMemoryBlock);

        if (!string.IsNullOrWhiteSpace(dialogueHistoryBlock))
            sb.Append("\n\n").Append(dialogueHistoryBlock);

        sb.Append("\n\nStudent: ").Append(query);
        return sb.ToString();
    }

    private static string BuildLocalFallbackResponse(string query, AnatomyTutorSceneSnapshot state, List<RAGIndex.Hit> hits)
    {
        string phase = state != null ? state.Phase : "trailhead";
        string shortQuery = string.IsNullOrWhiteSpace(query) ? "your question" : query.Trim();

        string guidance;
        switch (phase)
        {
            case "muscular":
                guidance = "Muscles contract to pull on the bones they're attached to, and they burn through oxygen and glucose from the blood to keep doing it.";
                break;
            case "circulatory":
                guidance = "Your heart is a pump, not a factory, and faster pumping moves more oxygen toward the lungs and leg muscles.";
                break;
            case "respiratory":
                guidance = "The diaphragm pulls air in, and the alveoli move oxygen into the blood before it heads back to the heart.";
                break;
            case "digestive":
                guidance = "The small intestine moves food along and absorbs glucose into the blood so the muscles can keep climbing.";
                break;
            case "summit":
                guidance = "The body is slowing back down and returning to homeostasis after the climb.";
                break;
            case "homeostasis":
                guidance = "Heart rate and breathing are easing back toward their resting baseline as the body returns to balance.";
                break;
            case "trailhead":
            default:
                guidance = "The brain sends the go signal down the spinal cord, and the skeleton locks the joints so the first step is stable.";
                break;
        }

        if (hits != null && hits.Count > 0)
        {
            string preview = hits[0].Chunk.Text.Trim();
            if (preview.Length > 140)
                preview = preview.Substring(0, 140).TrimEnd() + "...";
            guidance += $" The knowledge base also points toward: {preview}";
        }

        return $"You asked about {shortQuery}. {guidance}";
    }

    private IEnumerator EmitLocalFallback(string response, Action<string> onSentenceReady, Action<string> onComplete)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            onComplete?.Invoke(string.Empty);
            yield break;
        }

        onSentenceReady?.Invoke(response);
        yield return null;
        onComplete?.Invoke(response);
    }

    private string BuildPayload(string system, string user)
    {
        var sb = new StringBuilder(2048);
        sb.Append("{");
        sb.Append($"\"model\":\"{openaiModel}\",");
        sb.Append($"\"temperature\":{temperature:F1},");
        sb.Append($"\"max_tokens\":{maxTokens},");
        sb.Append("\"stream\":true,");
        sb.Append("\"messages\":[");
        sb.Append($"{{\"role\":\"system\",\"content\":\"{EscapeJson(system)}\"}},");
        sb.Append($"{{\"role\":\"user\",\"content\":\"{EscapeJson(user)}\"}}");
        sb.Append("]}");
        return sb.ToString();
    }

    // ── Request ───────────────────────────────────────────────────────────────

    private IEnumerator RunStreamingRequest(
        string         payload,
        string         apiKey,
        Action<string> onSentenceReady,
        Action<string> onComplete,
        bool           requireAssessmentTag = false,
        Action<StudentUnderstanding, string> onAssessmentParsed = null)
    {
        var req = new UnityWebRequest(ENDPOINT, "POST");
        byte[] body = Encoding.UTF8.GetBytes(payload);
        req.uploadHandler   = new UploadHandlerRaw(body);
        req.downloadHandler = new StreamingDownloadHandler(
            onSentenceReady, onComplete, streamingSentences, minCharsBeforeEarlyFire,
            requireAssessmentTag, onAssessmentParsed);
        req.SetRequestHeader("Content-Type",  "application/json");
        req.SetRequestHeader("Authorization", "Bearer " + apiKey);
        req.timeout = timeoutSeconds;

        yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
        bool ok = req.result == UnityWebRequest.Result.Success;
#else
        bool ok = !req.isNetworkError && !req.isHttpError;
#endif
        if (!ok)
        {
            Debug.LogError($"[AIResponseGenerator] Request failed: {req.error}");
            Debug.LogError($"[AIResponseGenerator] Body: {req.downloadHandler.text}");
            onComplete?.Invoke($"[Error: {req.error}]");
        }

        req.Dispose();
    }

    // ── Key resolution ────────────────────────────────────────────────────────

    public string OpenAIKey => ResolveApiKey();

    private string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(openaiApiKey)) return openaiApiKey;
        string env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n",  "\\n")
                .Replace("\r",  "\\r")
                .Replace("\t",  "\\t");
    }

    // ── Streaming download handler ────────────────────────────────────────────

    private class StreamingDownloadHandler : DownloadHandlerScript
    {
        private readonly Action<string> _onSentenceReady;
        private readonly Action<string> _onComplete;
        private readonly bool           _streaming;
        private readonly int            _minChars;

        // ── Socratic assessment-tag interception ──────────────────────────
        // While _requireAssessmentTag is true and _assessmentResolved is
        // false, nothing is ever flushed to _onSentenceReady — we hold the
        // whole accumulator back until the [[ASSESS:...]] tag is complete
        // (or a safety cap is hit), so the tag can NEVER be spoken by TTS.
        private readonly bool           _requireAssessmentTag;
        private readonly Action<StudentUnderstanding, string> _onAssessmentParsed;
        private bool  _assessmentResolved;
        private const int AssessmentSafetyCapChars = 200; // generous — real tags are ~30 chars

        private readonly StringBuilder _accumulator = new StringBuilder(512);
        private readonly StringBuilder _full        = new StringBuilder(512);
        private readonly StringBuilder _lineBuffer  = new StringBuilder(256);

        public StreamingDownloadHandler(
            Action<string> onSentenceReady,
            Action<string> onComplete,
            bool           streaming,
            int            minChars,
            bool           requireAssessmentTag = false,
            Action<StudentUnderstanding, string> onAssessmentParsed = null)
        {
            _onSentenceReady      = onSentenceReady;
            _onComplete           = onComplete;
            _streaming            = streaming;
            _minChars             = minChars;
            _requireAssessmentTag = requireAssessmentTag;
            _onAssessmentParsed   = onAssessmentParsed;
            _assessmentResolved   = !requireAssessmentTag; // nothing to resolve if not required
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            string chunk = Encoding.UTF8.GetString(data, 0, dataLength);
            _lineBuffer.Append(chunk);

            string buffered = _lineBuffer.ToString();
            int newlineIdx;
            while ((newlineIdx = buffered.IndexOf('\n')) >= 0)
            {
                string line = buffered.Substring(0, newlineIdx).Trim();
                buffered    = buffered.Substring(newlineIdx + 1);
                ProcessLine(line);
            }
            _lineBuffer.Clear();
            _lineBuffer.Append(buffered);

            return true;
        }

        private void ProcessLine(string line)
        {
            if (!line.StartsWith("data: ")) return;
            string data = line.Substring(6).Trim();
            if (data == "[DONE]")
            {
                if (!_assessmentResolved)
                    ResolveAssessment(forceEvenIfIncomplete: true);

                string remaining = _accumulator.ToString().Trim();
                if (!string.IsNullOrEmpty(remaining))
                {
                    _onSentenceReady?.Invoke(remaining);
                    _accumulator.Clear();
                }
                string finalText = _requireAssessmentTag
                    ? StripAssessmentTagIfAny(_full.ToString())
                    : _full.ToString();
                _onComplete?.Invoke(finalText);
                return;
            }

            string token = ExtractDeltaContent(data);
            if (string.IsNullOrEmpty(token)) return;

            _accumulator.Append(token);
            _full.Append(token);

            if (!_assessmentResolved)
            {
                bool tagLooksComplete = _accumulator.ToString().Contains("]]");
                bool safetyCapHit     = _accumulator.Length >= AssessmentSafetyCapChars;

                if (tagLooksComplete || safetyCapHit)
                    ResolveAssessment(forceEvenIfIncomplete: safetyCapHit && !tagLooksComplete);

                if (!_assessmentResolved) return; // still waiting on more tokens for the tag
            }

            if (!_streaming) return;

            string acc      = _accumulator.ToString();
            int    boundary = FindSentenceBoundary(acc);

            if (boundary > 0 && acc.Length >= _minChars)
            {
                string sentence = acc.Substring(0, boundary).Trim();
                string rest     = acc.Substring(boundary).TrimStart();
                _accumulator.Clear();
                _accumulator.Append(rest);
                _onSentenceReady?.Invoke(sentence);
            }
        }

        /// <summary>
        /// Parses and strips the [[ASSESS:...]] tag out of _accumulator,
        /// fires _onAssessmentParsed exactly once, and leaves _accumulator
        /// holding only the spoken remainder. If forceEvenIfIncomplete is
        /// true (safety-cap path), ParseAssessmentTag's own "no tag found"
        /// fallback (Partial, logged warning) applies — the FSM still
        /// advances instead of hanging forever on a malformed response.
        /// </summary>
        private void ResolveAssessment(bool forceEvenIfIncomplete)
        {
            var (understanding, tag, cleaned) =
                SocraticDialogueController.ParseAssessmentTag(_accumulator.ToString());

            _accumulator.Clear();
            _accumulator.Append(cleaned);
            _assessmentResolved = true;
            _onAssessmentParsed?.Invoke(understanding, tag);
        }

        private static string StripAssessmentTagIfAny(string full)
        {
            var (_, _, cleaned) = SocraticDialogueController.ParseAssessmentTag(full);
            return cleaned;
        }

        private static int FindSentenceBoundary(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '.' || c == '!' || c == '?')
                    if (i + 1 >= text.Length || text[i + 1] == ' ')
                        return i + 1;
            }
            return -1;
        }

        private static string ExtractDeltaContent(string json)
        {
            const string key = "\"content\":\"";
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return null;

            int start = idx + key.Length;
            var sb    = new StringBuilder();
            for (int i = start; i < json.Length; i++)
            {
                if (json[i] == '\\' && i + 1 < json.Length)
                {
                    char next = json[i + 1];
                    switch (next)
                    {
                        case '"':  sb.Append('"');  i++; break;
                        case 'n':  sb.Append('\n'); i++; break;
                        case 'r':  sb.Append('\r'); i++; break;
                        case 't':  sb.Append('\t'); i++; break;
                        case '\\': sb.Append('\\'); i++; break;
                        default:   sb.Append(next); i++; break;
                    }
                }
                else if (json[i] == '"') break;
                else sb.Append(json[i]);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        protected override string GetText() => _full.ToString();
    }

    /// <summary>
    /// One-off, non-streaming, silent request that condenses this
    /// mini-game's whole Socratic dialogue into a 1-2 sentence note for
    /// SocraticMemoryStore's cross-system memory. Never spoken to the
    /// student — no TTS, no [[ASSESS]] tag, nothing streamed. Call once,
    /// right when a SocraticDialogueController resolves.
    /// </summary>
    public IEnumerator GenerateDialogueSummary(MiniGameDialogueRecord record, Action<string> onSummary)
    {
        if (record == null || record.Turns.Count == 0)
        {
            onSummary?.Invoke(BuildLocalFallbackSummary(record));
            yield break;
        }

        string apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            onSummary?.Invoke(BuildLocalFallbackSummary(record));
            yield break;
        }

        var transcript = new StringBuilder();
        transcript.AppendLine($"Body system: {record.System}");
        foreach (DialogueTurn turn in record.Turns)
            transcript.AppendLine($"[{turn.Phase}] Student said: \"{turn.StudentText}\" (assessed: {turn.Assessment})");

        const string summarySystemPrompt =
            "You write short internal notes for a learning-analytics system — never seen by the " +
            "student. In 1-2 sentences and no more than 40 words, third person, summarize what the " +
            "student initially thought, what the simulation showed them, and what they understood by " +
            "the end. No preamble, just the note itself.";

        string payload = BuildNonStreamingPayload(summarySystemPrompt, transcript.ToString());

        EnsureSSLInitialized();
        var req = new UnityWebRequest(ENDPOINT, "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type",  "application/json");
        req.SetRequestHeader("Authorization", "Bearer " + apiKey);
        req.timeout = timeoutSeconds;

        yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
        bool ok = req.result == UnityWebRequest.Result.Success;
#else
        bool ok = !req.isNetworkError && !req.isHttpError;
#endif

        string summary = ok ? ExtractMessageContent(req.downloadHandler.text) : null;
        if (string.IsNullOrWhiteSpace(summary))
        {
            Debug.LogWarning(
                $"[AIResponseGenerator] Dialogue summary request failed or returned nothing for {record.System} — " +
                $"using local fallback summary. ok={ok}, responseCode={req.responseCode}, " +
                $"error='{req.error}', body='{Truncate(req.downloadHandler?.text, 500)}'");
            summary = BuildLocalFallbackSummary(record);
        }

        onSummary?.Invoke(summary.Trim());
        req.Dispose();
    }

    private static string Truncate(string s, int maxLen) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...");

    private string BuildNonStreamingPayload(string system, string user)
    {
        var sb = new StringBuilder(1024);
        sb.Append("{");
        sb.Append($"\"model\":\"{openaiModel}\",");
        sb.Append("\"temperature\":0.2,");
        sb.Append("\"max_tokens\":80,");
        sb.Append("\"messages\":[");
        sb.Append($"{{\"role\":\"system\",\"content\":\"{EscapeJson(system)}\"}},");
        sb.Append($"{{\"role\":\"user\",\"content\":\"{EscapeJson(user)}\"}}");
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>Extracts choices[0].message.content from a non-streaming chat completion response.
    /// Tolerant of both compact ("content":"...") and pretty-printed ("content": "...") JSON —
    /// the summary endpoint was observed returning the latter, which a naive fixed-string search misses entirely.</summary>
    private static string ExtractMessageContent(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        const string key = "\"content\"";
        int idx = json.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return null;

        int i = idx + key.Length;
        while (i < json.Length && (json[i] == ':' || char.IsWhiteSpace(json[i]))) i++;
        if (i >= json.Length || json[i] != '"') return null;
        i++; // skip opening quote

        var sb = new StringBuilder();
        for (; i < json.Length; i++)
        {
            if (json[i] == '\\' && i + 1 < json.Length)
            {
                char next = json[i + 1];
                switch (next)
                {
                    case '"':  sb.Append('"');  i++; break;
                    case 'n':  sb.Append(' ');  i++; break;
                    case 'r':  i++; break;
                    case 't':  sb.Append(' ');  i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    default:   sb.Append(next); i++; break;
                }
            }
            else if (json[i] == '"') break;
            else sb.Append(json[i]);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static string BuildLocalFallbackSummary(MiniGameDialogueRecord record)
    {
        if (record == null || record.Turns.Count == 0)
            return "Completed this mini-game.";
        string lastStudentLine = record.Turns[record.Turns.Count - 1].StudentText;
        return $"On {record.System}, the student's final explanation was: \"{lastStudentLine}\"";
    }

    /// <summary>
    /// Sends a minimal request to GPT on scene load to warm up the HTTP connection.
    /// Response is discarded — exists only to eliminate cold-start latency.
    /// </summary>
    public IEnumerator WarmUp()
    {
        string apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) yield break;

        string payload =
            $"{{\"model\":\"{openaiModel}\"," +
            "\"max_tokens\":1," +
            "\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}}";

        var req = new UnityWebRequest(ENDPOINT, "POST");
        req.uploadHandler   = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(payload));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type",  "application/json");
        req.SetRequestHeader("Authorization", "Bearer " + apiKey);
        req.timeout = 10;

        yield return req.SendWebRequest();

        Debug.Log("[AIResponseGenerator] GPT warmup complete.");
        req.Dispose();
    }
}