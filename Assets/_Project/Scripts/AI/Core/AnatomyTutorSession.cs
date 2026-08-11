using System;

/// <summary>
/// Shared anatomy tutor scene state and lightweight runtime memory.
/// This is the single source of truth for scene/phase context — both the
/// narrator scripts and the AI response pipeline read from
/// AnatomyTutorSession.Current directly. There is no separate "refresh"
/// step needed since this class is always live.
///
/// Declared in full narrative order — GameManager.SceneOrder derives its
/// order directly from this enum's declaration, so this IS the canonical
/// flow, not just documentation of it:
///
///   Trailhead → Nervous → Skeletal → SteepIncline → Muscular → Circulatory
///   → ThinAir → Respiratory → EnergyCrash → Digestive → Summit
///   → Homeostasis → Completion
///
/// Trailhead, SteepIncline, ThinAir, EnergyCrash, Summit, and Completion are
/// narrative/transition beats that all live in the SAME Unity scene
/// (_MountainTrail) at different trail checkpoints — see
/// SceneNarrationController's flow list, keyed by (scene name, checkpoint).
/// Nervous, Skeletal, Muscular, Circulatory, Respiratory, Digestive, and
/// Homeostasis each get their own dedicated Unity scene.
/// </summary>
public enum AnatomySceneId
{
    Trailhead,
    Nervous,
    Skeletal,
    SteepIncline,
    Muscular,
    Circulatory,
    ThinAir,
    Respiratory,
    EnergyCrash,
    Digestive,
    Summit,
    Homeostasis,
    Completion
}

[Serializable]
public sealed class AnatomyTutorSceneSnapshot
{
    public AnatomySceneId SceneId { get; }
    public string Phase { get; }
    public string SceneSummary { get; }
    public string CurrentObjective { get; }
    public string VisibleObjects { get; }
    public string ProgressSummary { get; }

    public AnatomyTutorSceneSnapshot(
        AnatomySceneId sceneId,
        string phase,
        string sceneSummary,
        string currentObjective,
        string visibleObjects,
        string progressSummary)
    {
        SceneId = sceneId;
        Phase = phase ?? string.Empty;
        SceneSummary = sceneSummary ?? string.Empty;
        CurrentObjective = currentObjective ?? string.Empty;
        VisibleObjects = visibleObjects ?? string.Empty;
        ProgressSummary = progressSummary ?? string.Empty;
    }

    /// <summary>
    /// Formats all scene fields into a single context block
    /// ready to inject into the AI user message.
    /// </summary>
    public string ToContextString()
    {
        return
            $"Scene id: {SceneId}\n" +
            $"Phase: {Phase}\n" +
            $"Scene: {SceneSummary}\n" +
            $"Current objective: {CurrentObjective}\n" +
            $"Visible objects: {VisibleObjects}\n" +
            $"Progress: {ProgressSummary}";
    }

    public override string ToString() =>
        $"Snapshot(scene={SceneId}, phase={Phase}, progress={ProgressSummary})";
}

public static class AnatomyTutorSession
{
    private static AnatomyTutorSceneSnapshot _current = BuildSnapshot(AnatomySceneId.Trailhead, string.Empty);

    public static string LastUserSpeech { get; private set; } = string.Empty;
    public static string LastTutorSpeech { get; private set; } = string.Empty;
    public static string LastTutorGesture { get; private set; } = string.Empty;
    public static string LastNote { get; private set; } = string.Empty;

    public static event Action OnChanged;

    public static AnatomyTutorSceneSnapshot Current => _current;

    public static void ResetToDefault()
    {
        SetScene(AnatomySceneId.Trailhead);
    }

    public static void SetScene(AnatomySceneId sceneId, string progressSummary = null)
    {
        _current = BuildSnapshot(sceneId, progressSummary ?? _current.ProgressSummary);
        OnChanged?.Invoke();
    }

    public static void SetCustomContext(
        AnatomySceneId sceneId,
        string phase,
        string sceneSummary,
        string currentObjective,
        string visibleObjects,
        string progressSummary)
    {
        _current = new AnatomyTutorSceneSnapshot(
            sceneId,
            phase,
            sceneSummary,
            currentObjective,
            visibleObjects,
            progressSummary);

        OnChanged?.Invoke();
    }

    public static void SetProgressSummary(string progressSummary)
    {
        _current = new AnatomyTutorSceneSnapshot(
            _current.SceneId,
            _current.Phase,
            _current.SceneSummary,
            _current.CurrentObjective,
            _current.VisibleObjects,
            progressSummary);

        OnChanged?.Invoke();
    }

    public static void RecordUserSpeech(string text)
    {
        LastUserSpeech = text ?? string.Empty;
    }

    public static void RecordTutorSpeech(string text)
    {
        LastTutorSpeech = text ?? string.Empty;
    }

    public static void RecordTutorGesture(string gestureName)
    {
        LastTutorGesture = gestureName ?? string.Empty;
    }

    public static void RecordNote(string note)
    {
        LastNote = note ?? string.Empty;
    }

