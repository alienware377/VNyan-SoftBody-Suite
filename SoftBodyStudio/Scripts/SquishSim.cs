using System.Collections.Generic;
using UnityEngine;

namespace SoftBodyStudio
{
    // A whole skinned mesh used as a collider, auto-reduced ONCE to a small set of BONE
    // CAPSULES: verts are grouped by dominant skin bone, each group fitted with a capsule in
    // that bone's local space â€” so the capsules follow animation for free (no per-frame
    // baking or clustering at all). Capsules that already intersect the painted region at
    // generation time (the chest wall under the breasts etc.) are skipped. Smooth solid
    // primitives give a natural squish direction at any penetration depth.
    public class MeshColliderCloud
    {
        public float radius = 0.015f;            // extra skin gap on top of each capsule

        class Cap { public Transform bone; public Vector3 la, lb; public float rad; }
        List<Cap> caps; int exCount = -1;

        // active capsules this frame (proxy-local) + broadphase (midpoint, reach^2)
        public Vector3[] ca, cb; public float[] cr; int nc;
        Vector3[] cm; float[] re2;
        Vector3 gC; float gR2;   // bounding sphere over ALL active capsules (global reject)
        public int nd { get { return nc; } }
        public string buildInfo = "";

        public bool HasBuild(int excluded) { return caps != null && exCount == excluded; }

        System.Text.StringBuilder bsb;
        HashSet<Transform> usedBones;

        public void BuildFromSkin(SkinnedMeshRenderer smr, HashSet<int> exclude, List<Vector3> regionWorld)
        {
            BeginBuild(exclude);
            AppendFromSkin(smr, exclude, regionWorld);
            EndBuild();
        }

        // one cloud built from SEVERAL meshes ("(all meshes)" collider) — bones already
        // covered by an earlier mesh are skipped so body+clothing don't double up
        public void BuildFromSkinMulti(List<SkinnedMeshRenderer> smrs, SkinnedMeshRenderer self, HashSet<int> exclude, List<Vector3> regionWorld)
        {
            BeginBuild(exclude);
            if (smrs != null)
                for (int i = 0; i < smrs.Count; i++)
                    if (smrs[i] != null)
                        AppendFromSkin(smrs[i], ReferenceEquals(smrs[i], self) ? exclude : null, regionWorld);
            EndBuild();
        }

        void BeginBuild(HashSet<int> exclude)
        {
            exCount = exclude != null ? exclude.Count : 0;
            caps = new List<Cap>();
            ca = cb = null; cr = null; nc = 0;
            bsb = new System.Text.StringBuilder();
            usedBones = new HashSet<Transform>();
        }

        void EndBuild()
        {
            ca = new Vector3[caps.Count]; cb = new Vector3[caps.Count]; cr = new float[caps.Count];
            cm = new Vector3[caps.Count]; re2 = new float[caps.Count];
            buildInfo = caps.Count + " capsules — " + bsb;
            bsb = null; usedBones = null;
        }

