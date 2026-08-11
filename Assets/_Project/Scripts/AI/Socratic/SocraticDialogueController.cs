using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// Drives one mini-game's Socratic dialogue: Articulate initial → Evaluate
/// (loops) → Articulate updated → Resolved, per the professor's spec. One
/// instance lives per active BodySystem mini-game — created on demand by
/// AITutor when the student enters a mini-game scene.
///
/// Why a finite state machine + labeled classification instead of a
/// Bayesian trace model: a real Bayesian update needs a calibrated
/// hypothesis space and a likelihood function, which would mean guessing
/// at numbers we have no data to justify — and it would make the system
/// harder to audit, which matters a lot given this whole feature exists to
/// be studied. This uses a plain discrete label instead: the AI tags its
/// own read of the student's LAST utterance as one of five categories, and
/// an explicit transition table below decides what happens next. Every
/// transition is inspectable in CombinedLogger's output.
///
/// IMPORTANT — how "articulate updated thinking" actually gets asked:
/// ArticulateUpdated never asks its own opening question. Evaluate's own
/// exit reply — on EITHER path out (recognizing normative early, or
/// hitting the round cap) — is what explicitly asks the student to state
/// their whole updated understanding. ArticulateUpdated's job is purely to
/// assess the answer to that question and respond: affirm + end, reveal +
/// end, or push back + loop to Evaluate. This split exists because a
/// resolving reply (affirm or reveal) is followed immediately by the scene
/// transitioning — if that same reply also asked a new question, nobody
/// would ever be there to answer it. Earlier versions of this file had
/// ArticulateUpdated ask its own question unconditionally, which is
/// exactly why that bug happened: the model would sometimes end a
/// resolving reply with a trailing question that the scene change made
/// unanswerable.
///
/// BuildPhaseInstructions() is called ONCE per turn, before the model
/// generates its reply, using whatever Phase already is — the FSM only
/// advances phases from INSIDE that same reply's assessment tag (see
/// AdvanceAfterAssessment), so the reply that triggers a phase change was
/// generated under the *previous* phase's instructions, not the new one.
/// </summary>
public class SocraticDialogueController
{
    // ── Tunables — adjust freely for playtesting, transition logic doesn't change ──
    public int MaxEvaluateRounds = 4;             // "articulate thinking" cap per pass
    public int MaxArticulateUpdatedAttempts = 3;  // "articulate updated thinking" cap
    public int MaxInitialElaborationPrompts = 2;  // follow-ups if the first answer is superficial

    public BodySystem System { get; }
    public SocraticPhase Phase { get; private set; } = SocraticPhase.ArticulateInitial;
    public int EvaluateRound { get; private set; } = 0;
    public int ArticulateUpdatedAttempt { get; private set; } = 0;
    public int InitialElaborationCount { get; private set; } = 0;
    public bool IsResolved => Phase == SocraticPhase.Resolved;

    // Set by AdvanceAfterAssessment when resolution happens on THIS turn, so
    // AITutor knows to fire MiniGameEvents.TriggerMiniGameComplete once the
    // reply finishes speaking.
    public bool JustResolvedThisTurn { get; private set; }
    public bool LastResolutionWasForcedReveal { get; private set; }

