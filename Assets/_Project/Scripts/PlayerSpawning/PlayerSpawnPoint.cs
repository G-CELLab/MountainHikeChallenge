using UnityEngine;

/// <summary>
/// Marks where the player's XR rig should be placed when this scene loads.
/// Mirrors AIGuideSpawnPoint's role for the AI guide, but for the player.
/// PlayerRigPositioner finds the one matching the current trail checkpoint
/// and moves the rig there.
///
/// A scene can have MULTIPLE PlayerSpawnPoints — e.g. _MountainTrail has one
/// per checkpoint, since the player lands further up the mountain each time
/// they return. Give each one a distinct Checkpoint Index (0, 1, 2, ...)
/// matching MiniGameSequencer.Instance.TrailCheckpointIndex at the moment
/// this scene loads.
///
/// The scene's AutoWalkController waypoint chain should typically start at
/// (or near) whichever spawn point is active for that checkpoint.
/// </summary>
public class PlayerSpawnPoint : MonoBehaviour, ICheckpointSpawnPoint
{
    [Tooltip("Which trail checkpoint this spawn point is for. 0 = first arrival, " +
             "1 = next return to this scene, etc. Must match MiniGameSequencer's " +
             "TrailCheckpointIndex exactly, or PlayerRigPositioner will log an error " +
             "and leave the rig where it was instead of guessing.")]
    public int checkpointIndex = 0;

    public int CheckpointIndex => checkpointIndex;

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position, 0.3f);
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 0.5f);
    }
#endif
}