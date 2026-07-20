using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Nervous scene minigame: touch each neuron along the spinal cord to trace
/// the signal from brain to legs, per scene_knowledge.md's "Trace the signal
/// with me, then look for the glowing neurons."
///
/// Deliberately does NOT touch HandManager or any custom gesture detection —
/// this is a poke, and Poke Interactor (already on both hands in the rig)
/// already does the touch detection. Each neuron just needs an
/// XRSimpleInteractable component; this script listens to the standard XRI
/// select event, same as any other interactable in the project.
///
/// Setup:
///   1. Add an XRSimpleInteractable to each neuron GameObject (needs a
///      trigger Collider for the poke interactor to detect).
///   2. Drag them into `neurons`, in the order the signal should trace
///      (top of spine → bottom), matching the visual path in the scene.
///   3. Drop this script on any GameObject in the scene and drag the same
///      neurons in.
///
/// Fires the shared MiniGameEvents contract other systems (GameManager,
/// HUD, etc.) already listen to — no direct references needed either way.
/// </summary>
public class NervousSystemInteraction : MonoBehaviour
{
    [Header("Neurons — in trace order (brain → legs)")]
    [SerializeField] private List<XRSimpleInteractable> neurons = new List<XRSimpleInteractable>();

    [Header("Sequencing")]
    [Tooltip("If true, neurons must be touched in list order — touching one out of turn is ignored " +
             "(not penalized, just doesn't count, so a student who taps the wrong one first isn't punished, " +
             "just has to keep going until they hit the right next one). If false, any order counts.")]
    [SerializeField] private bool requireSequential = true;

    [Header("Appearance Timing")]
    [Tooltip("Neurons are hidden at Start and revealed one at a time, waiting a random interval " +
             "in this range (seconds) between each reveal. Each neuron's own fade-in/pop animation " +
             "(NeuronVisual) plays automatically the moment it's revealed — nothing else to wire up.")]
    [SerializeField] private Vector2 appearIntervalRange = new Vector2(0.5f, 1f);

    [Header("Visuals")]
    [Tooltip("Ordered waypoints for the light effect — same order as neurons, plus one final point " +
             "past the last neuron (where the signal exits toward the legs). Usually just each neuron's " +
             "own transform plus one extra empty GameObject placed further down the spine.")]
    [SerializeField] private Transform[] spinePath;
    [SerializeField] private SpineSignalPulse spinePulse;
    [SerializeField] private float pulseSpeed = 4f;

    [Header("Final Flourish")]
    [Tooltip("Optional — if assigned and it has a NeuronVisual, it flashes/discharges alongside the final big pulse.")]
    [SerializeField] private Transform brain;
    [SerializeField] private float finalPulseSpeedMultiplier = 1.5f;
    [SerializeField] private float finalPulseScale = 2f;

    [Header("Debug")]
    [SerializeField] private bool verbose = true;

    private readonly HashSet<XRSimpleInteractable> _touched = new HashSet<XRSimpleInteractable>();
    private int _nextExpectedIndex = 0;
    private bool _started = false;
    private bool _completed = false;

    private void OnEnable()
    {
        for (int i = 0; i < neurons.Count; i++)
        {
            var neuron = neurons[i];
            if (neuron == null)
            {
                Debug.LogWarning($"[NervousSystemInteraction] Neuron slot {i} is empty — check the list in the Inspector.");
                continue;
            }
            neuron.selectEntered.AddListener(OnNeuronSelected);
        }
    }

    private void OnDisable()
    {
        foreach (var neuron in neurons)
        {
            if (neuron != null)
                neuron.selectEntered.RemoveListener(OnNeuronSelected);
        }
    }

    private void Start()
    {
        // Hide every neuron up front; RevealNeuronsSequentially() brings them
        // in one at a time, and each one's own fade-in/pop animation
        // (NeuronVisual) plays automatically the moment it's re-enabled —
        // nothing else needs to be triggered from here.
        foreach (var neuron in neurons)
            if (neuron != null) neuron.gameObject.SetActive(false);

        StartCoroutine(RevealNeuronsSequentially());
    }

