using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Deforms a muscle fiber model into a fusiform (spindle) shape as it
/// contracts — thin/tapered at both ends, bulging widest in the middle,
/// matching how a real muscle belly looks when flexed.
///
/// Built for models made of several separate child meshes (e.g. this
/// asset's Blood_Outer / Main / Main_inner / Red Cells / WhiteStuff
/// hierarchy) rather than a single mesh on one object. Attach this to the
/// PARENT object — the one with all the sub-mesh children under it — not
/// to any individual child. It automatically finds every MeshFilter
/// underneath it and deforms them all together using ONE shared axis
/// center and length, computed across all of them combined, so the whole
/// model bulges as a single coherent shape instead of each sub-mesh
/// bulging independently around its own center.
///
/// Handles child meshes that are offset/rotated relative to the parent by
/// transforming each vertex into the parent's local space before doing the
/// axis math, then transforming the result back into that child's own
/// local space before writing it to that child's mesh.
///
/// REQUIRES: every mesh being deformed needs "Read/Write Enabled" checked
/// in its Model import settings, or Unity throws isReadable errors.
///
/// PERFORMANCE NOTE: deforms every vertex on the CPU every frame, across
/// every sub-mesh, including RecalculateNormals() so lighting stays
/// correct. Fine for prototyping; if multiple fibers are active at once in
/// VR and framerate dips, this same math should move to a vertex shader
/// (Shader Graph, driven by one exposed float) so the GPU does the work.
///
/// NEXT STEP (not built yet): once hand controls exist, set
/// driveAutomatically to false and call SetContraction() from a grab
/// script based on hand distance instead of the automatic Update() loop.
/// </summary>
public class MuscleFiberBellyContract : MonoBehaviour
{
    private enum LongAxis { X, Y, Z }

    private class SubMesh
    {
        public MeshFilter filter;
        public Mesh mesh;
        public Vector3[] originalLocal;
        public Vector3[] working;
        public Matrix4x4 childToRoot;
        public Matrix4x4 rootToChild;
    }

    [Header("Fiber Orientation (in this object's own local space)")]
    [Tooltip("Which local axis of THIS object runs along the fiber's length.")]
    [SerializeField] private LongAxis longAxis = LongAxis.Z;

    [Header("Mesh Targets")]
    [Tooltip("If empty, every MeshFilter found in children is used automatically. Assign specific MeshFilters here instead if you want to exclude any part (e.g. leave Red Cells out of the bulge).")]
    [SerializeField] private List<MeshFilter> explicitTargets = new List<MeshFilter>();

    [Header("Automatic Test Loop")]
    [Tooltip("While true, this script drives the contraction on its own via a looping sine wave — turn off once a real grab script calls SetContraction() instead.")]
    [SerializeField] private bool driveAutomatically = true;

    [Tooltip("Seconds for one full contract-and-relax cycle.")]
    [SerializeField] private float cycleDuration = 2f;

    [Header("Belly Shape")]
    [Tooltip("How much wider the center gets at full contraction. 1 = no bulge, 1.5 = center is 50% wider than resting radius at peak contraction.")]
    [Range(1f, 2f)]
    [SerializeField] private float maxBellyBulge = 1.4f;

    [Tooltip("Overall length multiplier at full contraction. Keep this fairly mild — the belly bulge does most of the visual work.")]
    [Range(0.8f, 1f)]
    [SerializeField] private float contractedLengthMultiplier = 0.92f;

    [Tooltip("Higher values keep the taper narrower near the very ends, closer to a sharp point. Lower values spread the bulge more evenly across the whole length. IMPORTANT: if the mesh's own geometry already tapers to a thin point near its ends (common on fusiform/spindle-shaped models), keep this HIGH (4) — a low value lets the bulge push outward even where the geometry is already very thin, which can fold the tip's triangles over on themselves and produce a blown-out white specular artifact near the ends. Integer only — non-integer exponents here caused floating point issues right at the tips.")]
    [Range(0, 4)]
    [SerializeField] private int taperSharpness = 4;

    [Header("Normals (shading)")]
    [Tooltip("If false, skips per-frame normal recalculation entirely — cheaper, and useful for isolating whether normal recalculation is the source of a shading artifact (e.g. a blown-out bright spot on thin geometry). With this off, lighting is based on the mesh's original imported normals and won't visually respond to the bulge, but the base shading will look correct.")]
    [SerializeField] private bool recalculateNormals = true;

