/// <summary>
/// Implemented by any per-scene spawn point that varies by trail checkpoint
/// (PlayerSpawnPoint, AIGuideSpawnPoint). Lets CheckpointSpawnUtility find
/// the right one without duplicating matching logic in each positioner.
/// </summary>
public interface ICheckpointSpawnPoint
{
    /// <summary>
    /// Which MiniGameSequencer.TrailCheckpointIndex this spawn point corresponds to.
    /// 0 = the very first arrival in this scene, 1 = the next time the player/guide
    /// returns here, etc.
    /// </summary>
    int CheckpointIndex { get; }
}