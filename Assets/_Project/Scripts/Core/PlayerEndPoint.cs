using UnityEngine;

/// <summary>
/// Marks where the player should end up after a trail-checkpoint's auto-walk
/// segment completes — symmetric to PlayerSpawnPoint (Start).
///
/// A scene like _MountainTrail has one PlayerEndPoint per checkpoint (e.g.
/// EndPoint_0, EndPoint_1, ...), mirroring PlayerSpawnPoint's own numbering —
/// each checkpoint gets its own explicit, labeled Start/End pair rather than
/// relying on "whichever Transform happens to be last in the waypoints array."
/// A scene only ever visited at one checkpoint just needs a single EndPoint.
///
/// Usage: drag this object's Transform in as the LAST entry of that
/// checkpoint's waypoint chain on AutoWalkController — either the flat
/// `waypoints` field (single-segment scenes) or that checkpoint's entry in
/// `checkpointPaths` (multi-checkpoint scenes like _MountainTrail). The walk
/// will end here, and this is also the position to check against for
/// triggering the next scene's transition (portal trigger, mini-game start
/// zone, etc.).
///
/// Deliberately NOT meant to represent "the rest of the mountain" — the
/// distance between a checkpoint's Start and End is a short, real, walkable
/// segment. The big jumps up the mountain happen invisibly between scenes,
/// via teleporting straight to the next scene's PlayerSpawnPoint.
/// </summary>
public class PlayerEndPoint : MonoBehaviour
{
#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, 0.3f);
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 0.5f);
    }
#endif
}