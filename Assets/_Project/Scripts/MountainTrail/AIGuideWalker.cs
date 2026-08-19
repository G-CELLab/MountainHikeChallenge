using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Makes the AI guide walk alongside the player during AutoWalkController
/// segments, using Unity's built-in NavMesh (the free "AI Navigation"
/// package) — no third-party asset needed. AIGuidePositioner already
/// expects the guide to have a NavMeshAgent (it Warp()s it on scene load),
/// so this just puts that agent to work during the walk itself.
///
/// Rather than chasing the player's live position directly — which tends to
/// look like trailing/following, with lag and overshoot on turns — this
/// targets a point offset to the side of the player, so the guide keeps
/// pace roughly shoulder-to-shoulder instead of trailing behind.
///
/// Setup required in the Editor (one-time):
///   1. Window → Package Manager → install "AI Navigation" if not already
///      present (built into the Editor on older Unity versions, a separate
///      package on newer ones).
///   2. Bake a NavMesh over the MountainTrail terrain (Window → AI →
///      Navigation, or the AI Navigation package's Bake tab).
///   3. Add a NavMeshAgent component to the guide (Carla), sized to roughly
///      her capsule, and assign it to AIGuidePositioner's navMeshAgent field
///      too (so teleports between checkpoints stay Warp()-based instead of
///      desyncing the agent from a raw transform move).
///   4. Add THIS script to the guide as well.
///   5. Optional but recommended: wire the guide's Animator to
///      agent.velocity.magnitude (e.g. a "Speed" float parameter) so her
///      walk animation actually plays while the agent is moving — a
///      NavMeshAgent alone only moves the transform, it doesn't drive
///      animation by itself.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class AIGuideWalker : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Auto-found via FindAnyObjectByType if left empty. The guide only actively walks while " +
             "THIS is walking (IsWalking) — otherwise the NavMeshAgent is stopped.")]
    public AutoWalkController autoWalkController;
    [Tooltip("The player's XR rig root. Auto-resolved from PlayerRigPositioner.rigTransform if left " +
             "empty, same pattern PlayerRigPositioner/AutoWalkController already use elsewhere.")]
    public Transform playerRig;

    [Header("Follow Placement")]
    [Tooltip("How far to the side of the player the guide walks, in meters. Positive = player's right, negative = left.")]
    public float sideOffset = 1.0f;
    [Tooltip("How far ahead of (positive) or behind (negative) the player the guide walks, in meters.")]
    public float forwardOffset = 0f;
    [Tooltip("How often the destination is refreshed, in seconds. NavMeshAgent path recalculation isn't " +
             "free — every frame is unnecessary for a target that only moves as fast as the player walks.")]
    public float retargetInterval = 0.2f;

    [Header("Animator")]
    [Tooltip("Auto-found via GetComponentInChildren if left empty — same Animator TTSAnimatorDriver " +
             "drives for talking. A NavMeshAgent only moves the transform; it does NOT drive animation " +
             "on its own, so without this the guide will slide along the ground with no walk cycle.")]
    public Animator animator;
    [Tooltip("Bool parameter set the instant walking starts/stops — drives the Idle→WalkStart and " +
             "Walk/WalkStart→Idle transitions. Use Has Exit Time (not a condition) on the WalkStart→Walk " +
             "transition itself, so the one-shot start animation plays to completion before looping into " +
             "Walk, and add a WalkStart→Idle transition on IsWalking == false too (Has Exit Time OFF) so " +
             "stopping mid wind-up doesn't get stuck playing the rest of WalkStart first.")]
    public string isWalkingParam = "IsWalking";
    [Tooltip("Float parameter driven with agent.velocity.magnitude every frame — optional, but wire it " +
             "to the Walk state's Speed multiplier (state Inspector, not a transition condition) if you " +
             "want the loop to visually speed up/slow down with actual movement speed instead of always " +
             "playing at a fixed rate.")]
    public string speedParam = "WalkSpeed";

    [Header("Debug")]
    public bool verbose = true;

    private NavMeshAgent _agent;
    private float _retargetTimer;
    private int _speedHash;
    private int _isWalkingHash;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        _speedHash = Animator.StringToHash(speedParam);
        _isWalkingHash = Animator.StringToHash(isWalkingParam);
    }

    private void OnEnable()
    {
        if (autoWalkController == null)
            autoWalkController = FindAnyObjectByType<AutoWalkController>();

        if (playerRig == null)
        {
            var positioner = FindAnyObjectByType<PlayerRigPositioner>();
            if (positioner != null) playerRig = positioner.rigTransform;
        }

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        if (verbose && playerRig == null)
            Debug.LogWarning("[AIGuideWalker] Could not resolve playerRig — no PlayerRigPositioner found, or its rigTransform is unassigned.");
        if (verbose && animator == null)
            Debug.LogWarning("[AIGuideWalker] No Animator found — the guide will move but won't play a walk animation.");
    }

    private void Update()
    {
        // NavMeshAgent operations (isStopped, SetDestination) throw if the
        // agent isn't actually placed on a NavMesh — true for any scene that
        // doesn't have one baked (currently only 0_MountainTrail does, for
        // this companion walk). Bail out before touching the agent at all in
        // that case, rather than calling isStopped inside the "not on a
        // NavMesh" branch, which is exactly what was throwing.
        if (!_agent.isOnNavMesh || !_agent.enabled)
        {
            SetAnimatorState(false, 0f);
            return;
        }

        // AutoWalkController lives on a per-scene GameObject, not a persistent
        // one — re-resolve if the scene changed underneath us.
        if (autoWalkController == null)
            autoWalkController = FindAnyObjectByType<AutoWalkController>();

        if (autoWalkController == null || playerRig == null)
        {
            _agent.isStopped = true;
            SetAnimatorState(false, 0f);
            return;
        }

        if (!autoWalkController.IsWalking)
        {
            _agent.isStopped = true;
            SetAnimatorState(false, 0f);
            return;
        }

        _agent.isStopped = false;
        SetAnimatorState(true, _agent.velocity.magnitude);

        _retargetTimer -= Time.deltaTime;
        if (_retargetTimer > 0f) return;
        _retargetTimer = retargetInterval;

        Vector3 target = playerRig.position
            + playerRig.right * sideOffset
            + playerRig.forward * forwardOffset;

        _agent.SetDestination(target);
    }

    private void SetAnimatorState(bool isWalking, float speed)
    {
        if (animator == null) return;
        animator.SetBool(_isWalkingHash, isWalking);
        animator.SetFloat(_speedHash, speed);
    }
}