        void AppendFromSkin(SkinnedMeshRenderer smr, HashSet<int> exclude, List<Vector3> regionWorld)
        {
            Mesh mesh = smr != null ? smr.sharedMesh : null;
            if (mesh == null) return;
            BoneWeight[] bw = mesh.boneWeights;
            Transform[] bones = smr.bones;
            if (bw == null || bw.Length == 0 || bones == null || bones.Length == 0) return;

            Mesh tmp = new Mesh();
            smr.BakeMesh(tmp);
            Vector3[] mv = tmp.vertices;
            Transform ctr = smr.transform;
            int nv = Mathf.Min(mv.Length, bw.Length);

            // the whole hand (wrist + palm + fingers + thumb + wrist-twist) merges into ONE
            // fat capsule per side, anchored on the wrist â€” dozens of thin finger capsules
            // left slip gaps and their conflicting pushes sharded the skin
            int hwL = -1, hwR = -1;
            for (int b2 = 0; b2 < bones.Length; b2++)
            {
                if (bones[b2] == null) continue;
                string nm2 = bones[b2].name.ToLowerInvariant();
                if (nm2.Contains("wrist") && !nm2.Contains("twist"))
                { if (SideL(nm2)) hwL = b2; else if (SideR(nm2)) hwR = b2; }
            }
            for (int b2 = 0; b2 < bones.Length && (hwL < 0 || hwR < 0); b2++)
            {
                if (bones[b2] == null) continue;
                string nm2 = bones[b2].name.ToLowerInvariant();
                if (nm2.Contains("hand"))
                { if (hwL < 0 && SideL(nm2)) hwL = b2; else if (hwR < 0 && SideR(nm2)) hwR = b2; }
            }
            int[] remap = new int[bones.Length];
            for (int b2 = 0; b2 < bones.Length; b2++)
            {
                remap[b2] = b2;
                if (bones[b2] == null) continue;
                string nm2 = bones[b2].name.ToLowerInvariant();
                bool handish = nm2.Contains("wrist") || nm2.Contains("hand") || nm2.Contains("finger") || nm2.Contains("thumb");
                if (!handish) continue;
                if (SideL(nm2) && hwL >= 0) remap[b2] = hwL;
                else if (SideR(nm2) && hwR >= 0) remap[b2] = hwR;
            }

            List<Vector3>[] grp = new List<Vector3>[bones.Length];
            for (int i = 0; i < nv; i++)
            {
                if (exclude != null && exclude.Contains(i)) continue;
                BoneWeight w4 = bw[i];
                int b = w4.boneIndex0; float wt = w4.weight0;
                if (w4.weight1 > wt) { b = w4.boneIndex1; wt = w4.weight1; }
                if (w4.weight2 > wt) { b = w4.boneIndex2; wt = w4.weight2; }
                if (w4.weight3 > wt) { b = w4.boneIndex3; wt = w4.weight3; }
                if (wt < 0.35f || b < 0 || b >= bones.Length || bones[b] == null) continue;
                b = remap[b];
                if (grp[b] == null) grp[b] = new List<Vector3>();
                grp[b].Add(bones[b].InverseTransformPoint(ctr.TransformPoint(mv[i])));
            }
            Object.Destroy(tmp);

            System.Text.StringBuilder sb = bsb;
            for (int b = 0; b < bones.Length; b++)
            {
                List<Vector3> g = grp[b];
                if (g == null || g.Count < 15) continue;
                if (usedBones.Contains(bones[b])) continue;   // covered by an earlier mesh
                // endpoints: OWN joint pivot -> CHILD joint pivot whenever a child bone has
                // geometry. Chained capsules then SHARE the joint point and stay overlapped
                // however the joint bends â€” bbox fits left a wedge gap at bent elbows that
                // skin slipped into (ring artifact).
                bool isHand = (b == hwL || b == hwR);
                Vector3 pa = Vector3.zero, pb;
                int child = -1, childCnt = 0;
                for (int c2 = 0; c2 < bones.Length; c2++)
                    if (c2 != b && bones[c2] != null && bones[c2].parent == bones[b]
                        && grp[c2] != null && grp[c2].Count > childCnt)
                    { child = c2; childCnt = grp[c2].Count; }
                if (!isHand && child >= 0 && childCnt >= 15)
                    pb = bones[b].InverseTransformPoint(bones[child].position);
                else
                {
                    // leaf bone: fall back to a bbox fit along the longest local axis
                    Vector3 mn = g[0], mx = g[0];
                    for (int i = 1; i < g.Count; i++) { mn = Vector3.Min(mn, g[i]); mx = Vector3.Max(mx, g[i]); }
                    Vector3 ext = mx - mn, cen = (mn + mx) * 0.5f;
                    int ax = 0; if (ext.y > ext.x) ax = 1; if (ext.z > ext[ax]) ax = 2;
                    Vector3 dir = Vector3.zero; dir[ax] = 1f;
                    float half = ext[ax] * 0.5f;
                    pa = cen - dir * half; pb = cen + dir * half;
                }
                if (isHand)
                {
                    // trim the mitt to palm length — fitting the merged fingers' full
                    // extent produced an absurdly long capsule
                    Vector3 mid = (pa + pb) * 0.5f, ax2 = pb - pa;
                    float hl = ax2.magnitude * 0.5f;
                    if (hl > 0.065f) { ax2 /= hl * 2f; pa = mid - ax2 * 0.065f; pb = mid + ax2 * 0.065f; }
                }
                // radius: mean perpendicular distance of the verts from the SEGMENT
                float rsum = 0f;
                for (int i = 0; i < g.Count; i++)
                    rsum += (g[i] - Closest(g[i], pa, pb)).magnitude;
                float rad = Mathf.Clamp(rsum / g.Count * 1.45f, 0.008f, 0.12f);
                if (isHand)
                {
                    // one big fat mitt, extended elbow-ward so nothing slips between
                    // the hand capsule and the forearm capsule
                    rad = Mathf.Clamp(rad * 1.3f, 0.035f, 0.14f);
                    Vector3 axis = pb - pa;
                    if (axis.sqrMagnitude > 1e-8f && bones[b].parent != null)
                    {
                        axis.Normalize();
                        Vector3 pl = bones[b].InverseTransformPoint(bones[b].parent.position);
                        if ((pa - pl).sqrMagnitude < (pb - pl).sqrMagnitude) pa -= axis * 0.07f;
                        else pb += axis * 0.07f;
                    }
                }
                Cap cap = new Cap { bone = bones[b], la = pa, lb = pb, rad = rad };

                // STRICT WHITELIST: only the arm chain ever gets capsules. Statistical
                // filters (pre-clip overlap tests) let soft-tissue bones slip through
                // depending on paint/pose at build time (breast bones once got r=0.12
                // capsules) — the decided collider set must be identical for EVERY mesh.
                string bn = bones[b].name.ToLowerInvariant();
                bool limb = bn.Contains("arm") || bn.Contains("elbow") || bn.Contains("wrist")
                    || bn.Contains("hand") || bn.Contains("finger") || bn.Contains("thumb") || bn.Contains("shoulder");
                if (!limb) { sb.Append("SKIP(non-limb) ").Append(bones[b].name).Append("; "); continue; }
                sb.Append(bones[b].name).Append(" r=").Append(rad.ToString("0.000"))
                  .Append(" len=").Append((cap.lb - cap.la).magnitude.ToString("0.00")).Append("; ");
                usedBones.Add(bones[b]);
                caps.Add(cap);
            }
        }

