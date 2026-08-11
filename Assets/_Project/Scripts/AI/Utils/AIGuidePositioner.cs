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
    [Header("Scenes Without a Spawn Point")]
    [Tooltip("Scene names that intentionally have no AIGuideSpawnPoint (e.g. Bootstrap).")]
    public string[] scenesWithoutSpawnPoint = { "-1_Bootstrap" };

    [Header("Optional")]
    [Tooltip("If the guide uses a NavMeshAgent, assign it here so we Warp() instead of moving the transform directly (avoids NavMesh desync).")]
    public UnityEngine.AI.NavMeshAgent navMeshAgent;

    [Header("Debug")]
    public bool verbose = true;

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
        // Always move the top of the hierarchy, not just whatever object this
        // script happens to be attached to — otherwise, if this script lives
        // on a child (e.g. an "AI Logic" object under the visible character),
        // only that child moves and the visible model gets left behind.
        Transform root = transform.root;

        if (navMeshAgent != null && navMeshAgent.enabled)
        {
            navMeshAgent.Warp(position);
            root.rotation = rotation;
        }
        else
        {
            root.SetPositionAndRotation(position, rotation);
        }
    }
}