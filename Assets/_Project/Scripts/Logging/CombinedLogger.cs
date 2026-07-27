using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Writes a lightweight combined CSV for the current anatomy scene and the
/// latest tutor speech/gesture values.
///
/// This is intentionally self-contained so the AI tutor can run without the
/// old biology manager stack.
/// </summary>
public class CombinedLogger : MonoBehaviour
{
    [Header("Logging Settings")]
    [SerializeField] private float loggingInterval = 0.1f;
    [SerializeField] private bool logToConsole = false;
    [SerializeField] private bool logToCSV = true;

    [Tooltip("How often the buffered rows are flushed to disk. Rows are still " +
             "written to the writer every loggingInterval — this only controls " +
             "how often that's forced out to the actual file, so a crash loses " +
             "at most this many seconds of rows instead of nothing.")]
    [SerializeField] private float flushInterval = 2f;

    private string csvFilePath;
    private StreamWriter csvWriter;
    private float timeSinceLastLog = 0f;
    private float timeSinceLastFlush = 0f;
    private float sessionStartTime;

    private void Start()
    {
        if (logToConsole)
            Debug.Log("[CombinedLogger] START called");

        sessionStartTime = Time.time;

        if (logToCSV)
            InitializeCSVFile();

        if (logToConsole)
            Debug.Log("[CombinedLogger] Initialized. Logging to: " + csvFilePath);
    }

    private void LateUpdate()
    {
        timeSinceLastLog += Time.deltaTime;
        if (timeSinceLastLog >= loggingInterval)
        {
            LogCombinedRow();
            timeSinceLastLog = 0f;
        }

        if (csvWriter != null)
        {
            timeSinceLastFlush += Time.deltaTime;
            if (timeSinceLastFlush >= flushInterval)
            {
                csvWriter.Flush();
                timeSinceLastFlush = 0f;
            }
        }
    }

    private void OnDestroy()  => CloseWriter();
    private void OnApplicationQuit() => CloseWriter();

    private void CloseWriter()
    {
        if (csvWriter == null) return;
        try
        {
            csvWriter.Flush();
            csvWriter.Dispose();
        }
        catch (Exception ex)
        {
            Debug.LogError("[CombinedLogger] Failed to close CSV writer: " + ex.Message);
        }
        finally
        {
            csvWriter = null;
        }
    }

    private void LogCombinedRow()
    {
        float elapsed = Time.time - sessionStartTime;
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        AnatomyTutorSceneSnapshot scene = AnatomyTutorSession.Current;
        string userSpeech = AnatomyTutorSession.LastUserSpeech;
        string aiSpeech = AnatomyTutorSession.LastTutorSpeech;
        string aiGesture = AnatomyTutorSession.LastTutorGesture;
        string other = AnatomyTutorSession.LastNote;

        if (logToConsole)
            Debug.Log($"[CombinedLog] T={elapsed:F2}s | TS={timestamp} | Scene:{scene.SceneId} | Phase:{scene.Phase} | " +
                      $"Dialogue:{SocraticDialogueTelemetry.CurrentPhase}");

        if (logToCSV)
        {
            WriteToCSV(timestamp, elapsed, scene, userSpeech, aiSpeech, aiGesture, other);
        }
    }

    private void InitializeCSVFile()
    {
        try
        {
            string directory = Path.Combine(Application.persistentDataPath, "MountainHikeChallengeLogs");
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            csvFilePath = Path.Combine(directory, "CombinedLog.csv");

            csvWriter = new StreamWriter(csvFilePath, false, new UTF8Encoding(true)) { AutoFlush = false };
            csvWriter.WriteLine(
                "Timestamp,Time(s),SceneId,Phase,SceneSummary,CurrentObjective,VisibleObjects,ProgressSummary," +
                "User_Speech,AI_Speech,AI_Gesture,Other," +
                "Dialogue_System,Dialogue_Phase,Evaluate_Round,ArticulateUpdated_Attempt,Last_Assessment,Last_Misconception");
        }
        catch (Exception ex)
        {
            Debug.LogError("[CombinedLogger] Failed to initialize CSV: " + ex.Message);
        }
    }

    private void WriteToCSV(
        long timestamp,
        float elapsed,
        AnatomyTutorSceneSnapshot scene,
        string userSpeech,
        string aiSpeech,
        string aiGesture,
        string other)
    {
        if (csvWriter == null) return;

        try
        {
            csvWriter.WriteLine(
                $"{timestamp}," +
                $"{elapsed:F3}," +
                $"{EscapeCsvField(scene.SceneId.ToString())}," +
                $"{EscapeCsvField(scene.Phase)}," +
                $"{EscapeCsvField(scene.SceneSummary)}," +
                $"{EscapeCsvField(scene.CurrentObjective)}," +
                $"{EscapeCsvField(scene.VisibleObjects)}," +
                $"{EscapeCsvField(scene.ProgressSummary)}," +
                $"{EscapeCsvField(userSpeech)}," +
                $"{EscapeCsvField(aiSpeech)}," +
                $"{EscapeCsvField(aiGesture)}," +
                $"{EscapeCsvField(other)}," +
                $"{EscapeCsvField(SocraticDialogueTelemetry.CurrentSystem?.ToString() ?? "")}," +
                $"{EscapeCsvField(SocraticDialogueTelemetry.CurrentPhase.ToString())}," +
                $"{SocraticDialogueTelemetry.CurrentEvaluateRound}," +
                $"{SocraticDialogueTelemetry.CurrentArticulateUpdatedAttempt}," +
                $"{EscapeCsvField(SocraticDialogueTelemetry.LastAssessment.ToString())}," +
                $"{EscapeCsvField(SocraticDialogueTelemetry.LastMisconceptionTag)}");
        }
        catch (Exception ex)
        {
            Debug.LogError("[CombinedLogger] Failed to write to CSV: " + ex.Message);
        }
    }

    private string EscapeCsvField(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        string escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    public string GetCSVFilePath() => csvFilePath;
}