using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// Standalone TF-IDF knowledge base index.
/// Call Rebuild() once on startup, then BuildContextPrompt() per query.
/// </summary>
public class RAGIndex
{
    // ── Tunables ──────────────────────────────────────────────────────────
    public int ChunkSize    { get; set; } = 1500;
    public int ChunkOverlap { get; set; } = 80;
    public int ChunkCount   => _chunks.Count;

    // ── Internal types ────────────────────────────────────────────────────
    public sealed class Chunk
    {
        public string Source;
        public string Text;
    }

    public sealed class Hit
    {
        public Chunk  Chunk;
        public float  Score;
    }

    // ── State ─────────────────────────────────────────────────────────────
    private readonly List<Chunk>                       _chunks        = new List<Chunk>();
    private readonly Dictionary<string, int>           _docFreq       = new Dictionary<string, int>();
    private readonly List<Dictionary<string, float>>   _chunkTF       = new List<Dictionary<string, float>>();

    private static readonly Regex WordRx =
        new Regex(@"[a-zA-Z0-9_]+", RegexOptions.Compiled);

    public void Rebuild(string knowledgeBaseFolder)
    {
        _chunks.Clear();
        _docFreq.Clear();
        _chunkTF.Clear();

        if (string.IsNullOrWhiteSpace(knowledgeBaseFolder) ||
            !Directory.Exists(knowledgeBaseFolder))
        {
            Debug.LogWarning($"[RAGIndex] Folder not found: '{knowledgeBaseFolder}'");
            return;
        }

        int overlap = Mathf.Max(0, Mathf.Min(ChunkOverlap, ChunkSize / 2));
        var files   = Directory.GetFiles(knowledgeBaseFolder, "*.md", SearchOption.AllDirectories)
                                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                .ToArray();

        foreach (string path in files)
        {
            string raw = File.ReadAllText(path);
            foreach (string piece in ChunkText(raw, ChunkSize, overlap))
            {
                _chunks.Add(new Chunk { Source = Path.GetFileName(path), Text = piece });
            }
        }

        foreach (Chunk chunk in _chunks)
        {
            var tf = ComputeTF(chunk.Text);
            _chunkTF.Add(tf);
            foreach (string term in tf.Keys)
                _docFreq[term] = _docFreq.TryGetValue(term, out int v) ? v + 1 : 1;
        }

        Debug.Log($"[RAGIndex] Built index: {_chunks.Count} chunks from {files.Length} files in '{knowledgeBaseFolder}'");
    }

    /// <summary>Retrieve the top-K most relevant chunks for a query.</summary>
    public List<Hit> Retrieve(string query, int topK = 3)
    {
        if (_chunks.Count == 0) return new List<Hit>();

        var qTF  = ComputeTF(query);
        if (qTF.Count == 0) return new List<Hit>();

        var qVec  = TFIDF(qTF);
        double qN = Norm(qVec);
        if (qN <= 0d) return new List<Hit>();

        var results = new List<Hit>(_chunks.Count);
        for (int i = 0; i < _chunkTF.Count; i++)
        {
            var cVec  = TFIDF(_chunkTF[i]);
            double cN = Norm(cVec);
            if (cN <= 0d) continue;
            double sim = Dot(qVec, cVec) / (qN * cN);
            if (sim > 0d)
                results.Add(new Hit { Chunk = _chunks[i], Score = (float)sim });
        }

        return results.OrderByDescending(h => h.Score)
                        .ThenBy(h => h.Chunk.Source, StringComparer.OrdinalIgnoreCase)
                        .Take(Mathf.Max(1, topK))
                        .ToList();
    }

    /// <summary>
    /// Build a formatted context string ready to inject into a system prompt.
    /// Returns the context and outputs the best score/source for logging.
    /// </summary>
    public string BuildContextPrompt(
        string query,
        string phaseName,
        int    topK,
        int    maxChars,
        out float  bestScore,
        out string bestSource)
    {
        var hits = Retrieve(query, topK);
        bestScore  = hits.Count > 0 ? hits[0].Score : 0f;
        bestSource = hits.Count > 0 ? hits[0].Chunk.Source : string.Empty;

        var lines = new List<string>
        {
            "KNOWLEDGE BASE CONTEXT",
            $"Current phase: {phaseName}",
            $"Query: {query}",
            "Retrieved excerpts:"
        };

        if (hits.Count == 0)
        {
            lines.Add("(no relevant knowledge base entries found)");
        }
        else
        {
            foreach (var hit in hits)
            {
                string preview = hit.Chunk.Text.Trim();
                if (preview.Length > 900)
                    preview = preview.Substring(0, 900).TrimEnd() + "...";
                lines.Add($"[{hit.Chunk.Source} | score={hit.Score:0.000}] {preview}");
            }
        }

        lines.Add(string.Empty);
        lines.Add("Rules:");
        lines.Add("- Prefer the knowledge base; do not invent scene mechanics.");
        lines.Add("- Keep replies concise and conversational (1-2 sentences).");
        lines.Add("- If the answer is not in the knowledge base, say so briefly.");
        lines.Add("- If the learner asks about another phase, answer that phase directly.");

        string full = string.Join("\n", lines);
        return full.Length <= maxChars ? full : full.Substring(0, maxChars).TrimEnd() + "...";
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static IEnumerable<string> ChunkText(string text, int size, int overlap)
    {
        string norm = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        if (string.IsNullOrEmpty(norm)) yield break;

        int step  = Mathf.Max(1, size - overlap);
        int start = 0;
        while (start < norm.Length)
        {
            int end   = Mathf.Min(norm.Length, start + size);
            string piece = norm.Substring(start, end - start).Trim();
            if (!string.IsNullOrEmpty(piece)) yield return piece;
            if (end >= norm.Length) yield break;
            start += step;
        }
    }

    private static Dictionary<string, float> ComputeTF(string text)
    {
        var tokens = WordRx.Matches(text ?? string.Empty)
                            .Cast<System.Text.RegularExpressions.Match>()
                            .Select(m => m.Value.ToLowerInvariant())
                            .ToList();

        var tf = new Dictionary<string, float>();
        if (tokens.Count == 0) return tf;

        foreach (string t in tokens)
            tf[t] = tf.TryGetValue(t, out float v) ? v + 1f : 1f;

        float total = tokens.Count;
        foreach (string key in tf.Keys.ToList())
            tf[key] /= total;

        return tf;
    }

    private Dictionary<string, float> TFIDF(Dictionary<string, float> tf)
    {
        int n   = Mathf.Max(1, _chunks.Count);
        var out_ = new Dictionary<string, float>(tf.Count);
        foreach (var kv in tf)
        {
            int df   = _docFreq.TryGetValue(kv.Key, out int d) ? d : 0;
            double idf = Math.Log((1.0 + n) / (1.0 + df)) + 1.0;
            out_[kv.Key] = (float)(kv.Value * idf);
        }
        return out_;
    }

    private static double Dot(Dictionary<string, float> a, Dictionary<string, float> b)
    {
        if (a.Count > b.Count) { var tmp = a; a = b; b = tmp; }
        double sum = 0d;
        foreach (var kv in a)
            if (b.TryGetValue(kv.Key, out float other))
                sum += kv.Value * other;
        return sum;
    }

    private static double Norm(Dictionary<string, float> v)
    {
        double sum = 0d;
        foreach (float val in v.Values) sum += val * val;
        return Math.Sqrt(sum);
    }
}
