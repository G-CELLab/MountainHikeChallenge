using UnityEngine;

/// <summary>
/// Marks the spot where the AI guide should stand/teleport to when this
/// scene loads. Place exactly one of these in every gameplay scene, positioned
/// and rotated per scene_design.md (e.g. beside the student at the trailhead,
/// beside the heart in the Circulatory scene).
///
/// AIGuidePositioner (on the persistent AI agent) looks for this on every
/// scene load and moves the guide here.
/// </summary>
public class AIGuideSpawnPoint : MonoBehaviour
{
#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        Gizmos.color = Color.magenta;
        Gizmos.DrawWireSphere(transform.position, 0.3f);
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 0.5f);
    }
#endif
}