using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

/// <summary>
/// Makes the AI guide continuously follow a target (the player) using a
/// NavMeshAgent for pathfinding, while the Animator's root motion actually
/// drives her visible movement — matching this project's choice to keep
/// "Apply Root Motion" checked on Carla's Animator.
///
/// NavMeshAgent and root motion fight each other by default: the agent wants
/// to move the transform itself every frame, and so does the animator. The
/// fix is agent.updatePosition = false, so the agent stops moving the
/// transform on its own, and instead OnAnimatorMove() feeds the animator's
/// root motion result back into the agent every frame via agent.nextPosition.
/// The agent still computes the path and steering (desiredVelocity), which
/// drives the Speed parameter below — it just isn't the thing physically
/// moving her anymore. The walk animation is.
///
/// This script only handles WHERE she walks. Whether Talking/Thinking
/// animations interrupt the walk cycle is entirely about which Animator
/// layer those states live on — see the Animator Controller notes below.
/// For her to talk while walking, Talking/Thinking need to live on a
/// separate upper-body layer (with an Avatar Mask that excludes the legs)
/// so they blend on top of the Base layer's Idle/Walking states instead of
/// replacing them. This script does not (and can't, from a C# file) restructure
/// the Animator Controller — that part has to be done by hand in the Editor.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class AIGuideFollowController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Who to follow — usually the player's XR rig or camera transform. " +
             "Auto-resolves to Camera.main's transform on Awake if left empty.")]
    public Transform followTarget;

    [Tooltip("Auto-found via GetComponent if left empty.")]
    public NavMeshAgent agent;

    [Tooltip("Auto-found via GetComponent if left empty.")]
    public Animator animator;

    [Header("Follow Behaviour")]
    [Tooltip("How close she tries to get to followTarget before stopping.")]
    public float stoppingDistance = 1.5f;

    [Tooltip("How far the player has to move from her current destination before she " +
             "recalculates a path. Avoids spamming SetDestination every single frame.")]
    public float destinationUpdateThreshold = 0.3f;

    [Tooltip("Master on/off switch — set false to freeze her in place (e.g. during a " +
             "mini-game where she shouldn't be wandering toward the player). Also toggled " +
             "automatically by scenesToFollowIn below, so you don't need to flip this by hand " +
             "for normal scene-to-scene flow.")]
    public bool followEnabled = true;

    [Header("Scene Gating")]
    [Tooltip("Scene names where following is active. On every scene load, followEnabled is " +
             "set true if the new scene's name is in this list and false otherwise — e.g. " +
             "she should follow on the mountain trail but stand still (or use a different, " +
             "per-minigame positioning script) inside a mini-game scene.")]
    public string[] scenesToFollowIn = { "0_MountainTrail" };

    [Header("Animator")]
    [Tooltip("Float parameter driving the Idle <-> Walking blend/transitions. " +
             "Add this parameter to the Animator Controller (Base layer) and wire " +
             "Idle -> Walking Start on Speed > ~0.1, Walking -> Idle on Speed < ~0.1.")]
    public string speedParam = "Speed";

    [Header("Debug")]
    public bool verbose = true;

    private int _speedHash;
    private bool _hasSpeedParam;
    private Vector3 _lastDestination;
    private bool _hasDestination;

    private void Awake()
    {
        if (agent == null) agent = GetComponent<NavMeshAgent>();
        if (animator == null) animator = GetComponent<Animator>();

        if (followTarget == null && Camera.main != null)
            followTarget = Camera.main.transform;

        // Root motion (from the Walking clip) drives position; the agent only
        // computes the path and steering. Without this, the agent and the
        // animator both try to move the transform and stutter/fight each frame.
        agent.updatePosition = false;
        agent.stoppingDistance = stoppingDistance;

        _speedHash = Animator.StringToHash(speedParam);
        _hasSpeedParam = HasFloatParam(speedParam);

        if (verbose && !_hasSpeedParam)
            Debug.LogWarning($"[AIGuideFollowController] Animator has no float parameter " +
                              $"'{speedParam}' — add it so the Idle/Walking transitions can " +
                              $"react to movement speed.");

        // Cover the scene this object is already in when it first wakes up
        // (e.g. loaded straight into 0_MountainTrail), not just future loads.
        ApplySceneGate(SceneManager.GetActiveScene().name);
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += HandleSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        ApplySceneGate(scene.name);
    }

    private void ApplySceneGate(string sceneName)
    {
        bool shouldFollow = System.Array.IndexOf(scenesToFollowIn, sceneName) >= 0;
        SetFollowEnabled(shouldFollow);

        // Fresh scene means her old path/destination no longer applies.
        _hasDestination = false;
    }

    private void Update()
    {
        if (!followEnabled || followTarget == null || agent == null) return;
        if (!agent.isOnNavMesh) return;

        if (!_hasDestination || Vector3.Distance(_lastDestination, followTarget.position) > destinationUpdateThreshold)
        {
            agent.SetDestination(followTarget.position);
            _lastDestination = followTarget.position;
            _hasDestination = true;
        }

        if (_hasSpeedParam && animator != null)
        {
            // desiredVelocity reflects how fast the agent WANTS to move along
            // the path — used instead of agent.velocity since updatePosition
            // is false, which means the agent's own velocity tracking isn't
            // being driven by real movement anymore.
            float speed = agent.desiredVelocity.magnitude;
            animator.SetFloat(_speedHash, speed);
        }
    }

    /// <summary>
    /// Root motion callback — Unity calls this once per frame on any object
    /// with an Animator that has Apply Root Motion checked. This is where we
    /// hand the animation-driven movement back to the NavMeshAgent so it
    /// stays aware of where she actually is (for path replanning, stopping
    /// distance, etc.) without being the thing that moves her.
    /// </summary>
    private void OnAnimatorMove()
    {
        if (animator == null || agent == null || !agent.isOnNavMesh) return;

        Vector3 rootPosition = animator.rootPosition;
        transform.position = rootPosition;
        agent.nextPosition = rootPosition;
    }

    private bool HasFloatParam(string paramName)
    {
        if (animator == null || string.IsNullOrEmpty(paramName)) return false;
        foreach (var p in animator.parameters)
            if (p.type == AnimatorControllerParameterType.Float && p.name == paramName)
                return true;
        return false;
    }

    /// <summary>Call to pause/resume following without disabling the whole component.</summary>
    public void SetFollowEnabled(bool enabled_)
    {
        followEnabled = enabled_;
        if (verbose) Debug.Log($"[AIGuideFollowController] Follow {(enabled_ ? "resumed" : "paused")}.");
    }

    /// <summary>Call to change who she follows at runtime (e.g. switch to a different player rig).</summary>
    public void SetFollowTarget(Transform target)
    {
        followTarget = target;
        _hasDestination = false;
    }
}