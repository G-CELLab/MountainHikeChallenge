using UnityEngine;

/// <summary>
/// Marks where the player should end up after this scene's auto-walk
/// segment completes — symmetric to PlayerSpawnPoint (Start).
///
/// One PlayerSpawnPoint + one PlayerEndPoint per scene gives you an explicit,
/// labeled Start/End pair for all 5 scenes (Trailhead, Circulatory,
/// Respiratory, Digestive, Summit) rather than relying on "whichever
/// Transform happens to be last in the waypoints array."
///
/// Usage: drag this object's Transform into the scene's AutoWalkController
/// as the final entry in its waypoints array. The walk will end here, and
/// this is also the position to check against for triggering the next
/// scene's transition (portal trigger, mini-game start zone, etc.).
///
/// Deliberately NOT meant to represent "the rest of the mountain" — the
/// distance between a scene's Start and End is a short, real, walkable
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