    private static AnatomyTutorSceneSnapshot BuildSnapshot(AnatomySceneId sceneId, string progressSummary)
    {
        switch (sceneId)
        {
            case AnatomySceneId.Trailhead:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "trailhead",
                    "The student is at the base of Mount Timpanogos. The trail is steep and the guide is about to show how the body starts the climb. This is narrative only — the nervous and skeletal systems each get their own dedicated scene right after this one, where the actual interactions live.",
                    "Just watch and listen for now — the Nervous and Skeletal scenes right after this one are where the student will actually interact.",
                    "The base of the mountain trail. No hands-on interaction happens in this scene.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Trailhead ready. The climb has just begun."
                        : progressSummary);

            case AnatomySceneId.Nervous:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "nervous",
                    "The student is facing a full nervous system — a brain connected through the spinal cord to nerves that branch out to both arms and legs.",
                    "Touch each glowing neuron near the brain. Touching one automatically sends a glowing signal down the spinal cord, which then splits and travels down both legs — no tracing or gesture needed, just watch it happen.",
                    "A brain with a spinal cord and branching nerves reaching the arms and legs, plus 6 glowing neurons near the brain that discharge (flash and disappear) when touched.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Nervous scene ready. The signal to move is just starting."
                        : progressSummary);

            case AnatomySceneId.Skeletal:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "skeletal",
                    "The student is focused on the skeletal system — one hand on the lower leg to anchor it, one hand on the upper leg, swinging the upper leg like a hinge at the knee.",
                    "Swing the upper leg until the leg is straight to lock the knee into place, then bend it again to see how the joint works.",
                    "An upper leg bone, a lower leg bone, the kneecap sliding between them, and the guide gesturing toward the knee.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Skeletal scene ready. The knee is loose and waiting to be swung straight."
                        : progressSummary);

            case AnatomySceneId.SteepIncline:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "steep_incline",
                    "The student is back on the mountain trail, further up the climb. The trail has gotten steeper and the body is working harder.",
                    "Notice the legs working harder and the heart beating faster as the demand increases.",
                    "The steeper trail, the mountain scenery further up, and the guide commenting on the increased effort.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Steep incline reached. The body is working harder than before."
                        : progressSummary);

            case AnatomySceneId.Muscular:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "muscular",
                    "The student is inside the leg, looking at a muscle fiber up close, right after the Skeletal scene.",
                    "Grab one end of the muscle fiber with each hand and pull them toward each other to contract it, 3 separate times, to finish this scene — a small green ring floating near the fiber fills by a third with each completed squeeze and pops/disappears once all 3 are done. You (the tutor) cannot see the ring or count squeezes yourself — encourage the student toward the 3-squeeze goal and ask how they're doing if it's unclear, but take their own word for how many they've done rather than stating a specific count as fact.",
                    "A muscle fiber with a grippable end near each tip, tendons anchoring it to bone, a small green progress ring floating just above it, and the guide gesturing toward the fiber.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Muscular scene ready. The student hasn't reported finishing the squeezes yet."
                        : progressSummary);

            case AnatomySceneId.Circulatory:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "circulatory",
                    "The student is inside the chest cavity near the heart. Blood tubes lead toward the lungs and leg muscles.",
                    "Pump the heart to move existing blood faster so oxygen reaches the muscles.",
                    "Heart, artery tubes, blood packets, lungs in the upper view, and leg muscle tissue in the distance.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Circulatory scene ready. The heart is responding to the climb."
                        : progressSummary);

            case AnatomySceneId.ThinAir:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "thin_air",
                    "The student is back on the mountain trail, higher up where the air is noticeably thinner.",
                    "Notice the faster breathing as the body works harder to pull in the same amount of oxygen.",
                    "The higher, thinner trail, visible breath in the cold air, and the guide commenting on the altitude.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Thin air reached. Breathing is picking up to keep pace with the climb."
                        : progressSummary);

            case AnatomySceneId.Respiratory:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "respiratory",
                    "The student is inside the lungs where the diaphragm and alveoli can be seen clearly.",
                    "Push the diaphragm downward and move oxygen into the bloodstream at the alveoli.",
                    "Diaphragm dome, alveoli clusters, oxygen particles, carbon dioxide particles, and blood vessels.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Respiratory scene ready. The lungs are helping the climb."
                        : progressSummary);

            case AnatomySceneId.EnergyCrash:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "energy_crash",
                    "The student is back on the mountain trail, and the body is running low on fuel after the sustained climb.",
                    "Notice the sudden fatigue — that's blood sugar dropping and the body signaling it needs more fuel.",
                    "The trail with visible fatigue cues, and the guide pointing out the need to refuel.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Energy crash hit. The body needs fuel to keep climbing."
                        : progressSummary);

            case AnatomySceneId.Digestive:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "digestive",
                    "The student is inside the small intestine where food is being moved and absorbed.",
                    "Use peristalsis and absorb glucose into the blood so the muscles can keep climbing.",
                    "Small intestine tunnel, food particles, intestinal wall openings, and nearby blood vessels.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Digestive scene ready. Nutrients are moving into the blood."
                        : progressSummary);

            case AnatomySceneId.Summit:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "summit",
                    "The student has reached the mountain summit after every system pulled its weight along the way.",
                    "Take in the summit view and get ready to see how the body settles back down.",
                    "Mountain summit view, dashboard, and the guide celebrating the climb.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Summit reached. The climb is complete."
                        : progressSummary);

            case AnatomySceneId.Homeostasis:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "homeostasis",
                    "The student is watching the body settle back down after the climb — heart rate, breathing, and energy all returning to baseline.",
                    "Watch heart rate and breathing settle back to their resting baseline as the body returns to balance.",
                    "Heart, lungs, and a dashboard showing heart rate and breathing rate settling back down.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Homeostasis in progress. The body is returning to balance."
                        : progressSummary);

            case AnatomySceneId.Completion:
            default:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "completion",
                    "The student has finished the Summit Challenge — every system worked together to get them here.",
                    "Reflect on how muscular, skeletal, nervous, circulatory, respiratory, and digestive systems worked together, and how the body found balance again.",
                    "Mountain summit, dashboard showing all completed systems, and the guide giving closing remarks.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Summit Challenge complete."
                        : progressSummary);
        }
    }
}