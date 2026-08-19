using UnityEngine;
using UnityEngine.AI;
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
    [Tooltip("How far to search for a valid NavMesh near the target spawn position before giving up and " +
             "falling back to a plain transform move. Only 0_MountainTrail currently has a baked NavMesh " +
             "(for AIGuideWalker's companion walk) — every other scene falls back automatically, silently, " +
             "instead of Warp() logging Unity's own 'no valid NavMesh' error.")]
    public float navMeshSampleRadius = 2f;

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

        if (navMeshAgent != null && TryWarpOnNavMesh(position))
        {
            root.rotation = rotation;
        }
        else
        {
            root.SetPositionAndRotation(position, rotation);
        }
    }

    /// <summary>
    /// Not every scene has a baked NavMesh — only 0_MountainTrail currently
    /// does, for AIGuideWalker's companion walk. Calling NavMeshAgent.Warp()
    /// directly in a scene with none logs Unity's own "Failed to create agent
    /// because there is no valid NavMesh" error — and so does simply leaving
    /// the agent component ENABLED while active in such a scene, since Unity
    /// tries to auto-place it on its own the moment it's enabled, independent
    /// of any Warp() call we make. So this both samples first (to avoid our
    /// own Warp call failing) AND explicitly disables the component when no
    /// NavMesh is found, so Unity's own auto-placement doesn't fire either.
    /// </summary>
    private bool TryWarpOnNavMesh(Vector3 position)
    {
        if (!NavMesh.SamplePosition(position, out NavMeshHit hit, navMeshSampleRadius, NavMesh.AllAreas))
        {
            navMeshAgent.enabled = false;
            return false;
        }

        // Re-enable in case a previous NavMesh-less scene disabled it — safe
        // to do right before Warp() since we've already confirmed a valid
        // NavMesh position exists nearby.
        navMeshAgent.enabled = true;
        return navMeshAgent.Warp(hit.position);
    }
}