    [Tooltip("Smoothing angle (degrees) used when recalculating normals. Unity's default is 60. Lower values keep more edges 'hard' (less blending between neighboring faces) — try lowering this if thin/narrow geometry (like a small stub or connector) is producing a bright blown-out highlight, since that usually means normals are being over-blended across a sharp transition.")]
    [Range(1f, 180f)]
    [SerializeField] private float normalSmoothingAngle = 60f;

    private readonly List<SubMesh> _subMeshes = new List<SubMesh>();
    private float _axisCenter;   // in this object's local space
    private float _axisHalfSpan; // in this object's local space
    private float _currentContraction;

    private void Awake()
    {
        var filters = explicitTargets.Count > 0
            ? explicitTargets
            : new List<MeshFilter>(GetComponentsInChildren<MeshFilter>(true));

        if (filters.Count == 0)
        {
            Debug.LogWarning($"[MuscleFiberBellyContract] No MeshFilters found under {name} — nothing to deform.");
            return;
        }

        Vector3 axisDir = AxisVector((int)longAxis);
        float min = float.MaxValue, max = float.MinValue;

        foreach (var filter in filters)
        {
            var mesh = filter.mesh; // clones per-instance, never touches the shared asset
            var original = mesh.vertices;

            var sub = new SubMesh
            {
                filter = filter,
                mesh = mesh,
                originalLocal = original,
                working = new Vector3[original.Length],
                childToRoot = transform.worldToLocalMatrix * filter.transform.localToWorldMatrix,
            };
            sub.rootToChild = sub.childToRoot.inverse;

            for (int i = 0; i < original.Length; i++)
            {
                Vector3 rootSpacePos = sub.childToRoot.MultiplyPoint3x4(original[i]);
                float a = Vector3.Dot(rootSpacePos, axisDir);
                if (a < min) min = a;
                if (a > max) max = a;
            }

            _subMeshes.Add(sub);
        }

        _axisCenter = (min + max) * 0.5f;
        _axisHalfSpan = Mathf.Max(0.0001f, (max - min) * 0.5f);
    }

    private void Update()
    {
        if (!driveAutomatically) return;

        float t = (Mathf.Sin(Time.time * (2f * Mathf.PI / cycleDuration) - Mathf.PI / 2f) + 1f) * 0.5f;
        SetContraction(t);
    }

    /// <summary>0 = relaxed, 1 = fully contracted. Deforms every tracked sub-mesh together.</summary>
    public void SetContraction(float amount)
    {
        _currentContraction = Mathf.Clamp01(amount);

        Vector3 axisDir = AxisVector((int)longAxis);
        Vector3 centerOffset = axisDir * _axisCenter;
        float lengthScale = Mathf.Lerp(1f, contractedLengthMultiplier, _currentContraction);
        float bulgeAtCenter = Mathf.Lerp(1f, maxBellyBulge, _currentContraction);

        foreach (var sub in _subMeshes)
        {
            for (int i = 0; i < sub.originalLocal.Length; i++)
            {
                Vector3 rootPos = sub.childToRoot.MultiplyPoint3x4(sub.originalLocal[i]);

                float axisPos = Vector3.Dot(rootPos - centerOffset, axisDir);
                Vector3 radial = (rootPos - centerOffset) - axisDir * axisPos;

                // Normalized -1..1 position along the fiber, using the
                // SHARED axis span computed across all sub-meshes in Awake
                // — this is what keeps every part bulging in sync.
                float t = Mathf.Clamp(axisPos / _axisHalfSpan, -1f, 1f);
                float taper = Mathf.Pow(Mathf.Cos(t * Mathf.PI / 2f), (float)taperSharpness);
                float radialScale = Mathf.Lerp(1f, bulgeAtCenter, taper * _currentContraction);

                Vector3 deformedRoot = centerOffset + axisDir * (axisPos * lengthScale) + radial * radialScale;
                sub.working[i] = sub.rootToChild.MultiplyPoint3x4(deformedRoot);
            }

            sub.mesh.vertices = sub.working;
            if (recalculateNormals)
                sub.mesh.RecalculateNormals((UnityEngine.Rendering.MeshUpdateFlags)normalSmoothingAngle);
            sub.mesh.RecalculateBounds();
        }
    }

    private static Vector3 AxisVector(int axisIndex) => axisIndex switch
    {
        0 => Vector3.right,
        1 => Vector3.up,
        _ => Vector3.forward,
    };

    public float CurrentContraction => _currentContraction;
}