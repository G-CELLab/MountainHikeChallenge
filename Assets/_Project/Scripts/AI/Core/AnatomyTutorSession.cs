using System;

/// <summary>
/// Shared anatomy tutor scene state and lightweight runtime memory.
/// This is the single source of truth for scene/phase context — both the
/// narrator scripts and the AI response pipeline read from
/// AnatomyTutorSession.Current directly. There is no separate "refresh"
/// step needed since this class is always live.
/// </summary>
public enum AnatomySceneId
{
    Trailhead,
    Nervous,
    Skeletal,
    SteepIncline,
    Circulatory,
    Respiratory,
    Digestive,
    Summit
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
                    "The student is at the base of Mount Timpanogos. The trail is steep and the guide is about to show how the body starts the climb.",
                    "Watch the nervous system send the go signal and the skeletal system lock the joints before the first step.",
                    "Glowing neurons, spinal cord pathway, knee joints, and the guide's spine gesture.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Trailhead ready. The climb has just begun."
                        : progressSummary);

            case AnatomySceneId.Nervous:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "nervous",
                    "The student is focused on the nervous system — the brain sending the signal to move down the spinal cord.",
                    "Trace the signal down the spine and tap the glowing neurons to send it toward the legs.",
                    "Glowing neurons, spinal cord pathway, and the guide's spine-tracing gesture.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Nervous scene ready. The signal to move is just starting."
                        : progressSummary);

            case AnatomySceneId.Skeletal:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "skeletal",
                    "The student is focused on the skeletal system — the joints locking into place so the muscles have something to pull against.",
                    "Lock the knee joints into place to stabilize the body before the first step.",
                    "Knee joints, interlocking bone structures, and the guide gesturing toward the legs.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Skeletal scene ready. The joints are locking into place."
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
            default:
                return new AnatomyTutorSceneSnapshot(
                    sceneId,
                    "summit",
                    "The student is back on the mountain summit and the body is settling down after the climb.",
                    "Finish the climb and watch the body return to homeostasis.",
                    "Mountain summit, dashboard, heart, lungs, brain, and a calmer breathing rhythm.",
                    string.IsNullOrWhiteSpace(progressSummary)
                        ? "Summit reached. The body is returning to balance."
                        : progressSummary);
        }
    }
}