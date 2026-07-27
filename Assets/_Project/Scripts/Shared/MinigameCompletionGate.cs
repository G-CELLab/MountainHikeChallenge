using System.Collections.Generic;

/// <summary>
/// Decouples "the Socratic dialogue resolved" from "the mini-game is
/// actually complete." By default those are the same thing — as soon as
/// AITutor's dialogue resolves, the mini-game completes, exactly as before.
///
/// A scene's physical mini-game controller can opt a BodySystem INTO a
/// stricter rule by calling RegisterInteractionGate() once (e.g. in
/// Awake()): once registered, that system won't complete until BOTH
/// MarkDialogueResolved() AND MarkInteractionComplete() have been called
/// for it — whichever happens second is what actually fires
/// MiniGameEvents.TriggerMiniGameComplete.
///
/// Example: NervousSystemMiniGameController registers BodySystem.Nervous
/// and calls MarkInteractionComplete() only once all 6 neurons have been
/// touched AND finished their discharge animation (see NeuronVisual.
/// OnDischarged) — so the scene won't move on just because the student
/// talked their way to a normative explanation without ever touching
/// anything, or vice versa.
///
/// Systems nobody registers (Skeletal, Muscular, etc. today) are
/// unaffected — MarkDialogueResolved() alone completes them, same as
/// before this existed.
/// </summary>
public static class MiniGameCompletionGate
{
    private static readonly HashSet<BodySystem> _gatedSystems    = new HashSet<BodySystem>();
    private static readonly HashSet<BodySystem> _dialogueDone    = new HashSet<BodySystem>();
    private static readonly HashSet<BodySystem> _interactionDone = new HashSet<BodySystem>();

    /// <summary>
    /// Opts a system into requiring a physical-interaction signal on top of
    /// dialogue resolution. Call once per scene load (Awake/OnEnable is
    /// fine — registering twice is harmless, HashSet.Add no-ops on a
    /// duplicate).
    /// </summary>
    public static void RegisterInteractionGate(BodySystem system) => _gatedSystems.Add(system);

    /// <summary>Call from AITutor once a SocraticDialogueController resolves.</summary>
    public static void MarkDialogueResolved(BodySystem system)
    {
        _dialogueDone.Add(system);
        TryComplete(system);
    }

    /// <summary>Call from a scene's physical mini-game controller once its own completion condition is met.</summary>
    public static void MarkInteractionComplete(BodySystem system)
    {
        _interactionDone.Add(system);
        TryComplete(system);
    }

    private static void TryComplete(BodySystem system)
    {
        if (!_dialogueDone.Contains(system)) return;
        if (_gatedSystems.Contains(system) && !_interactionDone.Contains(system)) return;

        MiniGameEvents.TriggerMiniGameComplete(system.ToString());
    }

    /// <summary>Call from GameManager.ResetProgress() so a reset-and-replay doesn't inherit stale state.</summary>
    public static void ResetAll()
    {
        _gatedSystems.Clear();
        _dialogueDone.Clear();
        _interactionDone.Clear();
    }
}