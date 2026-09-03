using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.VFX.SDF;

/// <summary>
/// Visualizes the contact/penetration region between an ARBITRARY pair of objects.
/// For each object (slot A / slot B) all meshes under its root are combined and baked
/// into a 3D SDF (focused around the OTHER object so voxel resolution concentrates
/// where contact happens). The sign is recomputed with a voxel flood fill and the
/// surface is smoothed, so broken normals / non-watertight meshes work correctly.
/// Each object's material then samples the OTHER object's SDF and colors fragments
/// by penetration depth. Skinned meshes are snapshotted per pose and automatically
/// rebaked when the armature pose or the relative placement changes.
/// </summary>
[ExecuteAlways]
public class ContactPenetrationVisualizer : MonoBehaviour
{
    [System.Serializable]
    public class SdfEntry
    {
        [Tooltip("Root of the object; all child meshes are combined for the SDF")]
        public GameObject root;
        [Tooltip("Optional: bake only these renderers (e.g., exclude a cable). Empty = all children")]
        public Renderer[] renderers;
        [Tooltip("Material used by THIS object's renderers; it receives the OTHER object's SDF data")]
        public Material material;
        [Range(16, 256)] public int sdfMaxResolution = 256;
        public float boundsPadding = 1.15f;
        [Tooltip("Extra thickness (in voxels) of the surface band used to seal the mesh during the flood fill")]
        [Range(1f, 3f)] public float surfaceBandVoxels = 1.05f;

        [System.NonSerialized] public Texture3D sdfTex;
        [System.NonSerialized] public Mesh combined;
        [System.NonSerialized] public Vector3 bakeCenter;
        [System.NonSerialized] public Vector3 bakeSize;
        [System.NonSerialized] public int poseHash;
        [System.NonSerialized] public int relHash;
        [System.NonSerialized] public bool poseDirty;
        [System.NonSerialized] public float lastPoseChangeTime;
    }

    [Header("Object A (e.g., body)")]
    public SdfEntry objectA = new SdfEntry();

    [Header("Object B (e.g., product)")]
    public SdfEntry objectB = new SdfEntry();

    [Header("Bake Focus")]
    [Tooltip("Bake each SDF only around the OTHER object (margin in meters). Concentrates voxel resolution where contact happens; 0 = always bake the full mesh")]
    public float focusMargin = 0.3f;

    [Header("Auto Rebake")]
    [Tooltip("Automatically rebake when the skinned pose (armature) or the relative placement changes")]
    public bool autoRebakeOnPoseChange = true;
    [Tooltip("Seconds the pose must stay still before the rebake fires")]
    [Range(0.1f, 2f)] public float poseSettleDelay = 0.4f;

    void OnEnable() { BakeAll(); }
    void OnDisable() { Release(objectA); Release(objectB); }

    [ContextMenu("Rebake SDFs")]
    public void BakeAll()
    {
        Bake(objectA, objectB);
        Bake(objectB, objectA);
    }

    /// <summary>Public entry point for external tools.</summary>
    public void RefreshMaterials() { PushShaderGlobals(); }

