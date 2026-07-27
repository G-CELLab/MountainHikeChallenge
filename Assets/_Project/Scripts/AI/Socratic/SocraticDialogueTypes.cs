using System;
using System.Collections.Generic;

/// <summary>
/// Phase of the Socratic mini-game dialogue, per body system, per the
/// professor's spec:
///   1. ArticulateInitial  — "how do you think this system works?"
///   2. Evaluate           — test the claim against the sim/data, loops
///   3. ArticulateUpdated  — "now how do you think it works?"
///   4. Resolved           — win (normative) or graceful reveal (capped out)
/// See SocraticDialogueController for the full transition table.
/// </summary>
public enum SocraticPhase
{
    ArticulateInitial,
    Evaluate,
    ArticulateUpdated,
    Resolved
}

/// <summary>
/// How the AI's structured self-assessment tag classified the student's
/// most recent explanation. This is a label, not a probability — see
/// SocraticDialogueController's class doc for why a discrete FSM was
/// chosen over a Bayesian belief-state model.
/// </summary>
public enum StudentUnderstanding
{
    OffTopic,      // not actually an attempt to explain — confusion, small talk, "what do I do", silence
    Superficial,   // too vague/short to evaluate — ask them to unpack it
    Misconception, // maps to a known non-normative idea
    Partial,       // some correct elements, not yet complete/normative
    Normative      // scientifically accurate and complete
}

/// <summary>One turn of a Socratic dialogue, kept verbatim for history + logging.</summary>
[Serializable]
public sealed class DialogueTurn
{
    public SocraticPhase Phase;
    public string StudentText;
    public string TutorText;
    public StudentUnderstanding Assessment;
    public string MisconceptionTag; // null if none identified
    public long TimestampUnix;

    public DialogueTurn(
        SocraticPhase phase,
        string studentText,
        string tutorText,
        StudentUnderstanding assessment,
        string misconceptionTag)
    {
        Phase = phase;
        StudentText = studentText ?? string.Empty;
        TutorText = tutorText ?? string.Empty;
        Assessment = assessment;
        MisconceptionTag = misconceptionTag;
        TimestampUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}

/// <summary>
/// Full dialogue record for one body system's mini-game, kept for the
/// lifetime of the play session. Two purposes:
///   - while the mini-game is active, Turns is the "memory" the AI always
///     pulls from (see SocraticMemoryStore.BuildHistoryBlock)
///   - once resolved, CrossSystemSummary is what OTHER mini-games can pull
///     from later in the session (see BuildCrossSystemMemoryBlock)
/// </summary>
[Serializable]
public sealed class MiniGameDialogueRecord
{
    public BodySystem System;
    public List<DialogueTurn> Turns = new List<DialogueTurn>();
    // List, not HashSet — Unity's serializer doesn't support HashSet<T> (UAC1009).
    // Dedup is handled manually in SocraticMemoryStore.RecordTurn instead.
    public List<string> MisconceptionsSeen = new List<string>();
    public bool Resolved;
    public bool ResolvedByForcedReveal; // true if capped out rather than reaching normative
    public string CrossSystemSummary;   // filled in once, on resolution

    public MiniGameDialogueRecord(BodySystem system) { System = system; }
}

/// <summary>
/// Read-only snapshot of "what the active Socratic dialogue is doing right
/// now," updated by AITutor once per assessed turn. Exists purely so
/// CombinedLogger (and anything else studying the interaction) can read
/// dialogue-phase state the same way it already reads AnatomyTutorSession —
/// no direct dependency on AITutor's private controller dictionary needed.
/// </summary>
public static class SocraticDialogueTelemetry
{
    public static BodySystem?     CurrentSystem { get; private set; }
    public static SocraticPhase   CurrentPhase { get; private set; }
    public static int             CurrentEvaluateRound { get; private set; }
    public static int             CurrentArticulateUpdatedAttempt { get; private set; }
    public static StudentUnderstanding LastAssessment { get; private set; }
    public static string          LastMisconceptionTag { get; private set; } = string.Empty;

    public static void Update(
        BodySystem system,
        SocraticDialogueController controller,
        StudentUnderstanding lastAssessment,
        string misconceptionTag)
    {
        CurrentSystem                   = system;
        CurrentPhase                    = controller.Phase;
        CurrentEvaluateRound             = controller.EvaluateRound;
        CurrentArticulateUpdatedAttempt = controller.ArticulateUpdatedAttempt;
        LastAssessment                   = lastAssessment;
        LastMisconceptionTag             = misconceptionTag ?? string.Empty;
    }

    /// <summary>Call from GameManager.ResetProgress() alongside SocraticMemoryStore.ResetAll().</summary>
    public static void Clear()
    {
        CurrentSystem = null;
        CurrentPhase = SocraticPhase.ArticulateInitial;
        CurrentEvaluateRound = 0;
        CurrentArticulateUpdatedAttempt = 0;
        LastMisconceptionTag = string.Empty;
    }
}