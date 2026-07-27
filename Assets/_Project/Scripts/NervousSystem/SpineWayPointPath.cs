using UnityEngine;

/// <summary>
/// Holds an ordered list of empty-GameObject markers placed by hand along
/// the new nervous system model's spine, from neck (index 0) to tailbone
/// (last index). Since that model isn't rigged, there are no bones to pull
/// a path from automatically — this is the practical alternative: drop a
/// handful of empty GameObjects as children of the spine mesh, drag them
/// into position in the Scene view following the actual curve, assign them
/// here in order, and pass this component straight into
/// SpineSignalPulse.FirePulse(SpineWaypointPath, ...).
///
/// Because the markers are parented under the spine, they move, rotate,
/// and scale with it automatically — reposition the whole model later and
/// the path still lines up with no extra work.
///
/// Draws gizmo lines + spheres in the Scene view (Editor only, no runtime
/// cost) so you can see exactly what curve the pulse will follow while
/// you're still placing the markers, before ever hitting Play.
/// </summary>
public class SpineWaypointPath : MonoBehaviour
{
    [Tooltip("Ordered from neck (index 0) to tailbone (last index). 4-8 markers is usually enough to follow a spine's curve convincingly once smoothed.")]
    [SerializeField] private Transform[] waypoints;

    [Header("Gizmo Preview (Scene view only)")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private Color gizmoColor = new Color(0.3f, 0.8f, 1f);
    [SerializeField] private float gizmoSphereRadius = 0.02f;

    public Transform[] Waypoints => waypoints;

    private void OnDrawGizmos()
    {
        if (!drawGizmos || waypoints == null || waypoints.Length == 0) return;

        Gizmos.color = gizmoColor;

        for (int i = 0; i < waypoints.Length; i++)
        {
            if (waypoints[i] == null) continue;

            Gizmos.DrawSphere(waypoints[i].position, gizmoSphereRadius);

            if (i < waypoints.Length - 1 && waypoints[i + 1] != null)
                Gizmos.DrawLine(waypoints[i].position, waypoints[i + 1].position);
        }
    }
}