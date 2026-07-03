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
        "You are a friendly anatomy tutor inside the Mountain Hike Challenge VR experience for middle school students. " +
        "Use a warm, encouraging tone. Answer anatomy questions clearly and accurately. " +
        "Use the knowledge base excerpts for detailed information and scene guidance. " +
        "If asked what to do, guide the learner using the knowledge base for the current anatomy scene. " +
        "Keep replies to 1-2 sentences. Never invent instructions not in the knowledge base. " +
        "Always connect the current system to at least one other body system when appropriate.";

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Stream a response for userQuery given RAG chunks and scene state.
    /// onSentenceReady — called once per sentence as tokens arrive (for early TTS).
    /// onComplete      — called once with the full assembled response text.
    /// </summary>
    public IEnumerator GenerateStreamingResponse(
        string                    userQuery,
        List<RAGIndex.Hit>        ragHits,
        AnatomyTutorSceneSnapshot sceneState,
        Action<string>            onSentenceReady,
        Action<string>            onComplete)
    {
        var t0 = DateTime.Now;

        string systemPrompt = BuildSystemPrompt(ragHits);
        string userMessage  = BuildUserMessage(userQuery, sceneState);

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
            yield return EmitLocalFallback(localFallback, onSentenceReady, onComplete);
            yield break;
        }

        EnsureSSLInitialized();
        string payload = BuildPayload(systemPrompt, userMessage);
        yield return RunStreamingRequest(payload, apiKey, onSentenceReady, onComplete);

        if (logLatency)
            Debug.Log($"[AIResponseGenerator] Total time: {(DateTime.Now - t0).TotalMilliseconds:F0}ms");
    }

    // ── Prompt builders ───────────────────────────────────────────────────────

    /// <summary>
    /// System prompt = core template + RAG knowledge base excerpts.
    /// </summary>
    private static string BuildSystemPrompt(List<RAGIndex.Hit> hits)
    {
        var sb = new StringBuilder(SYSTEM_TEMPLATE);

        if (hits != null && hits.Count > 0)
        {
            sb.Append("\n\nKNOWLEDGE BASE:\n");
            for (int i = 0; i < hits.Count; i++)
                sb.Append($"[{i + 1}] {hits[i].Chunk.Text}\n\n");
            sb.Append("Prefer the knowledge base over generic answers.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// User message = scene context from AnatomyTutorSceneSnapshot + the learner's query.
    /// </summary>
    private static string BuildUserMessage(string query, AnatomyTutorSceneSnapshot state)
    {
        string context = state != null ? state.ToContextString() : "Scene context unavailable.";
        return $"{context}\n\nStudent: {query}";
    }

    private static string BuildLocalFallbackResponse(string query, AnatomyTutorSceneSnapshot state, List<RAGIndex.Hit> hits)
    {
        string phase = state != null ? state.Phase : "trailhead";
        string shortQuery = string.IsNullOrWhiteSpace(query) ? "your question" : query.Trim();

        string guidance;
        switch (phase)
        {
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
        Action<string> onComplete)
    {
        var req = new UnityWebRequest(ENDPOINT, "POST");
        byte[] body = Encoding.UTF8.GetBytes(payload);
        req.uploadHandler   = new UploadHandlerRaw(body);
        req.downloadHandler = new StreamingDownloadHandler(
            onSentenceReady, onComplete, streamingSentences, minCharsBeforeEarlyFire);
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

        private readonly StringBuilder _accumulator = new StringBuilder(512);
        private readonly StringBuilder _full        = new StringBuilder(512);
        private readonly StringBuilder _lineBuffer  = new StringBuilder(256);

        public StreamingDownloadHandler(
            Action<string> onSentenceReady,
            Action<string> onComplete,
            bool           streaming,
            int            minChars)
        {
            _onSentenceReady = onSentenceReady;
            _onComplete      = onComplete;
            _streaming       = streaming;
            _minChars        = minChars;
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
                string remaining = _accumulator.ToString().Trim();
                if (!string.IsNullOrEmpty(remaining))
                {
                    _onSentenceReady?.Invoke(remaining);
                    _accumulator.Clear();
                }
                _onComplete?.Invoke(_full.ToString());
                return;
            }

            string token = ExtractDeltaContent(data);
            if (string.IsNullOrEmpty(token)) return;

            _accumulator.Append(token);
            _full.Append(token);

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