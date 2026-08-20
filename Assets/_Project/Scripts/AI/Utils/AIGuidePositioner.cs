using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Lives on the persistent AI agent GameObject (the one sitting in Bootstrap
/// with DontDestroyOnLoad). Listens for every scene load — the first one from
/// BootstrapLoader and every later one from SceneTransitionManager — and
/// teleports the guide to the AIGuideSpawnPoint matching the current trail
/// checkpoint, via the same CheckpointSpawnUtility lookup PlayerRigPositioner
/// uses for the player rig. This is what keeps the guide standing next to the
/// player on repeat visits to a scene (e.g. _MountainTrail at checkpoint 1)
/// instead of both of them agreeing on "which visit is this" separately.
///
/// If no spawn point matches the current checkpoint, the guide just stays
/// wherever she last was and an error is logged (via CheckpointSpawnUtility),
/// so a missing marker fails loudly instead of silently placing her somewhere
/// wrong.
/// </summary>
public class AIGuidePositioner : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The guide's own transform to move. Leave empty to default to this GameObject's own " +
             "transform (correct when this script lives directly on Carla, as it does in this project).")]
    public Transform guideTransform;

    [Tooltip("Optional. If assigned, the initial placement uses NavMeshAgent.Warp() instead of a raw " +
             "transform move, so the agent's internal position is in sync with the NavMesh from the " +
             "start — required for AIGuideFollowController to path correctly right after a scene load. " +
             "Leave empty if the guide doesn't have a NavMeshAgent.")]
    public UnityEngine.AI.NavMeshAgent navMeshAgent;

    [Header("Scenes Without a Spawn Point")]
    [Tooltip("Scene names that intentionally have no AIGuideSpawnPoint (e.g. Bootstrap).")]
    public string[] scenesWithoutSpawnPoint = { "-1_Bootstrap" };

    [Header("Debug")]
    public bool verbose = true;

    private void Awake()
    {
        if (guideTransform == null)
            guideTransform = transform;

        if (navMeshAgent == null)
            navMeshAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
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
        if (System.Array.IndexOf(scenesWithoutSpawnPoint, scene.name) >= 0)
        {
            if (verbose)
                Debug.Log($"[AIGuidePositioner] '{scene.name}' has no spawn point by design — skipping.");
            return;
        }

        if (guideTransform == null)
        {
            Debug.LogWarning("[AIGuidePositioner] No guideTransform assigned — cannot position the AI guide.");
            return;
        }

        int checkpoint = MiniGameSequencer.Instance.TrailCheckpointIndex;

        var spawnPoint = CheckpointSpawnUtility.FindExactMatch<AIGuideSpawnPoint>(
            checkpoint, $"AI guide in '{scene.name}'");

        if (spawnPoint == null)
        {
            // Error already logged by CheckpointSpawnUtility. Leave the guide
            // where she was rather than guessing at a fallback position.
            return;
        }

        MoveTo(spawnPoint.transform.position, spawnPoint.transform.rotation);

        if (verbose)
            Debug.Log($"[AIGuidePositioner] Moved guide to checkpoint {checkpoint} spawn point in '{scene.name}'.");
    }

    private void MoveTo(Vector3 position, Quaternion rotation)
    {
        if (navMeshAgent != null && navMeshAgent.enabled)
        {
            bool warped = navMeshAgent.Warp(position);
            guideTransform.rotation = rotation;

            if (!warped)
            {
                // Warp fails if the target point isn't close enough to a baked
                // NavMesh triangle — make sure the NavMesh is baked and covers
                // every AIGuideSpawnPoint in this scene (Window > AI > Navigation).
                Debug.LogWarning($"[AIGuidePositioner] NavMeshAgent.Warp failed at {position} — " +
                                  "is the NavMesh baked and does it cover this spawn point? " +
                                  "Falling back to a direct transform move.");
                guideTransform.position = position;
            }
        }
        else
        {
            guideTransform.SetPositionAndRotation(position, rotation);
        }
    }
}