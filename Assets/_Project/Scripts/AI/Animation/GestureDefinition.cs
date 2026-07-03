using UnityEngine;

/// <summary>
/// Defines a single gesture animation — its animator trigger, the keywords
/// that cause it to fire, and its timing settings.
///
/// Create via: Right-click in Project → Create → Gestures → Gesture Definition
///
/// To add a new gesture:
///   1. Create a new GestureDefinition asset
///   2. Set the Animator Trigger Name to match your Animator parameter
///   3. Add keywords that should trigger this gesture
///   4. Drag the asset into the GestureSynchronizer's Gesture Definitions list
/// </summary>
[CreateAssetMenu(fileName = "NewGestureDefinition", menuName = "Gestures/Gesture Definition")]
public class GestureDefinition : ScriptableObject
{
    [Header("Identity")]
    [Tooltip("Human-readable name for logs and debugging (e.g. 'Eat', 'Condense', 'Split')")]
    public string gestureName = "New Gesture";

    [Header("Animator")]
    [Tooltip("Exact name of the Trigger parameter in your Animator Controller")]
    public string animatorTriggerName = "";

    [Header("Keywords")]
    [Tooltip("Words or phrases in the AI response that trigger this gesture. " +
             "List more specific phrases first (e.g. 'line up at the center' before 'line up'). " +
             "Matching is case-insensitive and uses whole-word boundaries.")]
    public string[] keywords = new string[0];

    [Header("Timing")]
    [Tooltip("Scales the estimated time-to-keyword. Values below 1.0 trigger earlier.")]
    [Range(0.2f, 2.0f)]
    public float timeScale = 1.0f;

    [Tooltip("Subtracts this many seconds from the calculated delay. Positive = trigger earlier.")]
    public float advanceSeconds = 0.0f;

    [Tooltip("Fallback delay in seconds if keyword position cannot be calculated.")]
    public float fallbackDelaySeconds = 2.0f;

    [Header("Debug")]
    [Tooltip("Emoji shown in logs to make this gesture easy to spot (e.g. 🍽️, ↔️, 📏)")]
    public string logEmoji = "🎭";
}