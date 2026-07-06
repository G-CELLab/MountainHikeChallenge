using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Lives on the persistent AI agent GameObject (the one sitting in Bootstrap
/// with DontDestroyOnLoad). Listens for every scene load — the first one from
/// BootstrapLoader and every later one from SceneTransitionManager — and
/// teleports the guide to that scene's AIGuideSpawnPoint.
///
/// If a scene has no spawn point, the guide just stays wherever she last was
/// and a warning is logged, so a missing marker fails loudly instead of
/// silently placing her somewhere wrong.
/// </summary>
public class AIGuidePositioner : MonoBehaviour
{
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
        if (scene.name == "_Bootstrap")
        {
            if (verbose)
                Debug.Log($"[AIGuidePositioner] '{scene.name}' has no spawn point by design — skipping.");
            return;
        }

        var spawnPoint = FindAnyObjectByType<AIGuideSpawnPoint>();

        if (spawnPoint == null)
        {
            if (verbose)
                Debug.LogWarning($"[AIGuidePositioner] No AIGuideSpawnPoint found in scene '{scene.name}' — guide stayed at her previous position.");
            return;
        }

        MoveTo(spawnPoint.transform.position, spawnPoint.transform.rotation);

        if (verbose)
            Debug.Log($"[AIGuidePositioner] Moved guide to spawn point in '{scene.name}'.");
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