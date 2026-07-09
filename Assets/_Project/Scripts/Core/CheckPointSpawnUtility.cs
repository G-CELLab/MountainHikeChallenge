using System.Linq;
using UnityEngine;

/// <summary>
/// Shared lookup for finding the spawn point matching the current trail
/// checkpoint. Used by both PlayerRigPositioner and AIGuidePositioner so the
/// matching rule lives in exactly one place.
///
/// Per project decision: if no spawn point exactly matches the current
/// checkpoint, this logs an error and returns null rather than guessing —
/// callers should NOT move their rig/guide in that case, so a missing spawn
/// point is loud and obvious during playtesting instead of silently
/// stranding the player at the wrong spot.
/// </summary>
public static class CheckpointSpawnUtility
{
    public static T FindExactMatch<T>(int checkpoint, string contextLabel) where T : MonoBehaviour, ICheckpointSpawnPoint
    {
        // Sort order doesn't matter here — we're scanning for an exact
        // checkpoint match, not relying on array order — so use the cheaper
        // unsorted mode rather than paying for a sort we don't need.
        // Use the non-obsolete overload. We don't need any specific sort order
        // or inactive-object behavior here, so the parameterless overload is
        // appropriate.
        T[] all = Object.FindObjectsByType<T>();

        if (all.Length == 0)
        {
            Debug.LogError($"[CheckpointSpawnUtility] No {typeof(T).Name} found in scene — {contextLabel}.");
            return null;
        }

        foreach (T candidate in all)
        {
            if (candidate.CheckpointIndex == checkpoint)
                return candidate;
        }

        string foundIndices = string.Join(", ", all.Select(c => c.CheckpointIndex));
        Debug.LogError($"[CheckpointSpawnUtility] No {typeof(T).Name} configured for checkpoint {checkpoint} " +
                        $"— {contextLabel}. Found indices in scene: [{foundIndices}]. " +
                        $"Add a {typeof(T).Name} with Checkpoint Index = {checkpoint}, or fix an existing one's index.");
        return null;
    }
}