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

    private string csvFilePath;
    private float timeSinceLastLog = 0f;
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
    }

    private void LogCombinedRow()
    {
        float elapsed = Time.time - sessionStartTime;
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        AnatomyTutorSceneSnapshot scene = AnatomyTutorSession.Current;
        string userSpeech = MainLogger.LastUserSpeech;
        string aiSpeech = MainLogger.LastAISpeech;
        string aiGesture = MainLogger.LastAIGesture;
        string other = MainLogger.LastOther;

        if (logToConsole)
            Debug.Log($"[CombinedLog] T={elapsed:F2}s | TS={timestamp} | Scene:{scene.SceneId} | Phase:{scene.Phase}");

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

            using (StreamWriter writer = new StreamWriter(csvFilePath, false, new UTF8Encoding(true)))
            {
                writer.WriteLine(
                    "Timestamp,Time(s),SceneId,Phase,SceneSummary,CurrentObjective,VisibleObjects,ProgressSummary," +
                    "User_Speech,AI_Speech,AI_Gesture,Other");
            }
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
        try
        {
            using (StreamWriter writer = new StreamWriter(csvFilePath, true, new UTF8Encoding(true)))
            {
                writer.WriteLine(
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
                    $"{EscapeCsvField(other)}");
            }
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