        // per-frame: move capsules with their bones, keep only those near the painted regions
        public void UpdateFrame(Transform proxyTr, List<Vector4> bounds)
        {
            nc = 0;
            if (caps == null || ca == null) return;
            float ps = Mathf.Max(1e-4f, proxyTr.lossyScale.x);
            for (int k = 0; k < caps.Count; k++)
            {
                Cap c = caps[k];
                if (c.bone == null) continue;
                Vector3 a = proxyTr.InverseTransformPoint(c.bone.TransformPoint(c.la));
                Vector3 b = proxyTr.InverseTransformPoint(c.bone.TransformPoint(c.lb));
                float r = c.rad * Mathf.Max(1e-4f, c.bone.lossyScale.x) / ps;
                bool near = false;
                for (int q = 0; q < bounds.Count; q++)
                {
                    Vector3 bc = new Vector3(bounds[q].x, bounds[q].y, bounds[q].z);
                    if (DistToSeg(bc, a, b) < bounds[q].w + r + radius) { near = true; break; }
                }
                if (!near) continue;
                ca[nc] = a; cb[nc] = b; cr[nc] = r;
                cm[nc] = (a + b) * 0.5f;
                float reach = (b - a).magnitude * 0.5f + r + radius + 0.02f;
                re2[nc] = reach * reach;
                nc++;
            }
            // global bounding sphere: one cheap test rejects far nodes before the capsule loop
            gC = Vector3.zero;
            for (int k = 0; k < nc; k++) gC += cm[k];
            if (nc > 0) gC /= nc;
            gR2 = 0f;
            for (int k = 0; k < nc; k++)
            {
                float rr = (cm[k] - gC).magnitude + Mathf.Sqrt(re2[k]);
                if (rr * rr > gR2) gR2 = rr * rr;
            }
        }

        // capsule barrier: radial push out of the engaged capsules, penetration-weighted.
        // Radial directions are stable at any depth â€” nothing to slide over or get behind.
        public bool TryPushPlanar(Vector3 p, float gap, out Vector3 push)
        {
            push = Vector3.zero;
            if (nc == 0) return false;
            float gx = p.x - gC.x, gy = p.y - gC.y, gz = p.z - gC.z;
            if (gx * gx + gy * gy + gz * gz > gR2) return false;   // far from every capsule
            Vector3 sum = Vector3.zero; float sumW = 0f, best = 0f;
            for (int k = 0; k < nc; k++)
            {
                // broadphase: cheap squared-distance reject (this loop runs ~a million
                // times a frame across all nodes â€” the sqrt-heavy math below must be rare)
                float bx = p.x - cm[k].x, by = p.y - cm[k].y, bz = p.z - cm[k].z;
                if (bx * bx + by * by + bz * bz > re2[k]) continue;
                Vector3 cp = Closest(p, ca[k], cb[k]);
                Vector3 d = p - cp; float dist = d.magnitude;
                float R = cr[k] + gap;
                if (dist >= R || dist < 1e-6f) continue;
                float pen = R - dist;
                float wgt = pen * pen;
                sum += d / dist * (pen * wgt); sumW += wgt;
                if (pen > best) best = pen;
            }
            if (sumW <= 1e-9f) return false;
            Vector3 dirv = sum / sumW;
            float dm = dirv.magnitude;
            if (dm < 1e-7f) return false;
            push = dirv / dm * best;
            return true;
        }

        // normalized penetration field for the soft dent (~3 cm of pen == full strength)
        public bool TryPushNorm(Vector3 p, out Vector3 pushNorm)
        {
            pushNorm = Vector3.zero;
            Vector3 push;
            if (!TryPushPlanar(p, radius, out push)) return false;
            pushNorm = push / 0.03f;
            return true;
        }

        static bool SideL(string nm)
        {
            return nm.Contains("left") || nm.EndsWith("_l") || nm.EndsWith(".l") || nm.Contains("_l_");
        }
        static bool SideR(string nm)
        {
            return nm.Contains("right") || nm.EndsWith("_r") || nm.EndsWith(".r") || nm.Contains("_r_");
        }

