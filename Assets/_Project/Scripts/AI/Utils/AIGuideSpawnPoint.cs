using UnityEngine;

/// <summary>
/// Marks the spot where the AI guide should stand/teleport to when this
/// scene loads, positioned and rotated per scene_design.md (e.g. beside the
/// student at the trailhead, beside the heart in the Circulatory scene).
///
/// A scene can contain MULTIPLE AIGuideSpawnPoints — mirrors PlayerSpawnPoint
/// exactly, for the same reason: a scene like _MountainTrail is revisited at
/// later trail checkpoints, and the guide needs to end up next to the player
/// each time, not stuck at whichever spawn point happened to be found first.
/// Give each one a distinct Checkpoint Index (0, 1, 2, ...) matching
/// MiniGameSequencer.Instance.TrailCheckpointIndex at the moment this scene
/// loads. For a scene that's only ever visited once, just leave a single
/// spawn point at Checkpoint Index 0.
///
/// AIGuidePositioner (on the persistent AI agent) looks for the one matching
/// the current checkpoint via CheckpointSpawnUtility on every scene load and
/// moves the guide there — same matching rule the player rig uses, so both
/// always agree on "which visit is this."
/// </summary>
public class AIGuideSpawnPoint : MonoBehaviour, ICheckpointSpawnPoint
{
    [Tooltip("Which trail checkpoint this spawn point is for. 0 = first arrival, " +
             "1 = next return to this scene, etc. Must match MiniGameSequencer's " +
             "TrailCheckpointIndex exactly, or AIGuidePositioner will log an error " +
             "and leave the guide where she was instead of guessing.")]
    public int checkpointIndex = 0;

    public int CheckpointIndex => checkpointIndex;

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        Gizmos.color = Color.magenta;
        Gizmos.DrawWireSphere(transform.position, 0.3f);
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 0.5f);
    }
#endif
}