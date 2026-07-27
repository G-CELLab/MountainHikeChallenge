using System.Collections.Generic;
using System.Linq;
using System.Text;

/// <summary>
/// Session-scoped memory of every mini-game's Socratic dialogue. Static and
/// lives for the lifetime of the play session — mirrors the existing
/// AnatomyTutorSession / MiniGameSequencer pattern (no disk persistence,
/// cleared via ResetAll() the same way GameManager.ResetProgress() clears
/// everything else). If cross-session (multi-day) memory is ever wanted,
/// this is the class to back with a save file — nothing else needs to
/// change to support that later.
///
/// Two jobs:
///
///   1. Per-system history: while the student is IN a mini-game, every AI
///      call includes everything THIS student has said so far in THIS
///      mini-game, not just the latest transcript — see BuildHistoryBlock().
///
///   2. Cross-system memory: once a mini-game resolves, a short summary of
///      what the student said and how their thinking moved is stored. Any
///      LATER mini-game's prompt includes prior summaries — see
///      BuildCrossSystemMemoryBlock() — so Carla can say things like
///      "remember how you described muscles squeezing together earlier?"
///      in a completely different scene.
/// </summary>
public static class SocraticMemoryStore
{
    private static readonly Dictionary<BodySystem, MiniGameDialogueRecord> _records
        = new Dictionary<BodySystem, MiniGameDialogueRecord>();

    // Preserves the order mini-games were resolved in, for stable, chronological prompt ordering.
    private static readonly List<BodySystem> _resolutionOrder = new List<BodySystem>();

    public static MiniGameDialogueRecord GetOrCreate(BodySystem system)
    {
        if (!_records.TryGetValue(system, out var record))
        {
            record = new MiniGameDialogueRecord(system);
            _records[system] = record;
        }
        return record;
    }

    public static MiniGameDialogueRecord Peek(BodySystem system) =>
        _records.TryGetValue(system, out var record) ? record : null;

    public static void RecordTurn(BodySystem system, DialogueTurn turn)
    {
        var record = GetOrCreate(system);
        record.Turns.Add(turn);

        if (turn.Assessment == StudentUnderstanding.Misconception &&
            !string.IsNullOrEmpty(turn.MisconceptionTag) &&
            !record.MisconceptionsSeen.Contains(turn.MisconceptionTag))
        {
            record.MisconceptionsSeen.Add(turn.MisconceptionTag);
        }
    }

    public static void MarkResolved(BodySystem system, bool forcedReveal, string crossSystemSummary)
    {
        var record = GetOrCreate(system);
        record.Resolved = true;
        record.ResolvedByForcedReveal = forcedReveal;
        record.CrossSystemSummary = crossSystemSummary;

        if (!_resolutionOrder.Contains(system))
            _resolutionOrder.Add(system);
    }

    /// <summary>
    /// Every student utterance from THIS mini-game's dialogue so far, in
    /// order, formatted for direct inclusion in the AI's user message. This
    /// is what makes the dialogue stateful instead of per-turn amnesia.
    /// </summary>
    public static string BuildHistoryBlock(BodySystem system)
    {
        var record = Peek(system);
        if (record == null || record.Turns.Count == 0)
            return "No prior exchanges yet in this mini-game — this is the first question.";

        var sb = new StringBuilder();
        sb.AppendLine("DIALOGUE SO FAR THIS MINI-GAME (most recent last):");
        foreach (var turn in record.Turns)
        {
            string misconceptionNote = string.IsNullOrEmpty(turn.MisconceptionTag)
                ? ""
                : $", misconception: {turn.MisconceptionTag}";
            sb.AppendLine($"- [{turn.Phase}] Student said: \"{turn.StudentText}\" (assessed: {turn.Assessment}{misconceptionNote})");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Summaries from every OTHER mini-game the student has already
    /// resolved this session. With only 7 systems total and short (1-2
    /// sentence) summaries, including everything completed so far is cheap
    /// and simple — deliberately not a second retrieval index over
    /// conversation history. If this ever grows unwieldy (e.g. summaries
    /// get long, or the KB expands well past 7 systems), filter by keyword
    /// overlap with the current system's KB terms before reaching for a
    /// real relevance-scoring step.
    /// </summary>
    public static string BuildCrossSystemMemoryBlock(BodySystem currentSystem)
    {
        var prior = _resolutionOrder
            .Where(s => s != currentSystem)
            .Select(s => _records[s])
            .Where(r => !string.IsNullOrWhiteSpace(r.CrossSystemSummary))
            .ToList();

        if (prior.Count == 0)
            return null; // nothing to add yet — caller should omit the block entirely

        var sb = new StringBuilder();
        sb.AppendLine("WHAT THIS STUDENT HAS ALREADY LEARNED THIS SESSION (from earlier mini-games):");
        foreach (var record in prior)
            sb.AppendLine($"- {record.System}: {record.CrossSystemSummary}");
        sb.AppendLine("If relevant, connect the current system back to one of these using the student's own words or ideas — don't force it if it doesn't fit.");
        return sb.ToString();
    }

    /// <summary>Call from GameManager.ResetProgress() so repeated playtests don't leak memory across runs.</summary>
    public static void ResetAll()
    {
        _records.Clear();
        _resolutionOrder.Clear();
        OnReset?.Invoke();
    }

    /// <summary>
    /// Fires whenever ResetAll() runs. AITutor subscribes to this to clear
    /// its own per-system SocraticDialogueController dictionary and
    /// "already announced" tracking — those live outside this store but
    /// need to reset in lockstep with it, or a replay after ResetProgress()
    /// would see stale resolved controllers and never re-trigger mini-games.
    /// </summary>
    public static event System.Action OnReset;
}