        static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float sq = ab.sqrMagnitude;
            if (sq < 1e-10f) return a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / sq);
            return a + ab * t;
        }

        static float DistToSeg(Vector3 p, Vector3 a, Vector3 b)
        {
            return (p - Closest(p, a, b)).magnitude;
        }
    }

    // XPBD soft-body solver on the painted (welded) vertices.
    // Per substep: verlet integrate, then N iterations of
    //   1. attach (soft spring + hard leash to the CURRENT skinned position),
    //   2. edge distance constraints (rest length = the current skinned bake, so any pose
    //      on any rig - however messy - is the natural rest state),
    //   3. capsule collision projection (hard, LAST, every iteration).
    // Non-penetration is structural: neighbours are dragged coherently by the distance
    // constraints instead of tearing, and a pressure term re-inflates displaced volume.
    public class SquishSim
    {
        public SquishRegion cfg;
        public int[] idx;            // representative mesh vertex per node
        public float[] w;            // paint weight per node
        int[][] members;             // duplicate verts per node
        int n;
        int[][] nbr;
        int[] eA, eB;                // unique surface edges between nodes
        int[] bA, bB;                // 2-ring "bending" edges (fold resistance)

        Vector3[] pos, prev;
        float[] restE, restB;        // per-frame rest lengths (computed once, not per iteration)
        int[] contact; int nContact;
        float[] localScale; float medianScale;
        public float measMinEdge, measMaxEdge;   // region's actual edge-length range (for auto grid sync)
        // smooth write-back: every ORIGINAL vertex blends offsets of nearby sim nodes —
        // writing one coarse node's offset verbatim to all its members made the
        // displacement piecewise-constant (checkerboard cliffs at every 2 mm cell border)
        int[] wbVert; int[] wbOff; int[] wbNode; float[] wbW; // nodes in collider range this substep (others skip narrowphase)
        bool primed;
        float[] rawPen;
        Vector3[] outOffs;
        float[] wS;                  // neighbour-smoothed weights (raw paint is vertex-noisy)
        Vector3[] nS;                // neighbour-smoothed normals (for the pressure lift)

        public Transform[] colTr;
        public SquishCollider[] colCfg;
        public MeshColliderCloud[] clouds;
        public bool skipCollision;   // set when chained onto Squish Studio's output —
                                     // collision was already resolved there; two solvers
                                     // projecting to different targets shred the contact
        public Transform refBone;
        public Quaternion refRest;
        public bool refCaptured;

        public void Build(SquishRegion region, Mesh mesh, int[] weldOf, List<int>[] weldMembers)
        {
            cfg = region;
            int mv = mesh.vertexCount;
            float[] wAll = new float[mv];
            for (int i = 0; i < region.vertIndex.Count; i++)
            {
                int vi = region.vertIndex[i];
                if (vi >= 0 && vi < mv) wAll[vi] = Mathf.Max(wAll[vi], region.weight[i]);
            }
            // COARSEN: micro-detail geometry (0.4 mm areola edges) is pathological for
            // PBD — mm-scale errors become cm-scale corrections and the patch oscillates
            // into a standing crumple. Merge welded nodes within ~2 mm into ONE sim node
            // (all duplicates still get written, so the rendered detail is untouched).
            Vector3[] mvp = mesh.vertices;
            // ADAPTIVE COARSEN: the cell size follows the LOCAL edge length, so dense
            // sub-mm geometry and sparse 8 mm geometry each get an appropriate node
            // spacing. A single uniform grid is always wrong somewhere on this mesh.
            // 1) collect the region's welded representatives
            List<int> regReps = new List<int>();
            Dictionary<int, int> regIdx = new Dictionary<int, int>();
            for (int v = 0; v < mv; v++)
            {
                if (wAll[v] <= 0f) continue;
                int rp = weldOf[v];
                if (regIdx.ContainsKey(rp)) continue;
                regIdx[rp] = regReps.Count;
                regReps.Add(rp);
            }
            int nr = regReps.Count;
            // 2) per-rep mean edge length + measured min/max over the region
            float[] edgeLen = new float[nr];
            int[] edgeCnt = new int[nr];
            List<int>[] radj = new List<int>[nr];
            for (int i = 0; i < nr; i++) radj[i] = new List<int>();
            int[] tris0 = mesh.triangles;
            measMinEdge = float.MaxValue; measMaxEdge = 0f;
            for (int t = 0; t < tris0.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = weldOf[tris0[t + e]], b = weldOf[tris0[t + (e + 1) % 3]];
                    int ra, rb;
                    if (a == b || !regIdx.TryGetValue(a, out ra) || !regIdx.TryGetValue(b, out rb)) continue;
                    float len = (mvp[a] - mvp[b]).magnitude;
                    if (len < 1e-6f) continue;
                    edgeLen[ra] += len; edgeCnt[ra]++;
                    radj[ra].Add(rb);
                    if (len < measMinEdge) measMinEdge = len;
                    if (len > measMaxEdge) measMaxEdge = len;
                }
            if (measMaxEdge <= 0f) { measMinEdge = 0.004f; measMaxEdge = 0.008f; }
            if (measMinEdge >= measMaxEdge) measMaxEdge = measMinEdge * 1.001f;
            for (int i = 0; i < nr; i++)
                edgeLen[i] = edgeCnt[i] > 0 ? edgeLen[i] / edgeCnt[i] : measMaxEdge;
            // smooth the field: cell-size discontinuities between neighbours create
            // node-density seams that print into the surface (the shard law applies to
            // EVERY field that shapes displacement, including this one)
            float[] tmpEL = new float[nr];
            for (int pass0 = 0; pass0 < 3; pass0++)
            {
                for (int i = 0; i < nr; i++)
                {
                    float acc = edgeLen[i]; int c2 = 1;
                    List<int> na2 = radj[i];
                    for (int j = 0; j < na2.Count; j++) { acc += edgeLen[na2[j]]; c2++; }
                    tmpEL[i] = acc / c2;
                }
                float[] sw = edgeLen; edgeLen = tmpEL; tmpEL = sw;
            }
            // 3) grid bounds: auto-sync to the measured edge range, or take the sliders
            if (cfg.xGridAuto > 0.5f) { cfg.xGridMin = measMinEdge; cfg.xGridMax = measMaxEdge; }
            float gMin = Mathf.Clamp(cfg.xGridMin, 0.0002f, 1f);
            float gMax = Mathf.Clamp(cfg.xGridMax, gMin, 1f);
            // 4) per-rep target cell (log-space remap of the edge range onto the grid
            // range) hashed on a power-of-two level pyramid so variable cell sizes
            // still cluster in O(n)
            int levels = Mathf.Clamp(Mathf.CeilToInt(Mathf.Log(Mathf.Max(gMax / gMin, 1f), 2f)) + 1, 1, 15);
            float lnLo = Mathf.Log(measMinEdge);
            float lnSpan = Mathf.Max(Mathf.Log(measMaxEdge) - lnLo, 1e-6f);
            Dictionary<long, int> cell = new Dictionary<long, int>();
            List<int> reps = new List<int>();
            Dictionary<int, int> repIndex = new Dictionary<int, int>();
            List<List<int>> merged = new List<List<int>>();
            for (int q = 0; q < nr; q++)
            {
                int rep = regReps[q];
                float tt = Mathf.Clamp01((Mathf.Log(edgeLen[q]) - lnLo) / lnSpan);
                float target = gMin * Mathf.Pow(gMax / gMin, tt);
                int lvl = Mathf.Clamp(Mathf.RoundToInt(Mathf.Log(target / gMin, 2f)), 0, levels - 1);
                float cs = gMin * (1 << lvl);
                Vector3 pv = mvp[rep];
                long key = ((long)lvl << 60)
                         | ((long)(Mathf.FloorToInt(pv.x / cs) & 0xFFFFF) << 40)
                         | ((long)(Mathf.FloorToInt(pv.y / cs) & 0xFFFFF) << 20)
                         | (long)(Mathf.FloorToInt(pv.z / cs) & 0xFFFFF);
                int node;
                if (!cell.TryGetValue(key, out node))
                {
                    node = reps.Count;
                    cell[key] = node;
                    reps.Add(rep);
                    merged.Add(new List<int>());
                }
                repIndex[rep] = node;
                merged[node].Add(rep);
            }
            n = reps.Count;
            // each node's representative = the member closest to the CELL CENTROID
            // (medoid). The first member can sit at the cell border, which gives
            // adjacent coarse nodes sub-millimeter rest separations - the same sub-mm
            // instability the coarsening was supposed to remove, reborn at node level.
            for (int i = 0; i < n; i++)
            {
                List<int> mreps = merged[i];
                if (mreps.Count <= 1) continue;
                Vector3 cen = Vector3.zero;
                for (int r = 0; r < mreps.Count; r++) cen += mvp[mreps[r]];
                cen /= mreps.Count;
                int best = mreps[0]; float bd = float.MaxValue;
                for (int r = 0; r < mreps.Count; r++)
                {
                    float dd = (mvp[mreps[r]] - cen).sqrMagnitude;
                    if (dd < bd) { bd = dd; best = mreps[r]; }
                }
                reps[i] = best;
            }
            idx = reps.ToArray();
            w = new float[n];
            members = new int[n][];
            for (int i = 0; i < n; i++)
            {
                List<int> mem = new List<int>();
                for (int r = 0; r < merged[i].Count; r++)
                    mem.AddRange(weldMembers[merged[i][r]]);
                members[i] = mem.ToArray();
                float mw = 0f;
                for (int j = 0; j < members[i].Length; j++) mw = Mathf.Max(mw, wAll[members[i][j]]);
                w[i] = mw;
            }
            int[] tris = mesh.triangles;
            HashSet<long> eSet = new HashSet<long>();
            List<int> ea = new List<int>(), eb = new List<int>();
            List<int>[] adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>();
            for (int t = 0; t < tris.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = weldOf[tris[t + e]], b = weldOf[tris[t + (e + 1) % 3]];
                    int na, nb2;
                    if (!repIndex.TryGetValue(a, out na) || !repIndex.TryGetValue(b, out nb2) || na == nb2) continue;
                    long k = na < nb2 ? ((long)na << 32) | (uint)nb2 : ((long)nb2 << 32) | (uint)na;
                    if (eSet.Contains(k)) continue;
                    eSet.Add(k);
                    if ((mvp[idx[na]] - mvp[idx[nb2]]).sqrMagnitude < 0.0008f * 0.0008f) continue;   // degenerate
                    ea.Add(na); eb.Add(nb2);
                    adj[na].Add(nb2); adj[nb2].Add(na);
                }
            }
            eA = ea.ToArray(); eB = eb.ToArray();
            nbr = new int[n][];
            for (int i = 0; i < n; i++) nbr[i] = adj[i].ToArray();
            // per-node LOCAL SCALE: mean rest distance to graph neighbours. This mesh mixes
            // 0.4 mm and 8 mm edges — anything scale-blind misbehaves somewhere, so tension
            // and correction limits adapt to it.
            localScale = new float[n];
            List<float> scaleSamples = new List<float>();
            for (int i = 0; i < n; i++)
            {
                float acc = 0f; int cnt2 = 0;
                for (int j = 0; j < nbr[i].Length; j++) { acc += (mvp[idx[i]] - mvp[idx[nbr[i][j]]]).magnitude; cnt2++; }
                localScale[i] = cnt2 > 0 ? acc / cnt2 : 0.005f;
                if (cnt2 > 0 && (i % 7) == 0) scaleSamples.Add(localScale[i]);
            }
            scaleSamples.Sort();
            medianScale = scaleSamples.Count > 0 ? scaleSamples[scaleSamples.Count / 2] : 0.005f;
            // 2-ring bending edges: pure distance constraints crumple like cloth (no fold
            // resistance) — an edge to each neighbour-of-neighbour keeps the surface flat
            HashSet<long> bSet = new HashSet<long>();
            List<int> ba = new List<int>(), bb = new List<int>();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < nbr[i].Length; j++)
                {
                    int mid = nbr[i][j];
                    for (int k = 0; k < nbr[mid].Length; k++)
                    {
                        int q = nbr[mid][k];
                        if (q == i) continue;
                        long key = i < q ? ((long)i << 32) | (uint)q : ((long)q << 32) | (uint)i;
                        if (bSet.Contains(key) || eSet.Contains(key)) continue;
                        bSet.Add(key);
                        ba.Add(i); bb.Add(q);
                    }
                }
            bA = ba.ToArray(); bB = bb.ToArray();
            // shard law: every field that scales displacement must be smooth — raw
            // hand-painted weights are vertex-noisy and print into the surface
            wS = new float[n];
            for (int i = 0; i < n; i++) wS[i] = w[i];
            for (int pass = 0; pass < 4; pass++)
                for (int i = 0; i < n; i++)
                {
                    int[] nb = nbr[i]; if (nb.Length == 0) continue;
                    float acc = wS[i];
                    for (int j = 0; j < nb.Length; j++) acc += wS[nb[j]];
                    wS[i] = acc / (nb.Length + 1);
                }
            // write-back interpolation tables (vertex -> up to 4 nearby nodes, gaussian)
            List<int> tVert = new List<int>(), tOff = new List<int>(), tNode = new List<int>();
            List<float> tW = new List<float>();
            float sigma = Mathf.Clamp(cfg.xSigma, 0.001f, 0.2f);
            float inv2s2 = 1f / (2f * sigma * sigma);
            int[] candBuf = new int[64]; float[] candW = new float[64];
            for (int i = 0; i < n; i++)
            {
                Vector3 npos = mvp[idx[i]];
                for (int mIdx = 0; mIdx < members[i].Length; mIdx++)
                {
                    int v = members[i][mIdx];
                    Vector3 vpos = mvp[v];
                    int nc2 = 0;
                    candBuf[nc2] = i;
                    candW[nc2++] = Mathf.Exp(-(vpos - npos).sqrMagnitude * inv2s2) + 1e-4f;
                    for (int j = 0; j < nbr[i].Length && nc2 < 64; j++)
                    {
                        int q = nbr[i][j];
                        candBuf[nc2] = q;
                        candW[nc2++] = Mathf.Exp(-(vpos - mvp[idx[q]]).sqrMagnitude * inv2s2);
                    }
                    tVert.Add(v);
                    tOff.Add(tNode.Count);
                    for (int keep = 0; keep < 4 && keep < nc2; keep++)
                    {
                        int best = -1; float bw = -1f;
                        for (int c = 0; c < nc2; c++) if (candW[c] > bw) { bw = candW[c]; best = c; }
                        if (bw <= 0f) break;
                        tNode.Add(candBuf[best]); tW.Add(bw);
                        candW[best] = -2f;
                    }
                }
            }
            tOff.Add(tNode.Count);
            wbVert = tVert.ToArray(); wbOff = tOff.ToArray(); wbNode = tNode.ToArray(); wbW = tW.ToArray();
            for (int v = 0; v < wbVert.Length; v++)
            {
                float sum = 0f;
                for (int k = wbOff[v]; k < wbOff[v + 1]; k++) sum += wbW[k];
                if (sum > 1e-9f) for (int k = wbOff[v]; k < wbOff[v + 1]; k++) wbW[k] /= sum;
            }

            nS = new Vector3[n];
            pos = new Vector3[n]; prev = new Vector3[n];
            rawPen = new float[n];
            primed = false;
        }

        // Deepest remaining penetration at point p (proxy-local) against every enabled
        // collider; dir = outward push direction. Same primitive math and mesh-cloud
        // occlusion bias as the solver's own collision pass. Used by the cage's
        // contact-boost stage (main thread, once per cage vert per frame).
        public float PenetrationAt(Vector3 p, Transform proxy, out Vector3 dir)
        {
            dir = Vector3.zero;
            float best = 0f;
            int ncol = colCfg != null ? colCfg.Length : 0;
            for (int c = 0; c < ncol; c++)
            {
                if (!colCfg[c].enabled) continue;
                if (!string.IsNullOrEmpty(colCfg[c].mesh))
                {
                    MeshColliderCloud cl = clouds != null && c < clouds.Length ? clouds[c] : null;
                    if (cl == null) continue;
                    Vector3 push;
                    if (cl.TryPushPlanar(p, -0.003f, out push))
                    {
                        float pm = push.magnitude;
                        if (pm > best) { best = pm; dir = push / pm; }
                    }
                }
                else
                {
                    Transform ct = colTr != null ? colTr[c] : null;
                    if (ct == null) continue;
                    Vector3 cp0 = proxy.InverseTransformPoint(ct.position);
                    float rad = colCfg[c].radius / Mathf.Max(0.0001f, proxy.lossyScale.x);
                    Vector3 cp = cp0;
                    if (colCfg[c].length > 0f)
                    {
                        Vector3 b3 = proxy.InverseTransformPoint(ct.position + ct.forward * colCfg[c].length);
                        cp = ClosestOnSegment(cp0, b3, p);
                    }
                    Vector3 dd = p - cp; float dist = dd.magnitude;
                    if (dist < rad && dist > 1e-6f && rad - dist > best) { best = rad - dist; dir = dd / dist; }
                }
            }
            return best;
        }

        public void StepDynamics(Vector3[] baked, Vector3[] normals, float sdt, Vector3 localDown, Transform proxy)
        {
            if (n == 0) return;
            if (!primed)
            {
                for (int i = 0; i < n; i++) { pos[i] = baked[idx[i]]; prev[i] = pos[i]; }
                primed = true;
            }
            // smoothed normal field for this frame (pressure lift direction)
            for (int i = 0; i < n; i++) nS[i] = normals[idx[i]];
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < n; i++)
                {
                    int[] nb = nbr[i]; if (nb.Length == 0) continue;
                    Vector3 acc = nS[i];
                    for (int j = 0; j < nb.Length; j++) acc += nS[nb[j]];
                    float am = acc.magnitude;
                    if (am > 1e-6f) nS[i] = acc / am;
                }

            float damp = Mathf.Clamp01(cfg.damping);
            float grav = Mathf.Clamp(cfg.gravity, 0f, 1f) * 0.35f;
            for (int i = 0; i < n; i++)
            {
                Vector3 x = pos[i];
                Vector3 v = (x - prev[i]) * (1f - damp);
                float vm = v.magnitude;
                if (vm > 0.05f) v *= 0.05f / vm;   // oscillation guard
                prev[i] = x;
                pos[i] = x + v + localDown * (grav * sdt * sdt * wS[i]);
            }

            int iters = Mathf.Clamp(Mathf.RoundToInt(cfg.xIter), 1, 30);
            // rest lengths from the current bake, ONCE per substep (was sqrt x edges x iters)
            if (restE == null || restE.Length != eA.Length) restE = new float[eA.Length];
            if (restB == null || restB.Length != bA.Length) restB = new float[bA.Length];
            for (int e = 0; e < eA.Length; e++) restE[e] = (baked[idx[eA[e]]] - baked[idx[eB[e]]]).magnitude;
            for (int e = 0; e < bA.Length; e++) restB[e] = (baked[idx[bA[e]]] - baked[idx[bB[e]]]).magnitude;
            if (contact == null || contact.Length != n) contact = new int[n];
            float kStretch = Mathf.Clamp01(cfg.xStretch);
            float attach = Mathf.Clamp01(cfg.xAttach);
            float maxS = Mathf.Max(0.005f, cfg.xMaxStretch);
            int ncol = colCfg != null ? colCfg.Length : 0;
            float corrCap = Mathf.Clamp(cfg.xCorr, 0.0005f, 0.1f);
            float colRelax = Mathf.Clamp(cfg.xColRelax, 0.05f, 1f);
            float compressK = Mathf.Clamp01(cfg.xCompress);
            float tension = Mathf.Clamp01(cfg.xTension) * 0.6f;

            for (int it = 0; it < iters; it++)
            {
                // 1. attach: soft spring toward the skinned shape + hard leash
                for (int i = 0; i < n; i++)
                {
                    Vector3 b = baked[idx[i]];
                    float freedom = wS[i];
                    // FLOOR on the pull: even fully painted flesh is anchored to the body —
                    // near-zero attach let the interior collapse into loose cloth
                    float pull = attach * (0.15f + 0.85f * (1f - freedom));
                    pos[i] = Vector3.Lerp(pos[i], b, pull);
                    Vector3 d = pos[i] - b;
                    float R = maxS * (0.25f + 0.75f * freedom);
                    float dm = d.magnitude;
                    if (dm > R) pos[i] = b + d * (R / dm);
                }
                // 2. distance constraints (rest = current skinned bake)
                for (int e = 0; e < eA.Length; e++)
                {
                    int a = eA[e], b2 = eB[e];
                    Vector3 d = pos[a] - pos[b2];
                    float L = d.magnitude;
                    if (L < 1e-7f) continue;
                    float diff = L - restE[e];
                    // FLESH, not cloth: resist stretch hard, allow compression almost
                    // freely - symmetric constraints force the surface to BUCKLE (fold)
                    // under a press, which is exactly the rainbow crinkle in the overlay
                    float k1 = diff > 0f ? kStretch : kStretch * compressK;
                    Vector3 c = d * (diff / L * 0.5f * k1);
                    float cl = c.magnitude;
                    float capE = Mathf.Min(corrCap, Mathf.Max(restE[e] * 0.75f, 0.001f));   // scale-adaptive
                    if (cl > capE) c *= capE / cl;
                    pos[a] -= c; pos[b2] += c;
                }
                // 2b. bending (2-ring) constraints — fold resistance so the surface stays
                // a smooth sheet instead of crumpling
                float kBend = kStretch * Mathf.Clamp01(cfg.xBend);
                for (int e = 0; e < bA.Length; e++)
                {
                    int a = bA[e], b2 = bB[e];
                    Vector3 d = pos[a] - pos[b2];
                    float L = d.magnitude;
                    if (L < 1e-7f) continue;
                    float diffB = L - restB[e];
                    float kb1 = diffB > 0f ? kBend : kBend * Mathf.Min(1f, compressK * 2f);
                    Vector3 c = d * (diffB / L * 0.5f * kb1);
                    float cl = c.magnitude;
                    if (cl > corrCap) c *= corrCap / cl;
                    pos[a] -= c; pos[b2] += c;
                }
                // SKIN TENSION: with compression nearly free, slack surface has no defined
                // resting shape and wanders into crinkle. Pull each node toward its
                // neighbours' AVERAGE OFFSET (Laplacian on the deformation field) — smooths
                // deformation without shrinking the rest shape (offset 0 stays 0).
                if (tension > 0.001f)
                    for (int i = 0; i < n; i++)
                    {
                        int[] nb = nbr[i];
                        if (nb.Length < 2) continue;
                        Vector3 avgOff = Vector3.zero;
                        for (int j = 0; j < nb.Length; j++) avgOff += pos[nb[j]] - baked[idx[nb[j]]];
                        avgOff /= nb.Length;
                        // density-adaptive: denser-than-median areas get proportionally MORE
                        // tension (their absolute wander is larger relative to edge length)
                        float tAdapt = Mathf.Min(0.95f, tension * Mathf.Clamp(medianScale / Mathf.Max(localScale[i], 1e-5f), 1f, 3f));
                        pos[i] = Vector3.Lerp(pos[i], baked[idx[i]] + avgOff, tAdapt);
                    }
                // 3. collision - hard, last. Iteration 0 scans ALL nodes and records the
                // contact set (+ neighbours); later iterations only re-test that set.
                if (skipCollision) continue;
                if (it == 0) nContact = 0;
                int scanCount = it == 0 ? n : nContact;
                for (int si = 0; si < scanCount; si++)
                {
                    int i = it == 0 ? si : contact[si];
                    rawPen[i] = 0f;
                    for (int c = 0; c < ncol; c++)
                    {
                        if (!colCfg[c].enabled) continue;
                        if (!string.IsNullOrEmpty(colCfg[c].mesh))
                        {
                            MeshColliderCloud cl = clouds != null && c < clouds.Length ? clouds[c] : null;
                            if (cl == null) continue;
                            Vector3 push;
                            if (cl.TryPushPlanar(pos[i], -0.003f, out push))   // tuck UNDER the glove surface — skin AT the surface renders grazing slivers
                            {
                                float relax = it == iters - 1 ? 1f : colRelax;   // no one-step flings
                                pos[i] += push * relax;
                                if (push.magnitude > rawPen[i]) rawPen[i] = push.magnitude;
                            }
                        }
                        else
                        {
                            Transform ct = colTr != null ? colTr[c] : null;
                            if (ct == null) continue;
                            Vector3 cp0 = proxy.InverseTransformPoint(ct.position);
                            float rad = colCfg[c].radius / Mathf.Max(0.0001f, proxy.lossyScale.x);
                            Vector3 cp = cp0;
                            if (colCfg[c].length > 0f)
                            {
                                Vector3 b3 = proxy.InverseTransformPoint(ct.position + ct.forward * colCfg[c].length);
                                cp = ClosestOnSegment(cp0, b3, pos[i]);
                            }
                            Vector3 dd = pos[i] - cp; float dist = dd.magnitude;
                            if (dist < rad && dist > 1e-6f)
                            { pos[i] += dd / dist * (rad - dist); if (rad - dist > rawPen[i]) rawPen[i] = rad - dist; }
                        }
                    }
                    if (it == 0 && rawPen[i] > 0f && nContact < n) contact[nContact++] = i;
                }
                if (it == 0 && nContact > 0)
                {
                    // widen the set by one ring so dragged neighbours stay covered
                    int baseCount = nContact;
                    for (int ci = 0; ci < baseCount && nContact < n; ci++)
                    {
                        int[] nb = nbr[contact[ci]];
                        for (int j = 0; j < nb.Length && nContact < n; j++)
                        {
                            int q = nb[j];
                            if (rawPen[q] > 0f) continue;
                            bool dup = false;
                            for (int k = baseCount; k < nContact; k++) if (contact[k] == q) { dup = true; break; }
                            if (!dup) contact[nContact++] = q;
                        }
                    }
                }
            }

            // pressure: contact-displaced volume re-inflates the untouched surface
            if (cfg.xPressure > 0.001f && !skipCollision)
            {
                float sunk = 0f; int cnt = 0;
                for (int i = 0; i < n; i++)
                {
                    float dv = Vector3.Dot(pos[i] - baked[idx[i]], normals[idx[i]]);
                    if (dv < 0f) { sunk -= dv; cnt++; }
                }
                if (cnt > 0 && n > cnt)
                {
                    float lift = Mathf.Min(sunk / (n - cnt) * Mathf.Clamp(cfg.xPressure, 0f, 2f), 0.02f);
                    for (int i = 0; i < n; i++)
                        if (rawPen[i] <= 0f) pos[i] += nS[i] * (lift * wS[i]);
                }
            }
        }

        public void FieldAndWrite(Vector3[] baked, Vector3[] normals, Vector3[] disp, float dt, List<SquishSim> others, Transform proxy)
        {
            if (n == 0 || !primed) return;
            bool blewUp = false;
            if (outOffs == null || outOffs.Length != n) outOffs = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 off = pos[i] - baked[idx[i]];
                float om = off.sqrMagnitude;
                if (float.IsNaN(om) || om > 1f) { off = Vector3.zero; blewUp = true; }
                outOffs[i] = off;
            }
            // cosmetic vertex-scale polish (shard law: the written field must be smooth)
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < n; i++)
                {
                    int[] nb = nbr[i]; if (nb.Length == 0) continue;
                    Vector3 avg = Vector3.zero;
                    for (int j = 0; j < nb.Length; j++) avg += outOffs[nb[j]];
                    outOffs[i] = Vector3.Lerp(outOffs[i], avg / nb.Length, 0.35f);
                }
            // Taubin smoothing (alternating +0.5 / -0.53 passes): removes PEAKS from the
            // deformation field while preserving the overall shape (shrink-free) — the
            // heavy-duty output filter for troubleshooting
            int tp = Mathf.Clamp(Mathf.RoundToInt(cfg.xSmoothPasses), 0, 100);
            for (int pass2 = 0; pass2 < tp; pass2++)
            {
                float lam = (pass2 & 1) == 0 ? 0.5f : -0.53f;
                for (int i = 0; i < n; i++)
                {
                    int[] nb = nbr[i];
                    if (nb.Length < 2) continue;
                    Vector3 avg = Vector3.zero;
                    for (int j = 0; j < nb.Length; j++) avg += outOffs[nb[j]];
                    avg /= nb.Length;
                    outOffs[i] += (avg - outOffs[i]) * lam;
                }
            }
            for (int v = 0; v < wbVert.Length; v++)
            {
                Vector3 off = Vector3.zero;
                for (int k = wbOff[v]; k < wbOff[v + 1]; k++)
                    off += outOffs[wbNode[k]] * wbW[k];
                int vi2 = wbVert[v];
                if (vi2 < disp.Length) disp[vi2] += off;
            }
            if (blewUp)
            {
                Debug.LogWarning("[SoftBody] region '" + cfg.name + "' destabilised (NaN/huge offset) - state reset");
                ResetState();
            }
        }

        public void DumpCsv(System.Text.StringBuilder sb)
        {
            for (int i = 0; i < n; i++)
                sb.Append(idx[i]).Append(',').Append(w[i].ToString("0.000")).Append(',')
                  .Append((primed ? (pos[i] - prev[i]).magnitude : 0f).ToString("0.0000")).Append(',')
                  .Append((rawPen != null ? rawPen[i] : 0f).ToString("0.0000")).Append(",0,0\n");
        }

        static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float t = Vector3.Dot(p - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude);
            return a + ab * Mathf.Clamp01(t);
        }

        public void ResetState() { primed = false; }
    }
}