    private IEnumerator RevealNeuronsSequentially()
    {
        foreach (var neuron in neurons)
        {
            if (neuron == null) continue;

            neuron.gameObject.SetActive(true);
            if (verbose) Debug.Log($"[NervousSystemInteraction] Neuron revealed: {neuron.name}");

            yield return new WaitForSeconds(Random.Range(appearIntervalRange.x, appearIntervalRange.y));
        }
    }

    private void OnNeuronSelected(SelectEnterEventArgs args)
    {
        if (_completed) return;

        var neuron = args.interactableObject as XRSimpleInteractable;
        if (neuron == null) return;

        int touchedIndex = neurons.IndexOf(neuron);
        if (touchedIndex < 0) return; // not one of ours

        if (!_started)
        {
            _started = true;
            MiniGameEvents.TriggerInteractionStarted();
            if (verbose) Debug.Log("[NervousSystemInteraction] 🧠 Trace started.");
        }

        if (requireSequential)
        {
            if (touchedIndex != _nextExpectedIndex)
            {
                if (verbose)
                    Debug.Log($"[NervousSystemInteraction] Touched neuron {touchedIndex}, expected {_nextExpectedIndex} — ignored.");
                return;
            }

            _nextExpectedIndex++;
        }

        if (_touched.Add(neuron))
        {
            float progress = (float)_touched.Count / neurons.Count;
            MiniGameEvents.TriggerThresholdReached(progress);

            if (verbose)
                Debug.Log($"[NervousSystemInteraction] ⚡ Neuron {touchedIndex} touched — progress {progress:P0}.");

            // Visual: this neuron flashes and disappears, and a small pulse of
            // light travels from it to the next point down the spine.
            neuron.GetComponent<NeuronVisual>()?.Discharge();

            if (spinePulse != null && spinePath != null && touchedIndex + 1 < spinePath.Length
                && spinePath[touchedIndex] != null && spinePath[touchedIndex + 1] != null)
            {
                var segment = new[] { spinePath[touchedIndex].position, spinePath[touchedIndex + 1].position };
                spinePulse.FirePulse(segment, pulseSpeed);
            }

            if (_touched.Count >= neurons.Count)
            {
                _completed = true;
                PlayFinalPulse();
            }
        }
    }

    /// <summary>
    /// The big "everything at once" pulse down the whole spine. Completion
    /// is deliberately fired from the pulse's onArrive callback rather than
    /// immediately, so GameManager/HUD react exactly when the light visually
    /// reaches the end — not a beat before the player sees anything happen.
    /// </summary>
    private void PlayFinalPulse()
    {
        brain?.GetComponent<NeuronVisual>()?.Discharge();

        if (spinePulse == null || spinePath == null || spinePath.Length < 2)
        {
            // No visual wired up — don't block gameplay progress on it.
            MiniGameEvents.TriggerMiniGameComplete("Nervous");
            if (verbose) Debug.Log("[NervousSystemInteraction] ✅ Nervous complete (no spinePath assigned — skipped final pulse visual).");
            return;
        }

        Vector3[] fullPath = new Vector3[spinePath.Length];
        for (int i = 0; i < spinePath.Length; i++)
            fullPath[i] = spinePath[i] != null ? spinePath[i].position : transform.position;

        spinePulse.FirePulse(fullPath, pulseSpeed * finalPulseSpeedMultiplier, finalPulseScale, onArrive: () =>
        {
            MiniGameEvents.TriggerMiniGameComplete("Nervous");
            if (verbose) Debug.Log("[NervousSystemInteraction] ✅ Signal traced — Nervous complete.");
        });
    }

    /// <summary>Resets progress without needing to reload the scene — handy for playtesting.</summary>
    [ContextMenu("Reset Trace")]
    public void ResetTrace()
    {
        _touched.Clear();
        _nextExpectedIndex = 0;
        _started = false;
        _completed = false;
    }
}