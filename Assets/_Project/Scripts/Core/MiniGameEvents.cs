using UnityEngine;
using System;

public static class MiniGameEvents
{
    // Fired when the student begins a gesture interaction
    public static event Action OnInteractionStarted;

    // Fired when a gesture reaches a meaningful threshold
    // (e.g. heart squeezed hard enough, muscle fiber pulled far enough)
    public static event Action<float> OnThresholdReached; // float = 0-1 progress

    // Fired when a mini-game is successfully completed
    public static event Action<string> OnMiniGameComplete; // string = system name

    // Fired when a mini-game fails or times out
    public static event Action<string> OnMiniGameFailed;

    // Invoke helpers
    public static void TriggerInteractionStarted() => OnInteractionStarted?.Invoke();
    public static void TriggerThresholdReached(float progress) => OnThresholdReached?.Invoke(progress);
    public static void TriggerMiniGameComplete(string system) => OnMiniGameComplete?.Invoke(system);
    public static void TriggerMiniGameFailed(string system) => OnMiniGameFailed?.Invoke(system);
}