using System.Collections.Generic;
using UnityEngine;

namespace SquishStudio
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
        // broadphase accessor for the region sleep gate: is a sphere anywhere near
        // this cloud's bounding sphere?
        public bool SphereNear(Vector3 c, float extra)
        {
            if (nc == 0) return false;
            float r = Mathf.Sqrt(gR2) + extra;
            return (c - gC).sqrMagnitude <= r * r;
        }

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

    // Per-region soft-body solver on WELDED position nodes (duplicate vertices along UV
    // seams move as one â€” simulating them independently tears the surface).
    //
    // Two-part update each frame:
    //   StepDynamics (substepped): springs, inertia, gravity, waves, oscillators.
    //   FieldAndWrite (once):      smooth collision field + cohesion + final output.
    public class SquishSim
    {
        public SquishRegion cfg;

        public int[] idx;            // representative mesh vertex per node
        public float[] w;            // node paint weight
        int[][] members;             // all duplicates per node
        int n;

        Vector3[] offset, vel, prevTarget;
        bool primed;
        int[][] nbr;
        float simT;

        // collision: separate smooth non-accumulating layer (UNITLESS field 0..~1,
        // scaled to metres only at output â€” so dent depth is independent of collider
        // radius AND of the jiggle maxOffset clamp)
        Vector3[] colCur, colTarget;
        Vector3[] colBase;           // per-node adaptive baseline (pose/breathing absorbed)
        float[] rawPen;              // per-node raw penetration this frame (pre-baseline)
        Vector3[] nSm;               // neighbour-smoothed normals (coherent dent direction)
        float[] needF;               // depth-field barrier: required clearance per node
        Vector3[] dirF;              // depth-field barrier: displacement direction per node
        Vector3[] hardOff;           // barrier layer (absolute target, separate from soft)
        Vector3[] totF;              // final combined offset (post cosmetic smoothing)
        float[] excF;                // per-node penetration EXCESS beyond the maxDent limiter

        // evacuation: excess penetration moves driver bones / the whole blob out of the way
        public Transform[] evacBones;                 // topmost driver bones (set by MeshProxy)
        public Vector3[] evacClusterPos;              // proxy-local root positions (fed per frame)
        public Vector3[] evacClusterDir; public float[] evacClusterExc;   // outputs per cluster
        // second, independent set: NESTED/child driver bones ("evacuate all bones" mode).
        // Kept separate so root binning — the validated behaviour — is untouched.
        public Transform[] evacBones2;
        public Vector3[] evacCluster2Pos;
        public Vector3[] evacCluster2Dir; public float[] evacCluster2Exc;
        public Vector3[] evacCur2W; public Vector3[] evacApplied2W;
        public int evacCluster2Count;
        public Vector3[] evacCurW, evacAppliedW;      // proxy-side spring/applied state (world)
        public int evacClusterCount;
        public Vector3 evacDir;                       // region-mean excess direction (proxy-local)
        public float evacExcess;                      // strongest excess this frame (m)
        Vector3 evacCur;                              // blob-shift spring state (proxy-local)
        float[] wSm;                 // neighbour-smoothed paint weights (noise-free ramp)
        float[] cSc;                 // cluster scalar scratch (depth-field smoothing)
        float prevPushMag;

        float[] clothH, clothV;
        Vector3 jelloPos, jelloVel;
        float[] jelloF;
        Vector3 swayPos, swayVel;
        float twist, twistVel;
        struct Wave { public Vector3 pos; public float t; public float amp; }
        readonly List<Wave> waves = new List<Wave>();
        float waveCooldown;
        float[] cellNoise;
        float cellNoiseSize = -1f;

        // carried from dynamics to output
        Vector3 lastLocalDown, lastCentroid, lastCentroidVel;

        // coarse RBF clusters for collision smoothing: the contact field is projected onto
        // ~dozens of control points spread over the region and interpolated back â€” giving
        // big smooth blobby dents REGARDLESS of mesh density (graph diffusion smoothed only
        // a few millimetres on dense meshes, which read as facet-level "jello destruction").
        int K;
        Vector3[] clusterC;
        int[][] ncIdx; float[][] ncW;         // per-node: up to 4 clusters + weights
        int[][] cNbr; float[][] cNbrW;        // cluster-to-cluster smoothing weights
        Vector3[] cAcc, cVec; float[] cWsum;

        public Transform refBone;
        public Quaternion refRest;
        public bool refCaptured;

        public Vector3 boundCenter;
        public float boundRadius;

        // ---------- contact sleep gate ----------
        // The full field pipeline (~20 region passes + 2x narrowphase) only runs while a
        // collider is NEAR or residual energy exists. Everything else is a cheap
        // proximity check + decay. Wakes ~12cm BEFORE contact, so no first-touch lag.
        public float sleepT; public float lastFieldEnergy; bool asleepZeroed;
        public bool pendingBlewUp;
        // MAIN-THREAD CAPTURE for the async worker: bone-capsule primitives (proxy-local)
        // and the pose factor — the worker must never touch Transforms
        public Vector3[] capCp0, capCp1; public float[] capRad; public bool[] capValid;
        public float capturedPoseFactor = 1f;
        public void CaptureFrameInputs(Transform proxy)
        {
            int ncol = colCfg != null ? colCfg.Length : 0;
            if (capCp0 == null || capCp0.Length != ncol)
            { capCp0 = new Vector3[ncol]; capCp1 = new Vector3[ncol]; capRad = new float[ncol]; capValid = new bool[ncol]; }
            for (int c = 0; c < ncol; c++)
            {
                capValid[c] = false;
                if (!colCfg[c].enabled || !string.IsNullOrEmpty(colCfg[c].mesh)) continue;
                Transform ct = colTr != null ? colTr[c] : null;
                if (ct == null) continue;
                capCp0[c] = proxy.InverseTransformPoint(ct.position);
                capRad[c] = colCfg[c].radius / Mathf.Max(0.0001f, proxy.lossyScale.x);
                capCp1[c] = colCfg[c].length > 0f
                    ? proxy.InverseTransformPoint(ct.position + ct.forward * colCfg[c].length) : capCp0[c];
                capValid[c] = true;
            }
            capturedPoseFactor = 1f;
            if (cfg.gravityPoseOnly)
            {
                capturedPoseFactor = 0f;
                if (refBone != null && refCaptured)
                    capturedPoseFactor = Mathf.Clamp01(Quaternion.Angle(refRest, refBone.localRotation) / 45f);
            }
        }
        public bool CheckAwake(Vector3[] baked, Transform proxy, List<SquishSim> others, float dt)
        {
            if (!primed || n == 0) return true;              // prime on the normal path first
            // asleep StepDynamics doesn't run, so refresh the bound centre sparsely —
            // the region rides the animation
            Vector3 cAcc = Vector3.zero; int cnt = 0;
            int stride = Mathf.Max(1, n / 128);
            for (int i = 0; i < n; i += stride) { if (idx[i] < baked.Length) { cAcc += baked[idx[i]]; cnt++; } }
            if (cnt > 0) boundCenter = cAcc / cnt;
            // radius refresh (conservative: grows with the sample, never shrinks) — a
            // frozen radius under blendshape growth would erode the wake margin
            float rMaxS = 0f;
            for (int i = 0; i < n; i += stride)
                if (idx[i] < baked.Length)
                { float rd2 = (baked[idx[i]] - boundCenter).sqrMagnitude; if (rd2 > rMaxS) rMaxS = rd2; }
            boundRadius = Mathf.Max(boundRadius, Mathf.Sqrt(rMaxS) * 1.15f);
            float reach = boundRadius + 0.12f;
            bool near = false;
            int ncol = colCfg != null ? colCfg.Length : 0;
            for (int c = 0; c < ncol && !near; c++)
            {
                if (!colCfg[c].enabled) continue;
                if (!string.IsNullOrEmpty(colCfg[c].mesh))
                {
                    MeshColliderCloud cl = clouds != null && c < clouds.Length ? clouds[c] : null;
                    if (cl != null && cl.SphereNear(boundCenter, reach)) near = true;
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
                        cp = ClosestOnSegment(cp0, b3, boundCenter);
                    }
                    if ((boundCenter - cp).magnitude < rad + reach) near = true;
                }
            }
            if (!near && cfg.selfSquish > 0.001f && others != null)
                for (int o = 0; o < others.Count && !near; o++)
                {
                    SquishSim os = others[o];
                    if (os == this || os.boundRadius <= 0f || boundRadius <= 0f) continue;
                    if ((boundCenter - os.boundCenter).magnitude < (boundRadius + os.boundRadius) * 0.8f + 0.05f) near = true;
                }
            if (near || lastFieldEnergy > 1e-7f) { sleepT = 0f; asleepZeroed = false; return true; }
            sleepT += dt;
            return sleepT < 0.35f;                          // grace: let fields settle first
        }

        // asleep frame: ease evac home, decay residual dent once, then near-zero cost
        public void SleepFrame(Vector3[] disp, float dt)
        {
            evacExcess = 0f;
            if (evacClusterCount > 0) for (int k = 0; k < evacClusterCount; k++) evacClusterExc[k] = 0f;
            if (evacCluster2Count > 0) for (int k = 0; k < evacCluster2Count; k++) evacCluster2Exc[k] = 0f;
            evacCur = Vector3.Lerp(evacCur, Vector3.zero, 1f - Mathf.Exp(-8f * dt));
            if (asleepZeroed || colCur == null || !primed) { asleepZeroed = true; return; }
            float k2 = Mathf.Exp(-10f * dt);
            float maxSq = 0f;
            for (int i = 0; i < n; i++)
            {
                colCur[i] *= k2;
                if (hardOff != null && i < hardOff.Length) hardOff[i] *= k2;
                // the adaptive baseline must decay too: frozen through sleep it suppresses
                // (shallows) the first press after waking
                if (colBase != null && i < colBase.Length)
                { colBase[i] *= k2; if (colBase[i].sqrMagnitude > maxSq) maxSq = colBase[i].sqrMagnitude; }
                Vector3 t2 = colCur[i] + (hardOff != null && i < hardOff.Length ? hardOff[i] : Vector3.zero);
                if (totF != null && i < totF.Length) totF[i] = t2;
                float m2 = t2.sqrMagnitude; if (m2 > maxSq) maxSq = m2;
                int[] mem = members[i];
                for (int mIdx = 0; mIdx < mem.Length; mIdx++)
                    if (mem[mIdx] < disp.Length) disp[mem[mIdx]] += t2;
            }
            if (maxSq < 1e-10f) asleepZeroed = true;
        }

        public Transform[] colTr;
        public SquishCollider[] colCfg;
        public MeshColliderCloud[] clouds;

        public void Build(SquishRegion region, Mesh restMesh, int[] weldOf, List<int>[] weldMembers)
        {
            cfg = region;
            simT = 0f;

            Dictionary<int, int> nodeOfWeld = new Dictionary<int, int>();
            List<int> repList = new List<int>();
            List<float> wList = new List<float>();
            for (int i = 0; i < region.vertIndex.Count; i++)
            {
                int vi = region.vertIndex[i];
                if (vi >= weldOf.Length) continue;
                int wg = weldOf[vi];
                int node;
                if (!nodeOfWeld.TryGetValue(wg, out node))
                {
                    node = repList.Count;
                    nodeOfWeld[wg] = node;
                    repList.Add(weldMembers[wg][0]);
                    wList.Add(region.weight[i]);
                }
                else if (region.weight[i] > wList[node]) wList[node] = region.weight[i];
            }
            n = repList.Count;
            idx = repList.ToArray(); w = wList.ToArray();
            members = new int[n][];
            foreach (KeyValuePair<int, int> kv in nodeOfWeld)
                members[kv.Value] = weldMembers[kv.Key].ToArray();

            offset = new Vector3[n]; vel = new Vector3[n]; prevTarget = new Vector3[n];
            colCur = new Vector3[n]; colTarget = new Vector3[n]; colBase = new Vector3[n];
            prevPushMag = 0f; evacCur = Vector3.zero;
            clothH = new float[n]; clothV = new float[n]; jelloF = new float[n];
            jelloPos = Vector3.zero; jelloVel = Vector3.zero;
            swayPos = Vector3.zero; swayVel = Vector3.zero; twist = 0f; twistVel = 0f;
            primed = false; waves.Clear(); cellNoiseSize = -1f;

            List<int>[] adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>(6);
            int[] tris = restMesh.triangles;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int na, nb, nc;
                bool ha = nodeOfWeld.TryGetValue(weldOf[tris[t]], out na);
                bool hb = nodeOfWeld.TryGetValue(weldOf[tris[t + 1]], out nb);
                bool hc = nodeOfWeld.TryGetValue(weldOf[tris[t + 2]], out nc);
                if (ha && hb && na != nb) AddEdge(adj, na, nb);
                if (hb && hc && nb != nc) AddEdge(adj, nb, nc);
                if (ha && hc && na != nc) AddEdge(adj, na, nc);
            }
            nbr = new int[n][];
            for (int i = 0; i < n; i++) nbr[i] = adj[i].ToArray();
        }

        static void AddEdge(List<int>[] adj, int a, int b)
        {
            if (!adj[a].Contains(b)) adj[a].Add(b);
            if (!adj[b].Contains(a)) adj[b].Add(a);
        }

        float diagTimer;

        void BuildClusters(Vector3[] baked, float rMax)
        {
            float cell = Mathf.Max(0.01f, rMax / 3.2f);
            Dictionary<long, int> grid = new Dictionary<long, int>();
            List<Vector3> sums = new List<Vector3>();
            List<int> counts = new List<int>();
            for (int i = 0; i < n; i++)
            {
                Vector3 p = baked[idx[i]];
                long k = ((long)(Mathf.FloorToInt(p.x / cell) & 0xFFFFF) << 40)
                       | ((long)(Mathf.FloorToInt(p.y / cell) & 0xFFFFF) << 20)
                       | (long)(Mathf.FloorToInt(p.z / cell) & 0xFFFFF);
                int c;
                if (!grid.TryGetValue(k, out c)) { c = sums.Count; grid[k] = c; sums.Add(Vector3.zero); counts.Add(0); }
                sums[c] += p; counts[c]++;
            }
            K = sums.Count;
            clusterC = new Vector3[K];
            for (int c = 0; c < K; c++) clusterC[c] = sums[c] / Mathf.Max(1, counts[c]);
            cAcc = new Vector3[K]; cVec = new Vector3[K]; cWsum = new float[K];

            float sig = cell; float inv2s2 = 1f / (2f * sig * sig);

            // cluster-to-cluster smoothing weights (gaussian by centre distance, incl. self)
            cNbr = new int[K][]; cNbrW = new float[K][];
            for (int a = 0; a < K; a++)
            {
                List<int> ids = new List<int>(); List<float> ws = new List<float>();
                for (int b = 0; b < K; b++)
                {
                    float d2 = (clusterC[a] - clusterC[b]).sqrMagnitude;
                    if (d2 > (2.2f * cell) * (2.2f * cell)) continue;
                    ids.Add(b); ws.Add(Mathf.Exp(-d2 * inv2s2));
                }
                float tot = 0f; for (int j = 0; j < ws.Count; j++) tot += ws[j];
                for (int j = 0; j < ws.Count; j++) ws[j] /= Mathf.Max(1e-6f, tot);
                cNbr[a] = ids.ToArray(); cNbrW[a] = ws.ToArray();
            }

            // per-node: top-4 nearest clusters with gaussian weights (normalised)
            ncIdx = new int[n][]; ncW = new float[n][];
            for (int i = 0; i < n; i++)
            {
                Vector3 p = baked[idx[i]];
                int[] bi = { -1, -1, -1, -1 }; float[] bw = { 0f, 0f, 0f, 0f };
                for (int c = 0; c < K; c++)
                {
                    float g = Mathf.Exp(-(p - clusterC[c]).sqrMagnitude * inv2s2);
                    for (int s = 0; s < 4; s++)
                        if (g > bw[s])
                        {
                            for (int t = 3; t > s; t--) { bw[t] = bw[t - 1]; bi[t] = bi[t - 1]; }
                            bw[s] = g; bi[s] = c; break;
                        }
                }
                float sum = bw[0] + bw[1] + bw[2] + bw[3];
                int cnt = 0; for (int s = 0; s < 4; s++) if (bi[s] >= 0 && bw[s] > 1e-5f) cnt++;
                ncIdx[i] = new int[cnt]; ncW[i] = new float[cnt];
                int at = 0;
                for (int s = 0; s < 4; s++)
                    if (bi[s] >= 0 && bw[s] > 1e-5f) { ncIdx[i][at] = bi[s]; ncW[i][at] = bw[s] / Mathf.Max(1e-6f, sum); at++; }
            }
        }

        // ==================== dynamics (substepped) ====================
        public void StepDynamics(Vector3[] baked, Vector3[] normals, float dt, Vector3 localDown, Transform proxy)
        {
            if (n == 0) return;
            simT += dt;
            lastLocalDown = localDown;

            if (!primed)
            {
                for (int i = 0; i < n; i++) prevTarget[i] = baked[idx[i]];
                Vector3 c0 = Vector3.zero;
                for (int i = 0; i < n; i++) c0 += baked[idx[i]];
                c0 /= n;
                float rMax = 0.0001f;
                for (int i = 0; i < n; i++)
                { float r = (baked[idx[i]] - c0).magnitude; if (r > rMax) rMax = r; }
                for (int i = 0; i < n; i++)
                    jelloF[i] = Mathf.Cos(Mathf.PI * (baked[idx[i]] - c0).magnitude / rMax);
                BuildClusters(baked, rMax);
                primed = true;
            }

            float poseFactor = capturedPoseFactor;   // captured main-side (refBone is a Transform)
            Vector3 grav = localDown * (cfg.gravity * 0.6f * poseFactor);

            float stiff = Mathf.Max(0.5f, cfg.stiffness);
            float damp = Mathf.Clamp01(cfg.damping);
            float clothC2 = Mathf.Lerp(40f, 400f, Mathf.Clamp01(cfg.clothSize)) * Mathf.Max(0.05f, cfg.waveSpeed);
            float clothDampMul = Mathf.Exp(-2.5f * dt);
            float jelloOmega = 2f * Mathf.PI * Mathf.Lerp(6f, 1.5f, Mathf.Clamp01(cfg.jelloSize)) * Mathf.Max(0.05f, cfg.waveSpeed);

            Vector3 centroid = Vector3.zero, prevCentroid = Vector3.zero;
            for (int i = 0; i < n; i++) { centroid += baked[idx[i]]; prevCentroid += prevTarget[i]; }
            centroid /= n; prevCentroid /= n;
            Vector3 dc = centroid - prevCentroid;
            bool teleport = dc.sqrMagnitude > 0.04f;
            Vector3 centroidVel = teleport ? Vector3.zero : dc / Mathf.Max(dt, 1e-5f);
            lastCentroid = centroid; lastCentroidVel = centroidVel;

            waveCooldown -= dt;
            if (cfg.liquid > 0.001f && waveCooldown <= 0f && centroidVel.magnitude > 0.25f)
            {
                Wave wv; wv.pos = centroid; wv.t = 0f;
                wv.amp = Mathf.Min(0.03f, centroidVel.magnitude * 0.01f) * cfg.liquid;
                waves.Add(wv); if (waves.Count > 6) waves.RemoveAt(0);
                waveCooldown = 0.15f;
            }
            for (int k = waves.Count - 1; k >= 0; k--)
            {
                Wave wv = waves[k]; wv.t += dt * Mathf.Max(0.05f, cfg.waveSpeed); waves[k] = wv;
                if (wv.t > 2.5f) waves.RemoveAt(k);
            }

            if (cfg.jello > 0.001f)
            {
                if (!teleport) jelloVel -= dc * 14f;
                jelloVel += -jelloOmega * jelloOmega * jelloPos * dt;
                jelloVel *= Mathf.Exp(-2.0f * dt);
                jelloPos += jelloVel * dt;
                float jm = jelloPos.magnitude; if (jm > 0.1f) jelloPos *= 0.1f / jm;
            }
            else { jelloPos = Vector3.zero; jelloVel = Vector3.zero; }

            if (cfg.sway > 0.001f)
            {
                Vector3 lat = dc - localDown * Vector3.Dot(dc, localDown);
                if (!teleport) swayVel -= lat * 10f;
                float swOmega = 2f * Mathf.PI * 1.3f * Mathf.Max(0.05f, cfg.waveSpeed);
                swayVel += -swOmega * swOmega * swayPos * dt;
                swayVel *= Mathf.Exp(-1.6f * dt);
                swayPos += swayVel * dt;
                float sm = swayPos.magnitude; if (sm > 0.08f) swayPos *= 0.08f / sm;
            }
            else { swayPos = Vector3.zero; swayVel = Vector3.zero; }

            if (cfg.twistJiggle > 0.001f)
            {
                Vector3 lat = dc - localDown * Vector3.Dot(dc, localDown);
                Vector3 fwd = Vector3.Cross(localDown, Vector3.right);
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.Cross(localDown, Vector3.forward);
                if (!teleport) twistVel -= Vector3.Dot(Vector3.Cross(localDown, lat), fwd.normalized) * 40f;
                float twOmega = 2f * Mathf.PI * 2.0f * Mathf.Max(0.05f, cfg.waveSpeed);
                twistVel += -twOmega * twOmega * twist * dt;
                twistVel *= Mathf.Exp(-1.8f * dt);
                twist += twistVel * dt;
                twist = Mathf.Clamp(twist, -0.6f, 0.6f);
            }
            else { twist = 0f; twistVel = 0f; }

            if (cfg.cellulite > 0.0001f && cellNoiseSize != cfg.celluliteSize)
            {
                cellNoise = new float[n];
                float freq = Mathf.Lerp(220f, 25f, Mathf.Clamp01(cfg.celluliteSize));
                for (int i = 0; i < n; i++)
                {
                    Vector3 p0 = baked[idx[i]];
                    float v1 = Mathf.PerlinNoise(p0.x * freq, p0.y * freq * 0.9f + p0.z * freq * 0.7f);
                    float v2 = Mathf.PerlinNoise(p0.z * freq * 1.3f + 11.7f, p0.x * freq * 1.1f + 5.3f);
                    cellNoise[i] = (v1 + v2) * 0.5f - 0.5f;
                }
                cellNoiseSize = cfg.celluliteSize;
            }

            float maxOff = Mathf.Max(0.001f, cfg.maxOffset);
            float dampMul = Mathf.Pow(1f - damp, dt * 30f);
            float r2max = 0f;

            for (int i = 0; i < n; i++)
            {
                int vi = idx[i];
                if (vi >= baked.Length) continue;
                Vector3 target = baked[vi];
                Vector3 db = target - prevTarget[i];
                prevTarget[i] = target;
                if (db.sqrMagnitude > 0.04f) db = Vector3.zero;

                vel[i] -= db * (cfg.bounce * 18f * w[i]);

                Vector3 F = offset[i] * (-stiff) + grav * w[i];
                vel[i] += F * dt;
                vel[i] *= dampMul;
                offset[i] += vel[i] * dt;

                float m = offset[i].magnitude;
                if (m > maxOff) { offset[i] *= maxOff / m; vel[i] *= 0.5f; }

                if (cfg.clothRipple > 0.001f && nbr[i].Length > 0)
                {
                    clothV[i] -= Vector3.Dot(db, normals[vi]) * 6f * w[i];
                    float lapH = 0f;
                    int[] nb = nbr[i];
                    for (int j = 0; j < nb.Length; j++) lapH += clothH[nb[j]];
                    lapH = lapH / nb.Length - clothH[i];
                    clothV[i] += (clothC2 * lapH - 30f * clothH[i]) * dt;
                    clothV[i] *= clothDampMul;
                    clothH[i] += clothV[i] * dt;
                    clothH[i] = Mathf.Clamp(clothH[i], -maxOff, maxOff);
                }
                else clothH[i] = 0f;

                float rr = (target + offset[i] - lastCentroid).sqrMagnitude;
                if (rr > r2max) r2max = rr;
            }
            boundCenter = lastCentroid;
            boundRadius = Mathf.Sqrt(r2max);
        }

        // ==================== collision field + output (once per frame) ====================
        public void FieldAndWrite(Vector3[] baked, Vector3[] normals, Vector3[] disp,
                                  float dt, List<SquishSim> others, Transform proxy)
        {
            if (n == 0 || !primed) return;
            Vector3 localDown = lastLocalDown;
            float maxOff = Mathf.Max(0.001f, cfg.maxOffset);

            // cohesion on the jiggle offsets (keeps the blob connected)
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < n; i++)
                {
                    int[] nb = nbr[i];
                    if (nb.Length == 0) continue;
                    Vector3 avg = Vector3.zero;
                    for (int j = 0; j < nb.Length; j++) avg += offset[nb[j]];
                    avg /= nb.Length;
                    offset[i] = Vector3.Lerp(offset[i], avg, 0.22f);
                }

            // ---- raw UNITLESS penetration field (0..1 per collider, dir * pen/radius) ----
            System.Array.Clear(colTarget, 0, colTarget.Length);
            int ncol = colCfg != null ? colCfg.Length : 0;
            for (int c = 0; c < ncol; c++)
            {
                if (!colCfg[c].enabled) continue;
                bool isMesh = !string.IsNullOrEmpty(colCfg[c].mesh);
                MeshColliderCloud cl = (isMesh && clouds != null && c < clouds.Length) ? clouds[c] : null;
                if (isMesh && cl == null) continue;
                if (!isMesh && (capValid == null || c >= capValid.Length || !capValid[c])) continue;

                Vector3 cp0 = Vector3.zero, cp1 = Vector3.zero; float rad = 0f;
                if (!isMesh)
                {
                    // captured on the main thread (worker-safe)
                    cp0 = capCp0[c]; cp1 = capCp1[c]; rad = capRad[c];
                }

                for (int i = 0; i < n; i++)
                {
                    Vector3 p = baked[idx[i]];
                    if (isMesh)
                    {
                        Vector3 pn;
                        if (cl.TryPushNorm(p, out pn)) colTarget[i] += pn;
                    }
                    else
                    {
                        Vector3 cp = colCfg[c].length > 0f ? ClosestOnSegment(cp0, cp1, p) : cp0;
                        Vector3 d = p - cp; float dist = d.magnitude;
                        if (dist < rad && dist > 1e-6f) colTarget[i] += d / dist * (1f - dist / rad);
                    }
                }
            }

            // region-vs-region (breasts pressing together), unitless
            if (cfg.selfSquish > 0.001f && others != null)
            {
                float lvl = Mathf.Clamp(cfg.selfSquish, 0f, 2f) * 0.5f;
                for (int o = 0; o < others.Count; o++)
                {
                    SquishSim os = others[o];
                    if (os == this || os.boundRadius <= 0f || boundRadius <= 0f) continue;
                    Vector3 dC = boundCenter - os.boundCenter;
                    float dist = dC.magnitude;
                    float span = (boundRadius + os.boundRadius) * 0.8f;
                    float overlap = span - dist;
                    if (overlap <= 0f || dist < 1e-5f) continue;
                    Vector3 dir = dC / dist;
                    float inv2s2 = 1f / (2f * os.boundRadius * os.boundRadius + 1e-6f);
                    float ovNorm = Mathf.Clamp01(overlap / Mathf.Max(0.02f, span * 0.5f));
                    for (int i = 0; i < n; i++)
                    {
                        float d2 = (baked[idx[i]] - os.boundCenter).sqrMagnitude;
                        colTarget[i] += dir * (ovNorm * lvl * Mathf.Exp(-d2 * inv2s2));
                    }
                }
            }

            float dbgRaw = 0f;
            if (MeshProxy.debugDraw)
                for (int i = 0; i < n; i++) { float m2 = colTarget[i].sqrMagnitude; if (m2 > dbgRaw) dbgRaw = m2; }

            // per-node cap + ADAPTIVE baseline (sustained pose/breathing contact absorbed
            // slowly; new pokes act instantly; released contact recovers fast)
            float upA = 1f - Mathf.Exp(-dt / 6f);
            float dnA = 1f - Mathf.Exp(-dt / 0.5f);
            if (rawPen == null || rawPen.Length != n) rawPen = new float[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 raw = colTarget[i];
                float rm = raw.magnitude;
                if (rm > 1.5f) { raw *= 1.5f / rm; rm = 1.5f; }
                rawPen[i] = rm;
                // absorb only WEAK sustained contact (resting pose / breathing). A real press
                // (strong pen) must never be absorbed or the dent decays during a hold and
                // the skin creeps back over the collider until it's swallowed.
                bool rising = rm * rm > colBase[i].sqrMagnitude;
                float a = rising ? (rm > 0.3f ? 0f : upA) : dnA;
                colBase[i] = Vector3.Lerp(colBase[i], raw, a);
                float bm = colBase[i].magnitude;
                if (bm > 0.3f) colBase[i] *= 0.3f / bm;   // baseline can never hide more than this
                float effM = Mathf.Max(0f, rm - colBase[i].magnitude);
                colTarget[i] = rm > 1e-6f ? raw * (effM / rm) : Vector3.zero;
            }

            float dbgEff = 0f;
            if (MeshProxy.debugDraw)
                for (int i = 0; i < n; i++) { float m2 = colTarget[i].sqrMagnitude; if (m2 > dbgEff) dbgEff = m2; }

            // ---- cluster RBF smoothing: project the contact field onto coarse control
            // points and interpolate back â€” a big soft blobby dent at ANY mesh density,
            // peak-preserved so smoothing widens the dent without flattening it.
            if (K > 0 && ncIdx != null)
            {
                float peakBefore = 0f;
                for (int i = 0; i < n; i++) { float m2 = colTarget[i].sqrMagnitude; if (m2 > peakBefore) peakBefore = m2; }

                // MAX-projection: each cluster takes its strongest contact (an averaging
                // projection diluted sparse contact ~15x â€” measured raw 0.96 -> rbf 0.05)
                System.Array.Clear(cVec, 0, K);
                for (int i = 0; i < n; i++)
                {
                    int[] ci = ncIdx[i]; float[] cw = ncW[i];
                    for (int j = 0; j < ci.Length; j++)
                    {
                        Vector3 cand = colTarget[i] * cw[j];
                        if (cand.sqrMagnitude > cVec[ci[j]].sqrMagnitude) cVec[ci[j]] = cand;
                    }
                }
                // one smoothing pass across clusters
                for (int c = 0; c < K; c++)
                {
                    Vector3 s = Vector3.zero;
                    int[] ids = cNbr[c]; float[] ws = cNbrW[c];
                    for (int j = 0; j < ids.Length; j++) s += cVec[ids[j]] * ws[j];
                    cAcc[c] = s;
                }
                for (int i = 0; i < n; i++)
                {
                    int[] ci = ncIdx[i]; float[] cw = ncW[i];
                    Vector3 v = Vector3.zero;
                    for (int j = 0; j < ci.Length; j++) v += cAcc[ci[j]] * cw[j];
                    colTarget[i] = v;
                }

                if (peakBefore > 1e-10f)
                {
                    float peakAfter = 0f;
                    for (int i = 0; i < n; i++) { float m2 = colTarget[i].sqrMagnitude; if (m2 > peakAfter) peakAfter = m2; }
                    float boost = Mathf.Min(6f, Mathf.Sqrt(peakBefore) / Mathf.Max(1e-5f, Mathf.Sqrt(peakAfter)));
                    for (int i = 0; i < n; i++) colTarget[i] *= boost;
                }
            }

            float dbgRbf = 0f;
            for (int i = 0; i < n; i++) { float m2 = colTarget[i].sqrMagnitude; if (m2 > dbgRbf) dbgRbf = m2; }

            // depth scale: user override, else proportional to the REGION SIZE
            float dentMax = cfg.squishDepth > 0.001f
                ? cfg.squishDepth
                : Mathf.Clamp(boundRadius * 0.4f, 0.02f, 0.08f);
            float squishS = Mathf.Clamp(cfg.squish, 0f, 2f);
            float rigidS = Mathf.Clamp(cfg.bulge, 0f, 2f);

            Vector3 meanPush = Vector3.zero;
            for (int i = 0; i < n; i++) meanPush += colTarget[i];
            meanPush /= n;
            float pushMag = meanPush.magnitude;
            if (pushMag - prevPushMag > 0.005f && pushMag > 1e-5f)
            {
                Vector3 kd = meanPush / pushMag;
                for (int i = 0; i < n; i++) vel[i] += kd * ((pushMag - prevPushMag) * dentMax * 25f * w[i]);
            }
            prevPushMag = pushMag;

            // sideways volume bulge: flesh displaced by the contact flows outward around it
            Vector3 contactC = Vector3.zero; float contactW = 0f;
            for (int i = 0; i < n; i++)
            {
                float m = colTarget[i].magnitude;
                contactC += baked[idx[i]] * m; contactW += m;
            }
            if (contactW > 1e-5f) contactC /= contactW;
            Vector3 meanDir = pushMag > 1e-6f ? meanPush / pushMag : Vector3.zero;
            if (contactW > 1e-5f && pushMag > 0.01f && cfg.bulge > 0.001f)
            {
                float sigma = Mathf.Max(0.03f, boundRadius * 0.8f);
                float inv2s2 = 1f / (2f * sigma * sigma);
                for (int i = 0; i < n; i++)
                {
                    Vector3 radial = baked[idx[i]] - contactC;
                    radial -= meanDir * Vector3.Dot(radial, meanDir);   // sideways only
                    float rl = radial.magnitude;
                    if (rl < 1e-5f) continue;
                    float g = Mathf.Exp(-rl * rl * inv2s2);
                    colTarget[i] += radial / rl * (pushMag * rigidS * 0.9f * g);
                }
            }

            float ease = 1f - Mathf.Exp(-14f * dt);
            float colCap = dentMax * 1.3f;
            for (int i = 0; i < n; i++)
            {
                // soft layer: dimple (squish) + whole-blob shift (bulge shares with sideways flow)
                Vector3 tgtC = colTarget[i] * (dentMax * squishS * 0.5f * w[i]) + meanPush * (dentMax * rigidS * 0.5f * w[i]);
                float cm = tgtC.magnitude;
                if (cm > colCap) tgtC *= colCap / cm;
                // UNIFORM ease only — a per-vert sticky/slow-release switch made adjacent
                // verts hold dents 4cm apart (11x edge stretch, THE shard source; measured
                // via the F11 dump). Hold-persistence is the barrier layer's job now.
                colCur[i] = Vector3.Lerp(colCur[i], tgtC, ease);
            }

            // ---- DEPTH-FIELD barrier: instead of per-vert positional projection (which
            // always ends up fighting whatever smoothing follows it — the shard machine),
            // measure a SCALAR "how deep must this vert dent to clear the collider",
            // smooth that scalar over the RBF clusters (a scalar can't fight anything),
            // and displace along a smoothed direction field by the smoothed depth.
            // Contact verts clear exactly; neighbours get an interpolated smooth skirt.
            if (nSm == null || nSm.Length != n) nSm = new Vector3[n];
            for (int i = 0; i < n; i++) nSm[i] = normals[idx[i]];
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < n; i++)
                {
                    int[] nb = nbr[i]; if (nb.Length == 0) continue;
                    Vector3 s = nSm[i];
                    for (int j = 0; j < nb.Length; j++) s += nSm[nb[j]];
                    float sm = s.magnitude;
                    if (sm > 1e-6f) nSm[i] = s / sm;
                }

            if (needF == null || needF.Length != n) { needF = new float[n]; dirF = new Vector3[n]; hardOff = new Vector3[n]; excF = new float[n]; }
            for (int i = 0; i < n; i++)
            {
                needF[i] = 0f; dirF[i] = -nSm[i]; excF[i] = 0f;
                Vector3 p = baked[idx[i]] + colCur[i];
                for (int c = 0; c < ncol; c++)
                {
                    if (!colCfg[c].enabled) continue;
                    bool isMesh = !string.IsNullOrEmpty(colCfg[c].mesh);
                    if (isMesh)
                    {
                        MeshColliderCloud cl = (clouds != null && c < clouds.Length) ? clouds[c] : null;
                        if (cl == null) continue;
                        Vector3 push;
                        if (cl.TryPushPlanar(p, cl.radius, out push))
                        {
                            // (no per-vert outward-drift strip — binary per-vert edits of the
                            // soft layer create neighbour divergence, same class as the
                            // sticky-ease shard bug)
                            float pm = push.magnitude;
                            if (pm < 1e-6f) continue;
                            // OCCLUSION BIAS: target the skin slightly BELOW the collider
                            // surface. Skin exactly AT the surface renders grazing slivers
                            // (the torn edge); skin just under it is cleanly occluded by the
                            // glove — the contact boundary becomes invisible by construction.
                            float depth = pm - cl.radius - 0.003f;
                            // PENETRATION LIMITER: past maxDent the flesh "gives way" — the
                            // excess feeds the evacuation layers instead of deepening the dent
                            float dentCap = cfg.maxDent > 0.005f ? cfg.maxDent : 0.07f;
                            if (depth > dentCap)
                            {
                                float exc = depth - dentCap;
                                if (exc > 0.2f) exc = 0.2f;
                                if (exc > excF[i]) excF[i] = exc;
                                depth = dentCap;
                            }
                            if (depth <= needF[i]) continue;
                            needF[i] = depth;
                            // direction by ALIGNMENT: radial when it wouldn't wrap the skin
                            // over the collider (cleavage/sides), inward dive otherwise
                            dirF[i] = Vector3.Dot(push, nSm[i]) < pm * 0.25f ? push / pm : -nSm[i];
                        }
                    }
                    else
                    {
                        if (capValid == null || c >= capValid.Length || !capValid[c]) continue;
                        Vector3 cp0 = capCp0[c];   // captured main-side (worker-safe)
                        float rad = capRad[c];
                        Vector3 cp = cp0;
                        if (colCfg[c].length > 0f)
                            cp = ClosestOnSegment(cp0, capCp1[c], p);
                        Vector3 d = p - cp; float dist = d.magnitude;
                        if (dist < rad && dist > 1e-6f && rad - dist > needF[i])
                        { needF[i] = rad - dist; dirF[i] = d / dist; }
                    }
                }
            }

            // evacuation aggregation: bin the excess by nearest driver-bone cluster and
            // as a whole-region mean (for the boneless blob-shift fallback)
            evacExcess = 0f; Vector3 evacDirAcc = Vector3.zero;
            if (evacClusterCount > 0)
                for (int k = 0; k < evacClusterCount; k++) { evacClusterExc[k] = 0f; evacClusterDir[k] = Vector3.zero; }
            if (evacCluster2Count > 0)
                for (int k = 0; k < evacCluster2Count; k++) { evacCluster2Exc[k] = 0f; evacCluster2Dir[k] = Vector3.zero; }
            for (int i = 0; i < n; i++)
            {
                if (excF[i] <= 0f) continue;
                Vector3 d = dirF[i];
                if (excF[i] > evacExcess) evacExcess = excF[i];
                evacDirAcc += d * excF[i];
                if (evacClusterCount > 0)
                {
                    int best = 0; float bd = float.MaxValue;
                    Vector3 bp = baked[idx[i]];
                    for (int k = 0; k < evacClusterCount; k++)
                    {
                        float dd = (bp - evacClusterPos[k]).sqrMagnitude;
                        if (dd < bd) { bd = dd; best = k; }
                    }
                    if (excF[i] > evacClusterExc[best]) evacClusterExc[best] = excF[i];
                    evacClusterDir[best] += d * excF[i];
                }
                if (evacCluster2Count > 0)
                {
                    // child set binned independently (nearest child bone) — the residual
                    // excess a child sees already includes the parent's applied shift
                    int b2 = 0; float bd2 = float.MaxValue;
                    Vector3 bp2 = baked[idx[i]];
                    for (int k = 0; k < evacCluster2Count; k++)
                    {
                        float dd = (bp2 - evacCluster2Pos[k]).sqrMagnitude;
                        if (dd < bd2) { bd2 = dd; b2 = k; }
                    }
                    if (excF[i] > evacCluster2Exc[b2]) evacCluster2Exc[b2] = excF[i];
                    evacCluster2Dir[b2] += d * excF[i];
                }
            }
            float edm = evacDirAcc.magnitude;
            evacDir = edm > 1e-6f ? evacDirAcc / edm : Vector3.zero;
            if (evacClusterCount > 0)
                for (int k = 0; k < evacClusterCount; k++)
                {
                    float m2 = evacClusterDir[k].magnitude;
                    if (m2 > 1e-6f) evacClusterDir[k] /= m2;
                }
            if (evacCluster2Count > 0)
                for (int k = 0; k < evacCluster2Count; k++)
                {
                    float m3 = evacCluster2Dir[k].magnitude;
                    if (m3 > 1e-6f) evacCluster2Dir[k] /= m3;
                }

            // scalar depth: cluster MAX-projection + one smoothing pass + interpolation —
            // smooth skirt around the contact with peaks preserved
            if (K > 0 && ncIdx != null)
            {
                if (cSc == null || cSc.Length != K) cSc = new float[K];
                if (cWsum == null || cWsum.Length != K) cWsum = new float[K];
                System.Array.Clear(cSc, 0, K);
                for (int i = 0; i < n; i++)
                {
                    int[] ci = ncIdx[i]; float[] cw = ncW[i];
                    for (int j = 0; j < ci.Length; j++)
                    { float cand = needF[i] * cw[j]; if (cand > cSc[ci[j]]) cSc[ci[j]] = cand; }
                }
                for (int c = 0; c < K; c++)
                {
                    float s = 0f;
                    int[] ids = cNbr[c]; float[] ws = cNbrW[c];
                    for (int j = 0; j < ids.Length; j++) s += cSc[ids[j]] * ws[j];
                    cWsum[c] = s;
                }
                for (int i = 0; i < n; i++)
                {
                    int[] ci = ncIdx[i]; float[] cw = ncW[i];
                    float sk = 0f;
                    for (int j = 0; j < ci.Length; j++) sk += cWsum[ci[j]] * cw[j];
                    if (sk > needF[i]) needF[i] = sk;
                }

                // marshmallow rounding as a MAX-ENVELOPE: take the larger of own depth and
                // the cluster-average — rounds the outer slope smoothly but NEVER shaves
                // clearance below the collider surface (averaging down left the skin exactly
                // AT the arm surface, and grazing surface intersections render as the ragged
                // torn edge no amount of smoothing could fix)
                for (int round = 0; round < 2; round++)
                {
                    System.Array.Clear(cSc, 0, K);
                    System.Array.Clear(cWsum, 0, K);
                    for (int i = 0; i < n; i++)
                    {
                        int[] ci = ncIdx[i]; float[] cw = ncW[i];
                        for (int j = 0; j < ci.Length; j++) { cSc[ci[j]] += needF[i] * cw[j]; cWsum[ci[j]] += cw[j]; }
                    }
                    for (int c = 0; c < K; c++) if (cWsum[c] > 1e-6f) cSc[c] /= cWsum[c];
                    for (int i = 0; i < n; i++)
                    {
                        int[] ci = ncIdx[i]; float[] cw = ncW[i];
                        float sm = 0f;
                        for (int j = 0; j < ci.Length; j++) sm += cSc[ci[j]] * cw[j];
                        if (sm > needF[i]) needF[i] = sm;
                    }
                }
            }

            // vertex-scale polish, also max-envelope (fills silhouette steps upward)
            for (int pass = 0; pass < 3; pass++)
                for (int i = 0; i < n; i++)
                {
                    int[] nb = nbr[i]; if (nb.Length == 0) continue;
                    float s = needF[i];
                    for (int j = 0; j < nb.Length; j++) s += needF[nb[j]];
                    s /= nb.Length + 1;
                    if (s > needF[i]) needF[i] = s;
                }

            // cluster-coherent direction field (need-weighted so contact dirs dominate):
            // the per-vert radial-vs-inward flip at the arm silhouette was the sawtooth
            if (K > 0 && ncIdx != null)
            {
                System.Array.Clear(cVec, 0, K);
                System.Array.Clear(cWsum, 0, K);
                for (int i = 0; i < n; i++)
                {
                    float wgt = needF[i] + 1e-4f;
                    int[] ci = ncIdx[i]; float[] cw = ncW[i];
                    for (int j = 0; j < ci.Length; j++) { cVec[ci[j]] += dirF[i] * (cw[j] * wgt); cWsum[ci[j]] += cw[j] * wgt; }
                }
                for (int c = 0; c < K; c++) if (cWsum[c] > 1e-6f) cVec[c] /= cWsum[c];
                for (int i = 0; i < n; i++)
                {
                    int[] ci = ncIdx[i]; float[] cw = ncW[i];
                    Vector3 sm = Vector3.zero;
                    for (int j = 0; j < ci.Length; j++) sm += cVec[ci[j]] * cw[j];
                    Vector3 bl = dirF[i] + sm * 2.5f;
                    float bm = bl.magnitude;
                    if (bm > 1e-6f) dirF[i] = bl / bm;
                }
            }

            // smooth the direction field so the skirt bends coherently with the contact
            for (int pass = 0; pass < 3; pass++)
                for (int i = 0; i < n; i++)
                {
                    int[] nb = nbr[i]; if (nb.Length == 0) continue;
                    Vector3 s = dirF[i];
                    for (int j = 0; j < nb.Length; j++) s += dirF[nb[j]];
                    float sm = s.magnitude;
                    if (sm > 1e-6f) dirF[i] = s / sm;
                }

            // the barrier is an ABSOLUTE per-frame target kept in its own layer — never an
            // increment onto persistent state. Skirt verts have no contact feedback, so an
            // increment integrates without bound (runaway sheets); a target cannot.
            // Paint-weight ramp: an unweighted barrier steps to zero at the ragged painted
            // boundary — taper it so displacement fades smoothly into unpainted skin.
            // (Full protection needs full paint over the contact area.)
            // smoothed paint weights for the barrier ramp — raw hand-painted weights are
            // vertex-noisy, and ramping by them re-injected that noise into the barrier
            // (measured: residual stretched verts sat at w~0.85 on the paint gradient)
            if (wSm == null || wSm.Length != n)
            {
                wSm = new float[n];
                for (int i = 0; i < n; i++) wSm[i] = w[i];
                for (int pass = 0; pass < 4; pass++)
                    for (int i = 0; i < n; i++)
                    {
                        int[] nb = nbr[i]; if (nb.Length == 0) continue;
                        float s = wSm[i];
                        for (int j = 0; j < nb.Length; j++) s += wSm[nb[j]];
                        wSm[i] = s / (nb.Length + 1);
                    }
            }

            // blob evacuation (works with or without driver bones): the whole painted
            // region shifts away from the press and squashes, water-balloon style
            Vector3 evacTgt = evacDir * (evacExcess * Mathf.Clamp(cfg.evacBlob, 0f, 2f) * 3f);
            float etm = evacTgt.magnitude;
            if (etm > 0.25f) evacTgt *= 0.25f / etm;
            evacCur = Vector3.Lerp(evacCur, evacTgt, 1f - Mathf.Exp(-8f * dt));
            float evM = evacCur.magnitude;
            Vector3 evD = evM > 1e-5f ? evacCur / evM : Vector3.zero;
            float evSq = Mathf.Clamp01(evM / Mathf.Max(0.05f, boundRadius));

            float dbgCur = 0f;
            if (totF == null || totF.Length != n) totF = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                needF[i] *= Mathf.Clamp01(wSm[i] * 2f);
                Vector3 tgtH = needF[i] > 0f ? dirF[i] * needF[i] : Vector3.zero;
                hardOff[i] = Vector3.Lerp(hardOff[i], tgtH, 0.6f);
                totF[i] = colCur[i] + hardOff[i];
                // blob evac ADDS ON TOP. (It used to be added BEFORE the assignment above
                // and was silently overwritten — the evacBlob slider did nothing.)
                // Smoothed weights per the shard law: raw paint is vertex-noisy.
                if (evM > 1e-5f)
                {
                    Vector3 rr2 = baked[idx[i]] - boundCenter;
                    float along = Vector3.Dot(rr2, evD);
                    // shift + flatten along the press + widen sideways (volume look)
                    totF[i] += (evacCur - evD * (along * 0.35f * evSq) + (rr2 - evD * along) * (0.18f * evSq)) * wSm[i];
                }
                float c2 = totF[i].sqrMagnitude; if (c2 > dbgCur) dbgCur = c2;
            }
            // final cosmetic polish: light one-ring smoothing of the TOTAL offset — erases
            // the last vertex-scale residue (0.1% of edges measured) without fighting the
            // barrier (its occlusion bias tolerates a few mm)
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < n; i++)
                {
                    int[] nb = nbr[i]; if (nb.Length == 0) continue;
                    Vector3 avg = Vector3.zero;
                    for (int j = 0; j < nb.Length; j++) avg += totF[nb[j]];
                    totF[i] = Vector3.Lerp(totF[i], avg / nb.Length, 0.35f);
                }

            // field energy drives the sleep gate: dent + blob evac + bone evac demand
            lastFieldEnergy = dbgCur + evacCur.sqrMagnitude + evacExcess * evacExcess;

            // pipeline diagnostics every ~2 s: raw contact -> after baseline -> after RBF -> final metres
            diagTimer += dt;
            if (MeshProxy.debugDraw && diagTimer > 2f)
            {
                diagTimer = 0f;
                int cloudPts = 0;
                if (clouds != null)
                    for (int c = 0; c < clouds.Length; c++) if (clouds[c] != null) cloudPts += clouds[c].nd;
                Debug.Log("[Squish] dbg '" + cfg.name + "' raw=" + Mathf.Sqrt(dbgRaw).ToString("0.000")
                    + " eff=" + Mathf.Sqrt(dbgEff).ToString("0.000")
                    + " rbf=" + Mathf.Sqrt(dbgRbf).ToString("0.000")
                    + " curM=" + Mathf.Sqrt(dbgCur).ToString("0.000")
                    + " K=" + K + " cloud=" + cloudPts + " dentMax=" + dentMax.ToString("0.00"));
            }

            // ---- output: jiggle + collision + all the styled modes, written to every duplicate ----
            float amp = Mathf.Max(0f, cfg.jiggle);
            float liqLambda = Mathf.Lerp(0.02f, 0.25f, Mathf.Clamp01(cfg.liquidSize));
            float pulseHz = Mathf.Lerp(0.15f, 2.2f, Mathf.Clamp01(cfg.pulseRate));
            float pulseS = Mathf.Sin(simT * 2f * Mathf.PI * pulseHz);
            Vector3 stretchDir = Vector3.zero; float stretchAmt = 0f;
            if (cfg.stretch > 0.001f)
            {
                float spd = lastCentroidVel.magnitude;
                if (spd > 0.05f) { stretchDir = lastCentroidVel / spd; stretchAmt = Mathf.Min(spd * cfg.stretch * 0.12f, 0.5f); }
            }
            float turbF = Mathf.Lerp(60f, 8f, Mathf.Clamp01(cfg.turbSize));

            bool blewUp = false;
            for (int i = 0; i < n; i++)
            {
                int vi = idx[i];
                if (vi >= baked.Length) continue;
                Vector3 target = baked[vi];
                Vector3 nrm = normals[vi];

                Vector3 outOff = totF != null ? totF[i] : colCur[i];   // smoothed soft dent + barrier
                float om = outOff.sqrMagnitude;
                if (float.IsNaN(om) || om > 1f) { outOff = Vector3.zero; blewUp = true; }

                int[] mem = members[i];
                for (int mIdx = 0; mIdx < mem.Length; mIdx++)
                    if (mem[mIdx] < disp.Length) disp[mem[mIdx]] += outOff;
            }
            if (blewUp)
            {
                pendingBlewUp = true;   // logged by the MAIN thread (a worker must never Debug.Log)
                ResetState();
            }
        }

        static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float t = Vector3.Dot(p - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude);
            return a + ab * Mathf.Clamp01(t);
        }

        // per-node field dump for offline analysis (F11)
        public void DumpCsv(System.Text.StringBuilder sb)
        {
            for (int i = 0; i < n; i++)
            {
                float nf = needF != null && i < needF.Length ? needF[i] : 0f;
                float ho = hardOff != null && i < hardOff.Length ? hardOff[i].magnitude : 0f;
                float rp = rawPen != null && i < rawPen.Length ? rawPen[i] : 0f;
                sb.Append(idx[i]).Append(',').Append(w[i].ToString("0.000")).Append(',')
                  .Append(nf.ToString("0.0000")).Append(',')
                  .Append(colCur[i].magnitude.ToString("0.0000")).Append(',')
                  .Append(ho.ToString("0.0000")).Append(',')
                  .Append(rp.ToString("0.000")).Append('\n');
            }
        }

        public void ResetState()
        {
            if (offset == null) return;
            for (int i = 0; i < offset.Length; i++)
            {
                offset[i] = Vector3.zero; vel[i] = Vector3.zero;
                if (clothH != null) { clothH[i] = 0f; clothV[i] = 0f; }
                if (colCur != null) { colCur[i] = Vector3.zero; colTarget[i] = Vector3.zero; colBase[i] = Vector3.zero; }
                if (hardOff != null && i < hardOff.Length) hardOff[i] = Vector3.zero;
            }
            jelloPos = Vector3.zero; jelloVel = Vector3.zero;
            swayPos = Vector3.zero; swayVel = Vector3.zero; twist = 0f; twistVel = 0f;
            prevPushMag = 0f;
            primed = false; waves.Clear();
        }
    }
}
