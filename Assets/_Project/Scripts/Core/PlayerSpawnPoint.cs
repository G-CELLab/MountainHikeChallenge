using UnityEngine;

/// <summary>
/// Marks where the player's XR rig should be placed when this scene loads.
/// One per scene — mirrors AIGuideSpawnPoint's role for the AI guide, but
/// for the player. PlayerRigPositioner finds this on scene load and moves
/// the rig here.
///
/// Place one of these in each scene (Trailhead, Circulatory, Respiratory,
/// Digestive, Summit) at the spot the player should appear. The scene's
/// AutoWalkController waypoint chain should typically start at (or near)
/// this same position and end wherever the player should walk to.
/// </summary>
public class PlayerSpawnPoint : MonoBehaviour
{
#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position, 0.3f);
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 0.5f);
    }
#endif
}