    private void Bake(SdfEntry e, SdfEntry other)
    {
        Release(e);
        if (e == null || e.root == null) return;

        Mesh mesh = BuildCombinedMesh(e);
        if (mesh == null || mesh.vertexCount == 0) return;
        e.combined = mesh;

        Bounds b = mesh.bounds;
        // Focus the bake box on the region around the OTHER object, so large bodies
        // keep fine voxels where the contact actually happens.
        if (focusMargin > 0.001f && other != null && other.root != null)
        {
            // Renderer.bounds is UNRELIABLE for glTF skinned meshes (stale bind-pose
            // localBounds under exotic node transforms) and can clip the focus box so
            // that parts of THIS object (e.g. a backrest cushion) end up outside the
            // SDF volume. Compute the other's true world bounds from its posed,
            // combined mesh instead.
            bool got = false; Bounds wb = new Bounds();
            Mesh om = BuildCombinedMesh(other);
            if (om != null && om.vertexCount > 0)
            {
                Bounds ob = om.bounds; // in other-root local space
                Matrix4x4 ol2w = other.root.transform.localToWorldMatrix;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 oc = new Vector3(
                        (k & 1) == 0 ? ob.min.x : ob.max.x,
                        (k & 2) == 0 ? ob.min.y : ob.max.y,
                        (k & 4) == 0 ? ob.min.z : ob.max.z);
                    Vector3 ow = ol2w.MultiplyPoint3x4(oc);
                    if (!got) { wb = new Bounds(ow, Vector3.zero); got = true; } else wb.Encapsulate(ow);
                }
            }
            if (om != null) DestroyImmediate(om);
            if (got)
            {
                wb.Expand(focusMargin * 2f);
                Matrix4x4 w2l = e.root.transform.worldToLocalMatrix;
                Vector3 mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int k = 0; k < 8; k++)
                {
                    Vector3 c = new Vector3(
                        (k & 1) == 0 ? wb.min.x : wb.max.x,
                        (k & 2) == 0 ? wb.min.y : wb.max.y,
                        (k & 4) == 0 ? wb.min.z : wb.max.z);
                    Vector3 l = w2l.MultiplyPoint3x4(c);
                    mn = Vector3.Min(mn, l); mx = Vector3.Max(mx, l);
                }
                Vector3 imin = Vector3.Max(b.min, mn);
                Vector3 imax = Vector3.Min(b.max, mx);
                if (imin.x < imax.x && imin.y < imax.y && imin.z < imax.z)
                    b = new Bounds((imin + imax) * 0.5f, imax - imin);
                // no overlap: keep the full mesh bounds
            }
        }
        e.relHash = ComputeRelHash(e, other);

        e.bakeSize = b.size * e.boundsPadding;
        float maxSide = Mathf.Max(e.bakeSize.x, Mathf.Max(e.bakeSize.y, e.bakeSize.z));
        if (maxSide <= 0f) return;
        e.bakeSize = Vector3.Max(e.bakeSize, Vector3.one * (maxSide * 0.1f)); // avoid overly thin boxes
        e.bakeCenter = b.center;

        MeshToSDFBaker baker = null;
        try
        {
            baker = new MeshToSDFBaker(e.bakeSize, e.bakeCenter, e.sdfMaxResolution, mesh);
            baker.BakeSDF();
            e.sdfTex = BuildSignedTexture(baker.SdfTexture, e);
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[ContactVis] SDF bake failed for '" + e.root.name + "': " + ex.Message);
        }
        finally
        {
            if (baker != null) baker.Dispose();
        }
    }

    /// <summary>
    /// Rebuilds a reliably SIGNED distance texture from the baker output:
    /// keep |distance|, flood-fill "outside" from the box boundary (blocked by a thin
    /// surface band), negate everything unreachable (= inside), then smooth the
    /// near-surface field so the zero-crossing renders as clean lines.
    /// </summary>
    private Texture3D BuildSignedTexture(RenderTexture src, SdfEntry e)
    {
        int W = src.width, H = src.height, D = src.volumeDepth;
        var req = AsyncGPUReadback.Request(src);
        req.WaitForCompletion();
        if (req.hasError)
        {
            Debug.LogError("[ContactVis] SDF readback failed");
            return null;
        }

        int n = W * H * D;
        var vals = new float[n];
        var raw = new float[n]; // baker's signed value (unreliable near bad geometry, good far from the surface)
        for (int z = 0; z < D; z++)
        {
            var slice = req.GetData<ushort>(z);
            int off = z * W * H;
            for (int i = 0; i < W * H; i++)
            {
                float rv = Mathf.HalfToFloat(slice[i]);
                raw[off + i] = rv;
                vals[off + i] = Mathf.Abs(rv);
            }
        }

        // Distances are normalized by the largest box side.
        float maxSide = Mathf.Max(e.bakeSize.x, Mathf.Max(e.bakeSize.y, e.bakeSize.z));
        float voxel = Mathf.Max(e.bakeSize.x / W, Mathf.Max(e.bakeSize.y / H, e.bakeSize.z / D)) / maxSide;
        float band = e.surfaceBandVoxels * voxel;

        // 0 = unknown (-> inside), 1 = surface band (wall), 2 = outside
        var state = new byte[n];
        for (int i = 0; i < n; i++) if (vals[i] < band) state[i] = 1;

        int WH = W * H;
        // TWO-PHASE outside flood. Real furniture is full of hollow shells (a seat
        // cushion is a closed hollow box) with small openings; a single-threshold
        // flood squeezes through those openings, marks the hollow "outside" and the
        // part collapses to a paper-thin skin. Phase A floods only through genuinely
        // OPEN space (>= openClear from any surface), so it cannot enter cavities
        // through small gaps. Phase B then grows a bounded shell (a few voxels) from
        // phase-A air through near-surface space, so signs right next to exterior
        // surfaces stay correct - without reaching deep inside hollows.
        float openClear = band * 3f;
        int growSteps = Mathf.CeilToInt(e.surfaceBandVoxels * 3f) + 4;

        var queue = new Queue<int>(n / 8);
        int seeds = 0;
        for (int z = 0; z < D; z++)
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (x != 0 && x != W - 1 && y != 0 && y != H - 1 && z != 0 && z != D - 1) continue;
                    int idx = z * WH + y * W + x;
                    if (state[idx] == 0 && raw[idx] > 0f && vals[idx] >= openClear) { state[idx] = 2; queue.Enqueue(idx); seeds++; }
                }
        if (seeds == 0)
        {
            // Degenerate case (tiny box or globally flipped baker signs): fall back
            // to seeding every non-band boundary voxel.
            for (int z = 0; z < D; z++)
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        if (x != 0 && x != W - 1 && y != 0 && y != H - 1 && z != 0 && z != D - 1) continue;
                        int idx = z * WH + y * W + x;
                        if (state[idx] == 0) { state[idx] = 2; queue.Enqueue(idx); }
                    }
        }

        // Phase A: open-air flood (only through comfortably-clear voxels).
        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int x = idx % W;
            int y = (idx / W) % H;
            int z = idx / WH;
            if (x > 0) TryFloodOpen(idx - 1, state, vals, openClear, queue);
            if (x < W - 1) TryFloodOpen(idx + 1, state, vals, openClear, queue);
            if (y > 0) TryFloodOpen(idx - W, state, vals, openClear, queue);
            if (y < H - 1) TryFloodOpen(idx + W, state, vals, openClear, queue);
            if (z > 0) TryFloodOpen(idx - WH, state, vals, openClear, queue);
            if (z < D - 1) TryFloodOpen(idx + WH, state, vals, openClear, queue);
        }

        // Phase B: bounded growth from open air through near-surface space, so the
        // few voxels hugging an exterior surface also read "outside". The step limit
        // keeps it from worming deep into hollows through small openings.
        var frontier = new List<int>();
        for (int i = 0; i < n; i++) if (state[i] == 2) frontier.Add(i);
        for (int step = 0; step < growSteps && frontier.Count > 0; step++)
        {
            var next = new List<int>();
            foreach (int fi in frontier)
            {
                int x = fi % W;
                int y = (fi / W) % H;
                int z = fi / WH;
                if (x > 0) TryGrow(fi - 1, state, next);
                if (x < W - 1) TryGrow(fi + 1, state, next);
                if (y > 0) TryGrow(fi - W, state, next);
                if (y < H - 1) TryGrow(fi + W, state, next);
                if (z > 0) TryGrow(fi - WH, state, next);
                if (z < D - 1) TryGrow(fi + WH, state, next);
            }
            frontier = next;
        }

        // Band voxels keep their |d| but get signs by adjacency: POSITIVE only if
        // touching outside and not inside - gives thin closed-off features a proper
        // negative core (cables, base plates).
        var sign = new sbyte[n];
        for (int i = 0; i < n; i++)
        {
            if (state[i] != 1) { sign[i] = state[i] == 2 ? (sbyte)1 : (sbyte)-1; continue; }
            bool nearInside = false, nearOutside = false;
            int x = i % W, y = (i / W) % H, z = i / WH;
            if (x > 0 && state[i - 1] != 1) { if (state[i - 1] == 0) nearInside = true; else nearOutside = true; }
            if (x < W - 1 && state[i + 1] != 1) { if (state[i + 1] == 0) nearInside = true; else nearOutside = true; }
            if (y > 0 && state[i - W] != 1) { if (state[i - W] == 0) nearInside = true; else nearOutside = true; }
            if (y < H - 1 && state[i + W] != 1) { if (state[i + W] == 0) nearInside = true; else nearOutside = true; }
            if (z > 0 && state[i - WH] != 1) { if (state[i - WH] == 0) nearInside = true; else nearOutside = true; }
            if (z < D - 1 && state[i + WH] != 1) { if (state[i + WH] == 0) nearInside = true; else nearOutside = true; }
            sign[i] = (nearOutside && !nearInside) ? (sbyte)1 : (sbyte)-1;
        }

        // signed field
        var f = new float[n];
        for (int i = 0; i < n; i++) f[i] = sign[i] < 0 ? -vals[i] : vals[i];

        // Two light smoothing passes near the surface: the band's per-voxel sign
        // decisions leave a jittery zero-crossing; averaging irons the surface so
        // contact boundaries render as clean lines instead of speckle.
        float nearBand = band * 3f;
        var g = new float[n];
        for (int pass = 0; pass < 2; pass++)
        {
            System.Array.Copy(f, g, n);
            for (int z = 1; z < D - 1; z++)
                for (int y = 1; y < H - 1; y++)
                {
                    int rowBase = z * WH + y * W;
                    for (int x = 1; x < W - 1; x++)
                    {
                        int i = rowBase + x;
                        float v = f[i];
                        if (v > nearBand || v < -nearBand) continue;
                        float avg = (f[i - 1] + f[i + 1] + f[i - W] + f[i + W] + f[i - WH] + f[i + WH]) * (1f / 6f);
                        g[i] = v * 0.4f + avg * 0.6f;
                    }
                }
            var tmpArr = f; f = g; g = tmpArr;
        }

        var half = new ushort[n];
        for (int i = 0; i < n; i++) half[i] = Mathf.FloatToHalf(f[i]);

        var tex = new Texture3D(W, H, D, TextureFormat.RHalf, false)
        {
            name = "ContactVis_SignedSDF",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };
        tex.SetPixelData(half, 0);
        tex.Apply(false, false); // keep CPU copy readable for tools/measurement
        return tex;
    }

    private static void TryFloodOpen(int idx, byte[] state, float[] vals, float openClear, Queue<int> queue)
    {
        if (state[idx] == 0 && vals[idx] >= openClear) { state[idx] = 2; queue.Enqueue(idx); }
    }

    private static void TryGrow(int idx, byte[] state, List<int> next)
    {
        if (state[idx] == 0) { state[idx] = 2; next.Add(idx); }
    }

    /// <summary>Combine every mesh under the root into ONE mesh in the ROOT's local space.</summary>
    private Mesh BuildCombinedMesh(SdfEntry e)
    {
        Renderer[] rends = (e.renderers != null && e.renderers.Length > 0)
            ? e.renderers
            : e.root.GetComponentsInChildren<Renderer>(true);

        Matrix4x4 rootW2L = e.root.transform.worldToLocalMatrix;
        var combines = new List<CombineInstance>();
        var temp = new List<Mesh>();

        foreach (var r in rends)
        {
            if (r == null) continue;
            var smr = r as SkinnedMeshRenderer;
            if (smr != null && smr.sharedMesh != null)
            {
                var snap = new Mesh();
                smr.BakeMesh(snap, true); // current pose snapshot
                temp.Add(snap);
                // Full localToWorldMatrix: glTF skinned nodes can carry non-identity
                // scale (e.g. 0.01) + rotation; BakeMesh output maps to world via l2w.
                // One CombineInstance per submesh - multi-material skins otherwise
                // silently drop everything but submesh 0.
                for (int s = 0; s < snap.subMeshCount; s++)
                    combines.Add(new CombineInstance
                    {
                        mesh = snap,
                        subMeshIndex = s,
                        transform = rootW2L * r.transform.localToWorldMatrix
                    });
            }
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    // CombineInstance bakes ONE submesh; multi-material meshes
                    // (e.g. frame + cushions inside one GLB mesh) need every submesh,
                    // or parts silently vanish from the SDF.
                    for (int s = 0; s < mf.sharedMesh.subMeshCount; s++)
                        combines.Add(new CombineInstance
                        {
                            mesh = mf.sharedMesh,
                            subMeshIndex = s,
                            transform = rootW2L * r.transform.localToWorldMatrix
                        });;
                }
            }
        }

        if (combines.Count == 0) return null;

        var mesh = new Mesh
        {
            name = "ContactVis_Combined",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            hideFlags = HideFlags.HideAndDontSave
        };
        mesh.CombineMeshes(combines.ToArray(), true, true, false);
        mesh.RecalculateBounds();

        foreach (var m in temp) DestroyImmediate(m);
        return mesh;
    }

    private void Release(SdfEntry e)
    {
        if (e == null) return;
        if (e.sdfTex != null) { DestroyImmediate(e.sdfTex); e.sdfTex = null; }
        if (e.combined != null) { DestroyImmediate(e.combined); e.combined = null; }
    }

    void LateUpdate()
    {
        if (autoRebakeOnPoseChange)
        {
            CheckPoseAndRebake(objectA, objectB);
            CheckPoseAndRebake(objectB, objectA);
        }
        PushShaderGlobals();
    }

    /// <summary>Rebake when the skinned pose or the relative placement changed and settled.</summary>
    private void CheckPoseAndRebake(SdfEntry e, SdfEntry other)
    {
        if (e == null || e.root == null || e.sdfTex == null) return;
        bool changed = false;
        int h = ComputePoseHash(e);
        if (h != 0 && h != e.poseHash) { e.poseHash = h; changed = true; }
        if (focusMargin > 0.001f)
        {
            int rh = ComputeRelHash(e, other);
            if (rh != 0 && rh != e.relHash) { e.relHash = rh; changed = true; }
        }
        if (changed)
        {
            e.poseDirty = true;
            e.lastPoseChangeTime = Time.realtimeSinceStartup;
            return;
        }
        if (e.poseDirty && Time.realtimeSinceStartup - e.lastPoseChangeTime > poseSettleDelay)
        {
            e.poseDirty = false;
            Bake(e, other);
        }
    }

    private static int ComputeRelHash(SdfEntry e, SdfEntry other)
    {
        if (e == null || e.root == null || other == null || other.root == null) return 0;
        Matrix4x4 m = e.root.transform.worldToLocalMatrix * other.root.transform.localToWorldMatrix;
        int h = 17;
        for (int i = 0; i < 16; i++) h = h * 31 + Mathf.RoundToInt(m[i] * 2000f);
        return h == 0 ? 1 : h;
    }

    private int ComputePoseHash(SdfEntry e)
    {
        Renderer[] rends = (e.renderers != null && e.renderers.Length > 0)
            ? e.renderers
            : e.root.GetComponentsInChildren<Renderer>(true);
        int h = 0;
        bool any = false;
        foreach (var r in rends)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr == null) continue;
            any = true;
            var bones = smr.bones;
            for (int i = 0; i < bones.Length; i++)
            {
                var t = bones[i];
                if (t == null) continue;
                Vector3 p = t.localPosition;
                Quaternion q = t.localRotation;
                h = h * 31 + Mathf.RoundToInt(p.x * 5000f);
                h = h * 31 + Mathf.RoundToInt(p.y * 5000f);
                h = h * 31 + Mathf.RoundToInt(p.z * 5000f);
                h = h * 31 + Mathf.RoundToInt(q.x * 5000f);
                h = h * 31 + Mathf.RoundToInt(q.y * 5000f);
                h = h * 31 + Mathf.RoundToInt(q.z * 5000f);
                h = h * 31 + Mathf.RoundToInt(q.w * 5000f);
            }
        }
        if (!any) return 0;
        return h == 0 ? 1 : h;
    }

    private void PushShaderGlobals()
    {
        ConfigureMaterial(objectA != null ? objectA.material : null, objectA, objectB);
        ConfigureMaterial(objectB != null ? objectB.material : null, objectB, objectA);
    }

    private static void ConfigureMaterial(Material target, SdfEntry self, SdfEntry other)
    {
        if (target == null) return;

        SetEntry(target, other, "_CV_Other");
        bool selfValid = SetEntry(target, self, "_CV_Self");
        if (selfValid)
        {
            float maxSide = Mathf.Max(self.bakeSize.x, Mathf.Max(self.bakeSize.y, self.bakeSize.z));
            int maxDim = Mathf.Max(self.sdfTex.width, Mathf.Max(self.sdfTex.height, self.sdfTex.depth));
            Vector3 ls = self.root.transform.lossyScale;
            float scaleToWorld = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
            target.SetFloat("_CV_SelfTol", (maxSide / Mathf.Max(1, maxDim)) * scaleToWorld);
        }
    }

    private static bool SetEntry(Material target, SdfEntry source, string prefix)
    {
        bool valid = source != null && source.root != null && source.sdfTex != null;
        target.SetFloat(prefix + "Valid", valid ? 1f : 0f);
        if (!valid) return false;

        Transform t = source.root.transform;
        Vector3 s = t.lossyScale;
        float scaleToWorld = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
        float maxSide = Mathf.Max(source.bakeSize.x, Mathf.Max(source.bakeSize.y, source.bakeSize.z));

        target.SetTexture(prefix + "SDF", source.sdfTex);
        target.SetMatrix(prefix + "WorldToLocal", t.worldToLocalMatrix);
        target.SetVector(prefix + "BoxCenter", source.bakeCenter);
        target.SetVector(prefix + "BoxSize", source.bakeSize);
        target.SetFloat(prefix + "DistScale", maxSide * scaleToWorld);
        int maxDim = Mathf.Max(source.sdfTex.width, Mathf.Max(source.sdfTex.height, source.sdfTex.depth));
        target.SetFloat(prefix + "Tol", (maxSide / Mathf.Max(1, maxDim)) * scaleToWorld);
        return true;
    }
}