using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Persistent script that moves the player's XR rig to this scene's
/// PlayerSpawnPoint every time a new scene loads. Mirrors AIGuidePositioner's
/// approach for the AI guide, but for the player rig.
///
/// Attach this to the same persistent object as GameManager/SceneTransitionManager
/// in _Bootstrap (DontDestroyOnLoad), and drag the XR rig root (the same
/// transform used elsewhere as "rigToMove"/"rigTransform") into rigTransform.
///
/// After positioning, if the new scene has an AutoWalkController, this will
/// call BeginAutoWalk() on it automatically — so the flow per scene is:
/// teleport to Start → auto-walk to End (last waypoint) → whatever happens
/// at End (mini-game trigger, portal, etc.) takes over from there.
/// Set autoStartWalkOnSceneLoad = false if you'd rather trigger the walk
/// manually (e.g. after an AI guide intro line finishes).
/// </summary>
public class PlayerRigPositioner : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The XR rig root (XR Origin) to move. Must persist across scenes (DontDestroyOnLoad).")]
    public Transform rigTransform;

    [Header("Scenes Without a Spawn Point")]
    [Tooltip("Scene names that intentionally have no PlayerSpawnPoint (e.g. Bootstrap).")]
    public string[] scenesWithoutSpawnPoint = { "_Bootstrap" };

    [Header("Auto-Walk")]
    [Tooltip("If true, automatically calls BeginAutoWalk() on the scene's AutoWalkController (if one exists) right after positioning the rig at the spawn point.")]
    public bool autoStartWalkOnSceneLoad = true;

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
                Debug.Log($"[PlayerRigPositioner] '{scene.name}' has no spawn point by design — skipping.");
            return;
        }

        if (rigTransform == null)
        {
            Debug.LogWarning("[PlayerRigPositioner] No rigTransform assigned — cannot position player.");
            return;
        }

        var spawnPoint = FindAnyObjectByType<PlayerSpawnPoint>();

        if (spawnPoint == null)
        {
            if (verbose)
                Debug.LogWarning($"[PlayerRigPositioner] No PlayerSpawnPoint found in scene '{scene.name}' — rig stayed at its previous position.");
            return;
        }

        rigTransform.SetPositionAndRotation(spawnPoint.transform.position, spawnPoint.transform.rotation);

        if (verbose)
            Debug.Log($"[PlayerRigPositioner] Moved player rig to spawn point in '{scene.name}'.");

        if (autoStartWalkOnSceneLoad)
        {
            var autoWalk = FindAnyObjectByType<AutoWalkController>();
            if (autoWalk != null)
            {
                autoWalk.BeginAutoWalk();
                if (verbose) Debug.Log($"[PlayerRigPositioner] Auto-walk started in '{scene.name}'.");
            }
            else if (verbose)
            {
                Debug.Log($"[PlayerRigPositioner] No AutoWalkController found in '{scene.name}' — player stays at spawn point.");
            }
        }
    }
}