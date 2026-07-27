using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lives in the Nervous mini-game scene. Tracks the 6 NeuronVisual objects
/// and, once every one of them has been touched AND finished its discharge
/// animation (flashed, shrunk, disappeared — see NeuronVisual.OnDischarged),
/// registers that with MiniGameCompletionGate.
///
/// Also fires the traveling glow along the spine (SpineSignalPulse) each
/// time a neuron discharges — a small pulse per neuron, and one bigger,
/// brighter pulse once all 6 are done. The pulse travels the trunk path
/// (brain → base of spine) first, then FORKS into two simultaneous pulses
/// continuing down the left and right leg paths, so it visually splits at
/// the pelvis rather than only ever going down one side.
///
/// Combined with AITutor marking the Socratic dialogue resolved, the scene
/// now only transitions once BOTH are true: the student articulated a
/// normative explanation of the nervous system AND actually touched all 6
/// neurons. Either alone used to be enough (dialogue resolving fired
/// completion immediately, regardless of whether any neuron had been
/// touched); now both are required.
/// </summary>
public class NervousSystemMiniGameController : MonoBehaviour
{
    [Tooltip("All 6 NeuronVisual objects in this scene. Assign in the Inspector.")]
    [SerializeField] private List<NeuronVisual> neurons = new List<NeuronVisual>();

    [Header("Spine Signal Visual")]
    [Tooltip("The SpineSignalPulse component in this scene (usually on the same GameObject as the spine model, or a dedicated manager object).")]
    [SerializeField] private SpineSignalPulse spineSignalPulse;

    [Tooltip("Trunk path: brain down to the base of the spine/pelvis. Its LAST waypoint should be the exact same object as the FIRST waypoint of both leg paths below, so the fork has no visible gap.")]
    [SerializeField] private SpineWaypointPath spinePath;

    [Tooltip("Continues from the trunk path's last waypoint down the left leg to the foot. Leave empty to skip the leg fork entirely (pulse just stops at the base of the spine).")]
    [SerializeField] private SpineWaypointPath leftLegPath;

    [Tooltip("Continues from the trunk path's last waypoint down the right leg to the foot. Leave empty to skip the leg fork entirely.")]
    [SerializeField] private SpineWaypointPath rightLegPath;

    [Tooltip("units/second for the small per-neuron pulses.")]
    [SerializeField] private float perNeuronPulseSpeed = 4f;

    [Tooltip("units/second for the bigger final pulse once all 6 neurons are done.")]
    [SerializeField] private float finalPulseSpeed = 6f;

    [Tooltip("How much bigger/brighter the final 'all done' pulse looks compared to a single-neuron pulse.")]
    [SerializeField] private float finalPulseScaleMultiplier = 1.8f;

    private int _dischargedCount;
    private bool _interactionMarkedComplete;

    private void Awake()
    {
        MiniGameCompletionGate.RegisterInteractionGate(BodySystem.Nervous);

        if (neurons == null || neurons.Count == 0)
            Debug.LogWarning("[NervousSystemMiniGameController] No neurons assigned — the interaction gate will " +
                              "never complete, and the Nervous mini-game will never transition. Assign the 6 " +
                              "NeuronVisual objects in the Inspector.");

        if (spineSignalPulse == null || spinePath == null)
            Debug.LogWarning("[NervousSystemMiniGameController] SpineSignalPulse or SpineWaypointPath not " +
                              "assigned — neuron touches will still count toward completion, but no glowing " +
                              "signal will travel down the spine.");
    }

    private void OnEnable()
    {
        foreach (NeuronVisual neuron in neurons)
        {
            if (neuron != null)
                neuron.OnDischarged += HandleNeuronDischarged;
        }
    }

    private void OnDisable()
    {
        foreach (NeuronVisual neuron in neurons)
        {
            if (neuron != null)
                neuron.OnDischarged -= HandleNeuronDischarged;
        }
    }

    private void HandleNeuronDischarged(NeuronVisual neuron)
    {
        if (_interactionMarkedComplete) return; // already reported — ignore any late/duplicate callbacks

        _dischargedCount++;
        Debug.Log($"[NervousSystemMiniGameController] Neuron discharged ({_dischargedCount}/{neurons.Count}).");

        bool isLastNeuron = _dischargedCount >= neurons.Count;
        float speed = isLastNeuron ? finalPulseSpeed : perNeuronPulseSpeed;
        float scale = isLastNeuron ? finalPulseScaleMultiplier : 1f;

        FireTrunkThenSplit(speed, scale);

        if (isLastNeuron)
        {
            _interactionMarkedComplete = true;
            Debug.Log("[NervousSystemMiniGameController] All neurons touched and discharged — interaction gate satisfied.");
            MiniGameCompletionGate.MarkInteractionComplete(BodySystem.Nervous);
        }
    }

    /// <summary>
    /// Fires the pulse along the trunk (brain → base of spine) first, then
    /// — once it actually arrives, not before, so the fork lines up visually
    /// with the pulse reaching the pelvis — fires both leg pulses at the
    /// same time so it reads as a single signal splitting in two, not two
    /// separate independent pulses.
    /// </summary>
    private void FireTrunkThenSplit(float speed, float scale)
    {
        if (spineSignalPulse == null || spinePath == null) return;

        spineSignalPulse.FirePulse(spinePath, speed, scale, onArrive: () =>
        {
            if (leftLegPath != null)
                spineSignalPulse.FirePulse(leftLegPath, speed, scale);

            if (rightLegPath != null)
                spineSignalPulse.FirePulse(rightLegPath, speed, scale);
        });
    }
}