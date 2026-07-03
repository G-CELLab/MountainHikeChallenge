/// <summary>
/// Compatibility logger used by the AI stack and gesture driver.
/// </summary>
public static class MainLogger
{
    public static string LastUserSpeech { get; private set; } = string.Empty;
    public static string LastAISpeech { get; private set; } = string.Empty;
    public static string LastAIGesture { get; private set; } = string.Empty;
    public static string LastPhase { get; private set; } = string.Empty;
    public static string LastOther { get; private set; } = string.Empty;

    public static void LogUserSpeech(string text)
    {
        LastUserSpeech = text ?? string.Empty;
        AnatomyTutorSession.RecordUserSpeech(LastUserSpeech);
        LastPhase = AnatomyTutorSession.Current.Phase;
    }

    public static void LogAISpeech(string text)
    {
        LastAISpeech = text ?? string.Empty;
        AnatomyTutorSession.RecordTutorSpeech(LastAISpeech);
        LastPhase = AnatomyTutorSession.Current.Phase;
    }

    public static void LogAIGestureEvent(string gestureName)
    {
        LastAIGesture = gestureName ?? string.Empty;
        AnatomyTutorSession.RecordTutorGesture(LastAIGesture);
        LastPhase = AnatomyTutorSession.Current.Phase;
    }

    public static void LogPhase(string phase)
    {
        LastPhase = phase ?? string.Empty;
    }

    public static void LogOther(string note)
    {
        LastOther = note ?? string.Empty;
        AnatomyTutorSession.RecordNote(LastOther);
    }
}