    private static readonly Regex AssessTagRegex = new Regex(
        @"^\s*\[\[ASSESS:(?<label>offtopic|superficial|partial|misconception|normative)(?:=(?<tag>[a-zA-Z0-9_]+))?\]\]\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public SocraticDialogueController(BodySystem system)
    {
        System = system;
    }

    /// <summary>
    /// Strips a leading [[ASSESS:...]] control tag from a raw model
    /// response and returns the parsed understanding + optional
    /// misconception tag alongside the cleaned spoken text. Falls back to
    /// Partial (logged as a warning) if the model didn't include a tag —
    /// this keeps a malformed response from silently breaking the FSM.
    /// </summary>
    public static (StudentUnderstanding understanding, string misconceptionTag, string cleanedText)
        ParseAssessmentTag(string rawText)
    {
        if (string.IsNullOrEmpty(rawText))
            return (StudentUnderstanding.Partial, null, rawText);

        Match match = AssessTagRegex.Match(rawText);
        if (!match.Success)
        {
            Debug.LogWarning("[SocraticDialogueController] No [[ASSESS:...]] tag found in model output — " +
                              "defaulting to Partial. Check the phase system prompt still requires the tag.");
            return (StudentUnderstanding.Partial, null, rawText);
        }

        string label = match.Groups["label"].Value.ToLowerInvariant();
        string tag = match.Groups["tag"].Success ? match.Groups["tag"].Value : null;
        string cleaned = AssessTagRegex.Replace(rawText, "", 1);

        StudentUnderstanding understanding;
        switch (label)
        {
            case "offtopic":       understanding = StudentUnderstanding.OffTopic;      break;
            case "superficial":    understanding = StudentUnderstanding.Superficial;   break;
            case "misconception":  understanding = StudentUnderstanding.Misconception; break;
            case "normative":      understanding = StudentUnderstanding.Normative;      break;
            default:               understanding = StudentUnderstanding.Partial;       break;
        }

        return (understanding, tag, cleaned);
    }

    /// <summary>
    /// Advances the FSM given the classification of the student's last
    /// answer. Call once per turn, right after ParseAssessmentTag and
    /// before the reply is spoken. See the class doc's diagram for the
    /// shape of this — this method IS that diagram.
    ///
    /// OffTopic is a hard short-circuit: things like "can you hear me?" or
    /// "what am I supposed to do?" are not an attempt to explain anything,
    /// so nothing about the dialogue's progress should change — the FSM
    /// stays exactly where it was and the same question effectively gets
    /// asked again (see BuildPhaseInstructions).
    /// </summary>
    public SocraticPhase AdvanceAfterAssessment(StudentUnderstanding understanding)
    {
        JustResolvedThisTurn = false;
        LastResolutionWasForcedReveal = false;

        if (understanding == StudentUnderstanding.OffTopic)
            return Phase; // no state change at all

        switch (Phase)
        {
            case SocraticPhase.ArticulateInitial:
                if (understanding == StudentUnderstanding.Superficial &&
                    InitialElaborationCount < MaxInitialElaborationPrompts)
                {
                    InitialElaborationCount++;
                    // stay in ArticulateInitial — ask them to unpack it further
                }
                else
                {
                    Phase = SocraticPhase.Evaluate;
                    EvaluateRound = 1;
                }
                break;

            case SocraticPhase.Evaluate:
                if (understanding == StudentUnderstanding.Normative || EvaluateRound >= MaxEvaluateRounds)
                {
                    // Either they've reconciled mid-discussion, or we've hit the
                    // round cap — either way, this moves to the required
                    // "articulate updated thinking" re-ask (step 3 of the
                    // professor's spec). Evaluate NEVER resolves the mini-game
                    // directly — only ArticulateUpdated's final attempt does
                    // (see BuildPhaseInstructions), so the student is always
                    // asked to restate their thinking at least once before
                    // any reveal happens.
                    Phase = SocraticPhase.ArticulateUpdated;
                    ArticulateUpdatedAttempt = 1;
                }
                else
                {
                    EvaluateRound++;
                }
                break;

            case SocraticPhase.ArticulateUpdated:
                if (understanding == StudentUnderstanding.Normative)
                {
                    // The affirm-and-reinforce instructions for this case
                    // were already folded into THIS attempt's prompt.
                    Phase = SocraticPhase.Resolved;
                    JustResolvedThisTurn = true;
                    LastResolutionWasForcedReveal = false;
                }
                else if (ArticulateUpdatedAttempt >= MaxArticulateUpdatedAttempts)
                {
                    // Same story — the graceful-reveal instructions were
                    // already in THIS attempt's prompt, since this was
                    // known to be the last one before we asked.
                    Phase = SocraticPhase.Resolved;
                    JustResolvedThisTurn = true;
                    LastResolutionWasForcedReveal = true;
                }
                else
                {
                    ArticulateUpdatedAttempt++;
                    Phase = SocraticPhase.Evaluate;
                    EvaluateRound = 1; // fresh probing pass focused on the updated idea
                }
                break;

            case SocraticPhase.Resolved:
                // No-op — mini-game is done. AITutor should stop routing turns
                // through this controller once IsResolved is true.
                break;
        }

        return Phase;
    }

    /// <summary>
    /// Phase-specific instructions appended to the system prompt, on top of
    /// the shared Carla persona + KB excerpts. This is what actually
    /// implements the professor's 3-step flow — AdvanceAfterAssessment is
    /// just the bookkeeping around it.
    ///
    /// Philosophy: this is a responsive-teacher conversation, not a
    /// withhold-until-the-end quiz. Carla can affirm, label things, and
    /// share small pieces of the mechanism as she goes — the constraint is
    /// pacing (build the explanation together across turns, don't dump the
    /// whole answer in one reply) and always keeping the student engaged
    /// with a next question or a concrete thing to try, not silence about
    /// content. Only ArticulateUpdated's two resolving branches (normative,
    /// or not-normative-on-the-final-attempt) end without a question, since
    /// the scene transitions right after and nothing would answer it.
    /// </summary>
    public string BuildPhaseInstructions()
    {
        const string keepGoing =
            " Keep the reply focused on one next step. HARD RULE: never combine a command/instruction " +
            "(telling the student to physically do something in the simulation right now) with a question in " +
            "the same reply — pick exactly one. Example of what NOT to do: \"Contract the muscle three times " +
            "and let me know what you notice\" (that's a command AND a question at once). Instead, either give " +
            "the instruction on its own with no trailing question, or ask a question on its own with no new " +
            "instruction attached. The one exception: you may ask about something they already did in the past " +
            "(\"What did you notice when you did that?\") since that's a question about a completed action, not " +
            "paired with a new command. Do not ask more than one question in a reply either way. Build the " +
            "explanation together across a few turns without piling on multiple prompts.";

        switch (Phase)
        {
            case SocraticPhase.ArticulateInitial:
                return
                    "DIALOGUE MODE: Articulate initial thinking. The student was just asked what they see. If " +
                    "their last message is just naming what they see (objects, colors, shapes), warmly affirm " +
                    "it and briefly label anything worth naming for them (e.g. what an object in the scene is " +
                    "actually called) — then ask how they think those specific things work together to help the " +
                    "hiker climb; that's the real question. If they've already made an attempt at the mechanism, " +
                    "respond to it directly: affirm what's on the right track, and if it's vague, only partly " +
                    "there, or hesitant (\"maybe...\", \"I'm not sure, but...\"), it's fine to share a small " +
                    "piece of the real mechanism to help them build on it, the way a teacher would in " +
                    "conversation — then ask a related follow-up that keeps them building the rest of the " +
                    "explanation themselves." + keepGoing;

            case SocraticPhase.Evaluate:
                return
                    $"DIALOGUE MODE: Evaluate thinking (round {EvaluateRound} of {MaxEvaluateRounds} — a ceiling, " +
                    "not a target; move on the moment their explanation is normative, however early that " +
                    "happens). Respond to their last answer directly: affirm what's correct, and if something " +
                    "is off, incomplete, or they seem unsure/stuck, it's fine to gently steer them with a small " +
                    "hint or a piece of the mechanism — don't just withhold and re-ask. Check the DIALOGUE SO " +
                    "FAR history above so you don't repeat a question you already asked or ask them to re-explain " +
                    "something they already told you — build on it instead. TRUST SELF-REPORTS: if, anywhere " +
                    "earlier in this conversation, the student has already said they performed the scene's " +
                    "hands-on action (e.g. \"I did it three times\", \"I already contracted it\", \"I touched " +
                    "them all\"), take them at their word — do not ask them to repeat that action again this " +
                    "mini-game, even if you can't personally verify it. At some point this phase (if the " +
                    "student hasn't already told you they've done the hands-on action from the scene's Current " +
                    "objective), encourage them toward it — as its own reply, an instruction with no question " +
                    "attached (see the hard rule below). Once they've done it (or told you they have), a LATER " +
                    "reply can separately ask what they noticed — that's a question about a completed action, " +
                    "not a new command, so it's fine on its own. If their explanation matches a known " +
                    "misconception from the knowledge base, it's fine to name the discrepancy plainly rather " +
                    "than dancing around it." + keepGoing +
                    " Once you judge their explanation normative (see the normative guidance above), affirm that " +
                    "specifically and ask them to state their whole explanation once more so it's on the record " +
                    "— that's the bridge into the next phase.";

            case SocraticPhase.ArticulateUpdated:
                bool isFinalAttempt = ArticulateUpdatedAttempt >= MaxArticulateUpdatedAttempts;
                string notNormativeBranch = isFinalAttempt
                    ? "If it's still NOT normative: this is their last attempt (see the cap above) — warmly " +
                      "acknowledge what was productive in what they've said throughout this conversation, then, " +
                      "connecting to those same ideas, explain how scientists describe this system. This ends " +
                      "the mini-game, so don't ask a new question after this."
                    : "If it's still NOT normative: affirm anything that's on the right track, share a hint if " +
                      "they seem stuck, and ask them to reconsider — this does not end the mini-game, so ending " +
                      "with a real question here is correct.";

                return
                    $"DIALOGUE MODE: Articulate updated thinking (attempt {ArticulateUpdatedAttempt} of " +
                    $"{MaxArticulateUpdatedAttempts} — a ceiling, not a target; resolve the moment their answer " +
                    "is normative, however early that happens). The student was just asked to state their whole " +
                    "updated understanding — their last message is that restatement. If it IS normative (see " +
                    "the normative guidance above): affirm it specifically, connecting to their own words from " +
                    "earlier in the conversation, and briefly reinforce the correct framing. This ends the " +
                    "mini-game — the scene moves on right after you finish speaking, so this reply should be a " +
                    "warm wrap-up statement, not end with a new question. " + notNormativeBranch;

            case SocraticPhase.Resolved:
                // Not reached in the current wiring — AITutor stops routing
                // turns through this controller once IsResolved is true, and
                // the reveal/affirm text is generated a phase earlier (see
                // the branches above). Kept only as a safe default.
                return string.Empty;

            default:
                return string.Empty;
        }
    }

    public const string ASSESSMENT_TAG_INSTRUCTION =
        "Before your spoken reply, output exactly one control tag on its own, in exactly this format and " +
        "nothing else around it: [[ASSESS:offtopic]], [[ASSESS:superficial]], [[ASSESS:partial]], " +
        "[[ASSESS:misconception=<short_tag>]], or [[ASSESS:normative]] — classifying the STUDENT'S LAST " +
        "MESSAGE, not your own reply. This tag is stripped before the student hears anything, so never " +
        "mention it to them.\n\n" +
        "offtopic = not actually an attempt to engage with the question (small talk, \"can you hear me\", " +
        "silence). Also use offtopic for \"I don't know\" / \"I'm not sure\" / stuck responses, but treat " +
        "those warmly in your spoken reply — reassure them and offer a smaller, easier version of the question " +
        "or a concrete nudge toward the simulation, not a flat repeat of what you just asked.\n" +
        "superficial = a real attempt, but too vague to evaluate (or, in Articulate Initial, just an " +
        "observation of what they see rather than a mechanism attempt).\n" +
        "partial = on the right track but not complete or fully accurate yet.\n" +
        "misconception=<tag> = matches a known non-normative idea from the knowledge base (short snake_case " +
        "label, e.g. misconception=heart_makes_blood).\n" +
        "normative = compare their explanation to the \"Short answer\" line for this scene in the knowledge " +
        "base — if they've captured that same core idea in their own words, even informally, imprecisely, or " +
        "spread across a couple of turns rather than one tidy sentence, tag it normative. This is a 7th grader " +
        "talking out loud, not a textbook — don't withhold normative just because they could theoretically say " +
        "more or use better vocabulary. The round/attempt caps mentioned in the dialogue mode notes are a safety " +
        "net for students still working through it, not a quota — a correct answer on round 1 is normative on " +
        "round 1. This tag is a private classification separate from what you say out loud — keep following the " +
        "current DIALOGUE MODE instructions for your spoken reply regardless of which tag you use.";
}