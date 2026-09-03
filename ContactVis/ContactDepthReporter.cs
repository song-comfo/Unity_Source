using UnityEngine;

/// <summary>
/// Numeric contact measurement for a ContactPenetrationVisualizer pair.
/// Samples every vertex of object A (the body) against object B's signed SDF and
/// logs penetration depth (max/avg/min), contact area, pressure and total load
/// to the console whenever the values change. Also shown in the Inspector.
/// Area is integrated on object A's surface (each vertex represents 1/3 of the
/// area of its adjacent triangles).
/// </summary>
[ExecuteAlways]
public class ContactDepthReporter : MonoBehaviour
{
    [Tooltip("The visualizer pair to measure (object A verts vs object B SDF)")]
    public ContactPenetrationVisualizer pair;

    [Tooltip("Seconds between measurements")]
    [Range(0.1f, 2f)] public float interval = 0.5f;

    [Tooltip("Provisional pressure model: p = k x depth (kPa per mm)")]
    public float pressureKPaPerMm = 1.5f;

    [Tooltip("Write a console log line whenever the measurement changes")]
    public bool logToConsole = true;

    [Header("Last Measurement (read-only)")]
    public int contactVertexCount;
    public float maxDepthMm;
    public float avgDepthMm;
    public float minDepthMm;
    public float contactAreaCm2;
    public float avgPressureKPa;
    public float maxPressureKPa;
    public float totalLoadN;

    private float _nextTime;
    private int _lastLogHash;

    void Update()
    {
        if (pair == null) return;
        float now = Time.realtimeSinceStartup;
        if (now < _nextTime) return;
        _nextTime = now + interval;
        Measure();
    }

    [ContextMenu("Measure Now")]
    public void Measure()
    {
        var a = pair.objectA;
        var b = pair.objectB;
        if (a == null || b == null || a.root == null || b.root == null) return;
        var tex = b.sdfTex as Texture3D;
        if (tex == null) return; // not baked yet

        // --- object B SDF sampling setup ---
        Matrix4x4 w2lB = b.root.transform.worldToLocalMatrix;
        Vector3 c = b.bakeCenter, s = b.bakeSize;
        Vector3 lsB = b.root.transform.lossyScale;
        float scaleB = Mathf.Max(s.x, Mathf.Max(s.y, s.z)) *
                       Mathf.Max(Mathf.Abs(lsB.x), Mathf.Max(Mathf.Abs(lsB.y), Mathf.Abs(lsB.z)));

        int count = 0;
        double sumDepth = 0, sumArea = 0, sumLoad = 0;
        float maxDepth = 0f, minDepth = float.MaxValue, maxP = 0f;

        Renderer[] rends = (a.renderers != null && a.renderers.Length > 0)
            ? a.renderers
            : a.root.GetComponentsInChildren<Renderer>(true);

        foreach (var r in rends)
        {
            if (r == null) continue;
            Mesh mesh = null;
            bool temp = false;
            var smr = r as SkinnedMeshRenderer;
            if (smr != null && smr.sharedMesh != null)
            {
                mesh = new Mesh();
                smr.BakeMesh(mesh, true); // current pose
                temp = true;
            }
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null) mesh = mf.sharedMesh;
            }
            if (mesh == null) continue;

            Vector3[] verts = mesh.vertices;
            Matrix4x4 l2w = r.transform.localToWorldMatrix;

            // Per-vertex representative area: 1/3 of each adjacent triangle (world space).
            var vertArea = new float[verts.Length];
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                int[] tris = mesh.GetTriangles(sm);
                for (int t = 0; t < tris.Length; t += 3)
                {
                    Vector3 p0 = l2w.MultiplyPoint3x4(verts[tris[t]]);
                    Vector3 p1 = l2w.MultiplyPoint3x4(verts[tris[t + 1]]);
                    Vector3 p2 = l2w.MultiplyPoint3x4(verts[tris[t + 2]]);
                    float third = Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5f / 3f;
                    vertArea[tris[t]] += third;
                    vertArea[tris[t + 1]] += third;
                    vertArea[tris[t + 2]] += third;
                }
            }

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 pw = l2w.MultiplyPoint3x4(verts[i]);
                Vector3 lp = w2lB.MultiplyPoint3x4(pw);
                float ux = (lp.x - c.x) / s.x + 0.5f;
                float uy = (lp.y - c.y) / s.y + 0.5f;
                float uz = (lp.z - c.z) / s.z + 0.5f;
                if (ux < 0.001f || ux > 0.999f || uy < 0.001f || uy > 0.999f ||
                    uz < 0.001f || uz > 0.999f) continue; // outside SDF box = no contact
                float d = tex.GetPixelBilinear(ux, uy, uz).r * scaleB;
                if (d >= 0f) continue; // not inside object B

                float depthMm = -d * 1000f;
                float area = vertArea[i]; // m^2
                float pKPa = pressureKPaPerMm * depthMm;

                count++;
                sumDepth += depthMm;
                sumArea += area;
                sumLoad += pKPa * 1000.0 * area; // kPa * m^2 = kN -> N
                if (depthMm > maxDepth) { maxDepth = depthMm; maxP = pKPa; }
                if (depthMm < minDepth) minDepth = depthMm;
            }

            if (temp) DestroyImmediate(mesh);
        }

        contactVertexCount = count;
        if (count == 0)
        {
            maxDepthMm = avgDepthMm = minDepthMm = 0f;
            contactAreaCm2 = avgPressureKPa = maxPressureKPa = totalLoadN = 0f;
        }
        else
        {
            maxDepthMm = maxDepth;
            avgDepthMm = (float)(sumDepth / count);
            minDepthMm = minDepth;
            contactAreaCm2 = (float)(sumArea * 10000.0); // m^2 -> cm^2
            avgPressureKPa = pressureKPaPerMm * avgDepthMm;
            maxPressureKPa = maxP;
            totalLoadN = (float)sumLoad;
        }

        if (!logToConsole) return;

        // Log only when the (rounded) measurement actually changes.
        int h = 17;
        h = h * 31 + Mathf.RoundToInt(maxDepthMm * 10f);
        h = h * 31 + Mathf.RoundToInt(avgDepthMm * 10f);
        h = h * 31 + Mathf.RoundToInt(minDepthMm * 10f);
        h = h * 31 + Mathf.RoundToInt(contactAreaCm2);
        h = h * 31 + Mathf.RoundToInt(totalLoadN);
        if (h == _lastLogHash) return;
        _lastLogHash = h;

        if (count == 0)
        {
            Debug.Log("[ContactVis] 접촉 없음");
        }
        else
        {
            Debug.Log(string.Format(
                "[ContactVis] 침투 깊이: 최대 {0:F1} / 평균 {1:F1} / 최소 {2:F1} mm | " +
                "접촉 면적: {3:F1} cm² | 압력: 평균 {4:F1} / 최대 {5:F1} kPa | " +
                "압박도(총 하중): {6:F1} N  (접촉 버텍스 {7}개)",
                maxDepthMm, avgDepthMm, minDepthMm,
                contactAreaCm2, avgPressureKPa, maxPressureKPa, totalLoadN, count));
        }
    }
}
