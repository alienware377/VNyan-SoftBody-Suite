using System.Collections.Generic;
using UnityEngine;

namespace SoftBodySuite.Jello
{
    // the suite's shared types, seen under the names this file was written against
    using SquishMesh = SoftBodySuite.SuiteMesh;
    using SquishConfig = SoftBodySuite.SuiteConfig;
    using SquishSettings = SoftBodySuite.SuiteSettings;
    using SquishRegion = SoftBodySuite.JelloRegion;

    // Live deformable copy of a SkinnedMeshRenderer.
    //
    // Every frame: bake the skinned mesh (pose + blendshapes included), add each
    // region's simulated displacement, push the result into a plain MeshRenderer copy
    // that uses the ORIGINAL materials (Poiyomi etc. untouched). The original renderer
    // is hidden while the proxy is live and restored when it isn't. Because the bake
    // happens after animation/tracking, this works on any rig, however messy.
    public class MeshProxy
    {
        public SkinnedMeshRenderer smr;
        public SquishMesh cfg;
        public readonly List<SquishSim> sims = new List<SquishSim>();

        // ---- in-suite chain ----
        // The stage before this one hands its arrays straight over. The old build passed
        // the body between plugins by writing a hidden mesh and reading it back with
        // GetVertices, which cost a copy each way and put the stages a frame apart.
        public Vector3[] upVerts, upNormals;
        // What the stage before us ADDED this frame (wobble's jiggle). The clothes must be
        // attached against the body WITHOUT it — see cageClean.
        public Vector3[] upDisp;
        // The cage surface of the CLEAN, un-wobbled body. The clothes take their resting fit
        // from this, never from the wobbling surface, or whatever swing the body happened to
        // be in at attach time gets saved as the garment's normal shape.
        Vector3[] cageClean, cleanScratch;

        // what the next stage (and, at the very end, the cloth pass) reads
        public Vector3[] OutVerts { get { return bakedVerts; } }
        public Vector3[] OutNormals { get { return bakedNormals; } }
        // this stage's own offset this frame (zeros with the sim off), for the look probe
        public Vector3[] Disp { get { return disp; } }
        public Vector3[] BaseVerts { get { return bakedVerts; } }

        // only the last stage that actually runs should be on screen
        public void SetVisible(bool v)
        {
            // coming back on screen: the hidden frames were not uploaded, so push this one now
            // or one stale frame would flash
            if (v && mr != null && !mr.enabled && display != null && framedOnce && bakedVerts != null)
            {
                display.SetVertices(bakedVerts);
                if (bakedNormals != null) display.SetNormals(bakedNormals);
            }
            if (mr != null && mr.enabled != v) mr.enabled = v;
        }

        GameObject go;
        MeshFilter mf;
        MeshRenderer mr;
        Mesh baked;                 // re-baked every frame
        Mesh display;               // what we render (topology copied once)

        Vector3[] bakedVerts;
        Vector3[] bakedNormals;
        Vector3[] disp;             // per-vertex displacement accumulator
        readonly List<Vector3> scratch = new List<Vector3>();

        // Chaining with Wobble Studio: if that plugin already deforms this mesh, read ITS
        // output (jiggled vertices) as our base instead of re-baking the raw skin — the two
        // plugins stack (Wobble at execution order 19000, Squish at 20000) instead of
        // rendering two fighting copies. Its renderer is hidden; ours is the final image.
        MeshFilter wobbleSrc;
        MeshRenderer wobbleMR;
        int chainCheck;

        // overlay (weight-paint heatmap, Blender-style)
        GameObject overlayGO;
        MeshRenderer overlayMR;
        Material overlayMat;
        Color32[] overlayColors;
        public static Material overlayMatOverride;
        public static SquishSettings settingsRef;   // plugin-global settings (remesh cage options)
        public static SquishConfig configRef;       // full config (follower exclusion checks)
        public RemeshCage cage; public SquishSim cageSim; public SquishRegion cageSrc;

        // ----- cage followers: other meshes (clothing etc.) driven by THIS mesh's cage -----
        // One shared sim: each follower vert binds to its normal-gated nearest cage tri at
        // rest and replays the same smoothed cage displacement field every frame, so the
        // clothing moves WITH the body instead of running its own diverging sim (no clipping).
        class Follower
        {
            public SkinnedMeshRenderer smr;
            public GameObject go; public MeshFilter mf; public MeshRenderer mr;
            public Mesh baked, display;
            public Vector3[] verts;
            public bool boundsSet;
            public bool whole;                  // bound to the WHOLE body surface vs the region cage
            // driven WELD GROUPS (UV-seam duplicates merged — per-render smoothing would tear)
            public int[] gRep;                  // group -> representative render vert
            public int[] gMemStart, gMem;       // CSR: members (render verts) per group
            public int[] gA, gB, gC;            // bound tri verts: CAGE indices, or SOURCE RENDER indices (whole)
            public float[] gwA, gwB, gwC, gFall;
            public Vector3[] gOff;              // rest offset in the bound tri's local frame (WRAP bind)
            public Vector3[] gOffRest;          // ...and as a plain vector, for long-lever damping
            public int[][] gAdj;                // adjacency among driven groups
            public float[] gSeam;               // metric distance to the driven-area boundary
            public int[] gBand;                 // groups sorted by gSeam (active band = prefix)
            public Vector3[] gDisp, gDisp2;     // per-frame scratch
            public Vector3[] gNrm;              // per-frame bound-tri normal (anti-clip enforcement)
            public float[] gProt;               // per-frame protected outward component (anti-clip floor)
            public float[] gDnRaw;              // full-wrap normal component (clearance reference)
            public bool[] gIsFill;              // valley-gap groups: no binding, diffused from neighbors
            public bool hasFill;
            // ---- FOLLOW DIAGNOSTICS (see FollowDiag* below) ----
            public int[] dEdgeA, dEdgeB;        // unique render-vert edges, one per welded pair
            public int[] dState;                // per render vert: FD_* state code
            public int[] dBone;                 // per render vert: dominant bone index into dBoneNames
            public string[] dBoneNames;
            public Vector3[] skin;              // this frame's plain skinned verts, before we move them
            public Vector3[] dCp, dN;           // per group: bound surface point + outward normal (src space)
            public bool[] dOk;
            public float dT; public int dFrames;
            public float dMaxDisp, dMaxStretch, dMinClear; public int dOver20, dStretch2, dClip;
            public int dWorstA = -1, dWorstB = -1; public float dWorstDispA, dWorstDispB;
            // ---- floating islands, moved rigidly (see RigidIslands) ----
            public int[] gIsl;                  // per group: island id, -1 = not rigid
            public int islN;
            public Quaternion[] islQ;           // last frame's fit, warm start
            public int[][] islMem;              // per island: its groups, ascending (built with gIsl)
            // ---- baked early this frame (while jell-o's solve runs on a worker) ----
            public readonly List<Vector3> nrm = new List<Vector3>();
            public bool haveN;
            public Matrix4x4 frameM;
            public int bakedFrame = -1;
            // fast-wrap self-check scratch (allocated on the first check)
            public Vector3[] ckD, ckN, ckD2, ckN2; public float[] ckP, ckR, ckP2, ckR2; public bool ckLogged;
            public bool allRigid;               // every bound group is in a rigid island (a bow)
            // ---- skin guard: final push-out against the body on screen (see ApplySkinGuard) ----
            public int[] kRep;                  // watched weld group -> representative render vert
            public int[] kMemStart, kMem;       // CSR: members (render verts) per watched group
            public int[] kTA, kTB, kTC;         // body RENDER triangle under the group at bind
            public Vector3[] kBar;              // barycentric point on it
            public float[] kSign, kRest;        // winding fix so the normal points OUT; rest gap (m)
            public bool[] kFix;                 // sunk into the skin on the model itself: guarded even at rest
            public int[][] kAdj;                // adjacency among watched groups
            public float[][] kAdjLen;           // ...and the rest length of each of those edges
            public MinHeap kHeap;               // cone-spread sweep, reused every frame
            public Vector3[] kCorr, kCorr2, kNl, kNs; public float[] kNeed;   // per-frame scratch
            public int[] kSeed;                 // per frame: the pushed group a spread push came from
            public int kWatched;                // groups that can push (the rest only carry the falloff)
            public int dGuardHits, dGuardDeep; public float dGuardMax;
            public int dTear; public float dTearMax;   // edges grown by more than 5 mm (absolute, not a ratio)
            public int dTearA = -1, dTearB = -1; public float dTearDispA, dTearDispB;
            public float[] dGuardV;              // per render vert: the guard's push this frame (diag only)
        }
        readonly List<Follower> followers = new List<Follower>();
        readonly List<SkinnedMeshRenderer> folQueue = new List<SkinnedMeshRenderer>();  // bind 1/frame
        RemeshCage folCage;            // cage the current followers/queue were built against
        bool folGenActive;             // a follower generation exists (whole mode has no cage)
        bool folLastWhole;
        int folLastBoneFilter, folLastFillMax, folLastAnchor;
        float folLastRange, folRangeT, folRetryT;
        readonly List<SkinnedMeshRenderer> folFailed = new List<SkinnedMeshRenderer>();
        // meshes no bone of which skins the paint (hair, face...): slow retry only
        readonly List<SkinnedMeshRenderer> folDead = new List<SkinnedMeshRenderer>();
        float folDeadT;
        int lastFleshCand;   // set by FleshBoneGate: groups that had a flesh bone at all
        // bind-generation search structures: a CURRENT-POSE grid (rest-space search mis-bound
        // verts and froze pose error into the fit)
        RemeshCage.TriGrid bindGrid;
        Vector3[] bindPts;             // positions the grid indexes (refreshed in place per bind call)
        int[] bindTris;                // triangle array the grid's results index into
        // skin guard: a grid over the CLEAN body render mesh, rebuilt when a garment binds
        // more than 2 s after the last build (the pose has moved by then)
        RemeshCage.TriGrid guardGrid; Vector3[] guardPts; int[] guardTris; float guardGridT = -999f;
        Vector3[] guardFin;            // this frame's finished body (what is on screen), or null
        const float GUARD_WELD = 0.001f;    // garment verts closer than this share ONE guard group (seam slivers)
        const float GUARD_DEEP = 0.08f;     // deeper than this = the wrong surface: leave it alone
                                            // (was 0.12; real flesh motion here peaks near 80 mm)
        const int GUARD_SPREAD = 2;         // light rounding passes after the cone spread
        float folLastGuardReach, folLastGuardSunk;
        const float GUARD_LIFT = 0.002f;    // where sunk cloth is lifted to: just above the skin
        Vector3[] cageOut, cageHeld, cagePrev, cageRaw, cagePreBoost;   // follower-side field mirror
        bool cageHeldValid;

        // orthonormal frame of a triangle (for wrap offsets); false = degenerate
        static bool TriFrame(Vector3 A, Vector3 B, Vector3 C, out Vector3 e1, out Vector3 e2, out Vector3 n)
        {
            e1 = B - A; float m1 = e1.magnitude;
            Vector3 ac = C - A;
            n = Vector3.Cross(e1, ac); float mn = n.magnitude;
            if (m1 < 1e-9f || mn < 1e-12f) { e1 = Vector3.right; e2 = Vector3.up; n = Vector3.forward; return false; }
            e1 /= m1; n /= mn; e2 = Vector3.Cross(n, e1);
            return true;
        }

        // pending-destroy corpses are inactive — same hazard ProxyAlive guards against
        static bool FollowClaimAlive(SkinnedMeshRenderer r)
        {
            Transform t = r.transform.Find(r.name + "_JelloFollow");
            return t != null && t.gameObject.activeSelf;
        }

        // is this mesh already rendered/driven by any studio's proxy (incl. another Jello)?
        static bool OtherStudioProxyAlive(SkinnedMeshRenderer r)
        {
            string[] sfx = { "_WobbleProxy", "_SoftBodyProxy", "_SquishProxy", "_JelloProxy" };
            for (int i = 0; i < sfx.Length; i++)
            {
                Transform t = r.transform.Find(r.name + sfx[i]);
                if (t != null && t.gameObject.activeSelf) return true;
            }
            return false;
        }
        Mesh cageMesh; Mesh cageVizMesh; GameObject cageVizGo;
        // async cage build: remeshing runs on a worker thread so VNyan never freezes
        RemeshCage cageBuilding; int cageToken;
        System.Diagnostics.Stopwatch cageSw;
        GameObject avatarRef; Animator animRef;
        public bool overlayOn;
        public int overlayMode;      // 0 = paint weights, 1 = SHARPNESS heatmap (jaggedness)
        int[] sharpTris; Vector3[] faceNorm; int[] vFaceOff, vFaceIdx; float[] sharpVal;
        public float overlayOpacity = 0.75f;

        public bool Alive { get { return smr != null && go != null; } }
        public int VertexCount { get { return bakedVerts != null ? bakedVerts.Length : 0; } }
        public Vector3[] BakedVerts { get { return bakedVerts; } }
        public Transform Root { get { return go != null ? go.transform : null; } }

        public void Attach(SkinnedMeshRenderer target, SquishMesh meshCfg, GameObject avatar, Animator anim)
        {
            Detach();
            smr = target; cfg = meshCfg;
            if (smr == null || smr.sharedMesh == null) return;

            baked = new Mesh(); baked.MarkDynamic();
            smr.BakeMesh(baked);
            display = Object.Instantiate(baked);
            display.MarkDynamic();
            display.name = smr.name + "_squish";

            int n = display.vertexCount;
            bakedVerts = new Vector3[n];
            bakedNormals = new Vector3[n];
            disp = new Vector3[n];
            BuildWeldMap();

            go = new GameObject(smr.name + "_JelloProxy");
            go.transform.SetParent(smr.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = display;
            mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = smr.sharedMaterials;
            mr.shadowCastingMode = smr.shadowCastingMode;
            mr.receiveShadows = smr.receiveShadows;

            smr.forceRenderingOff = true;   // hide original but keep it animating
            FindWobbleProxy();

            // one-shot diagnostics: scale/space mismatches show up here in the log
            Debug.Log("[Jello] attach '" + smr.name + "' verts=" + n
                + " lossyScale=" + smr.transform.lossyScale.ToString("0.###")
                + " bakeBounds=" + baked.bounds.size.ToString("0.###")
                + " smrLocalBounds=" + smr.localBounds.size.ToString("0.###"));

            // build sims
            BuildSims(avatar, anim);
            ResolveColliderMeshes(avatar);
        }

        public void ResolveRegionRefs(SquishSim s, GameObject avatar, Animator anim)
        {
            // reference bone for pose-gated gravity: explicit name, else highest-skin-weight bone
            Transform rb = null;
            if (!string.IsNullOrEmpty(s.cfg.refBone)) rb = FindBone(avatar, anim, s.cfg.refBone);
            if (rb == null) rb = AutoRefBone(s);
            s.refBone = rb;
            if (rb != null) { s.refRest = rb.localRotation; s.refCaptured = true; }

            // colliders
            int nc = s.cfg.colliders.Count;
            s.colTr = new Transform[nc];
            s.colCfg = new SquishCollider[nc];
            s.clouds = new MeshColliderCloud[nc];
            for (int c = 0; c < nc; c++)
            {
                s.colCfg[c] = s.cfg.colliders[c];
                if (string.IsNullOrEmpty(s.cfg.colliders[c].mesh))
                    s.colTr[c] = FindBone(avatar, anim, s.cfg.colliders[c].bone);
            }
        }

        // mesh-collider machinery: SMRs resolved once, clouds rebuilt each frame
        readonly Dictionary<string, SkinnedMeshRenderer> colMeshSmr = new Dictionary<string, SkinnedMeshRenderer>();
        readonly Dictionary<string, MeshColliderCloud> colMeshCloud = new Dictionary<string, MeshColliderCloud>();
        Mesh colBakeScratch;

        List<SkinnedMeshRenderer> allColSmrs;   // resolved targets for a "*" (all meshes) collider

        HashSet<int> cachedSelfExclude;

        public void ResolveColliderMeshes(GameObject avatar)
        {
            colMeshSmr.Clear(); colMeshCloud.Clear(); allColSmrs = null; cachedSelfExclude = null;
            if (avatar == null || cfg == null) return;
            SkinnedMeshRenderer[] rends = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int r = 0; r < cfg.regions.Count; r++)
                for (int c = 0; c < cfg.regions[r].jello.colliders.Count; c++)
                {
                    string nm = cfg.regions[r].jello.colliders[c].mesh;
                    if (string.IsNullOrEmpty(nm) || colMeshSmr.ContainsKey(nm)) continue;
                    if (nm == "*")
                    {
                        colMeshSmr["*"] = null;
                        allColSmrs = new List<SkinnedMeshRenderer>();
                        for (int i = 0; i < rends.Length; i++)
                            if (rends[i] != null && rends[i].sharedMesh != null) allColSmrs.Add(rends[i]);
                        continue;
                    }
                    for (int i = 0; i < rends.Length; i++)
                        if (rends[i] != null && rends[i].name == nm) { colMeshSmr[nm] = rends[i]; break; }
                }
        }

        // Reduce every referenced collider mesh ONCE to auto-fitted bone capsules (see
        // MeshColliderCloud) — no per-frame baking. When the collider is the region's OWN
        // mesh, painted vertices are excluded so the region doesn't collide with itself —
        // hands (same mesh) still squish the chest.
        void UpdateColliderClouds()
        {
            if (colMeshSmr.Count == 0) return;
            if (colBakeScratch == null) { colBakeScratch = new Mesh(); colBakeScratch.MarkDynamic(); }
            Transform tr = go.transform;

            // bounding spheres of the painted regions (proxy-local) — only discs near these
            // stay active, so far-away body parts cost nothing
            List<Vector4> regBounds = new List<Vector4>();
            for (int r = 0; r < cfg.regions.Count; r++)
            {
                SquishRegion reg = cfg.regions[r].jello;
                if (!reg.enabled || reg.vertIndex.Count == 0) continue;
                Vector3 cen = Vector3.zero; int cnt = 0;
                for (int v = 0; v < reg.vertIndex.Count; v++)
                {
                    int vi = reg.vertIndex[v];
                    if (vi < bakedVerts.Length && reg.weight[v] > 0.05f) { cen += bakedVerts[vi]; cnt++; }
                }
                if (cnt == 0) continue;
                cen /= cnt;
                float rr = 0f;
                for (int v = 0; v < reg.vertIndex.Count; v++)
                {
                    int vi = reg.vertIndex[v];
                    if (vi < bakedVerts.Length && reg.weight[v] > 0.05f)
                    { float d = (bakedVerts[vi] - cen).sqrMagnitude; if (d > rr) rr = d; }
                }
                regBounds.Add(new Vector4(cen.x, cen.y, cen.z, Mathf.Sqrt(rr) + 0.06f));
            }

            // the self-collision exclude set depends only on the paint, and was rebuilt
            // (9k hash inserts) every frame; it is cleared with the collider set on rebuild
            HashSet<int> selfExclude = cachedSelfExclude;
            foreach (KeyValuePair<string, SkinnedMeshRenderer> kv in colMeshSmr)
            {
                SkinnedMeshRenderer csmr = kv.Value;
                bool all = kv.Key == "*";
                if (csmr == null && !all) continue;
                bool self = all || ReferenceEquals(csmr, smr);
                if (self && selfExclude == null)
                {
                    selfExclude = new HashSet<int>();
                    for (int r = 0; r < cfg.regions.Count; r++)
                        for (int v = 0; v < cfg.regions[r].jello.vertIndex.Count; v++)
                            if (cfg.regions[r].jello.weight[v] > 0.15f) selfExclude.Add(cfg.regions[r].jello.vertIndex[v]);
                    cachedSelfExclude = selfExclude;
                }

                MeshColliderCloud cloud;
                if (!colMeshCloud.TryGetValue(kv.Key, out cloud)) { cloud = new MeshColliderCloud(); colMeshCloud[kv.Key] = cloud; }

                // skin gap: use the largest radius any collider entry asked for on this mesh
                float rad = 0.015f;
                for (int r = 0; r < cfg.regions.Count; r++)
                    for (int c = 0; c < cfg.regions[r].jello.colliders.Count; c++)
                        if (cfg.regions[r].jello.colliders[c].mesh == kv.Key && cfg.regions[r].jello.colliders[c].radius > rad)
                            rad = cfg.regions[r].jello.colliders[c].radius;
                cloud.radius = rad;

                int excluded = self && selfExclude != null ? selfExclude.Count : 0;
                if (!cloud.HasBuild(excluded))
                {
                    List<Vector3> regionWorld = new List<Vector3>();
                    for (int r = 0; r < cfg.regions.Count; r++)
                    {
                        SquishRegion reg = cfg.regions[r].jello;
                        for (int v = 0; v < reg.vertIndex.Count; v++)
                        {
                            int vi = reg.vertIndex[v];
                            if (vi < bakedVerts.Length && reg.weight[v] > 0.15f)
                                regionWorld.Add(tr.TransformPoint(bakedVerts[vi]));
                        }
                    }
                    if (all) cloud.BuildFromSkinMulti(allColSmrs, smr, selfExclude, regionWorld);
                    else cloud.BuildFromSkin(csmr, self ? selfExclude : null, regionWorld);
                    Debug.Log("[Jello] built " + (all ? "(all meshes)" : kv.Key) + " capsule colliders");
                }
                cloud.UpdateFrame(tr, regBounds);
            }

            // hand each sim its per-collider cloud references
            for (int s = 0; s < sims.Count; s++)
            {
                SquishSim sim = sims[s];
                if (sim.clouds == null) continue;
                for (int c = 0; c < sim.colCfg.Length; c++)
                {
                    string nm = sim.colCfg[c].mesh;
                    sim.clouds[c] = (!string.IsNullOrEmpty(nm) && colMeshCloud.ContainsKey(nm)) ? colMeshCloud[nm] : null;
                }
            }
        }

        // union-select from MULTIPLE bone groups (weights summed, then clamped)
        public static void SelectFromBonesOn(SkinnedMeshRenderer smr, SquishRegion region,
                                             List<string> boneNames, float threshold, bool includeChildren)
        {
            if (smr == null || smr.sharedMesh == null || boneNames == null || boneNames.Count == 0) return;
            bool[] pick = new bool[smr.bones.Length];
            for (int nmi = 0; nmi < boneNames.Count; nmi++)
            {
                Transform rootBone = null;
                for (int b = 0; b < smr.bones.Length; b++)
                    if (smr.bones[b] != null && smr.bones[b].name == boneNames[nmi]) { rootBone = smr.bones[b]; break; }
                if (rootBone == null) continue;
                for (int b = 0; b < smr.bones.Length; b++)
                {
                    Transform t = smr.bones[b]; if (t == null || pick[b]) continue;
                    if (t == rootBone) { pick[b] = true; continue; }
                    if (includeChildren)
                        for (Transform pp = t.parent; pp != null; pp = pp.parent)
                            if (pp == rootBone) { pick[b] = true; break; }
                }
            }
            BoneWeight[] bw = smr.sharedMesh.boneWeights;
            region.vertIndex.Clear(); region.weight.Clear();
            for (int i = 0; i < bw.Length; i++)
            {
                float wv = 0f;
                if (bw[i].boneIndex0 < pick.Length && pick[bw[i].boneIndex0]) wv += bw[i].weight0;
                if (bw[i].boneIndex1 < pick.Length && pick[bw[i].boneIndex1]) wv += bw[i].weight1;
                if (bw[i].boneIndex2 < pick.Length && pick[bw[i].boneIndex2]) wv += bw[i].weight2;
                if (bw[i].boneIndex3 < pick.Length && pick[bw[i].boneIndex3]) wv += bw[i].weight3;
                if (wv >= threshold) { region.vertIndex.Add(i); region.weight.Add(Mathf.Clamp01(wv)); }
            }
        }

        Transform AutoRefBone(SquishSim s)
        {
            if (smr.sharedMesh == null || smr.bones == null || smr.bones.Length == 0) return null;
            BoneWeight[] bw = smr.sharedMesh.boneWeights;
            if (bw == null || bw.Length == 0) return null;
            Dictionary<int, float> tally = new Dictionary<int, float>();
            for (int i = 0; i < s.idx.Length; i++)
            {
                int vi = s.idx[i]; if (vi >= bw.Length) continue;
                Acc(tally, bw[vi].boneIndex0, bw[vi].weight0 * s.w[i]);
                Acc(tally, bw[vi].boneIndex1, bw[vi].weight1 * s.w[i]);
                Acc(tally, bw[vi].boneIndex2, bw[vi].weight2 * s.w[i]);
                Acc(tally, bw[vi].boneIndex3, bw[vi].weight3 * s.w[i]);
            }
            int best = -1; float bestW = 0f;
            foreach (KeyValuePair<int, float> kv in tally)
                if (kv.Value > bestW) { bestW = kv.Value; best = kv.Key; }
            // walk one level up: the deform bone's PARENT is what rotates the region around
            if (best >= 0 && best < smr.bones.Length && smr.bones[best] != null)
            {
                Transform b = smr.bones[best];
                return b.parent != null ? b.parent : b;
            }
            return null;
        }

        static void Acc(Dictionary<int, float> d, int k, float v)
        {
            if (v <= 0f) return;
            float cur; d.TryGetValue(k, out cur); d[k] = cur + v;
        }

        public static Transform FindBone(GameObject avatar, Animator anim, string name)
        {
            if (avatar == null || string.IsNullOrEmpty(name)) return null;
            if (anim != null && anim.isHuman)
            {
                HumanBodyBones hb;
                if (System.Enum.TryParse<HumanBodyBones>(name, true, out hb) && hb != HumanBodyBones.LastBone)
                {
                    Transform t = anim.GetBoneTransform(hb);
                    if (t != null) return t;
                }
            }
            return FindRecursive(avatar.transform, name.ToLowerInvariant());
        }
        static Transform FindRecursive(Transform t, string lower)
        {
            if (t.name.ToLowerInvariant() == lower) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform r = FindRecursive(t.GetChild(i), lower);
                if (r != null) return r;
            }
            return null;
        }

        // ---------- late cloth pass (runs after the LAST body deformer) ----------
        // Jello sits mid-chain: Wobble/SoftBody feed its input (so their motion is already
        // in the cage), but Squish runs downstream and renders the final body. Driving the
        // cloth from here — and folding Squish's contribution onto the cage — means the
        // garments follow the whole pipeline instead of a mid-chain snapshot.
        static readonly List<Vector3> lateScratch = new List<Vector3>();

        // Set by the suite to the last stage that actually runs. Before the merge this
        // had to be found by GameObject name and read back out of a mesh; now it is just
        // the downstream stage's array.
        public System.Func<Vector3[]> finalBodySource;

        // What the stage BEFORE us added this frame. Without it the clothes only
        // ever saw the jell-o wobble and slid straight through anything wobble did.
        public System.Func<Vector3[]> upstreamExtra;
        // perf probe: the slowest follower-lifecycle frame (binds, retries) this window
        public static float bindMaxMs;
        // true when the final body this frame IS our own output, unchanged (set by the plugin)
        public System.Func<bool> finalIsOwnOutput;

        Vector3[] FinalBodyVerts()
        {
            if (smr == null || bakedVerts == null) return null;
            if (finalBodySource != null)
            {
                Vector3[] fv = finalBodySource();
                return (fv != null && fv.Length == bakedVerts.Length) ? fv : null;
            }
            Transform t = smr.transform.Find(smr.name + "_SquishProxy");
            if (t == null || !t.gameObject.activeSelf) return null;
            MeshFilter mfS = t.GetComponent<MeshFilter>();
            if (mfS == null || mfS.sharedMesh == null) return null;
            if (mfS.sharedMesh.vertexCount != bakedVerts.Length) return null;
            mfS.sharedMesh.GetVertices(lateScratch);
            if (lateScratch.Count != bakedVerts.Length) return null;
            if (finalVerts == null || finalVerts.Length != bakedVerts.Length)
                finalVerts = new Vector3[bakedVerts.Length];
            lateScratch.CopyTo(finalVerts);
            return finalVerts;
        }

        // CLOTHES COST PROBE: each step summed over all garments, marked once a frame, so
        // the [Perf] block says where the clothes pass spends its time.
        readonly System.Diagnostics.Stopwatch cloSw = new System.Diagnostics.Stopwatch();
        float cloBake, cloWrap, cloSeam, cloSmooth, cloRigid, cloGuard, cloUpload, cloDiag;
        float CloLap() { float ms = (float)cloSw.Elapsed.TotalMilliseconds; cloSw.Reset(); cloSw.Start(); return ms; }

        public void LateFollowUpdate()
        {
            if (!Alive || !framedOnce) return;   // bakedVerts is still all zeros before the first Frame()
            cloSw.Reset(); cloSw.Start();
            // difference between the rendered body and our own output = everything the
            // downstream stage added this frame
            // a downstream stage disables OUR renderer when it chains onto us; until it
            // does, its mesh is not derived from our output and subtracting would be wrong
            bool wantFollow = settingsRef != null && settingsRef.cloth.follow.enabled;
            bool chainedDownstream = wantFollow &&
                (finalBodySource != null || (mr != null && !mr.enabled));
            Vector3[] fin = chainedDownstream ? FinalBodyVerts() : null;
            Vector3[] up = upstreamExtra != null ? upstreamExtra() : null;
            int n = bakedVerts.Length;
            if (up != null && up.Length != n) up = null;
            // The field handed OUR verts out byte for byte (Field Mix 0, asleep, ...): the
            // body on screen is this stage's own output, so fin - bakedVerts is exactly zero
            // everywhere and building, sampling and smoothing it was ~0.5 ms for nothing.
            // The skin guard still gets the rendered body (same numbers either way).
            Vector3[] finExtra = (fin != null && finalIsOwnOutput != null && finalIsOwnOutput()) ? null : fin;

            if (finExtra != null || up != null)
            {
                // backing buffers survive pass-through frames (no 460 KB re-allocation when
                // the field wakes up again)
                if (downBuf == null || downBuf.Length != n) downBuf = new Vector3[n];
                downExtra = downBuf;
                // everything the cage field does NOT already carry: what the stage before
                // us added (wobble) plus what the stage after us adds (squish)
                // per-vert, each writes its own slot: same numbers on many cores
                Vector3[] dx = downExtra, bv = bakedVerts;
                if (finExtra != null) SoftBodySuite.Field.ParallelLoop.For(n, delegate (int lo, int hi)
                {
                    for (int i = lo; i < hi; i++) dx[i] = finExtra[i] - bv[i];
                });
                else System.Array.Clear(downExtra, 0, n);
                if (up != null) SoftBodySuite.Field.ParallelLoop.For(n, delegate (int lo, int hi)
                {
                    for (int i = lo; i < hi; i++) dx[i] += up[i];
                });

                if (cage != null && cage.SimVertCount > 0)
                {
                    if (cageExBuf == null || cageExBuf.Length != cage.SimVertCount)
                        cageExBuf = new Vector3[cage.SimVertCount];
                    cageExtra = cageExBuf;
                    cage.InterpDelta(downExtra, cageExtra);
                    int exSm = settingsRef != null ? Mathf.Clamp(Mathf.RoundToInt(settingsRef.cloth.follow.folSmooth), 0, 60) : 0;
                    if (exSm > 0) cage.SmoothArray(cageExtra, exSm);
                }
            }
            else { finalVerts = null; downExtra = null; cageExtra = null; }
            guardFin = (fin != null && fin.Length == n) ? fin : null;

            SoftBodySuite.Field.FieldDiag.Mark("clo_extra", CloLap());
            UpdateFollowerLifecycle();
            float bindMs = CloLap();
            if (bindMs > bindMaxMs) bindMaxMs = bindMs;
            SoftBodySuite.Field.FieldDiag.Mark("clo_bind", bindMs);
            cloBake = cloWrap = cloSeam = cloSmooth = cloRigid = cloGuard = cloUpload = cloDiag = 0f;
            cloBake = cloPreBake; cloPreBake = 0f;
            UpdateFollowers();
            if (followers.Count > 0)
            {
                SoftBodySuite.Field.FieldDiag.Mark("clo_bake", cloBake);
                SoftBodySuite.Field.FieldDiag.Mark("clo_wrap", cloWrap);
                SoftBodySuite.Field.FieldDiag.Mark("clo_seam", cloSeam);
                SoftBodySuite.Field.FieldDiag.Mark("clo_smooth", cloSmooth);
                SoftBodySuite.Field.FieldDiag.Mark("clo_rigid", cloRigid);
                SoftBodySuite.Field.FieldDiag.Mark("clo_guard", cloGuard);
                SoftBodySuite.Field.FieldDiag.Mark("clo_upload", cloUpload);
                SoftBodySuite.Field.FieldDiag.Mark("clo_diag", cloDiag);
            }
        }

        Vector3[] finalVerts, downExtra, cageExtra;
        Vector3[] downBuf, cageExBuf;
        bool framedOnce;

        // ---------- cage followers ----------
        // Called when the clothes pass cannot run at all — this stage switched off, or the
        // follower turned off. Without it the garments keep the last mesh we drove them to
        // and simply freeze in place, because their own renderer is still hidden.
        public void ReleaseFollowers()
        {
            if (folGenActive || followers.Count > 0) DetachFollowers();
        }

        void UpdateFollowerLifecycle()
        {
            bool whole = settingsRef != null && settingsRef.cloth.follow.bindWhole;
            bool want = settingsRef != null && settingsRef.cloth.follow.enabled &&
                        (whole || (cage != null && settingsRef.jello.useRemesh > 0.5f));
            if (!want) { if (folGenActive || followers.Count > 0) DetachFollowers(); return; }
            float range = Mathf.Clamp(settingsRef.cloth.follow.range, 0.005f, FOLLOW_RANGE_MAX);
            // cage rebuilt (cage mode) or bind-mode flipped: detach and RETURN — requeueing
            // next frame lets the pending-destroy "_JelloFollow" corpses actually die
            if (folGenActive && (folLastWhole != whole || (!whole && folCage != cage))) { DetachFollowers(); return; }
            // bind-affecting knobs: debounce the drag; followers keep running until settled
            int kFilt = settingsRef.cloth.follow.boneFilter ? 1 : 0;
            int kFill = Mathf.RoundToInt(settingsRef.cloth.follow.fillMax);
            int kAnch = Mathf.RoundToInt(settingsRef.cloth.follow.anchorMin);
            Vector3 offNow = new Vector3(settingsRef.cloth.follow.offsetX,
                                         settingsRef.cloth.follow.offsetY,
                                         settingsRef.cloth.follow.offsetZ);
            if (folGenActive && (Mathf.Abs(range - folLastRange) > 0.0005f ||
                (offNow - folLastOffset).sqrMagnitude > 0.0005f * 0.0005f ||
                Mathf.Abs(settingsRef.cloth.follow.floatDist - folLastFloat) > 0.0005f ||
                Mathf.Abs(settingsRef.cloth.follow.guardReach - folLastGuardReach) > 0.0005f ||
                Mathf.Abs(settingsRef.cloth.follow.guardSunk - folLastGuardSunk) > 0.0005f ||
                kFilt != folLastBoneFilter || kFill != folLastFillMax || kAnch != folLastAnchor))
            { folRangeT = 0.5f; folLastRange = range; folLastOffset = offNow; folLastFloat = settingsRef.cloth.follow.floatDist; folLastGuardReach = settingsRef.cloth.follow.guardReach; folLastGuardSunk = settingsRef.cloth.follow.guardSunk; folLastBoneFilter = kFilt; folLastFillMax = kFill; folLastAnchor = kAnch; }
            if (folRangeT > 0f)
            {
                folRangeT -= Time.deltaTime;
                if (folRangeT <= 0f) { DetachFollowers(); return; }   // rebind next frame at the settled range
            }
            if (!folGenActive)
            {
                folGenActive = true; folCage = cage; folLastWhole = whole; folLastRange = range; folLastOffset = offNow;
                folLastFloat = settingsRef.cloth.follow.floatDist;
                folLastGuardReach = settingsRef.cloth.follow.guardReach;
                folLastGuardSunk = settingsRef.cloth.follow.guardSunk;
                folLastBoneFilter = settingsRef.cloth.follow.boneFilter ? 1 : 0;
                folLastFillMax = Mathf.RoundToInt(settingsRef.cloth.follow.fillMax);
                folLastAnchor = Mathf.RoundToInt(settingsRef.cloth.follow.anchorMin);
                QueueFollowerCandidates();
            }
            if (folQueue.Count == 0 && folDead.Count > 0 && (folDeadT += Time.deltaTime) > 60f)
            {
                folDeadT = 0f;
                for (int i = 0; i < folDead.Count; i++) if (folDead[i] != null) folFailed.Add(folDead[i]);
                folDead.Clear();
            }
            if (folQueue.Count > 0) BindNextFollower(range, whole);   // one mesh per frame spreads the cost
            else if (folFailed.Count > 0)
            {
                // binding is pose-dependent: meshes that failed coverage retry every few
                // seconds (a permanent one-shot latch left garments unbound for the session)
                folRetryT += Time.deltaTime;
                if (folRetryT > 5f)
                {
                    folRetryT = 0f;
                    // rebuild the search grid: its cell binning froze at generation start,
                    // and the whole point of the retry is that the pose has MOVED since —
                    // stale cells either find nothing or mis-bind to the wrong body patch
                    bindGrid = null; bindPts = null; bindTris = null;
                    for (int i = 0; i < folFailed.Count; i++) if (folFailed[i] != null) folQueue.Add(folFailed[i]);
                    folFailed.Clear();
                }
            }
        }

        void QueueFollowerCandidates()
        {
            folQueue.Clear();
            if (avatarRef == null || smr == null) return;
            SkinnedMeshRenderer[] rends = avatarRef.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < rends.Length; i++)
            {
                SkinnedMeshRenderer r = rends[i];
                if (r == null || r == smr || r.sharedMesh == null) continue;
                // hidden/disabled/outfit-off meshes stay untouched — a follower copy would
                // RESURRECT them on screen
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (r.forceRenderingOff) continue;                 // some proxy already owns its rendering
                if (OtherStudioProxyAlive(r)) continue;            // Wobble/SoftBody/Squish/Jello drive it
                // meshes with their own enabled sim run themselves; hidden meshes stay hidden
                bool owned = false;
                if (configRef != null && configRef.meshes != null)
                    for (int m = 0; m < configRef.meshes.Count; m++)
                        if (configRef.meshes[m] != null && configRef.meshes[m].enabled &&
                            configRef.meshes[m].mesh == r.name) { owned = true; break; }
                if (owned) continue;
                if (settingsRef.hiddenMeshes != null && settingsRef.hiddenMeshes.Contains(r.name)) continue;
                if (FollowClaimAlive(r)) continue;                 // claimed by another proxy (live, not corpse)
                folQueue.Add(r);
            }
        }

        Vector3 bindMin, bindMax;
        Vector3 folLastOffset;
        float folLastFloat = -1f;
        // was 0.5 m. Loose or puffy garments sit further off the body than that.
        public const float FOLLOW_RANGE_MAX = 1.5f;

        void ComputeBindBounds()
        {
            bindMin = bindPts[0]; bindMax = bindPts[0];
            for (int i = 1; i < bindPts.Length; i++)
            { bindMin = Vector3.Min(bindMin, bindPts[i]); bindMax = Vector3.Max(bindMax, bindPts[i]); }
        }

        // Build (once per follower generation) a search grid over the CURRENT-POSE clean
        // surface — rest-space search mis-bound verts and froze pose error into the fit.
        // bindPts stays live: cage mode points at cage.simBaked; whole mode is refreshed
        // in place per bind call (cell membership drifts a vert-cm, well under a cell).
        Vector3 UpDispAt(int i)
        {
            return (upDisp != null && i < upDisp.Length) ? upDisp[i] : Vector3.zero;
        }

        bool EnsureBindGrid(bool whole)
        {
            if (bindGrid != null)
            {
                if (whole && bindPts != null && bakedVerts != null && bindPts.Length == bakedVerts.Length)
                    for (int i = 0; i < bindPts.Length; i++) bindPts[i] = bakedVerts[i] - disp[i] - UpDispAt(i);
                ComputeBindBounds();
                return true;
            }
            if (whole)
            {
                if (display == null || bakedVerts == null || bakedNormals == null) return false;
                int n = bakedVerts.Length;
                bindPts = new Vector3[n];
                for (int i = 0; i < n; i++) bindPts[i] = bakedVerts[i] - disp[i] - UpDispAt(i);
                bindTris = display.triangles;
            }
            else
            {
                if (cage == null || cage.simBaked == null || cage.SimVertCount < 1 ||
                    (cage.simBaked[0] == Vector3.zero && cage.simBaked[cage.SimVertCount - 1] == Vector3.zero))
                    return false;   // freshly built cage hasn't interpolated yet — wait one tick
                // stays live either way: both arrays are rewritten every frame
                bindPts = (cageClean != null && cageClean.Length == cage.SimVertCount &&
                           cageClean[0] != Vector3.zero) ? cageClean : cage.simBaked;
                bindTris = cage.simTris;
            }
            int nt = bindTris.Length / 3;
            Vector3[] tn = new Vector3[nt];
            Vector3[] nsrc = whole ? bakedNormals : cage.simNormals;
            for (int t = 0; t < nt; t++)
            {
                Vector3 s = nsrc[bindTris[t * 3]] + nsrc[bindTris[t * 3 + 1]] + nsrc[bindTris[t * 3 + 2]];
                float m = s.magnitude;
                tn[t] = m > 1e-9f ? s / m : Vector3.up;
            }
            bindGrid = new RemeshCage.TriGrid(bindPts, bindTris, 0.02f);
            bindGrid.triNormals = tn;
            ComputeBindBounds();
            return true;
        }

        void BindNextFollower(float range, bool whole)
        {
            SkinnedMeshRenderer r = folQueue[folQueue.Count - 1];
            folQueue.RemoveAt(folQueue.Count - 1);
            if (r == null || r.sharedMesh == null || go == null) return;
            // re-check at bind time: another proxy may have claimed/hidden it since queueing
            if (FollowClaimAlive(r) || OtherStudioProxyAlive(r)) return;
            if (!r.enabled || !r.gameObject.activeInHierarchy || r.forceRenderingOff) return;
            if (!EnsureBindGrid(whole)) { folQueue.Add(r); return; }

            Mesh bk = new Mesh();
            r.BakeMesh(bk);
            Vector3[] pts = bk.vertices;
            Vector3[] nrm = bk.normals;
            Matrix4x4 toSrc = go.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
            bool haveN = nrm != null && nrm.Length == pts.Length;
            // capture-zone offset, avatar axes -> this proxy's local space
            Vector3 offSrc = Vector3.zero;
            if (settingsRef != null && avatarRef != null)
            {
                ClothFollow cf = settingsRef.cloth.follow;
                Transform at = avatarRef.transform;
                Vector3 w = at.right * cf.offsetX + at.up * cf.offsetY + at.forward * cf.offsetZ;
                offSrc = go.transform.InverseTransformVector(w);
            }

            // ---- weld duplicate render verts (UV/normal seams) into groups ----
            Dictionary<long, int> cellMap = new Dictionary<long, int>();
            int[] gOfGlobal = new int[pts.Length];
            List<int> gFirst = new List<int>();
            List<Vector3> gNormSum = new List<Vector3>();
            for (int i = 0; i < pts.Length; i++)
            {
                Vector3 p = pts[i];
                long key = (((long)Mathf.RoundToInt(p.x * 20000f) & 0x1FFFFF) << 42)
                         | (((long)Mathf.RoundToInt(p.y * 20000f) & 0x1FFFFF) << 21)
                         | ((long)Mathf.RoundToInt(p.z * 20000f) & 0x1FFFFF);
                int gg;
                if (!cellMap.TryGetValue(key, out gg))
                { gg = gFirst.Count; cellMap[key] = gg; gFirst.Add(i); gNormSum.Add(Vector3.zero); }
                gOfGlobal[i] = gg;
                if (haveN) gNormSum[gg] += nrm[i];
            }

            // ---- bind each group once (shared by all its duplicates — no seam tearing) ----
            // AABB prefilter keeps far meshes (hair etc.) nearly free. Double-sided cloth:
            // opposing duplicate normals cancel → ungated (but range-limited) fallback.
            Vector3 lo = bindMin - Vector3.one * range, hi = bindMax + Vector3.one * range;
            int nGlobal = gFirst.Count;
            int[] localOf = new int[nGlobal];
            for (int i = 0; i < nGlobal; i++) localOf[i] = -1;
            List<int> rep = new List<int>(), A = new List<int>(), B = new List<int>(), C = new List<int>();
            List<float> WA = new List<float>(), WB = new List<float>(), WC = new List<float>(), FF = new List<float>();
            // ---- BONE FILTER: only garment verts weighted to the region's bones may bind ----
            // The bones come straight from Squish Studio's vertex-group picker (mirrored per
            // region as srcBones), so selecting the region's bones there selects them here.
            bool[] boneOkG = null;
            if (settingsRef != null && settingsRef.cloth.follow.boneFilter && cfg != null && cfg.regions != null)
            {
                HashSet<string> names = new HashSet<string>();
                for (int ri = 0; ri < cfg.regions.Count; ri++)
                {
                    SquishRegion rg = cfg.regions[ri].jello;
                    if (rg == null || !rg.enabled || rg.srcBones == null) continue;
                    for (int bi = 0; bi < rg.srcBones.Count; bi++)
                        if (!string.IsNullOrEmpty(rg.srcBones[bi])) names.Add(rg.srcBones[bi]);
                }
                if (names.Count > 0 && r.sharedMesh != null && r.bones != null)
                {
                    Transform[] gb = r.bones;
                    bool[] pick = new bool[gb.Length];
                    // ...and by POSITION. A garment that ships its own skeleton names its bones
                    // its own way: the leg harness on Nami is skinned to "Right Leg" / "Left
                    // Leg" while the thighs region is "Thigh_R" / "Thigh_L", so every one of its
                    // 1875 groups failed the filter and it never followed. A garment bone that
                    // sits on a region bone (within 2 cm) IS that bone; its children count too.
                    List<Vector3> regPos = RegionBonePositions(names);
                    int byPos = 0;
                    for (int b0 = 0; b0 < gb.Length; b0++)
                    {
                        Transform t0 = gb[b0];
                        if (t0 == null) continue;
                        if (names.Contains(t0.name)) { pick[b0] = true; continue; }
                        bool hit = false;
                        for (Transform pp = t0.parent; pp != null && !hit; pp = pp.parent)   // children of picked bones count too
                            if (names.Contains(pp.name)) hit = true;
                        for (Transform pp = t0; pp != null && !hit && regPos.Count > 0; pp = pp.parent)
                            if (OnRegionBone(pp.position, regPos)) { hit = true; byPos++; }
                        pick[b0] = hit;
                    }
                    if (followDiag)
                    {
                        // which garment bones matched, and for the most-used bone where the
                        // nearest region bone is, so a miss can be read straight off the log
                        System.Text.StringBuilder sbm = new System.Text.StringBuilder();
                        for (int b0 = 0; b0 < gb.Length; b0++)
                            if (pick[b0] && gb[b0] != null && !names.Contains(gb[b0].name)) sbm.Append(gb[b0].name).Append(", ");
                        BoneWeight[] gw0 = r.sharedMesh.boneWeights;
                        int[] use = new int[gb.Length];
                        if (gw0 != null) for (int i = 0; i < gw0.Length; i++) if (gw0[i].boneIndex0 < use.Length) use[gw0[i].boneIndex0]++;
                        int top = -1; for (int b0 = 0; b0 < use.Length; b0++) if (top < 0 || use[b0] > use[top]) top = b0;
                        string near = "-";
                        if (top >= 0 && gb[top] != null)
                        {
                            float bd = float.MaxValue; string bn = "?";
                            Transform[] bb = smr != null ? smr.bones : null;
                            if (bb != null) for (int i = 0; i < bb.Length; i++)
                                if (bb[i] != null && names.Contains(bb[i].name))
                                { float d = (bb[i].position - gb[top].position).magnitude; if (d < bd) { bd = d; bn = bb[i].name; } }
                            string chain = ""; int hops = 0;
                            for (Transform pp = gb[top].parent; pp != null && hops < 6; pp = pp.parent, hops++) chain += pp.name + " < ";
                            near = "'" + gb[top].name + "' nearest region bone '" + bn + "' at " + (bd * 1000f).ToString("0") + "mm, parents: " + chain;
                        }
                        Debug.Log("[FollowDiag] '" + r.name + "' bone filter: region bones [" + string.Join(", ", new List<string>(names).ToArray())
                            + "], picked (by name, a picked parent, or position): [" + sbm.ToString() + "], most-used " + near);
                    }
                    BoneWeight[] gw = r.sharedMesh.boneWeights;
                    if (gw != null && gw.Length == pts.Length)
                    {
                        boneOkG = new bool[nGlobal];
                        for (int i = 0; i < gw.Length; i++)
                        {
                            BoneWeight w4 = gw[i];
                            bool ok = (w4.weight0 > 0.05f && w4.boneIndex0 < pick.Length && pick[w4.boneIndex0])
                                   || (w4.weight1 > 0.05f && w4.boneIndex1 < pick.Length && pick[w4.boneIndex1])
                                   || (w4.weight2 > 0.05f && w4.boneIndex2 < pick.Length && pick[w4.boneIndex2])
                                   || (w4.weight3 > 0.05f && w4.boneIndex3 < pick.Length && pick[w4.boneIndex3]);
                            if (ok) boneOkG[gOfGlobal[i]] = true;   // any member qualifies the group
                        }
                    }
                }
            }

            if (boneOkG != null)
            {
                int okN = 0;
                for (int gg = 0; gg < nGlobal; gg++) if (boneOkG[gg]) okN++;
                if (okN < 8)
                {
                    int added = FleshBoneGate(r, pts, toSrc, gOfGlobal, gFirst, boneOkG);
                    if (followDiag || added > 0)
                        Debug.Log("[FollowDiag] '" + r.name + "': the region bones skin none of it by name ("
                            + okN + " groups); flesh fallback let " + added + " groups through (their bone moves the painted"
                            + " flesh AND the skin under them is painted)");
                }
            }

            // Diagnose each garment ONCE. Failed binds retry every 5 s, and doing a body
            // search for every rejected vert on every retry froze VNyan (hair alone is
            // 12.5k groups). The first report says everything the retries would.
            bool diagThis = followDiag && diagReported.Add(r.name);
            int[] whyG = new int[nGlobal];      // FD_* reason this group did or did not bind
            float[] distG = new float[nGlobal]; // distance to the nearest body surface (diagnostic, -1 unknown)
            for (int gg = 0; gg < nGlobal; gg++)
            {
                distG[gg] = -1f;
                Vector3 qd = toSrc.MultiplyPoint3x4(pts[gFirst[gg]]) - offSrc;
                if (boneOkG != null && !boneOkG[gg]) { whyG[gg] = FD_BONE; if (diagThis) distG[gg] = DiagDist(qd); continue; }
                Vector3 q = qd;
                if (q.x < lo.x || q.y < lo.y || q.z < lo.z || q.x > hi.x || q.y > hi.y || q.z > hi.z) { whyG[gg] = FD_FAR; continue; }
                Vector3 ns = gNormSum[gg];
                Vector3 nq = ns.sqrMagnitude > 1e-6f ? toSrc.MultiplyVector(ns).normalized : Vector3.zero;
                int bt; Vector3 cp, bar; bool viaGate;
                if (!bindGrid.NearestGatedWithin(q, nq, 0.35f, range * 1.25f, out bt, out cp, out bar, out viaGate))
                { whyG[gg] = FD_NOHIT; if (diagThis) distG[gg] = DiagDist(q); continue; }
                float dist = (q - cp).magnitude;
                distG[gg] = dist;
                if (dist > range) { whyG[gg] = FD_RANGE; continue; }
                whyG[gg] = FD_BOUND;
                float u = Mathf.Clamp01((dist - range * 0.5f) / (range * 0.5f));
                float fall = 1f - u * u * (3f - 2f * u);   // 1 inside half-range, smooth to 0 at range
                localOf[gg] = rep.Count;
                rep.Add(gFirst[gg]);
                A.Add(bindTris[bt]); B.Add(bindTris[bt + 1]); C.Add(bindTris[bt + 2]);
                WA.Add(bar.x); WB.Add(bar.y); WC.Add(bar.z); FF.Add(fall);
            }
            if (rep.Count < 8)
            {
                if (diagThis) FollowDiagBind(r, pts, gOfGlobal, gFirst, whyG, distG, null, range, whole, offSrc, null);
                // Not one group passed the bone filter, flesh fallback included (hair, face,
                // shoes): no pose can change that, only a new paint or region bones, and those
                // start a new follower generation anyway. Retrying such meshes every 5 s re-baked
                // ~15 of them (11k-group twin-tails among them) one per frame: a stutter burst.
                // (Pose CAN matter for the flesh fallback, so a mesh only counts as dead when
                // not one of its verts has a flesh bone at all; dead ones still retry once a
                // minute, and at once after a paint or region-bone rebuild.)
                bool boneDead = false;
                if (boneOkG != null && lastFleshCand == 0)
                {
                    boneDead = true;
                    for (int gg = 0; gg < boneOkG.Length; gg++) if (boneOkG[gg]) { boneDead = false; break; }
                }
                Object.Destroy(bk);
                if (boneDead) folDead.Add(r); else folFailed.Add(r);   // not covered (maybe off-pose): retry later
                return;
            }
            int drivenCount = rep.Count;

            // ---- VALLEY-GAP INFILL ----
            // Where the region has a valley (cleavage), the cloth bridging it often fails to
            // bind (the walls face away from the bridge). Small/medium UNBOUND islands that
            // sit between bound areas get FILL groups: no binding of their own — their motion
            // is diffused from the surrounding bound cloth every frame ("transfer, average
            // and smooth from nearby"). Large unbound sheets (skirts, straps heading away)
            // stay untracked by design.
            List<bool> FILL = new List<bool>();
            for (int i = 0; i < drivenCount; i++) FILL.Add(false);
            {
                List<int>[] adjG = new List<int>[nGlobal];
                for (int i = 0; i < nGlobal; i++) adjG[i] = new List<int>(6);
                int[] tris0 = bk.triangles;
                for (int t = 0; t + 2 < tris0.Length; t += 3)
                {
                    int ga = gOfGlobal[tris0[t]], gb = gOfGlobal[tris0[t + 1]], gc = gOfGlobal[tris0[t + 2]];
                    if (ga != gb && !adjG[ga].Contains(gb)) { adjG[ga].Add(gb); adjG[gb].Add(ga); }
                    if (gb != gc && !adjG[gb].Contains(gc)) { adjG[gb].Add(gc); adjG[gc].Add(gb); }
                    if (ga != gc && !adjG[ga].Contains(gc)) { adjG[ga].Add(gc); adjG[gc].Add(ga); }
                }
                int fillMax = settingsRef != null ? Mathf.Max(0, Mathf.RoundToInt(settingsRef.cloth.follow.fillMax)) : 600;
                int anchorMin = settingsRef != null ? Mathf.Max(1, Mathf.RoundToInt(settingsRef.cloth.follow.anchorMin)) : 150;

                // size the BOUND areas first: only components at least `anchorMin` big count
                // as anchors — a stray bound speck can't legitimise filling around itself
                int[] drvComp = new int[nGlobal];
                for (int i = 0; i < nGlobal; i++) drvComp[i] = -1;
                List<int> drvSize = new List<int>();
                List<int> stack = new List<int>();
                for (int s0 = 0; s0 < nGlobal; s0++)
                {
                    if (drvComp[s0] >= 0 || localOf[s0] < 0) continue;
                    int id = drvSize.Count; drvSize.Add(0);
                    stack.Clear(); stack.Add(s0); drvComp[s0] = id;
                    while (stack.Count > 0)
                    {
                        int g0 = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1);
                        drvSize[id]++;
                        List<int> nb = adjG[g0];
                        for (int j = 0; j < nb.Count; j++)
                            if (localOf[nb[j]] >= 0 && drvComp[nb[j]] < 0) { drvComp[nb[j]] = id; stack.Add(nb[j]); }
                    }
                }

                bool[] seen = new bool[nGlobal];
                List<int> comp = new List<int>();
                for (int s0 = 0; s0 < nGlobal; s0++)
                {
                    if (seen[s0] || localOf[s0] >= 0) continue;
                    comp.Clear(); stack.Clear(); stack.Add(s0); seen[s0] = true;
                    int anchoredTouch = 0;
                    while (stack.Count > 0)
                    {
                        int g0 = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1);
                        comp.Add(g0);
                        List<int> nb = adjG[g0];
                        for (int j = 0; j < nb.Count; j++)
                        {
                            int b0 = nb[j];
                            if (localOf[b0] >= 0)
                            { if (drvSize[drvComp[b0]] >= anchorMin) anchoredTouch++; continue; }
                            if (!seen[b0]) { seen[b0] = true; stack.Add(b0); }
                        }
                    }
                    // small/medium island, meaningfully surrounded by LARGE tracked areas
                    if (fillMax > 0 && comp.Count <= fillMax && anchoredTouch >= 6 && anchoredTouch * 6 >= comp.Count)
                        for (int j = 0; j < comp.Count; j++)
                        {
                            localOf[comp[j]] = rep.Count;
                            rep.Add(gFirst[comp[j]]);
                            A.Add(0); B.Add(0); C.Add(0);
                            WA.Add(0f); WB.Add(0f); WC.Add(0f); FF.Add(0f);
                            FILL.Add(true);
                        }
                }
            }

            Follower f = new Follower();
            f.smr = r; f.baked = bk; f.baked.MarkDynamic();
            f.display = Object.Instantiate(bk); f.display.MarkDynamic();
            f.display.name = r.name + "_jellofollow";
            int G = rep.Count;
            f.whole = whole;
            f.gRep = rep.ToArray(); f.gA = A.ToArray(); f.gB = B.ToArray(); f.gC = C.ToArray();
            f.gwA = WA.ToArray(); f.gwB = WB.ToArray(); f.gwC = WC.ToArray(); f.gFall = FF.ToArray();
            f.verts = new Vector3[pts.Length];
            f.gDisp = new Vector3[G]; f.gDisp2 = new Vector3[G];
            f.gNrm = new Vector3[G]; f.gProt = new float[G]; f.gDnRaw = new float[G];
            f.gIsFill = FILL.ToArray();
            f.hasFill = G > drivenCount;
            for (int gg = 0; gg < nGlobal; gg++)
                if (localOf[gg] >= 0 && f.gIsFill[localOf[gg]]) whyG[gg] = FD_FILL;

            // WRAP offsets: each group's rest position expressed in its bound tri's local
            // frame, measured on the CLEAN current-pose surface (bindPts: skinned bra vs
            // skinned body, SAME frame, sim removed). Per frame the group rides the tri
            // absolutely, so sim, blendshapes and skinning divergence are all tracked 1:1.
            f.gOff = new Vector3[G];
            f.gOffRest = new Vector3[G];
            Vector3[] refN = whole ? bakedNormals : cage.simNormals;
            for (int g2 = 0; g2 < G; g2++)
            {
                if (f.gIsFill[g2]) continue;   // fill groups have no binding to measure
                Vector3 q2 = toSrc.MultiplyPoint3x4(pts[f.gRep[g2]]);
                Vector3 A3 = bindPts[f.gA[g2]], B3 = bindPts[f.gB[g2]], C3 = bindPts[f.gC[g2]];
                Vector3 cpB = A3 * f.gwA[g2] + B3 * f.gwB[g2] + C3 * f.gwC[g2];
                Vector3 e1, e2, n3;
                TriFrame(A3, B3, C3, out e1, out e2, out n3);
                // canonicalize the frame: cage-tri winding isn't guaranteed, and the anti-
                // clip floor must know which way is genuinely OUT of the body. Same rule
                // is applied per frame, so offsets stay consistent.
                if (Vector3.Dot(n3, refN[f.gA[g2]] + refN[f.gB[g2]] + refN[f.gC[g2]]) < 0f) { n3 = -n3; e2 = -e2; }
                Vector3 d3 = q2 - cpB;
                f.gOffRest[g2] = d3;
                f.gOff[g2] = new Vector3(Vector3.Dot(d3, e1), Vector3.Dot(d3, e2), Vector3.Dot(d3, n3));
            }

            if (followDiag) FollowDiagBind(r, pts, gOfGlobal, gFirst, whyG, distG, f, range, whole, offSrc, bk.triangles, diagThis);

            // members CSR (driven groups only)
            int[] cnt = new int[G];
            for (int i = 0; i < pts.Length; i++) { int lg = localOf[gOfGlobal[i]]; if (lg >= 0) cnt[lg]++; }
            f.gMemStart = new int[G + 1];
            for (int g2 = 0; g2 < G; g2++) f.gMemStart[g2 + 1] = f.gMemStart[g2] + cnt[g2];
            f.gMem = new int[f.gMemStart[G]];
            int[] cur = new int[G];
            for (int i = 0; i < pts.Length; i++)
            {
                int lg = localOf[gOfGlobal[i]];
                if (lg >= 0) f.gMem[f.gMemStart[lg] + cur[lg]++] = i;
            }

            // adjacency among driven groups + boundary detection (edge into undriven territory)
            List<int>[] adj = new List<int>[G];
            for (int g2 = 0; g2 < G; g2++) adj[g2] = new List<int>(6);
            bool[] boundary = new bool[G];
            int[] tris = bk.triangles;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int la = localOf[gOfGlobal[tris[t]]], lb = localOf[gOfGlobal[tris[t + 1]]], lc = localOf[gOfGlobal[tris[t + 2]]];
                if (la >= 0 && lb >= 0 && la != lb && !adj[la].Contains(lb)) { adj[la].Add(lb); adj[lb].Add(la); }
                if (lb >= 0 && lc >= 0 && lb != lc && !adj[lb].Contains(lc)) { adj[lb].Add(lc); adj[lc].Add(lb); }
                if (la >= 0 && lc >= 0 && la != lc && !adj[la].Contains(lc)) { adj[la].Add(lc); adj[lc].Add(la); }
                if (la >= 0 && (lb < 0 || lc < 0)) boundary[la] = true;
                if (lb >= 0 && (la < 0 || lc < 0)) boundary[lb] = true;
                if (lc >= 0 && (la < 0 || lb < 0)) boundary[lc] = true;
            }
            f.gAdj = new int[G][];
            for (int g2 = 0; g2 < G; g2++) f.gAdj[g2] = adj[g2].ToArray();
            FindFloatingIslands(f, r.name, OffSkinFlags(f, toSrc, pts));
            f.allRigid = f.islN > 0;
            for (int g2 = 0; g2 < f.gRep.Length && f.allRigid; g2++)
                if (!f.gIsFill[g2] && f.gIsl[g2] < 0) f.allRigid = false;
            BuildSkinGuard(f, r.name, toSrc, pts, gOfGlobal, gFirst, gNormSum, localOf, tris);

            // seam field: multi-source Dijkstra from the driven-area boundary (mirrors the
            // main mesh's painted<->unpainted seam machinery, reusing the same sliders)
            f.gSeam = new float[G];
            for (int g2 = 0; g2 < G; g2++) f.gSeam[g2] = float.MaxValue;
            MinHeap heap = new MinHeap(G);
            for (int g2 = 0; g2 < G; g2++)
                if (boundary[g2] || (!f.gIsFill[g2] && f.gFall[g2] < 0.5f)) { f.gSeam[g2] = 0f; heap.Push(0f, g2); }
            while (heap.Count > 0)
            {
                float d2; int a2; heap.Pop(out d2, out a2);
                if (d2 > f.gSeam[a2] || d2 > SEAM_MAXR) continue;
                Vector3 pa = pts[f.gRep[a2]];
                int[] nb = f.gAdj[a2];
                for (int j = 0; j < nb.Length; j++)
                {
                    int b2 = nb[j];
                    float nd = d2 + (pa - pts[f.gRep[b2]]).magnitude;
                    if (nd < f.gSeam[b2]) { f.gSeam[b2] = nd; if (nd <= SEAM_MAXR) heap.Push(nd, b2); }
                }
            }
            List<int> band = new List<int>();
            for (int g2 = 0; g2 < G; g2++) if (f.gSeam[g2] <= SEAM_MAXR) band.Add(g2);
            f.gBand = band.ToArray();
            float[] gs = f.gSeam;
            System.Array.Sort(f.gBand, delegate(int x, int y) { return gs[x].CompareTo(gs[y]); });
            f.go = new GameObject(r.name + "_JelloFollow");
            f.go.transform.SetParent(r.transform, false);
            f.go.transform.localPosition = Vector3.zero;
            f.go.transform.localRotation = Quaternion.identity;
            f.go.transform.localScale = Vector3.one;
            f.mf = f.go.AddComponent<MeshFilter>(); f.mf.sharedMesh = f.display;
            f.mr = f.go.AddComponent<MeshRenderer>();
            f.mr.sharedMaterials = r.sharedMaterials;
            f.mr.shadowCastingMode = r.shadowCastingMode;
            f.mr.receiveShadows = r.receiveShadows;
            r.forceRenderingOff = true;
            followers.Add(f);
            Debug.Log("[Jello] cage follower '" + r.name + "': " + f.gMem.Length + "/" + pts.Length
                + " verts (" + G + " groups) driven, range " + range.ToString("0.000") + " m");
        }

        void UpdateFollowers()
        {
            if (followers.Count == 0 || go == null) return;
            bool cageOk = cage != null && cageOut != null && cageOut.Length == cage.SimVertCount;
            Matrix4x4 srcL2W = go.transform.localToWorldMatrix;
            float fieldMax = 0f;
            if (cageOut != null)
                for (int ci = 0; ci < cageOut.Length; ci++)
                { float m2 = cageOut[ci].sqrMagnitude; if (m2 > fieldMax) fieldMax = m2; }
            float extraMax = 0f;
            if (cageExtra != null)
                for (int ci = 0; ci < cageExtra.Length; ci++)
                { float m3 = cageExtra[ci].sqrMagnitude; if (m3 > extraMax) extraMax = m3; }
            // the ceiling has to cover the downstream contribution too, or the very
            // contact motion this pass exists to carry gets clamped away
            float folMaxMove = Mathf.Max(0.02f, (Mathf.Sqrt(fieldMax) + Mathf.Sqrt(extraMax)) * 2.5f);
            bool rigidFull = settingsRef == null || settingsRef.cloth.follow.rigidFloat >= 0.999f;
            int frameNow = Time.frameCount;
            for (int fi = followers.Count - 1; fi >= 0; fi--)
            {
                Follower f = followers[fi];
                // usually already baked this frame, while jell-o's solve ran (PreBakeFollowers)
                if (f.bakedFrame != frameNow)
                {
                    CloLap();
                    bool alive = BakeFollower(fi, srcL2W);
                    cloBake += CloLap();
                    if (!alive) continue;
                }
                bool haveN = f.haveN;
                Matrix4x4 M = f.frameM;
                if (!f.whole && !cageOk) continue;   // cage followers wait out a rebuild frame
                float fit = settingsRef != null ? Mathf.Clamp(settingsRef.cloth.follow.fitStrength, 0f, 3f) : 1f;
                float inf = settingsRef != null ? Mathf.Clamp(settingsRef.cloth.follow.inflate, 0f, 0.1f) : 0f;
                float dyn = settingsRef != null ? Mathf.Clamp(settingsRef.cloth.follow.inflateDyn, 0f, 3f) : 0f;
                int G = f.gRep.Length;
                Vector3[] cb = f.whole ? (finalVerts != null ? finalVerts : bakedVerts) : cage.simBaked;
                // each group reads the shared cage + its own rest offset and writes only its
                // own slots, so the groups split across cores (results are identical).
                // Every 600 frames the old Vector3 version runs too and must match exactly.
                // (the first 8 checks of a session only: each one re-runs the old version)
                bool wrapCheck = (Time.frameCount % 600) == 37 && wrapChecks < 8 * Mathf.Max(1, followers.Count);
                if (wrapCheck) WrapSnapshot(f);
                WrapAllFast(f, cb, M, fit, inf, dyn, folMaxMove);
                if (wrapCheck) WrapVerify(f, cb, M, fit, inf, dyn, folMaxMove);

                cloWrap += CloLap();
                // driven-area seam polish: SAME sliders as the painted<->unpainted seam.
                // Smooths across the boundary band of the garment and cone-clamps the ramp
                // so the driven edge can't crease or pop through.
                if (settingsRef != null && f.gBand.Length > 0 && !(f.allRigid && rigidFull))
                {
                    int slv = Mathf.Clamp(Mathf.RoundToInt(settingsRef.jello.seamLevel), 0, 200);
                    float srange = Mathf.Min(settingsRef.jello.seamRange, SEAM_MAXR);
                    float smax = settingsRef.jello.seamMaxStretch;
                    if ((slv > 0 || smax > 0.0001f) && srange > 0.0002f)
                    {
                        int active = f.gBand.Length;
                        for (int i = 0; i < f.gBand.Length; i++) if (f.gSeam[f.gBand[i]] > srange) { active = i; break; }
                        float invR = 1f / srange;
                        // each band group reads the previous pass and writes only its own slot:
                        // the pass splits across cores with identical results
                        int[] bandS = f.gBand; float[] seamS = f.gSeam; int[][] adjB = f.gAdj;
                        Vector3[] dA = f.gDisp, dB = f.gDisp2;
                        for (int pass = 0; pass < slv; pass++)
                        {
                            SoftBodySuite.Field.ParallelLoop.For(active, 256, delegate (int lo, int hi)
                            {
                                for (int i = lo; i < hi; i++)
                                {
                                    int gg = bandS[i];
                                    float bw2 = 1f - seamS[gg] * invR;
                                    int[] nb = adjB[gg];
                                    if (bw2 <= 0f || nb.Length < 2) { dB[gg] = dA[gg]; continue; }
                                    float ax = 0f, ay = 0f, az = 0f;
                                    for (int j = 0; j < nb.Length; j++) { Vector3 v = dA[nb[j]]; ax += v.x; ay += v.y; az += v.z; }
                                    float inv = 1f / nb.Length;
                                    Vector3 c0 = dA[gg];
                                    dB[gg] = new Vector3(c0.x + (ax * inv - c0.x) * bw2, c0.y + (ay * inv - c0.y) * bw2, c0.z + (az * inv - c0.z) * bw2);
                                }
                            });
                            SoftBodySuite.Field.ParallelLoop.For(active, 2048, delegate (int lo, int hi)
                            {
                                for (int i = lo; i < hi; i++) { int gg = bandS[i]; dA[gg] = dB[gg]; }
                            });
                        }
                        if (smax > 0.0001f)
                        {
                            float slope = smax / Mathf.Max(srange, 0.0005f);
                            SoftBodySuite.Field.ParallelLoop.For(bandS.Length, 2048, delegate (int lo, int hi)
                            {
                                for (int i = lo; i < hi; i++)
                                {
                                    int gg = bandS[i];
                                    float allowed = seamS[gg] * slope;
                                    float dm = dA[gg].magnitude;
                                    if (dm > allowed && dm > 1e-9f) dA[gg] *= allowed / dm;
                                }
                            });
                        }
                    }
                }

                // ---- CLOTH-SIDE ANTI-CLIP (the body is never touched) ----
                // Each group knows how far it rested OUTSIDE the body (gOff.z) and what the
                // full wrap would have done (gDnRaw). A garment may drift closer to the skin
                // than its rest gap, but never nearer than `minClear` — so the flesh cannot
                // emerge through it, and the fix is applied entirely to the garment.
                if (settingsRef != null && settingsRef.cloth.follow.clothOutside)
                {
                    float minClear = Mathf.Clamp(settingsRef.cloth.follow.minClear, 0f, 0.05f);
                    SoftBodySuite.Field.ParallelLoop.For(G, 2048, delegate (int lo, int hi)
                    {
                        for (int g2 = lo; g2 < hi; g2++)
                        {
                            if (f.gIsFill[g2]) continue;   // no surface reference to clamp against
                            float allow = Mathf.Max(0f, Mathf.Abs(f.gOff[g2].z) - minClear);
                            float need = f.gDnRaw[g2] - allow;
                            float cur = Vector3.Dot(f.gDisp[g2], f.gNrm[g2]);
                            if (cur >= need) continue;
                            float add = Mathf.Min(need - cur, 0.03f);   // never teleport a vert
                            f.gDisp[g2] += f.gNrm[g2] * add;
                        }
                    });
                }

                // anti-clip floor: whatever the seam smoothing/clamps did above, the protected
                // outward component survives — a close-fitting cup can never be pierced.
                // The floor still obeys the seam RAMP law near the driven edge (a boundary
                // group snapping to full amplitude against undriven neighbours is a tear).
                float pSmax = settingsRef != null ? settingsRef.jello.seamMaxStretch : 0f;
                float pSrange = settingsRef != null ? Mathf.Min(settingsRef.jello.seamRange, SEAM_MAXR) : 0f;
                float pSlope = (pSmax > 0.0001f && pSrange > 0.0005f) ? pSmax / pSrange : -1f;
                SoftBodySuite.Field.ParallelLoop.For(G, 2048, delegate (int lo, int hi)
                {
                    for (int g2 = lo; g2 < hi; g2++)
                    {
                        float prot = f.gProt[g2];
                        if (prot <= 0f) continue;
                        if (pSlope > 0f) prot = Mathf.Min(prot, f.gSeam[g2] * pSlope);
                        float dn2 = Vector3.Dot(f.gDisp[g2], f.gNrm[g2]);
                        if (dn2 < prot) f.gDisp[g2] += f.gNrm[g2] * (prot - dn2);
                    }
                });

                cloSeam += CloLap();
                // CLOTH FIELD SMOOTHING: diffuse the finished per-group motion over the
                // garment itself. This used to be gated behind 'sharpness', so at the
                // default the slider did nothing at all.
                int clothSm = settingsRef != null ? Mathf.Clamp(Mathf.RoundToInt(settingsRef.cloth.follow.folSmooth), 0, 60) : 0;
                // a garment that is one rigid piece (the bow) is overwritten by the rigid fit
                // below, so smoothing it was ~1/3 of this pass for nothing
                if (f.allRigid && rigidFull) clothSm = 0;
                int[] islS = (f.islN > 0 && rigidFull) ? f.gIsl : null;
                for (int pass = 0; pass < clothSm; pass++)
                {
                    Vector3[] src = f.gDisp, dst = f.gDisp2;
                    int[][] adjS = f.gAdj;
                    SoftBodySuite.Field.ParallelLoop.For(G, 256, delegate (int lo, int hi)
                    {
                        for (int g2 = lo; g2 < hi; g2++)
                        {
                            int[] nb = adjS[g2];
                            if (nb.Length < 2 || (islS != null && islS[g2] >= 0)) { dst[g2] = src[g2]; continue; }
                            float ax = 0f, ay = 0f, az = 0f;
                            for (int j = 0; j < nb.Length; j++) { Vector3 v = src[nb[j]]; ax += v.x; ay += v.y; az += v.z; }
                            float inv = 0.5f / nb.Length;
                            Vector3 c0 = src[g2];
                            dst[g2] = new Vector3(c0.x * 0.5f + ax * inv, c0.y * 0.5f + ay * inv, c0.z * 0.5f + az * inv);
                        }
                    });
                    f.gDisp = dst; f.gDisp2 = src;   // swap the two scratch buffers instead of copying
                }

                // valley-gap infill: unbound islands between tracked cloth inherit motion by
                // diffusion from their neighbors (warm-started, converges in a few sweeps)
                if (f.hasFill)
                    for (int pass = 0; pass < 4; pass++)
                        for (int g2 = 0; g2 < G; g2++)
                        {
                            if (!f.gIsFill[g2]) continue;
                            int[] nb = f.gAdj[g2];
                            if (nb.Length == 0) continue;
                            Vector3 avg = Vector3.zero;
                            for (int j = 0; j < nb.Length; j++) avg += f.gDisp[nb[j]];
                            f.gDisp[g2] = avg / nb.Length;
                        }

                cloSmooth += CloLap();
                if (f.islN > 0) RigidIslands(f);

                SoftBodySuite.Field.ParallelLoop.For(G, 2048, delegate (int lo, int hi)
                {
                    for (int g2 = lo; g2 < hi; g2++)
                    {
                        Vector3 dl = f.gDisp[g2];
                        int e = f.gMemStart[g2 + 1];
                        for (int m = f.gMemStart[g2]; m < e; m++) f.verts[f.gMem[m]] += dl;
                    }
                });
                cloRigid += CloLap();
                if (guardFin != null && f.kRep != null && settingsRef != null && settingsRef.cloth.follow.skinGuard)
                    ApplySkinGuard(f, M, guardFin);
                cloGuard += CloLap();
                f.display.SetVertices(f.verts);
                cloUpload += CloLap();
                if (followDiag) FollowDiagFrame(f, M);
                cloDiag += CloLap();
                if (haveN) f.display.SetNormals(f.nrm);
                if (!f.boundsSet)
                { f.display.bounds = new Bounds(f.display.bounds.center, f.display.bounds.size + Vector3.one * 2f); f.boundsSet = true; }
                cloUpload += CloLap();
            }
        }

        readonly List<Vector3> folNrmScratch = new List<Vector3>();


        // The wrap written out per component: the SAME operations in the SAME order with the
        // same float32 roundings as the Vector3/Matrix4x4 version (WrapAllRef): every Vector3
        // temporary or float return value is a float local here. Mono does not inline the
        // operators, so this is ~2x faster with the same numbers.
        void WrapAllFast(Follower f, Vector3[] cb, Matrix4x4 M, float fit, float inf, float dyn, float folMaxMove)
        {
            int G = f.gRep.Length;
            bool whole = f.whole;
            Vector3[] co = cageOut, ce = cageExtra;
            Vector3[] NR = whole ? bakedNormals : cage.simNormals;
            bool diag = followDiag && f.dCp != null;
            float m00 = M.m00, m01 = M.m01, m02 = M.m02, m03 = M.m03;
            float m10 = M.m10, m11 = M.m11, m12 = M.m12, m13 = M.m13;
            float m20 = M.m20, m21 = M.m21, m22 = M.m22, m23 = M.m23;
            SoftBodySuite.Field.ParallelLoop.For(G, 256, delegate (int lo, int hi)
            {
                for (int g2 = lo; g2 < hi; g2++)
                {
                    if (f.gIsFill[g2]) { f.gProt[g2] = 0f; continue; }
                    int ia = f.gA[g2], ib = f.gB[g2], ic = f.gC[g2];
                    float ax, ay, az, bx, by, bz, cx, cy, cz;
                    if (whole)
                    {
                        ax = cb[ia].x; ay = cb[ia].y; az = cb[ia].z;
                        bx = cb[ib].x; by = cb[ib].y; bz = cb[ib].z;
                        cx = cb[ic].x; cy = cb[ic].y; cz = cb[ic].z;
                    }
                    else if (ce != null)
                    {
                        float sx, sy, sz;
                        sx = cb[ia].x + co[ia].x; sy = cb[ia].y + co[ia].y; sz = cb[ia].z + co[ia].z;
                        ax = sx + ce[ia].x; ay = sy + ce[ia].y; az = sz + ce[ia].z;
                        sx = cb[ib].x + co[ib].x; sy = cb[ib].y + co[ib].y; sz = cb[ib].z + co[ib].z;
                        bx = sx + ce[ib].x; by = sy + ce[ib].y; bz = sz + ce[ib].z;
                        sx = cb[ic].x + co[ic].x; sy = cb[ic].y + co[ic].y; sz = cb[ic].z + co[ic].z;
                        cx = sx + ce[ic].x; cy = sy + ce[ic].y; cz = sz + ce[ic].z;
                    }
                    else
                    {
                        ax = cb[ia].x + co[ia].x; ay = cb[ia].y + co[ia].y; az = cb[ia].z + co[ia].z;
                        bx = cb[ib].x + co[ib].x; by = cb[ib].y + co[ib].y; bz = cb[ib].z + co[ib].z;
                        cx = cb[ic].x + co[ic].x; cy = cb[ic].y + co[ic].y; cz = cb[ic].z + co[ic].z;
                    }
                    // ---- TriFrame
                    float e1x = bx - ax, e1y = by - ay, e1z = bz - az;
                    float m1 = (float)System.Math.Sqrt(e1x * e1x + e1y * e1y + e1z * e1z);
                    float acx = cx - ax, acy = cy - ay, acz = cz - az;
                    float nx = e1y * acz - e1z * acy, ny = e1z * acx - e1x * acz, nz = e1x * acy - e1y * acx;
                    float mn = (float)System.Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (m1 < 1e-9f || mn < 1e-12f)
                    { f.gDisp[g2] = Vector3.zero; f.gNrm[g2] = Vector3.up; f.gProt[g2] = 0f; continue; }
                    e1x = e1x / m1; e1y = e1y / m1; e1z = e1z / m1;
                    nx = nx / mn; ny = ny / mn; nz = nz / mn;
                    float e2x = ny * e1z - nz * e1y, e2y = nz * e1x - nx * e1z, e2z = nx * e1y - ny * e1x;
                    // ---- canonical side
                    float rx = NR[ia].x + NR[ib].x, ry = NR[ia].y + NR[ib].y, rz = NR[ia].z + NR[ib].z;
                    rx = rx + NR[ic].x; ry = ry + NR[ic].y; rz = rz + NR[ic].z;
                    float side = nx * rx + ny * ry + nz * rz;
                    if (side < 0f) { nx = -nx; ny = -ny; nz = -nz; e2x = -e2x; e2y = -e2y; e2z = -e2z; }
                    // ---- cp2 = A*wA + B*wB + C*wC
                    float wa = f.gwA[g2], wb = f.gwB[g2], wc = f.gwC[g2];
                    float pax = ax * wa, pay = ay * wa, paz = az * wa;
                    float pbx = bx * wb, pby = by * wb, pbz = bz * wb;
                    float pcx = cx * wc, pcy = cy * wc, pcz = cz * wc;
                    float qx = pax + pbx, qy = pay + pby, qz = paz + pbz;
                    float cpx = qx + pcx, cpy = qy + pcy, cpz = qz + pcz;
                    if (diag) { f.dCp[g2] = new Vector3(cpx, cpy, cpz); f.dN[g2] = new Vector3(nx, ny, nz); f.dOk[g2] = true; }
                    // ---- rotated = e1*off.x + e2*off.y + n2*off.z
                    Vector3 off = f.gOff[g2];
                    float r1x = e1x * off.x, r1y = e1y * off.x, r1z = e1z * off.x;
                    float r2x = e2x * off.y, r2y = e2y * off.y, r2z = e2z * off.y;
                    float r3x = nx * off.z, r3y = ny * off.z, r3z = nz * off.z;
                    float rsx = r1x + r2x, rsy = r1y + r2y, rsz = r1z + r2z;
                    float rotx = rsx + r3x, roty = rsy + r3y, rotz = rsz + r3z;
                    Vector3 orr = f.gOffRest[g2];
                    float offLen = (float)System.Math.Sqrt(orr.x * orr.x + orr.y * orr.y + orr.z * orr.z);
                    float wRot = 1f - Mathf.Clamp01((offLen - 0.02f) / 0.06f);
                    wRot = wRot * wRot * (3f - 2f * wRot);
                    // ---- wrapSrc = cp2 + Lerp(offRest, rotated, wRot)
                    float tl = Mathf.Clamp01(wRot);
                    float lx = orr.x + (rotx - orr.x) * tl, ly = orr.y + (roty - orr.y) * tl, lz = orr.z + (rotz - orr.z) * tl;
                    float wsx = cpx + lx, wsy = cpy + ly, wsz = cpz + lz;
                    // ---- dlRaw = M.MultiplyPoint3x4(wrapSrc) - verts[rep]
                    float px = m00 * wsx + m01 * wsy + m02 * wsz + m03;
                    float py = m10 * wsx + m11 * wsy + m12 * wsz + m13;
                    float pz = m20 * wsx + m21 * wsy + m22 * wsz + m23;
                    Vector3 vr = f.verts[f.gRep[g2]];
                    float dlx = px - vr.x, dly = py - vr.y, dlz = pz - vr.z;
                    // ---- nl = M.MultiplyVector(n2).normalized
                    float mvx = m00 * nx + m01 * ny + m02 * nz;
                    float mvy = m10 * nx + m11 * ny + m12 * nz;
                    float mvz = m20 * nx + m21 * ny + m22 * nz;
                    float mvm = (float)System.Math.Sqrt(mvx * mvx + mvy * mvy + mvz * mvz);
                    float nlx, nly, nlz;
                    if (mvm > 1E-05f) { nlx = mvx / mvm; nly = mvy / mvm; nlz = mvz / mvm; }
                    else { nlx = 0f; nly = 0f; nlz = 0f; }
                    // ---- anti-clip split (see WrapAllRef for the reasoning)
                    float baseW = f.gFall[g2] * fit;
                    float dn = dlx * nlx + dly * nly + dlz * nlz;
                    float t1x = nlx * dn, t1y = nly * dn, t1z = nlz * dn;
                    float dtx = dlx - t1x, dty = dly - t1y, dtz = dlz - t1z;
                    float wClip = 1f - Mathf.Clamp01((Mathf.Abs(off.z) - 0.01f) / 0.02f);
                    float ef = Mathf.Clamp01(f.gSeam[g2] / 0.02f);
                    wClip *= ef * ef * (3f - 2f * ef);
                    float outW = Mathf.Max(baseW, wClip);
                    float prot = 0f;
                    float ox = dtx * baseW, oy = dty * baseW, oz = dtz * baseW;
                    if (dn > 0f)
                    {
                        float s1 = dn * outW;
                        float ux = nlx * s1, uy = nly * s1, uz = nlz * s1;
                        ox = ox + ux; oy = oy + uy; oz = oz + uz;
                        prot = dn * outW;
                        if (dyn > 0f)
                        {
                            float s2 = dn * outW * dyn;
                            float vx = nlx * s2, vy = nly * s2, vz = nlz * s2;
                            ox = ox + vx; oy = oy + vy; oz = oz + vz;
                            prot += dn * outW * dyn;
                        }
                    }
                    else
                    {
                        float s3 = dn * baseW;
                        float ux = nlx * s3, uy = nly * s3, uz = nlz * s3;
                        ox = ox + ux; oy = oy + uy; oz = oz + uz;
                    }
                    if (inf > 0f)
                    {
                        float add = inf * Mathf.Max(f.gFall[g2], wClip);
                        float ux = nlx * add, uy = nly * add, uz = nlz * add;
                        ox = ox + ux; oy = oy + uy; oz = oz + uz;
                        prot += add;
                    }
                    float dlM = (float)System.Math.Sqrt(ox * ox + oy * oy + oz * oz);
                    if (dlM > folMaxMove)
                    {
                        float rr = folMaxMove / dlM;
                        ox = ox * rr; oy = oy * rr; oz = oz * rr;
                        prot = Mathf.Min(prot, folMaxMove);
                    }
                    f.gNrm[g2] = new Vector3(nlx, nly, nlz); f.gProt[g2] = prot; f.gDnRaw[g2] = dn;
                    f.gDisp[g2] = new Vector3(ox, oy, oz);
                }
            });
        }

        // ---- the fast wrap's self-check: snapshot, run fast, run the reference, compare ----
        static int wrapChecks, wrapMismatchGroups;
        void WrapSnapshot(Follower f)
        {
            int G = f.gRep.Length;
            if (f.ckD == null || f.ckD.Length != G)
            { f.ckD = new Vector3[G]; f.ckN = new Vector3[G]; f.ckP = new float[G]; f.ckR = new float[G];
              f.ckD2 = new Vector3[G]; f.ckN2 = new Vector3[G]; f.ckP2 = new float[G]; f.ckR2 = new float[G]; }
            System.Array.Copy(f.gDisp, f.ckD, G); System.Array.Copy(f.gNrm, f.ckN, G);
            System.Array.Copy(f.gProt, f.ckP, G); System.Array.Copy(f.gDnRaw, f.ckR, G);
        }

        void WrapVerify(Follower f, Vector3[] cb, Matrix4x4 M, float fit, float inf, float dyn, float folMaxMove)
        {
            int G = f.gRep.Length;
            // keep the fast result, put the inputs back, run the reference over them
            System.Array.Copy(f.gDisp, f.ckD2, G); System.Array.Copy(f.gNrm, f.ckN2, G);
            System.Array.Copy(f.gProt, f.ckP2, G); System.Array.Copy(f.gDnRaw, f.ckR2, G);
            System.Array.Copy(f.ckD, f.gDisp, G); System.Array.Copy(f.ckN, f.gNrm, G);
            System.Array.Copy(f.ckP, f.gProt, G); System.Array.Copy(f.ckR, f.gDnRaw, G);
            WrapAllRef(f, cb, M, fit, inf, dyn, folMaxMove);
            int bad = 0, first = -1;
            for (int g = 0; g < G; g++)
            {
                // exact float compares (Vector3 == is approximate in Unity)
                bool same = f.gDisp[g].x == f.ckD2[g].x && f.gDisp[g].y == f.ckD2[g].y && f.gDisp[g].z == f.ckD2[g].z
                    && f.gNrm[g].x == f.ckN2[g].x && f.gNrm[g].y == f.ckN2[g].y && f.gNrm[g].z == f.ckN2[g].z
                    && f.gProt[g] == f.ckP2[g] && f.gDnRaw[g] == f.ckR2[g];
                if (!same) { bad++; if (first < 0) first = g; }
            }
            wrapChecks++;
            if (bad > 0) wrapMismatchGroups += bad;
            if (bad > 0 || !f.ckLogged)
            {
                f.ckLogged = true;
                Debug.Log("[Wrap] self-check '" + (f.smr != null ? f.smr.name : "?") + "': " + G + " groups, "
                    + bad + " differ from the reference" + (first >= 0
                        ? " (first g" + first + ": fast " + f.ckD2[first].ToString("F7") + " ref " + f.gDisp[first].ToString("F7") + ")"
                        : "") + "  [checks " + wrapChecks + ", differing groups so far " + wrapMismatchGroups + "]");
            }
            // the reference result stays in place (identical when the check passes)
        }

        // The wrap exactly as it was written with Vector3 operators: the reference the fast
        // version is checked against (WrapVerify), byte for byte.
        void WrapAllRef(Follower f, Vector3[] cb, Matrix4x4 M, float fit, float inf, float dyn, float folMaxMove)
        {
            int G = f.gRep.Length;
            SoftBodySuite.Field.ParallelLoop.For(G, 256, delegate (int lo, int hi)
            {
                for (int g2 = lo; g2 < hi; g2++)
                {
                    if (f.gIsFill[g2]) { f.gProt[g2] = 0f; continue; }   // diffused later, warm-started
                    // WRAP: rebuild the bound tri's frame on the CURRENT surface and re-place
                    // the stored rest offset. whole mode rides Jello's FINAL output verts
                    // (post-lerp, seam-smoothed); cage mode rides skinned interp + sim mirror.
                    int ia = f.gA[g2], ib = f.gB[g2], ic = f.gC[g2];
                    Vector3 A2, B2, C2;
                    if (f.whole) { A2 = cb[ia]; B2 = cb[ib]; C2 = cb[ic]; }
                    else if (cageExtra != null)
                    {
                        A2 = cb[ia] + cageOut[ia] + cageExtra[ia];
                        B2 = cb[ib] + cageOut[ib] + cageExtra[ib];
                        C2 = cb[ic] + cageOut[ic] + cageExtra[ic];
                    }
                    else
                    {
                        A2 = cb[ia] + cageOut[ia];
                        B2 = cb[ib] + cageOut[ib];
                        C2 = cb[ic] + cageOut[ic];
                    }
                    Vector3 e1, e2, n2;
                    if (!TriFrame(A2, B2, C2, out e1, out e2, out n2))
                    { f.gDisp[g2] = Vector3.zero; f.gNrm[g2] = Vector3.up; f.gProt[g2] = 0f; continue; }
                    // same canonicalization as at bind — the floor must push OUT of the body
                    Vector3 nrf = f.whole
                        ? bakedNormals[ia] + bakedNormals[ib] + bakedNormals[ic]
                        : cage.simNormals[ia] + cage.simNormals[ib] + cage.simNormals[ic];
                    if (Vector3.Dot(n2, nrf) < 0f) { n2 = -n2; e2 = -e2; }
                    Vector3 cp2 = A2 * f.gwA[g2] + B2 * f.gwB[g2] + C2 * f.gwC[g2];
                    if (followDiag && f.dCp != null) { f.dCp[g2] = cp2; f.dN[g2] = n2; f.dOk[g2] = true; }
                    Vector3 off = f.gOff[g2];
                    // A wrap re-orients the stored offset by the bound triangle's frame.
                    // That is exact for cloth ON the surface, but for a vert bound far
                    // away the offset is a long lever: a degree of triangle wobble becomes
                    // centimetres of motion, and a frame flip becomes a spike. So rotate
                    // the offset only for close cloth and fade to pure translation as the
                    // rest distance grows.
                    Vector3 rotated = e1 * off.x + e2 * off.y + n2 * off.z;
                    float offLen = f.gOffRest[g2].magnitude;
                    float wRot = 1f - Mathf.Clamp01((offLen - 0.02f) / 0.06f);
                    wRot = wRot * wRot * (3f - 2f * wRot);
                    Vector3 wrapSrc = cp2 + Vector3.Lerp(f.gOffRest[g2], rotated, wRot);
                    Vector3 dlRaw = M.MultiplyPoint3x4(wrapSrc) - f.verts[f.gRep[g2]];
                    Vector3 nl = M.MultiplyVector(n2).normalized;

                    // ANTI-CLIP SPLIT: only the tangential part obeys falloff/fit and the seam
                    // machinery below. The OUTWARD-normal part — the component that stops the
                    // breast punching through the cup — gets a protected floor for close-
                    // fitting verts (rest clearance |off.z| under ~1 cm, fading out by 3 cm).
                    float baseW = f.gFall[g2] * fit;
                    float dn = Vector3.Dot(dlRaw, nl);
                    Vector3 dt = dlRaw - nl * dn;
                    float wClip = 1f - Mathf.Clamp01((Mathf.Abs(off.z) - 0.01f) / 0.02f);
                    // the floor fades in over the first ~2 cm from the driven edge: an
                    // unprotectable boundary vert can't guard the cup anyway — an uncapped
                    // floor there only re-creates the seam step the smoothing removed
                    float ef = Mathf.Clamp01(f.gSeam[g2] / 0.02f);
                    wClip *= ef * ef * (3f - 2f * ef);
                    float outW = Mathf.Max(baseW, wClip);
                    float prot = 0f;
                    Vector3 dl = dt * baseW;
                    if (dn > 0f)
                    {
                        dl += nl * (dn * outW); prot = dn * outW;
                        if (dyn > 0f) { dl += nl * (dn * outW * dyn); prot += dn * outW * dyn; }
                    }
                    else dl += nl * (dn * baseW);   // inward (body moving away): soft, clampable
                    if (inf > 0f)
                    {
                        float add = inf * Mathf.Max(f.gFall[g2], wClip);
                        dl += nl * add; prot += add;
                    }
                    float dlM = dl.magnitude;
                    if (dlM > folMaxMove) { dl *= folMaxMove / dlM; prot = Mathf.Min(prot, folMaxMove); }
                    f.gNrm[g2] = nl; f.gProt[g2] = prot; f.gDnRaw[g2] = dn;
                    f.gDisp[g2] = dl;
                }
            });
        }

        // Ownership checks + BakeMesh of one garment, results kept on the follower for this
        // frame. False = the follower was dropped (hidden, taken over, mesh swapped).
        bool BakeFollower(int fi, Matrix4x4 srcL2W)
        {
            Follower f = followers[fi];
            if (f.smr == null || f.go == null) { RemoveFollower(fi); return false; }
            // hidden meanwhile (hide panel sets enabled=false): drop the follower and
            // RELEASE forceRenderingOff (enabled=false is what hides it, and leaving
            // our flag set would keep the mesh invisible forever after an un-hide)
            if (!f.smr.enabled || !f.smr.gameObject.activeInHierarchy ||
                (settingsRef != null && settingsRef.hiddenMeshes != null && settingsRef.hiddenMeshes.Contains(f.smr.name)))
            { RemoveFollower(fi, true); return false; }
            // another studio took ownership: they manage visibility now
            if (OtherStudioProxyAlive(f.smr)) { RemoveFollower(fi, false); return false; }
            f.smr.forceRenderingOff = true;   // we render this mesh while driving it
            f.smr.BakeMesh(f.baked);
            f.baked.GetVertices(scratch);
            if (scratch.Count != f.verts.Length) { RemoveFollower(fi); return false; }   // mesh swapped
            scratch.CopyTo(f.verts);
            if (followDiag)
            {
                if (f.skin == null || f.skin.Length != f.verts.Length) f.skin = new Vector3[f.verts.Length];
                scratch.CopyTo(f.skin);
            }
            f.baked.GetNormals(f.nrm);
            f.haveN = f.nrm.Count == f.verts.Length;
            f.frameM = f.smr.transform.worldToLocalMatrix * srcL2W;
            f.bakedFrame = Time.frameCount;
            return true;
        }

        // The garments' BakeMesh calls (main thread only) done EARLY, while jell-o's cage
        // solve runs on a worker. Nothing between here and the clothes pass moves a bone,
        // so the garments come out exactly as they would later in the frame.
        void PreBakeFollowers()
        {
            if (followers.Count == 0 || go == null) return;
            Matrix4x4 srcL2W = go.transform.localToWorldMatrix;
            System.Diagnostics.Stopwatch sw = preBakeSw;
            sw.Reset(); sw.Start();
            for (int fi = followers.Count - 1; fi >= 0; fi--) BakeFollower(fi, srcL2W);
            cloPreBake += (float)sw.Elapsed.TotalMilliseconds;
        }
        readonly System.Diagnostics.Stopwatch preBakeSw = new System.Diagnostics.Stopwatch();
        float cloPreBake;

        // ---- jell-o's cage solve on a worker, overlapped with PreBakeFollowers ----
        public static bool overlapSolve = true;
        readonly System.Threading.ManualResetEventSlim solveDone = new System.Threading.ManualResetEventSlim(true);
        System.Threading.WaitCallback solveJob;
        System.Exception solveErr; bool solveBroken;
        int solveSubsteps; float solveSdt, solvePdt; Vector3 solveDown;

        void SolveJob(object state)
        {
            try
            {
                for (int s = 0; s < solveSubsteps; s++)
                    cageSim.StepDynamics(cage.simBaked, cage.simNormals, solveSdt, solveDown, null);
                System.Array.Clear(cage.simDisp, 0, cage.simDisp.Length);
                cageSim.FieldAndWrite(cage.simBaked, cage.simNormals, cage.simDisp, solvePdt, SimsAsList(), null);
            }
            catch (System.Exception e) { solveErr = e; }
            finally { solveDone.Set(); }
        }

        // =====================================================================
        // FOLLOW DIAGNOSTICS
        // Written for a second avatar where a bow on the top tears into shards and the
        // top clips with every follow option maxed. Raising the range and shifting the
        // zone forward changed nothing, so range is not what stops those verts binding.
        // At bind: per garment, WHY each part did or did not bind, its bone, and its
        // distance to the body. Every 2 s: how far the cloth moved, the worst stretched
        // edge (the shard) and how much bound cloth sits INSIDE the body (the clip).
        // A CSV per garment lands next to the configs for offline digging.
        // =====================================================================
        // Off by default: it cost ~5 ms a frame on three garments (measured). The plugin
        // turns it on together with the collider view (F10 / Show colliders).
        public static bool followDiag = false;
        // garments already reported this session; cleared when the follower set is rebuilt
        readonly HashSet<string> diagReported = new HashSet<string>();
        const int FD_FREE = 0, FD_BOUND = 1, FD_FILL = 2, FD_BONE = 3, FD_FAR = 4, FD_NOHIT = 5, FD_RANGE = 6;
        static readonly string[] FD_NAMES = { "free", "bound", "fill", "boneFilter", "outsideBox", "noSurface", "beyondRange" };

        // ---- FLESH FALLBACK for the bone filter --------------------------------
        // A garment rigged to the main leg bone ("Right Leg") while the region was picked from
        // the thigh flesh helpers ("Thigh_R", 107 mm away) shares no bone with the region by
        // name or by position, so the gate dropped all 1875 groups of the leg harness. Used
        // ONLY when the name gate would leave a garment unbound: a vert passes if one of its
        // bones really skins the painted flesh (>= 3% of that bone's skin weight lies in the
        // paint) AND the body vert right under it (within 8 cm) is painted (>= 20% of that
        // region's strongest paint). The back of a top and a sleeve still fail the second test.
        HashSet<string> shareBones;          // cached per follower generation
        float[] paintW;                      // per body vert: the strongest enabled region's paint
        Dictionary<long, List<int>> paintGrid;
        Vector3[] paintPts;
        BoneWeight[] bodyBW;                 // the body's skin weights, for the shared-bone test
        string[] bodyBoneNames;
        const float PAINT_CELL = 0.04f;

        void EnsureFleshData()
        {
            if (shareBones != null) return;
            shareBones = new HashSet<string>();
            if (smr == null || smr.sharedMesh == null || bakedVerts == null || cfg == null || cfg.regions == null) return;
            int n = bakedVerts.Length;
            paintW = new float[n];
            for (int ri = 0; ri < cfg.regions.Count; ri++)
            {
                SoftBodySuite.SuiteRegion reg = cfg.regions[ri];
                if (reg == null || !reg.enabled) continue;
                // relative to the region's OWN strongest paint: a region picked from bone
                // weights can top out at 0.19 (Nami's thighs did), and that is still "painted"
                float rmax = 0f;
                for (int k = 0; k < reg.weight.Count; k++) if (reg.weight[k] > rmax) rmax = reg.weight[k];
                if (rmax <= 1e-4f) continue;
                for (int k = 0; k < reg.vertIndex.Count; k++)
                {
                    int vi = reg.vertIndex[k];
                    float w = reg.weight[k] / rmax;
                    if (vi >= 0 && vi < n && w > paintW[vi]) paintW[vi] = w;
                }
            }
            BoneWeight[] bw = null;
            try { bw = smr.sharedMesh.boneWeights; } catch (System.Exception) { bw = null; }
            Transform[] sb = smr.bones;
            bodyBW = (bw != null && bw.Length == n) ? bw : null;
            if (sb != null)
            {
                bodyBoneNames = new string[sb.Length];
                for (int b = 0; b < sb.Length; b++) bodyBoneNames[b] = sb[b] != null ? sb[b].name : null;
            }
            if (bw != null && sb != null && bw.Length == n)
            {
                float[] tot = new float[sb.Length], inP = new float[sb.Length];
                for (int i = 0; i < n; i++)
                {
                    BoneWeight w = bw[i]; float pw = paintW[i];
                    if (w.boneIndex0 < sb.Length) { tot[w.boneIndex0] += w.weight0; inP[w.boneIndex0] += w.weight0 * pw; }
                    if (w.boneIndex1 < sb.Length) { tot[w.boneIndex1] += w.weight1; inP[w.boneIndex1] += w.weight1 * pw; }
                    if (w.boneIndex2 < sb.Length) { tot[w.boneIndex2] += w.weight2; inP[w.boneIndex2] += w.weight2 * pw; }
                    if (w.boneIndex3 < sb.Length) { tot[w.boneIndex3] += w.weight3; inP[w.boneIndex3] += w.weight3 * pw; }
                }
                // a bone counts if a real part of its skin lies in the paint (Nami's main leg
                // bones carry 6% of their weight in the thigh paint, the chest 6% in the breasts;
                // hair and arm bones carry none). The skin-under-the-vert test does the rest.
                for (int b = 0; b < sb.Length; b++)
                    if (sb[b] != null && tot[b] > 1f && (inP[b] >= 0.03f * tot[b] || inP[b] >= 10f)) shareBones.Add(sb[b].name);
                if (followDiag)
                {
                    // the 16 bones carrying the most painted weight, with their share
                    List<int> ord = new List<int>();
                    for (int b = 0; b < sb.Length; b++) if (inP[b] > 0f) ord.Add(b);
                    ord.Sort(delegate (int x, int y) { return inP[y].CompareTo(inP[x]); });
                    System.Text.StringBuilder sbd = new System.Text.StringBuilder();
                    for (int k = 0; k < ord.Count && k < 16; k++)
                    {
                        int b = ord[k];
                        sbd.Append(sb[b] != null ? sb[b].name : "?").Append(' ').Append(inP[b].ToString("0")).Append('/')
                           .Append(tot[b].ToString("0")).Append(" (").Append((inP[b] / Mathf.Max(tot[b], 1e-6f) * 100f).ToString("0")).Append("%), ");
                    }
                    int painted = 0; for (int i = 0; i < n; i++) if (paintW[i] > 0f) painted++;
                    Debug.Log("[FollowDiag] painted body verts " + painted + "; bones by painted weight: " + sbd.ToString());
                }
            }
            // clean current-pose body, hashed for "what skin is under this garment vert"
            paintPts = new Vector3[n];
            paintGrid = new Dictionary<long, List<int>>();
            for (int i = 0; i < n; i++)
            {
                paintPts[i] = bakedVerts[i] - (disp != null && i < disp.Length ? disp[i] : Vector3.zero) - UpDispAt(i);
                long key = HashCell(paintPts[i], PAINT_CELL, 0, 0, 0);
                List<int> l;
                if (!paintGrid.TryGetValue(key, out l)) { l = new List<int>(8); paintGrid[key] = l; }
                l.Add(i);
            }
            if (followDiag)
                Debug.Log("[FollowDiag] flesh bones (>= 3% of their weight in the paint): "
                    + string.Join(", ", new List<string>(shareBones).ToArray()));
        }

        int NearestBodyVert(Vector3 q, float maxD)
        {
            int best = -1; float bd = maxD * maxD;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        List<int> l;
                        if (!paintGrid.TryGetValue(HashCell(q, PAINT_CELL, dx, dy, dz), out l)) continue;
                        for (int j = 0; j < l.Count; j++)
                        {
                            float d2 = (paintPts[l[j]] - q).sqrMagnitude;
                            if (d2 < bd) { bd = d2; best = l[j]; }
                        }
                    }
            return best;
        }

        bool SharesBone(BoneWeight gwv, Transform[] gb, int bodyVert)
        {
            if (bodyBW == null || bodyBoneNames == null) return true;   // cannot tell: do not refuse
            BoneWeight bv = bodyBW[bodyVert];
            return BoneIn(gwv.boneIndex0, gwv.weight0, gb, bv) || BoneIn(gwv.boneIndex1, gwv.weight1, gb, bv)
                || BoneIn(gwv.boneIndex2, gwv.weight2, gb, bv) || BoneIn(gwv.boneIndex3, gwv.weight3, gb, bv);
        }

        bool BoneIn(int gIdx, float gW, Transform[] gb, BoneWeight bv)
        {
            if (gW <= 0.05f || gIdx < 0 || gIdx >= gb.Length || gb[gIdx] == null) return false;
            string nm = gb[gIdx].name;
            return Named(bv.boneIndex0, bv.weight0, nm) || Named(bv.boneIndex1, bv.weight1, nm)
                || Named(bv.boneIndex2, bv.weight2, nm) || Named(bv.boneIndex3, bv.weight3, nm);
        }

        bool Named(int bIdx, float w, string nm)
        {
            return w > 0.02f && bIdx >= 0 && bIdx < bodyBoneNames.Length && bodyBoneNames[bIdx] == nm;
        }

        int FleshBoneGate(SkinnedMeshRenderer r, Vector3[] pts, Matrix4x4 toSrc, int[] gOfGlobal, List<int> gFirst, bool[] boneOkG)
        {
            EnsureFleshData();
            lastFleshCand = 0;
            if (shareBones == null || shareBones.Count == 0 || paintGrid == null) return 0;
            Transform[] gb = r.bones;
            BoneWeight[] gw = r.sharedMesh != null ? r.sharedMesh.boneWeights : null;
            if (gb == null || gw == null || gw.Length != pts.Length) return 0;
            bool[] bOk = new bool[gb.Length];
            for (int b = 0; b < gb.Length; b++) bOk[b] = gb[b] != null && shareBones.Contains(gb[b].name);
            bool[] cand = new bool[boneOkG.Length];
            for (int i = 0; i < gw.Length; i++)
            {
                BoneWeight w = gw[i];
                if ((w.weight0 > 0.05f && w.boneIndex0 < bOk.Length && bOk[w.boneIndex0])
                    || (w.weight1 > 0.05f && w.boneIndex1 < bOk.Length && bOk[w.boneIndex1])
                    || (w.weight2 > 0.05f && w.boneIndex2 < bOk.Length && bOk[w.boneIndex2])
                    || (w.weight3 > 0.05f && w.boneIndex3 < bOk.Length && bOk[w.boneIndex3]))
                    cand[gOfGlobal[i]] = true;
            }
            for (int gg = 0; gg < boneOkG.Length; gg++) if (cand[gg] && !boneOkG[gg]) lastFleshCand++;
            int added = 0, refused = 0;
            for (int gg = 0; gg < boneOkG.Length; gg++)
            {
                if (boneOkG[gg] || !cand[gg]) continue;
                int nv = NearestBodyVert(toSrc.MultiplyPoint3x4(pts[gFirst[gg]]), 0.08f);
                if (nv < 0 || paintW[nv] < 0.2f) continue;
                // ...and the garment vert and that skin must share a bone. Nami's breasts hang
                // down to her waist, so the skin "under" the skirt's waistband was breast, and
                // the waistband (Hips / Spine) rode the breast jiggle: 92-111 mm. The leg
                // harness (Right Leg) and the thigh skin (Right Leg + Thigh_R) do share one.
                if (!SharesBone(gw[gFirst[gg]], gb, nv)) { refused++; continue; }
                boneOkG[gg] = true; added++;
            }
            if (followDiag && refused > 0)
                Debug.Log("[FollowDiag] '" + r.name + "': flesh fallback refused " + refused
                    + " groups whose painted skin underneath belongs to other bones");
            return added;
        }

        // World positions of this body's bones named in `names` (the regions' picked bones).
        List<Vector3> RegionBonePositions(HashSet<string> names)
        {
            List<Vector3> outP = new List<Vector3>();
            if (smr == null || smr.bones == null) return outP;
            Transform[] bb = smr.bones;
            for (int i = 0; i < bb.Length; i++)
                if (bb[i] != null && names.Contains(bb[i].name)) outP.Add(bb[i].position);
            return outP;
        }

        static bool OnRegionBone(Vector3 p, List<Vector3> regPos)
        {
            for (int i = 0; i < regPos.Count; i++)
                if ((regPos[i] - p).sqrMagnitude < 0.02f * 0.02f) return true;
            return false;
        }

        // nearest body distance for a vert the binder rejected, ignoring range and gate
        float DiagDist(Vector3 q)
        {
            if (bindGrid == null) return -1f;
            int bt; Vector3 cp, bar; bool via;
            if (!bindGrid.NearestGatedWithin(q, Vector3.zero, 0.35f, 0.10f, out bt, out cp, out bar, out via)) return -1f;
            return (q - cp).magnitude;
        }

        void FollowDiagBind(SkinnedMeshRenderer r, Vector3[] pts, int[] gOfGlobal, List<int> gFirst,
                            int[] whyG, float[] distG, Follower f, float range, bool whole,
                            Vector3 offSrc, int[] tris)
        { FollowDiagBind(r, pts, gOfGlobal, gFirst, whyG, distG, f, range, whole, offSrc, tris, true); }

        // `report` false = a re-bind: only set up the runtime arrays, no log line, no CSV
        void FollowDiagBind(SkinnedMeshRenderer r, Vector3[] pts, int[] gOfGlobal, List<int> gFirst,
                            int[] whyG, float[] distG, Follower f, float range, bool whole,
                            Vector3 offSrc, int[] tris, bool report)
        {
            if (!followDiag) return;
            try
            {
                int nG = gFirst.Count;
                if (!report && f == null) return;
                string[] bn = null; int[] vb = new int[pts.Length];
                for (int i = 0; i < vb.Length; i++) vb[i] = -1;
                if (r.sharedMesh != null && r.bones != null)
                {
                    Transform[] bs = r.bones;
                    bn = new string[bs.Length];
                    for (int b = 0; b < bs.Length; b++) bn[b] = bs[b] != null ? bs[b].name : "?";
                    BoneWeight[] bw = r.sharedMesh.boneWeights;
                    if (bw != null && bw.Length == pts.Length)
                        for (int i = 0; i < bw.Length; i++) vb[i] = bw[i].boneIndex0;   // weight0 is the largest
                }
                int[] cnt = new int[7]; int[] near3 = new int[7]; int[] near10 = new int[7];
                Dictionary<string, int>[] bones = new Dictionary<string, int>[7];
                for (int k = 0; k < 7; k++) bones[k] = new Dictionary<string, int>();
                for (int g = 0; g < nG; g++)
                {
                    int w = whyG[g];
                    cnt[w]++;
                    float d = distG[g];
                    if (d >= 0f && d <= 0.03f) near3[w]++;
                    if (d >= 0f && d <= 0.10f) near10[w]++;
                    int b = vb[gFirst[g]];
                    string key = (bn != null && b >= 0 && b < bn.Length) ? bn[b] : "?";
                    int c0; bones[w].TryGetValue(key, out c0); bones[w][key] = c0 + 1;
                }
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("[FollowDiag] BIND '").Append(r.name).Append("' ").Append(whole ? "whole-body" : "cage")
                  .Append(" range=").Append(range.ToString("0.000")).Append("m offset=").Append(offSrc.ToString("F3"))
                  .Append(" groups=").Append(nG).Append(f == null ? "  ** FAILED (<8 bound) **" : "");
                for (int k = 0; k < 7; k++)
                {
                    if (cnt[k] == 0) continue;
                    sb.Append("\n   ").Append(FD_NAMES[k].PadRight(12)).Append(cnt[k].ToString().PadLeft(6))
                      .Append("   within 3cm of body: ").Append(near3[k]).Append("   within 10cm: ").Append(near10[k])
                      .Append("   bones: ").Append(TopBones(bones[k], 5));
                }
                if (report) Debug.Log(sb.ToString());

                System.Globalization.CultureInfo ic = System.Globalization.CultureInfo.InvariantCulture;
                string path = System.IO.Path.Combine(Application.persistentDataPath,
                    "softfleshfield.followdiag_" + Safe(r.name) + ".csv");
                if (report)
                using (System.IO.StreamWriter w = new System.IO.StreamWriter(path, false))
                {
                    w.WriteLine("group,vert,state,bone,dist_m,x,y,z");
                    for (int g = 0; g < nG; g++)
                    {
                        int v = gFirst[g]; Vector3 p = pts[v]; int b = vb[v];
                        w.WriteLine(g + "," + v + "," + FD_NAMES[whyG[g]] + "," +
                            ((bn != null && b >= 0 && b < bn.Length) ? bn[b] : "?") + "," +
                            distG[g].ToString("0.0000", ic) + "," + p.x.ToString("0.0000", ic) + "," +
                            p.y.ToString("0.0000", ic) + "," + p.z.ToString("0.0000", ic));
                    }
                }

                if (f == null) return;
                f.dBoneNames = bn; f.dBone = vb;
                f.dState = new int[pts.Length];
                for (int i = 0; i < pts.Length; i++) f.dState[i] = whyG[gOfGlobal[i]];
                int G = f.gRep.Length;
                f.dCp = new Vector3[G]; f.dN = new Vector3[G]; f.dOk = new bool[G];
                HashSet<long> seen = new HashSet<long>();
                List<int> ea = new List<int>(), eb = new List<int>();
                if (tris != null)
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                        for (int e = 0; e < 3; e++)
                        {
                            int a = gOfGlobal[tris[t + e]], b = gOfGlobal[tris[t + (e + 1) % 3]];
                            if (a == b) continue;
                            long k2 = a < b ? (((long)a << 32) | (uint)b) : (((long)b << 32) | (uint)a);
                            if (!seen.Add(k2)) continue;
                            ea.Add(gFirst[a]); eb.Add(gFirst[b]);
                        }
                f.dEdgeA = ea.ToArray(); f.dEdgeB = eb.ToArray();
                f.dMinClear = float.MaxValue;
            }
            catch (System.Exception ex) { Debug.LogWarning("[FollowDiag] bind report failed: " + ex.Message); }
        }

        void FollowDiagFrame(Follower f, Matrix4x4 M)
        {
            if (f.skin == null || f.dEdgeA == null) return;
            Vector3[] v = f.verts, s0 = f.skin;
            for (int i = 0; i < v.Length; i++)
            {
                float d = (v[i] - s0[i]).magnitude;
                if (d > f.dMaxDisp) f.dMaxDisp = d;
                if (d > 0.02f) f.dOver20++;
            }
            for (int e = 0; e < f.dEdgeA.Length; e++)
            {
                int a = f.dEdgeA[e], b = f.dEdgeB[e];
                float rest = (s0[a] - s0[b]).magnitude;
                if (rest < 1e-5f) continue;
                float len = (v[a] - v[b]).magnitude;
                float ratio = len / rest;
                if (ratio > 2f) f.dStretch2++;
                // ratios explode on 0.1 mm seam edges; this is what a viewer actually sees
                float grow = len - rest;
                if (grow > 0.005f) f.dTear++;
                if (grow > f.dTearMax)
                {
                    f.dTearMax = grow; f.dTearA = a; f.dTearB = b;
                    f.dTearDispA = (v[a] - s0[a]).magnitude; f.dTearDispB = (v[b] - s0[b]).magnitude;
                }
                if (ratio > f.dMaxStretch)
                {
                    f.dMaxStretch = ratio; f.dWorstA = a; f.dWorstB = b;
                    f.dWorstDispA = (v[a] - s0[a]).magnitude; f.dWorstDispB = (v[b] - s0[b]).magnitude;
                }
            }
            if (f.dOk != null)
            {
                Matrix4x4 Mi = M.inverse;
                for (int g = 0; g < f.gRep.Length; g++)
                {
                    if (!f.dOk[g]) continue;
                    // Only cloth that RESTED outside its body triangle can clip through it.
                    // A sleeve bound to a chest triangle sits behind that plane by design,
                    // and counting it reported a hoodie "34 cm inside the body".
                    if (f.gOff[g].z < 0f) continue;
                    Vector3 pS = Mi.MultiplyPoint3x4(v[f.gRep[g]]);
                    float c = Vector3.Dot(pS - f.dCp[g], f.dN[g]);
                    if (c < 0f) f.dClip++;
                    if (c < f.dMinClear) f.dMinClear = c;
                }
            }
            f.dFrames++;
            f.dT += Time.deltaTime;
            if (f.dT < 2f) return;
            string wa = DiagDescribe(f, f.dWorstA), wb = DiagDescribe(f, f.dWorstB);
            Debug.Log("[FollowDiag] '" + f.smr.name + "' frames=" + f.dFrames
                + "  maxMove=" + (f.dMaxDisp * 1000f).ToString("0.0") + "mm  verts>20mm/frame=" + (f.dOver20 / f.dFrames)
                + "  worstStretch=x" + f.dMaxStretch.ToString("0.00") + "  edges>x2/frame=" + (f.dStretch2 / f.dFrames)
                + "  grown>5mm/frame=" + (f.dTear / f.dFrames) + " maxGrow=" + (f.dTearMax * 1000f).ToString("0.0") + "mm"
                + "  clipped/frame=" + (f.dClip / f.dFrames) + " of " + f.gRep.Length + " groups"
                + "  deepest=" + (f.dMinClear == float.MaxValue ? "-" : (f.dMinClear * 1000f).ToString("0.0") + "mm")
                + (f.kRep != null ? "  | skinGuard pushed/frame=" + (f.dGuardHits / f.dFrames) + " of " + f.kWatched
                    + " max=" + (f.dGuardMax * 1000f).ToString("0.0") + "mm tooDeep/frame=" + (f.dGuardDeep / f.dFrames) : "")
                + "\n   worst edge: [" + wa + " moved " + (f.dWorstDispA * 1000f).ToString("0.0") + "mm]  <->  ["
                + wb + " moved " + (f.dWorstDispB * 1000f).ToString("0.0") + "mm]"
                + "\n   most grown edge: [" + DiagDescribe(f, f.dTearA) + " moved " + (f.dTearDispA * 1000f).ToString("0.0")
                + "mm guard " + GuardAt(f, f.dTearA) + "]  <->  [" + DiagDescribe(f, f.dTearB) + " moved "
                + (f.dTearDispB * 1000f).ToString("0.0") + "mm guard " + GuardAt(f, f.dTearB) + "]");
            f.dT = 0f; f.dFrames = 0; f.dMaxDisp = 0f; f.dOver20 = 0; f.dMaxStretch = 0f; f.dStretch2 = 0;
            f.dClip = 0; f.dMinClear = float.MaxValue; f.dWorstA = -1; f.dWorstB = -1;
            f.dGuardHits = 0; f.dGuardDeep = 0; f.dGuardMax = 0f; f.dTear = 0; f.dTearMax = 0f;
        }

        // =====================================================================
        // SKIN GUARD
        // The follow above rides a SMOOTHED copy of the body: the region cage, with the
        // field's motion interpolated onto it and blurred ("Cloth smoothing"), and then the
        // cloth's own copy is blurred again AFTER the anti-clip floors, which pulls them
        // back in. Measured on Tops 1: ~70 groups a frame inside, up to 55 mm. On top of
        // that 42% of that top never binds at all (only-region-bones filter), so nothing
        // ever moves it out of the way of the flesh.
        // This pass runs LAST, on every garment vert near the skin, bound or not, against
        // the body that is actually rendered this frame. Each watched weld group keeps the
        // body RENDER triangle under it at bind plus its rest gap, and is pushed out along
        // that triangle's CURRENT normal whenever it comes nearer than
        // min(rest gap, Min cloth gap). Only where the downstream stage really moved the
        // body: elsewhere the rest fit still holds and the cloth is left exactly as before.
        // Rigid accessories (the bow) are skipped; they already move as one piece.
        // =====================================================================
        bool EnsureGuardGrid()
        {
            if (guardGrid != null && Time.time - guardGridT < 2f) return true;
            if (display == null || bakedVerts == null || bakedNormals == null || bakedNormals.Length != bakedVerts.Length)
                return false;
            int n = bakedVerts.Length;
            guardPts = new Vector3[n];
            for (int i = 0; i < n; i++)
                guardPts[i] = bakedVerts[i] - (disp != null && i < disp.Length ? disp[i] : Vector3.zero) - UpDispAt(i);
            guardTris = display.triangles;
            int nt = guardTris.Length / 3;
            Vector3[] tn = new Vector3[nt];
            for (int t = 0; t < nt; t++)
            {
                Vector3 sN = bakedNormals[guardTris[t * 3]] + bakedNormals[guardTris[t * 3 + 1]] + bakedNormals[guardTris[t * 3 + 2]];
                float m = sN.magnitude;
                tn[t] = m > 1e-9f ? sN / m : Vector3.up;
            }
            guardGrid = new RemeshCage.TriGrid(guardPts, guardTris, 0.02f);
            guardGrid.triNormals = tn;
            guardGridT = Time.time;
            return true;
        }

        static int UfFind(int[] par, int x)
        {
            while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; }
            return x;
        }

        void BuildSkinGuard(Follower f, string meshName, Matrix4x4 toSrc, Vector3[] pts, int[] gOfGlobal,
                            List<int> gFirst, List<Vector3> gNormSum, int[] localOf, int[] tris)
        {
            f.kRep = null;
            if (!EnsureGuardGrid()) return;
            float reach = Mathf.Clamp(settingsRef.cloth.follow.guardReach, 0.005f, 0.3f);
            float sunk = Mathf.Clamp(settingsRef.cloth.follow.guardSunk, 0f, 0.1f);
            int nSunk = 0, nWatch = 0;
            int nGlobal = gFirst.Count;
            // Glue weld groups joined by an edge shorter than GUARD_WELD into ONE guard group:
            // seams with verts 0.14 mm apart were bound to different skin triangles at a fold,
            // pushed in two directions, and that sliver stretched 85-160x.
            int[] par = new int[nGlobal];
            for (int i = 0; i < nGlobal; i++) par[i] = i;
            float w2 = GUARD_WELD * GUARD_WELD;
            for (int t = 0; t + 2 < tris.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int va = tris[t + e], vb = tris[t + (e + 1) % 3];
                    int ga = gOfGlobal[va], gb = gOfGlobal[vb];
                    if (ga == gb) continue;
                    if (toSrc.MultiplyVector(pts[va] - pts[vb]).sqrMagnitude > w2) continue;
                    int ra = UfFind(par, ga), rb = UfFind(par, gb);
                    if (ra != rb) par[ra] = rb;
                }
            int[] kOf = new int[nGlobal];
            for (int i = 0; i < nGlobal; i++) kOf[i] = -1;
            List<int> rp = new List<int>(), ta = new List<int>(), tb = new List<int>(), tc = new List<int>();
            List<Vector3> br = new List<Vector3>();
            List<float> sg = new List<float>(), rs = new List<float>();
            List<bool> fx = new List<bool>();
            for (int gg = 0; gg < nGlobal; gg++)
            {
                if (UfFind(par, gg) != gg) continue;   // bound once, by its root
                int lg = localOf[gg];
                if (lg >= 0 && f.gIsl != null && f.gIsl[lg] >= 0) continue;   // rigid piece: has its own motion
                Vector3 q = toSrc.MultiplyPoint3x4(pts[gFirst[gg]]);           // true position, no zone offset
                Vector3 ns = gNormSum[gg];
                Vector3 nq = ns.sqrMagnitude > 1e-6f ? toSrc.MultiplyVector(ns).normalized : Vector3.zero;
                // Every other group joins as PASSIVE: it never pushes, but it carries the soft
                // falloff of its pushed neighbours. Leaving it out tore the cloth exactly at the
                // edge of the watched area (measured: a leg-harness strap pushed 81 mm next to an
                // unwatched neighbour that moved 0).
                int bt; Vector3 cp, bar; bool via;
                bool watch = guardGrid.NearestGatedWithin(q, nq, 0.35f, reach, out bt, out cp, out bar, out via)
                             && (q - cp).sqrMagnitude <= reach * reach;
                int a = -1, b = -1, c = -1; float sgn = 1f, h0 = 0f; bool fix = false;
                if (watch)
                {
                    a = guardTris[bt]; b = guardTris[bt + 1]; c = guardTris[bt + 2];
                    Vector3 nr = Vector3.Cross(guardPts[b] - guardPts[a], guardPts[c] - guardPts[a]);
                    if (nr.sqrMagnitude < 1e-14f) watch = false;
                    else
                    {
                        sgn = Vector3.Dot(nr, bakedNormals[a] + bakedNormals[b] + bakedNormals[c]) < 0f ? -1f : 1f;
                        nr = nr.normalized * sgn;
                        h0 = Vector3.Dot(q - cp, nr);
                        // Rests INSIDE this skin. Deeper than 1 cm it is usually not a layer over
                        // it at all (a sleeve over the chest), unless the user says the model
                        // itself sinks the cloth in that far: then it is lifted just above it.
                        fix = sunk > 0f && h0 < 0f && h0 >= -sunk;
                        if (h0 < -0.01f && !fix) watch = false;
                    }
                }
                if (!watch) { a = -1; b = -1; c = -1; fix = false; h0 = 0f; }
                if (fix) nSunk++;
                if (watch) nWatch++;
                kOf[gg] = rp.Count;
                rp.Add(gFirst[gg]); ta.Add(a); tb.Add(b); tc.Add(c); br.Add(bar); sg.Add(sgn);
                rs.Add(fix ? GUARD_LIFT : h0); fx.Add(fix);
            }
            for (int gg = 0; gg < nGlobal; gg++) kOf[gg] = kOf[UfFind(par, gg)];
            int K = rp.Count;
            if (K == 0 || nWatch == 0) return;
            int[] cnt = new int[K];
            for (int i = 0; i < pts.Length; i++) { int k = kOf[gOfGlobal[i]]; if (k >= 0) cnt[k]++; }
            f.kMemStart = new int[K + 1];
            for (int k = 0; k < K; k++) f.kMemStart[k + 1] = f.kMemStart[k] + cnt[k];
            f.kMem = new int[f.kMemStart[K]];
            int[] cur = new int[K];
            for (int i = 0; i < pts.Length; i++) { int k = kOf[gOfGlobal[i]]; if (k >= 0) f.kMem[f.kMemStart[k] + cur[k]++] = i; }
            List<int>[] adj = new List<int>[K];
            for (int k = 0; k < K; k++) adj[k] = new List<int>(6);
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int ka = kOf[gOfGlobal[tris[t]]], kb = kOf[gOfGlobal[tris[t + 1]]], kc = kOf[gOfGlobal[tris[t + 2]]];
                if (ka >= 0 && kb >= 0 && ka != kb && !adj[ka].Contains(kb)) { adj[ka].Add(kb); adj[kb].Add(ka); }
                if (kb >= 0 && kc >= 0 && kb != kc && !adj[kb].Contains(kc)) { adj[kb].Add(kc); adj[kc].Add(kb); }
                if (ka >= 0 && kc >= 0 && ka != kc && !adj[ka].Contains(kc)) { adj[ka].Add(kc); adj[kc].Add(ka); }
            }
            f.kAdj = new int[K][];
            f.kAdjLen = new float[K][];
            for (int k = 0; k < K; k++)
            {
                f.kAdj[k] = adj[k].ToArray();
                f.kAdjLen[k] = new float[f.kAdj[k].Length];
                for (int j = 0; j < f.kAdj[k].Length; j++)   // SOURCE space: the units pushes are measured in
                    f.kAdjLen[k][j] = toSrc.MultiplyVector(pts[rp[k]] - pts[rp[f.kAdj[k][j]]]).magnitude;
            }
            f.kHeap = new MinHeap(256);
            f.kTA = ta.ToArray(); f.kTB = tb.ToArray(); f.kTC = tc.ToArray();
            f.kBar = br.ToArray(); f.kSign = sg.ToArray(); f.kRest = rs.ToArray(); f.kFix = fx.ToArray();
            f.kCorr = new Vector3[K]; f.kCorr2 = new Vector3[K]; f.kNl = new Vector3[K]; f.kNs = new Vector3[K]; f.kNeed = new float[K];
            f.kSeed = new int[K];
            f.kWatched = nWatch;
            f.kRep = rp.ToArray();
            Debug.Log("[FollowDiag] '" + meshName + "' skin guard watches " + nWatch + " groups (+" + (K - nWatch) + " that only carry its falloff) of " + nGlobal
                + " (within " + (reach * 1000f).ToString("0") + "mm of the skin, seams under "
                + (GUARD_WELD * 1000f).ToString("0") + "mm glued"
                + (sunk > 0f ? ", " + nSunk + " sunk up to " + (sunk * 1000f).ToString("0") + "mm will be lifted" : "") + ")");
        }

        // this group's skin normal on the body as rendered, pointing OUT (zero if degenerate)
        Vector3 GuardNormal(Follower f, int k, Vector3[] fin)
        {
            int a = f.kTA[k], b = f.kTB[k], c = f.kTC[k];
            Vector3 A = fin[a];
            Vector3 nr = Vector3.Cross(fin[b] - A, fin[c] - A);
            float m = nr.magnitude;
            // The skinned normal (no jiggle) is the safety net. A render triangle a few mm
            // wide can fold or flip under the field's displacement, and a flipped normal reads
            // the cloth as deep INSIDE the skin and shoves it the wrong way: the guard was
            // pushing leg-harness straps by its full 120 mm limit while the flesh moved < 80.
            Vector3 sk = bakedNormals[a] + bakedNormals[b] + bakedNormals[c];
            float sm = sk.magnitude;
            if (sm > 1e-9f) sk /= sm;
            if (m < 1e-12f) return sm > 1e-9f ? sk : Vector3.zero;
            nr *= f.kSign[k] / m;
            if (sm > 1e-9f && Vector3.Dot(nr, sk) < 0.5f) return sk;   // tilted past 60 degrees: trust the skin
            return nr;
        }

        void ApplySkinGuard(Follower f, Matrix4x4 M, Vector3[] fin)
        {
            int K = f.kRep.Length;
            if (fin.Length != bakedVerts.Length) return;
            ClothFollow cf = settingsRef.cloth.follow;
            float gapMax = Mathf.Clamp(cf.guardGap, 0f, 0.05f);
            float slope = 1f / Mathf.Max(cf.guardSoft, 0.05f);
            float str = Mathf.Clamp01(cf.guardStrength);
            if (str <= 0f) return;
            Matrix4x4 Mi = M.inverse;
            Vector3[] bkv = bakedVerts;
            // every group is independent here and writes only its own slots: all cores.
            // "Too deep" is marked -1 and counted afterwards, single-threaded.
            SoftBodySuite.Field.ParallelLoop.For(K, 512, delegate (int lo, int hi)
            {
                for (int k = lo; k < hi; k++)
                {
                    f.kNeed[k] = 0f; f.kNs[k] = Vector3.zero; f.kSeed[k] = k;
                    int a = f.kTA[k], b = f.kTB[k], c = f.kTC[k];
                    if (a < 0) continue;   // passive: carries falloff only
                    Vector3 A = fin[a], B = fin[b], C = fin[c];
                    // the downstream stage left this patch alone: the rest fit still holds
                    // (except cloth the model itself sank into the skin: that is fixed at rest too)
                    if (!f.kFix[k] && (A - bkv[a]).sqrMagnitude < 1e-6f && (B - bkv[b]).sqrMagnitude < 1e-6f
                        && (C - bkv[c]).sqrMagnitude < 1e-6f) continue;
                    Vector3 nr = GuardNormal(f, k, fin);
                    if (nr == Vector3.zero) continue;
                    f.kNs[k] = nr;
                    Vector3 bw = f.kBar[k];
                    Vector3 cp = A * bw.x + B * bw.y + C * bw.z;
                    Vector3 p = Mi.MultiplyPoint3x4(f.verts[f.kRep[k]]);
                    float gap = Vector3.Dot(p - cp, nr);
                    float target = Mathf.Min(f.kRest[k], gapMax);
                    if (gap >= target) continue;
                    float push = target - gap;
                    f.kNeed[k] = push > GUARD_DEEP ? -1f : push;
                }
            });
            int hits = 0;
            for (int k = 0; k < K; k++)
            {
                float nk = f.kNeed[k];
                if (nk == 0f) continue;
                if (nk < 0f) { f.kNeed[k] = 0f; if (followDiag) f.dGuardDeep++; continue; }
                hits++;
                if (followDiag && nk > f.dGuardMax) f.dGuardMax = nk;
            }
            if (followDiag) f.dGuardHits += hits;
            if (followDiag)
            {
                if (f.dGuardV == null || f.dGuardV.Length != f.verts.Length) f.dGuardV = new float[f.verts.Length];
                else System.Array.Clear(f.dGuardV, 0, f.dGuardV.Length);
            }
            if (hits == 0) return;
            // CONE SPREAD on the push SIZE: every pushed group lifts its neighbours by its own
            // push minus slope x the cloth distance between them (largest-first sweep). Each
            // group then moves along ITS OWN skin normal: inheriting the pusher's direction
            // carried sideways shoves across the cleavage and round the back.
            MinHeap hp = f.kHeap;
            hp.Count = 0;
            for (int k = 0; k < K; k++) if (f.kNeed[k] > 0f) hp.Push(-f.kNeed[k], k);
            while (hp.Count > 0)
            {
                float key; int k;
                hp.Pop(out key, out k);
                float nk = -key;
                if (nk + 1e-7f < f.kNeed[k]) continue;   // superseded by a bigger push
                int[] nb = f.kAdj[k]; float[] ln = f.kAdjLen[k];
                for (int j = 0; j < nb.Length; j++)
                {
                    float cand = nk - slope * ln[j];
                    int q = nb[j];
                    if (cand <= f.kNeed[q] + 1e-6f) continue;
                    f.kNeed[q] = cand;
                    f.kSeed[q] = f.kSeed[k];
                    hp.Push(-cand, q);
                }
            }
            for (int k = 0; k < K; k++)
            {
                Vector3 v = Vector3.zero;
                if (f.kNeed[k] > 0f)
                {
                    // lifted by a neighbour: its own skin normal if it has a skin triangle,
                    // else (passive cloth off the skin) the direction of the push it carries
                    if (f.kNs[k] == Vector3.zero)
                        f.kNs[k] = f.kTA[k] >= 0 ? GuardNormal(f, k, fin) : f.kNs[f.kSeed[k]];
                    v = M.MultiplyVector(f.kNs[k] * f.kNeed[k]);
                }
                f.kCorr[k] = v; f.kNl[k] = v;   // kNl keeps the floor for after the rounding
            }
            // round the cone's tip off, then lift every group back to its floor: the flesh
            // makes a soft bulge in the cloth, never a tent or a hole
            for (int pass = 0; pass < GUARD_SPREAD; pass++)
            {
                Vector3[] src = f.kCorr, dst = f.kCorr2;
                SoftBodySuite.Field.ParallelLoop.For(K, 1024, delegate (int lo, int hi)
                {
                    for (int k = lo; k < hi; k++)
                    {
                        int[] nb = f.kAdj[k];
                        if (nb.Length == 0 || f.kNeed[k] <= 0f) { dst[k] = src[k]; continue; }
                        Vector3 avg = Vector3.zero;
                        for (int j = 0; j < nb.Length; j++) avg += src[nb[j]];
                        dst[k] = Vector3.Lerp(src[k], avg / nb.Length, 0.5f);
                    }
                });
                f.kCorr = dst; f.kCorr2 = src;
            }
            for (int k = 0; k < K; k++)
            {
                if (f.kNeed[k] <= 0f) continue;
                Vector3 fl = f.kNl[k];
                float flm = fl.magnitude;
                if (flm > 1e-9f)
                {
                    Vector3 dir = fl / flm;
                    float have = Vector3.Dot(f.kCorr[k], dir);
                    if (have < flm) f.kCorr[k] += dir * (flm - have);
                }
                Vector3 d = f.kCorr[k] * str;
                int e = f.kMemStart[k + 1];
                for (int mm = f.kMemStart[k]; mm < e; mm++) f.verts[f.kMem[mm]] += d;
                if (followDiag && f.dGuardV != null)
                    for (int mm = f.kMemStart[k]; mm < e; mm++) f.dGuardV[f.kMem[mm]] = d.magnitude;
            }
        }

        // ---- floating islands -------------------------------------------------
        // Which groups sit further than floatDist from the real SKIN (the whole body render
        // mesh, clean pose), not from the bound triangle. The first version measured the
        // bound triangle, and in cage mode that is the painted region only - so a top
        // lying right on the ribcage read as "100% off-body" and was frozen solid along
        // with the bow. Nearest body VERT, via a hash grid of cell = floatDist: within
        // half an edge of the true surface distance, which is plenty against 20 mm.
        bool[] OffSkinFlags(Follower f, Matrix4x4 toSrc, Vector3[] pts)
        {
            int G = f.gRep.Length;
            bool[] far = new bool[G];
            if (bakedVerts == null || settingsRef == null) return far;
            float cell = Mathf.Max(0.004f, settingsRef.cloth.follow.floatDist);
            float c2 = cell * cell;
            Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
            Vector3[] body = new Vector3[bakedVerts.Length];
            for (int i = 0; i < bakedVerts.Length; i++)
            {
                body[i] = bakedVerts[i] - (disp != null && i < disp.Length ? disp[i] : Vector3.zero) - UpDispAt(i);
                long k = HashCell(body[i], cell, 0, 0, 0);
                List<int> l;
                if (!grid.TryGetValue(k, out l)) { l = new List<int>(8); grid[k] = l; }
                l.Add(i);
            }
            for (int g = 0; g < G; g++)
            {
                Vector3 q = toSrc.MultiplyPoint3x4(pts[f.gRep[g]]);
                bool near = false;
                for (int dx = -1; dx <= 1 && !near; dx++)
                    for (int dy = -1; dy <= 1 && !near; dy++)
                        for (int dz = -1; dz <= 1 && !near; dz++)
                        {
                            List<int> l;
                            if (!grid.TryGetValue(HashCell(q, cell, dx, dy, dz), out l)) continue;
                            for (int j = 0; j < l.Count; j++)
                                if ((body[l[j]] - q).sqrMagnitude <= c2) { near = true; break; }
                        }
                far[g] = !near;
            }
            return far;
        }

        static long HashCell(Vector3 p, float cell, int dx, int dy, int dz)
        {
            long x = Mathf.FloorToInt(p.x / cell) + dx, y = Mathf.FloorToInt(p.y / cell) + dy, z = Mathf.FloorToInt(p.z / cell) + dz;
            return ((x & 0x1FFFFF) << 42) | ((y & 0x1FFFFF) << 21) | (z & 0x1FFFFF);
        }

        void FindFloatingIslands(Follower f, string meshName, bool[] offSkin)
        {
            f.gIsl = null; f.islN = 0; f.islQ = null; f.islMem = null;
            if (settingsRef == null) return;
            float floatD = Mathf.Max(0.001f, settingsRef.cloth.follow.floatDist);
            int G = f.gRep.Length;
            int[] isl = new int[G];
            for (int g = 0; g < G; g++) isl[g] = -1;
            List<int> stack = new List<int>(), members = new List<int>();
            int id = 0; int rigidGroups = 0;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int[] tmp = new int[G];
            for (int g = 0; g < G; g++) tmp[g] = -2;
            for (int s0 = 0; s0 < G; s0++)
            {
                if (tmp[s0] != -2) continue;
                members.Clear(); stack.Clear(); stack.Add(s0); tmp[s0] = -3;
                while (stack.Count > 0)
                {
                    int a = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1);
                    members.Add(a);
                    int[] nb = f.gAdj[a];
                    for (int j = 0; j < nb.Length; j++) if (tmp[nb[j]] == -2) { tmp[nb[j]] = -3; stack.Add(nb[j]); }
                }
                int far = 0, real = 0;
                for (int j = 0; j < members.Count; j++)
                {
                    int g = members[j];
                    if (f.gIsFill[g]) continue;
                    real++;
                    if (offSkin != null ? offSkin[g] : f.gOffRest[g].magnitude > floatD) far++;
                }
                // floating = most of the piece hangs off the body, and big enough to fit
                bool floating = real >= 8 && far * 10 >= real * 6;
                for (int j = 0; j < members.Count; j++) tmp[members[j]] = -1;
                if (!floating) continue;
                for (int j = 0; j < members.Count; j++) isl[members[j]] = id;
                rigidGroups += members.Count;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(members.Count).Append(" groups (").Append(far * 100 / Mathf.Max(1, real)).Append("% off-body)");
                id++;
            }
            if (id == 0) return;
            f.gIsl = isl; f.islN = id;
            // each island's groups in ascending order: the fit used to scan every group of
            // the garment once per island (3 sweeps each); same order = same sums
            int[] cnt = new int[id];
            for (int g = 0; g < G; g++) if (isl[g] >= 0) cnt[isl[g]]++;
            f.islMem = new int[id][];
            for (int k = 0; k < id; k++) f.islMem[k] = new int[cnt[k]];
            for (int k = 0; k < id; k++) cnt[k] = 0;
            for (int g = 0; g < G; g++) if (isl[g] >= 0) f.islMem[isl[g]][cnt[isl[g]]++] = g;
            f.islQ = new Quaternion[id];
            for (int i = 0; i < id; i++) f.islQ[i] = Quaternion.identity;
            Debug.Log("[FollowDiag] '" + meshName + "' " + id + " floating piece(s) will move rigidly (beyond "
                + (floatD * 1000f).ToString("0") + "mm): " + sb.ToString());
        }

        // Replace each floating island's per-group motion with ONE best-fit rigid motion:
        // the rotation + shift that best carries the island's cloth from where skinning put
        // it this frame to where the per-point follow wanted it. Horn's quaternion method,
        // because a flat ribbon makes the 3x3 covariance rank-deficient and an SVD-free
        // polar iteration would blow up on it; the 4x4 dominant eigenvector does not.
        Vector3[] islP, islQv; float[] islW;
        void RigidIslands(Follower f)
        {
            float amt = settingsRef != null ? Mathf.Clamp01(settingsRef.cloth.follow.rigidFloat) : 1f;
            if (amt <= 0f) return;
            int G = f.gRep.Length;
            if (f.islMem == null || f.islMem.Length != f.islN) return;
            // islands own disjoint groups (and their own islQ slot): fit them side by side
            SoftBodySuite.Field.ParallelLoop.For(f.islN, 2, delegate (int klo, int khi)
            {
                for (int k = klo; k < khi; k++) RigidIsland(f, k, amt);
            });
        }

        void RigidIsland(Follower f, int k, float amt)
        {
            int[] mem = f.islMem[k];
            {
                // centroids, weighted by the per-point bind confidence (fill groups have none)
                Vector3 pc = Vector3.zero, qc = Vector3.zero; float ws = 0f;
                for (int mi = 0; mi < mem.Length; mi++)
                {
                    int g = mem[mi];
                    if (f.gIsFill[g]) continue;
                    float w = Mathf.Max(0.05f, f.gFall[g]);
                    Vector3 p = f.verts[f.gRep[g]];
                    pc += p * w; qc += (p + f.gDisp[g]) * w; ws += w;
                }
                if (ws <= 1e-6f) return;
                pc /= ws; qc /= ws;
                double sxx = 0, sxy = 0, sxz = 0, syx = 0, syy = 0, syz = 0, szx = 0, szy = 0, szz = 0;
                for (int mi = 0; mi < mem.Length; mi++)
                {
                    int g = mem[mi];
                    if (f.gIsFill[g]) continue;
                    float w = Mathf.Max(0.05f, f.gFall[g]);
                    Vector3 p = f.verts[f.gRep[g]] - pc;
                    Vector3 q = f.verts[f.gRep[g]] + f.gDisp[g] - qc;
                    sxx += w * p.x * q.x; sxy += w * p.x * q.y; sxz += w * p.x * q.z;
                    syx += w * p.y * q.x; syy += w * p.y * q.y; syz += w * p.y * q.z;
                    szx += w * p.z * q.x; szy += w * p.z * q.y; szz += w * p.z * q.z;
                }
                double[,] N = new double[4, 4];
                N[0, 0] = sxx + syy + szz; N[0, 1] = syz - szy;      N[0, 2] = szx - sxz;       N[0, 3] = sxy - syx;
                N[1, 0] = N[0, 1];         N[1, 1] = sxx - syy - szz; N[1, 2] = sxy + syx;      N[1, 3] = szx + sxz;
                N[2, 0] = N[0, 2];         N[2, 1] = N[1, 2];         N[2, 2] = -sxx + syy - szz; N[2, 3] = syz + szy;
                N[3, 0] = N[0, 3];         N[3, 1] = N[1, 3];         N[3, 2] = N[2, 3];        N[3, 3] = -sxx - syy + szz;
                double shift = 0;
                for (int a = 0; a < 4; a++) for (int b = 0; b < 4; b++) shift += System.Math.Abs(N[a, b]);
                Quaternion q0 = f.islQ[k];
                double qw = q0.w, qx = q0.x, qy = q0.y, qz = q0.z;
                for (int it = 0; it < 24; it++)
                {
                    double nw = (N[0, 0] + shift) * qw + N[0, 1] * qx + N[0, 2] * qy + N[0, 3] * qz;
                    double nx = N[1, 0] * qw + (N[1, 1] + shift) * qx + N[1, 2] * qy + N[1, 3] * qz;
                    double ny = N[2, 0] * qw + N[2, 1] * qx + (N[2, 2] + shift) * qy + N[2, 3] * qz;
                    double nz = N[3, 0] * qw + N[3, 1] * qx + N[3, 2] * qy + (N[3, 3] + shift) * qz;
                    double len = System.Math.Sqrt(nw * nw + nx * nx + ny * ny + nz * nz);
                    if (len < 1e-12) break;
                    qw = nw / len; qx = nx / len; qy = ny / len; qz = nz / len;
                }
                Quaternion R = new Quaternion((float)qx, (float)qy, (float)qz, (float)qw);
                if (float.IsNaN(R.x)) R = Quaternion.identity;
                f.islQ[k] = R;
                for (int mi = 0; mi < mem.Length; mi++)
                {
                    int g = mem[mi];
                    Vector3 p = f.verts[f.gRep[g]];
                    Vector3 rigid = R * (p - pc) + qc - p;
                    f.gDisp[g] = Vector3.Lerp(f.gDisp[g], rigid, amt);
                }
            }
        }

        static string GuardAt(Follower f, int v)
        {
            if (f.dGuardV == null || v < 0 || v >= f.dGuardV.Length) return "-";
            return (f.dGuardV[v] * 1000f).ToString("0.0") + "mm";
        }

        static string DiagDescribe(Follower f, int v)
        {
            if (v < 0 || f.dState == null || v >= f.dState.Length) return "-";
            int b = f.dBone != null ? f.dBone[v] : -1;
            string bone = (f.dBoneNames != null && b >= 0 && b < f.dBoneNames.Length) ? f.dBoneNames[b] : "?";
            return "v" + v + " " + FD_NAMES[f.dState[v]] + " bone=" + bone;
        }

        static string TopBones(Dictionary<string, int> d, int n)
        {
            List<KeyValuePair<string, int>> l = new List<KeyValuePair<string, int>>(d);
            l.Sort((x, y) => y.Value.CompareTo(x.Value));
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < l.Count && i < n; i++)
            { if (i > 0) sb.Append(", "); sb.Append(l[i].Key).Append('(').Append(l[i].Value).Append(')'); }
            return sb.ToString();
        }

        static string Safe(string s)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        void RemoveFollower(int i) { RemoveFollower(i, true); }

        void RemoveFollower(int i, bool restoreVisibility)
        {
            Follower f = followers[i];
            // never un-hide a mesh some other proxy is currently driving/hiding
            if (f.smr != null && restoreVisibility && !OtherStudioProxyAlive(f.smr))
                f.smr.forceRenderingOff = false;
            if (f.go != null) { f.go.SetActive(false); Object.Destroy(f.go); }
            if (f.baked != null) Object.Destroy(f.baked);
            if (f.display != null) Object.Destroy(f.display);
            followers.RemoveAt(i);
        }

        public void DetachFollowers()
        {
            for (int i = followers.Count - 1; i >= 0; i--) RemoveFollower(i);
            folQueue.Clear(); folFailed.Clear(); folDead.Clear(); folDeadT = 0f;
            diagReported.Clear();   // a fresh bind (new range / offset) gets a fresh report
            folRetryT = 0f; folRangeT = 0f;
            folCage = null; folGenActive = false;
            bindGrid = null; bindPts = null; bindTris = null;
            shareBones = null; paintW = null; paintGrid = null; paintPts = null; bodyBW = null; bodyBoneNames = null;   // paint may have changed
            guardGrid = null; guardPts = null; guardTris = null; guardFin = null;
            // drop the timing mirrors: a rebuilt cage has a different vert count, and a
            // held frame would otherwise feed followers a stale wrong-length field
            cageOut = null; cageHeld = null; cagePrev = null; cageHeldValid = false;
        }

        public void Detach()
        {
            DetachFollowers();
            if (wobbleMR != null) wobbleMR.enabled = true;   // hand rendering back to Wobble
            wobbleSrc = null; wobbleMR = null;
            // keep the original hidden if ANY other plugin's proxy still drives this mesh
            if (smr != null)
                smr.forceRenderingOff = ProxyAlive("_WobbleProxy") || ProxyAlive("_SoftBodyProxy") || ProxyAlive("_SquishProxy");
            if (go != null) { go.SetActive(false); Object.Destroy(go); }        // hide NOW (Destroy is end-of-frame)
            if (overlayGO != null) { overlayGO.SetActive(false); Object.Destroy(overlayGO); }
            if (baked != null) Object.Destroy(baked);
            if (display != null) Object.Destroy(display);
            if (colBakeScratch != null) Object.Destroy(colBakeScratch);
            go = null; overlayGO = null; baked = null; display = null; smr = null; colBakeScratch = null;
            sims.Clear(); colMeshSmr.Clear(); colMeshCloud.Clear();
            framedOnce = false; finalVerts = null; downExtra = null; cageExtra = null;
            cage = null; cageSim = null; cageSrc = null; DestroyCageViz();
            cageToken++; cageBuilding = null;
            if (cageMesh != null) { Object.Destroy(cageMesh); cageMesh = null; }
            for (int i = 0; i < dbgPool.Count; i++) if (dbgPool[i] != null) Object.Destroy(dbgPool[i].gameObject);
            dbgPool.Clear();
        }

        bool ProxyAlive(string suffix)
        {
            if (smr == null) return false;
            Transform t = smr.transform.Find(smr.name + suffix);
            return t != null && t.gameObject.activeSelf;   // pending-destroy corpses are inactive
        }

        // upstream source, best-first: SoftBody's output (order 20500, runs before us at
        // 20600), else Wobble's. Squish Studio is DOWNSTREAM (20700, final) — never a source.
        // The suite hands verts over directly, so looking for an upstream proxy
        // GameObject is not just unnecessary — it is wrong. A stage that is switched
        // off keeps its last mesh, and reading it would freeze the body.
        public static bool directChain;

        Transform FindChainSource()
        {
            if (directChain) return null;
            if (smr == null) return null;
            Transform t = smr.transform.Find(smr.name + "_SoftBodyProxy");
            if (t != null && t.gameObject.activeSelf) return t;
            t = smr.transform.Find(smr.name + "_WobbleProxy");
            return (t != null && t.gameObject.activeSelf) ? t : null;
        }

        void FindWobbleProxy()
        {
            wobbleSrc = null; wobbleMR = null;
            Transform t = FindChainSource();
            if (t == null) return;
            MeshFilter mfW = t.GetComponent<MeshFilter>();
            if (mfW == null || mfW.sharedMesh == null || mfW.sharedMesh.vertexCount != bakedVerts.Length) return;
            wobbleSrc = mfW;
            wobbleMR = t.GetComponent<MeshRenderer>();
            if (wobbleMR != null) wobbleMR.enabled = false;   // we render the final result
            // Squish is downstream-only now: this plugin ALWAYS runs its own collision
            for (int i = 0; i < sims.Count; i++) sims[i].skipCollision = false;
            Debug.Log("[Jello] chaining (source: " + t.name + ") on '" + smr.name + "'");
        }

        // ---------- troubleshooting: F10 toggles collider draw + perf log ----------
        public static bool debugDraw;
        List<Transform> dbgPool = new List<Transform>();
        static Material dbgMat;
        float msBake, msSim, dbgLogT;
        bool dispBoundsSet;
        public static bool halfRate;                 // set by the plugin from settings
        public static bool halfRateLerp;
        bool frameFlip; Vector3[] heldDisp, heldPrev; bool heldValid;
        System.Diagnostics.Stopwatch swDbg = new System.Diagnostics.Stopwatch();
        // sub-pass timer inside jel_run, so the probe can say WHICH part of the 5 ms it is
        readonly System.Diagnostics.Stopwatch swJel = new System.Diagnostics.Stopwatch();
        float JelLap() { float ms = (float)swJel.Elapsed.TotalMilliseconds; swJel.Reset(); swJel.Start(); return ms; }

        public void Frame(float dt, int substeps, Vector3 worldDown, bool simEnabled)
        {
            if (!Alive) { return; }
            PollCageBuild();
            swDbg.Restart();

            // upstream appearing/disappearing: cheap re-check 1x/second for UPGRADES, but
            // re-chain INSTANTLY while unchained or the source died (fake-null after a
            // rebind destroys it) — waiting a whole second flashed two bodies per tweak

            // handed to us directly by the stage before this one
            if (upVerts != null && upVerts.Length == bakedVerts.Length)
            {
                if (wobbleSrc != null) { wobbleSrc = null; wobbleMR = null; }
                System.Array.Copy(upVerts, bakedVerts, bakedVerts.Length);
                if (upNormals != null && upNormals.Length == bakedNormals.Length)
                    System.Array.Copy(upNormals, bakedNormals, bakedNormals.Length);
                RunSim(dt, substeps, worldDown, simEnabled);
                return;
            }
            if (wobbleSrc == null) FindWobbleProxy();
            if (++chainCheck >= 60)
            {
                chainCheck = 0;
                Transform best = FindChainSource();
                if (best == null) { if (wobbleSrc != null) { wobbleSrc = null; wobbleMR = null; } }
                else if (wobbleSrc == null || wobbleSrc.transform != best) FindWobbleProxy();
                smr.forceRenderingOff = true;   // upstream detach re-shows the original; we own the final image
            }

            if (wobbleSrc != null)
            {
                if (wobbleMR != null && wobbleMR.enabled) wobbleMR.enabled = false;
                wobbleSrc.sharedMesh.GetVertices(scratch);   // base = the previous deformer's output
            }
            else smr.BakeMesh(baked);

            if (wobbleSrc != null)
            {
                if (scratch.Count != bakedVerts.Length) { Detach(); return; }
                scratch.CopyTo(bakedVerts);
                wobbleSrc.sharedMesh.GetNormals(scratch);
                if (scratch.Count == bakedVerts.Length) scratch.CopyTo(bakedNormals);
                RunSim(dt, substeps, worldDown, simEnabled);
                return;
            }

            baked.GetVertices(scratch);
            if (scratch.Count != bakedVerts.Length)
            {
                // the renderer's mesh was swapped out from under us (outfit systems etc.)
                Debug.LogWarning("[Jello] '" + smr.name + "' vertex count changed ("
                    + bakedVerts.Length + " -> " + scratch.Count + ") — detaching, will rebind");
                Detach();
                return;
            }
            scratch.CopyTo(bakedVerts);
            baked.GetNormals(scratch);
            if (scratch.Count == bakedVerts.Length)
                scratch.CopyTo(bakedNormals);

            RunSim(dt, substeps, worldDown, simEnabled);
        }

        void RunSim(float dt, int substeps, Vector3 worldDown, bool simEnabled)
        {
            msBake = Mathf.Lerp(msBake, (float)swDbg.Elapsed.TotalMilliseconds, 0.08f);
            SoftBodySuite.Field.FieldDiag.Mark("jel_bake", (float)swDbg.Elapsed.TotalMilliseconds);
            swDbg.Restart();
            swJel.Reset(); swJel.Start();
            System.Array.Clear(disp, 0, disp.Length);

            // Stage switched off: it adds nothing, but the cloth pass still hangs off this
            // stage, so the cage surface has to keep tracking the body. Without this the
            // garments stay bound to a frozen shape and the body pokes straight through.
            if (!simEnabled && cage != null && cageSim != null)
            {
                cage.InterpBaked(bakedVerts, bakedNormals);
                // The clothes read this field every frame and SKIP updating entirely when it
                // does not exist. It was only ever created by the sim, so with this stage off
                // (and every toggle building a fresh proxy) it stayed null: the garments were
                // bound once, their own renderers hidden, and the bind-time snapshot was left
                // on screen forever. Make sure it exists, and hold it at zero.
                int nc = cage.SimVertCount;
                if (cageOut == null || cageOut.Length != nc)
                {
                    cageOut = new Vector3[nc];
                    cageHeld = new Vector3[nc];
                    cagePrev = new Vector3[nc];
                }
                else
                {
                    System.Array.Clear(cageOut, 0, nc);
                    System.Array.Clear(cageHeld, 0, nc);
                }
                cageHeldValid = false;
            }

            frameFlip = !frameFlip;
            bool hrAny = halfRate || halfRateLerp;
            if (simEnabled && hrAny && !frameFlip && heldValid && heldDisp != null && heldDisp.Length == disp.Length)
            {
                // HELD frame: reuse last computed displacement (fresh skinning still flows
                // through — only the offset field is one frame old)
                System.Array.Copy(heldDisp, disp, disp.Length);
                if (cageHeld != null && cageOut != null && cageHeld.Length == cageOut.Length)
                    System.Array.Copy(cageHeld, cageOut, cageOut.Length);   // followers hold too
                // keep simBaked FRESH on held frames — followers rebuild their wrap frames
                // from it every frame against a fresh garment bake; a stale interp made the
                // garment lag the body by a frame at half rate
                if (cage != null && cageSim != null) cage.InterpBaked(bakedVerts, bakedNormals);
            }
            else if (simEnabled)
            {
                float pdt = hrAny ? Mathf.Min(dt * 2f, 0.05f) : dt;   // physics dt spans the held frame
                SoftBodySuite.Field.FieldDiag.Mark("jel.clear", JelLap());
                UpdateColliderClouds();
                SoftBodySuite.Field.FieldDiag.Mark("jel.colliders", JelLap());
                Vector3 localDown = go.transform.InverseTransformDirection(worldDown);
                float sdt = pdt / Mathf.Max(1, substeps);
                // dynamics substepped; collision field + output written ONCE per frame
                if (cage != null && cageSim != null)
                {
                    // physics on the uniform CAGE, result projected back onto the mesh.
                    // live-sync the tuning sliders onto the cage's cloned region first
                    if (cageSrc != null) SyncSolverParams(cageSim.cfg, cageSrc);
                    cage.InterpBaked(bakedVerts, bakedNormals);
                    SoftBodySuite.Field.FieldDiag.Mark("jel.interpbaked", JelLap());
                    bool overlap = overlapSolve && !solveBroken && followers.Count > 0 && settingsRef != null
                        && settingsRef.cloth.enabled && settingsRef.cloth.follow.enabled;
                    if (overlap)
                    {
                        // the solve is plain array maths: run it on a worker while this thread
                        // does the garments' BakeMesh calls, which only the main thread can do
                        cageSim.PrepareStep(go.transform);
                        solveSubsteps = substeps; solveSdt = sdt; solvePdt = pdt; solveDown = localDown;
                        solveErr = null;
                        if (solveJob == null) solveJob = SolveJob;
                        solveDone.Reset();
                        System.Threading.ThreadPool.QueueUserWorkItem(solveJob);
                        try { PreBakeFollowers(); }
                        finally { solveDone.Wait(); }   // never leave the solve running unjoined
                        { string lg = SquishSim.TakeLog(); if (lg != null) Debug.Log(lg); }
                        SoftBodySuite.Field.FieldDiag.Mark("jel.solve+garmentbake", JelLap());
                        if (solveErr != null)
                        {
                            solveBroken = true;
                            Debug.LogWarning("[Jello] cage solve on a worker failed, back to the main thread: " + solveErr);
                        }
                    }
                    else
                    {
                        for (int s = 0; s < substeps; s++)
                            cageSim.StepDynamics(cage.simBaked, cage.simNormals, sdt, localDown, go.transform);
                        SoftBodySuite.Field.FieldDiag.Mark("jel.dynamics", JelLap());
                        { string lg = SquishSim.TakeLog(); if (lg != null) Debug.Log(lg); }
                        System.Array.Clear(cage.simDisp, 0, cage.simDisp.Length);
                        cageSim.FieldAndWrite(cage.simBaked, cage.simNormals, cage.simDisp, pdt, SimsAsList(), go.transform);
                        SoftBodySuite.Field.FieldDiag.Mark("jel.fieldwrite", JelLap());
                    }
                    // (The raw/sharpened CLOTH copy of this field that used to be built here,
                    // with its own smoothing and a held/lerp mirror, was dead work: further
                    // down, cage.InterpDelta(disp, cageOut) overwrites cageOut every sim frame
                    // before any garment reads it, so the clothes ride exactly the body's
                    // field. Removing it changes no output and saved ~0.5 ms a frame.)
                    if (settingsRef != null)
                    {
                        int ts = Mathf.Clamp(Mathf.RoundToInt(settingsRef.jello.proxySmooth), 0, 400);
                        int av = Mathf.Clamp(Mathf.RoundToInt(settingsRef.jello.projAvg), 0, 400);
                        if (ts > 0 || av > 0) cage.SmoothDisp(ts, av);
                        SoftBodySuite.Field.FieldDiag.Mark("jel.smoothdisp", JelLap());
                        // 2nd-level squish: restore the sharp dent AFTER the smoothers
                        if (settingsRef.jello.boostStrength > 0.001f || settingsRef.jello.slapPower > 0.001f)
                        {
                            // (the boost reaches the clothes through disp -> cageOut below)
                            cage.ContactBoost(cageSim, go.transform, settingsRef.jello.boostStrength,
                                Mathf.Clamp(Mathf.RoundToInt(settingsRef.jello.boostSpread), 0, 60),
                                Mathf.Clamp(settingsRef.jello.boostMax, 0.001f, 0.2f),
                                Mathf.Max(0f, settingsRef.jello.slapSens), settingsRef.jello.slapPower, pdt);
                        }
                    }
                    // the garments read cageOut; it is filled from the finished disp below
                    if (cageOut == null || cageOut.Length != cage.simDisp.Length)
                    {
                        cageOut = new Vector3[cage.simDisp.Length];
                        cageHeld = new Vector3[cage.simDisp.Length];
                        cagePrev = new Vector3[cage.simDisp.Length];
                        cageHeldValid = false;
                    }

                    SoftBodySuite.Field.FieldDiag.Mark("jel.folsrc", JelLap());
                    cage.Project(disp);
                    SoftBodySuite.Field.FieldDiag.Mark("jel.project", JelLap());
                    if (cageVizGo != null && cageVizGo.activeSelf && cageVizMesh != null) cage.UpdateViz(cageVizMesh);
                }
                else
                {
                    for (int s = 0; s < substeps; s++)
                        for (int r = 0; r < sims.Count; r++)
                            if (sims[r].cfg.enabled)
                                sims[r].StepDynamics(bakedVerts, bakedNormals, sdt, localDown, go.transform);
                    for (int r = 0; r < sims.Count; r++)
                        if (sims[r].cfg.enabled)
                            sims[r].FieldAndWrite(bakedVerts, bakedNormals, disp, pdt, SimsAsList(), go.transform);
                }

                // boundary seam smoothing — final polish on the painted<->unpainted edge
                if (settingsRef != null)
                {
                    int slv = Mathf.Clamp(Mathf.RoundToInt(settingsRef.jello.seamLevel), 0, 200);
                    if ((slv > 0 || settingsRef.jello.seamMaxStretch > 0.0001f) && settingsRef.jello.seamRange > 0.0002f)
                        ApplySeamSmoothing(disp, slv, settingsRef.jello.seamRange, settingsRef.jello.seamMaxStretch);
                }
                SoftBodySuite.Field.FieldDiag.Mark("jel.seam", JelLap());
                if (heldDisp == null || heldDisp.Length != disp.Length) { heldDisp = new Vector3[disp.Length]; heldPrev = new Vector3[disp.Length]; }
                if (heldPrev == null || heldPrev.Length != disp.Length) heldPrev = new Vector3[disp.Length];
                if (halfRateLerp && heldValid)
                {
                    // LERP mode: show the midpoint between the previous and the new tick —
                    // smoother than holding, at half a physics tick of extra latency
                    System.Array.Copy(heldDisp, heldPrev, disp.Length);
                    System.Array.Copy(disp, heldDisp, disp.Length);
                    for (int li = 0; li < disp.Length; li++) disp[li] = (heldPrev[li] + heldDisp[li]) * 0.5f;
                }
                else System.Array.Copy(disp, heldDisp, disp.Length);
                heldValid = true;
            }

            // The clothes must ride EXACTLY the jell-o movement the body got. They used to get
            // their own separately filtered copy of it — no seam smoothing, no edge clamp. With
            // wobble feeding in, jell-o's field grows wobble-sized and mostly pulls AGAINST the
            // wobble, so that unclamped copy cancelled more of the wobble than the body did, and
            // the body poked through the clothes. This is the single point every branch (off,
            // held, blended, fresh) passes through, so the two can never drift apart again.
            // (stage off: disp is all zeros and cageOut was zeroed above - nothing to carry)
            SoftBodySuite.Field.FieldDiag.Mark("jel.hold", JelLap());
            // overall jiggle strength, applied to what is SHOWN (held/lerp state stays raw),
            // before the clothes take their copy so they ride exactly the scaled body
            float js = SoftBodySuite.JiggleGlobal.strength;
            if (simEnabled && js != 1f)
            {
                Vector3[] dj = disp;
                SoftBodySuite.Field.ParallelLoop.For(dj.Length, delegate (int lo, int hi)
                {
                    for (int i = lo; i < hi; i++) dj[i] *= js;
                });
            }
            if (simEnabled && cage != null && cageOut != null && cageOut.Length == cage.SimVertCount)
                cage.InterpDelta(disp, cageOut);
            SoftBodySuite.Field.FieldDiag.Mark("jel.interpdelta", JelLap());

            // bakedVerts here is still (skin + wobble); take the wobble back out
            if (cage != null && cage.SimVertCount > 0 && cage.simBaked != null)
            {
                int nc = cage.SimVertCount;
                if (cageClean == null || cageClean.Length != nc) cageClean = new Vector3[nc];
                if (upDisp != null && upDisp.Length == bakedVerts.Length)
                {
                    if (cleanScratch == null || cleanScratch.Length != bakedVerts.Length)
                        cleanScratch = new Vector3[bakedVerts.Length];
                    Vector3[] cs = cleanScratch, bv = bakedVerts, ud = upDisp;
                    SoftBodySuite.Field.ParallelLoop.For(bv.Length, delegate (int lo, int hi)
                    {
                        for (int i = lo; i < hi; i++) cs[i] = bv[i] - ud[i];
                    });
                    cage.InterpDelta(cleanScratch, cageClean);
                }
                else System.Array.Copy(cage.simBaked, cageClean, nc);
            }
            SoftBodySuite.Field.FieldDiag.Mark("jel.clean", JelLap());

            if (simEnabled)
            {
                Vector3[] bv2 = bakedVerts, dp = disp;
                SoftBodySuite.Field.ParallelLoop.For(bv2.Length, delegate (int lo, int hi)
                {
                    for (int i = lo; i < hi; i++) bv2[i] += dp[i];
                });
            }
            SoftBodySuite.Field.FieldDiag.Mark("jel.add", JelLap());
            framedOnce = true;
            // Upload only what someone can see. With the field or squish on screen this mesh
            // is hidden, and pushing all 38k verts + normals to it every frame was pure cost.
            if ((mr != null && mr.enabled) || overlayGO != null)
            {
                display.SetVertices(bakedVerts);
                display.SetNormals(bakedNormals);
            }
            // fixed expanded bounds once — per-frame RecalculateBounds is a full-mesh scan
            if (!dispBoundsSet) { display.bounds = new Bounds(display.bounds.center, display.bounds.size + Vector3.one * 2f); dispBoundsSet = true; }
            if (overlayOn && overlayMode == 1) RefreshSharpColors();   // live jaggedness view

            msSim = Mathf.Lerp(msSim, (float)swDbg.Elapsed.TotalMilliseconds, 0.08f);
            SoftBodySuite.Field.FieldDiag.Mark("jel_run", (float)swDbg.Elapsed.TotalMilliseconds);
            DrawDebug();
            if (debugDraw && (dbgLogT += dt) > 2f)
            {
                dbgLogT = 0f;
                int act = 0; foreach (KeyValuePair<string, MeshColliderCloud> kv in colMeshCloud) act += kv.Value.nd;
                float srcMax = 0f;
                if (cageOut != null) for (int i = 0; i < cageOut.Length; i++)
                { float m2 = cageOut[i].sqrMagnitude; if (m2 > srcMax) srcMax = m2; }
                srcMax = Mathf.Sqrt(srcMax);
                Debug.Log("[Jello] perf '" + (smr != null ? smr.name : "?") + "': bake+chain=" + msBake.ToString("0.00")
                    + "ms sim+write=" + msSim.ToString("0.00") + "ms activeCapsules=" + act
                    + " followers=" + followers.Count + " fieldMax=" + (srcMax * 1000f).ToString("0.0") + "mm");
                // per-garment: is the cloth actually being moved, and by how much?
                for (int fi = 0; fi < followers.Count; fi++)
                {
                    Follower f = followers[fi];
                    if (f.gRep == null || f.gRep.Length == 0) continue;
                    float mx = 0f, sum = 0f, clr = 0f; int prot = 0;
                    for (int g2 = 0; g2 < f.gRep.Length; g2++)
                    {
                        float m3 = f.gDisp[g2].magnitude;
                        sum += m3; if (m3 > mx) mx = m3;
                        clr += Mathf.Abs(f.gOff[g2].z);
                        if (f.gProt[g2] > 0f) prot++;
                    }
                    Debug.Log("[Jello] FOL '" + (f.smr != null ? f.smr.name : "?") + "' " + (f.whole ? "WHOLE" : "CAGE")
                        + " groups=" + f.gRep.Length
                        + " move avg=" + (sum / f.gRep.Length * 1000f).ToString("0.0")
                        + "mm max=" + (mx * 1000f).ToString("0.0")
                        + "mm restClear=" + (clr / f.gRep.Length * 1000f).ToString("0.0") + "mm protected=" + prot);
                }
            }
        }

        // translucent primitives showing every ACTIVE capsule (skin gap included)
        void DrawDebug()
        {
            int used = 0;
            if (debugDraw)
            {
                foreach (KeyValuePair<string, MeshColliderCloud> kv in colMeshCloud)
                {
                    MeshColliderCloud cl = kv.Value;
                    for (int k = 0; k < cl.nd; k++)
                    {
                        if (used >= dbgPool.Count) dbgPool.Add(MakeDbg());
                        Transform t = dbgPool[used++];
                        if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                        Vector3 a = cl.ca[k], b = cl.cb[k];
                        float r = cl.cr[k] + cl.radius;
                        Vector3 ab = b - a; float len = ab.magnitude;
                        t.localPosition = (a + b) * 0.5f;
                        t.localRotation = len > 1e-5f ? Quaternion.FromToRotation(Vector3.up, ab) : Quaternion.identity;
                        t.localScale = new Vector3(r * 2f, (len + 2f * r) * 0.5f, r * 2f);
                    }
                }
            }
            for (int i = used; i < dbgPool.Count; i++)
                if (dbgPool[i] != null && dbgPool[i].gameObject.activeSelf) dbgPool[i].gameObject.SetActive(false);
        }

        Transform MakeDbg()
        {
            GameObject g = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Collider c = g.GetComponent<Collider>();
            if (c != null) Object.Destroy(c);
            if (dbgMat == null)
            {
                dbgMat = new Material(Shader.Find("Sprites/Default"));
                dbgMat.color = new Color(0f, 1f, 1f, 0.4f);
            }
            g.GetComponent<MeshRenderer>().sharedMaterial = dbgMat;
            g.transform.SetParent(go.transform, false);
            return g.transform;
        }

        public void SetRendererVisible(bool vis)
        {
            if (mr != null) mr.enabled = vis;
        }

        // ---------- sharpness overlay: acute points/edges red, smooth curves blue ----------
        void BuildSharpAdjacency()
        {
            sharpTris = display.triangles;
            int nf = sharpTris.Length / 3;
            faceNorm = new Vector3[nf];
            int n = bakedVerts.Length;
            int[] cnt = new int[n];
            for (int t = 0; t < sharpTris.Length; t++) cnt[sharpTris[t]]++;
            vFaceOff = new int[n + 1];
            for (int v = 0; v < n; v++) vFaceOff[v + 1] = vFaceOff[v] + cnt[v];
            vFaceIdx = new int[sharpTris.Length];
            int[] cur = new int[n];
            for (int f = 0; f < nf; f++)
                for (int e = 0; e < 3; e++)
                {
                    int v = sharpTris[f * 3 + e];
                    vFaceIdx[vFaceOff[v] + cur[v]++] = f;
                }
            sharpVal = new float[n];
        }

        public void RefreshSharpColors()
        {
            if (!Alive) return;
            if (sharpTris == null) BuildSharpAdjacency();
            int nf = sharpTris.Length / 3;
            for (int f = 0; f < nf; f++)
            {
                Vector3 a = bakedVerts[sharpTris[f * 3]];
                Vector3 nrm = Vector3.Cross(bakedVerts[sharpTris[f * 3 + 1]] - a, bakedVerts[sharpTris[f * 3 + 2]] - a);
                float m = nrm.magnitude;
                faceNorm[f] = m > 1e-12f ? nrm / m : Vector3.up;
            }
            int n = bakedVerts.Length;
            if (overlayColors == null || overlayColors.Length != n) overlayColors = new Color32[n];
            for (int v = 0; v < n; v++)
            {
                int s0 = vFaceOff[v], e0 = vFaceOff[v + 1];
                float worst = 0f;
                if (e0 > s0)
                {
                    Vector3 avg = Vector3.zero;
                    for (int k = s0; k < e0; k++) avg += faceNorm[vFaceIdx[k]];
                    float am = avg.magnitude;
                    if (am > 1e-9f)
                    {
                        avg /= am;
                        for (int k = s0; k < e0; k++)
                        {
                            float d = 1f - Vector3.Dot(faceNorm[vFaceIdx[k]], avg);
                            if (d > worst) worst = d;
                        }
                    }
                }
                sharpVal[v] = worst;
                overlayColors[v] = Ramp(Mathf.Clamp01(worst / 0.5f));   // 0 = blue, 60deg+ = red
            }
            display.colors32 = overlayColors;
        }

        public void LogColliderInfo()
        {
            foreach (KeyValuePair<string, MeshColliderCloud> kv in colMeshCloud)
                Debug.Log("[Jello] colliders from '" + kv.Key + "': " + kv.Value.buildInfo);
        }

        // F11: dump displaced mesh + rest mesh + per-node solver fields for offline analysis
        public void DumpDebug()
        {
            if (!Alive) return;
            try
            {
                string dir = System.IO.Path.Combine(Application.persistentDataPath, "jellodebug");
                System.IO.Directory.CreateDirectory(dir);
                int[] tris = display.triangles;
                System.Text.StringBuilder sb = new System.Text.StringBuilder(1 << 23);
                for (int i = 0; i < bakedVerts.Length; i++)
                {
                    Vector3 v = bakedVerts[i];
                    sb.Append("v ").Append(v.x.ToString("0.00000")).Append(' ')
                      .Append(v.y.ToString("0.00000")).Append(' ').Append(v.z.ToString("0.00000")).Append('\n');
                }
                for (int t = 0; t < tris.Length; t += 3)
                    sb.Append("f ").Append(tris[t] + 1).Append(' ').Append(tris[t + 1] + 1).Append(' ').Append(tris[t + 2] + 1).Append('\n');
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, smr.name + "_disp.obj"), sb.ToString());

                sb.Length = 0;
                for (int i = 0; i < bakedVerts.Length; i++)
                {
                    Vector3 v = bakedVerts[i] - disp[i];   // pre-displacement (this frame)
                    sb.Append("v ").Append(v.x.ToString("0.00000")).Append(' ')
                      .Append(v.y.ToString("0.00000")).Append(' ').Append(v.z.ToString("0.00000")).Append('\n');
                }
                for (int t = 0; t < tris.Length; t += 3)
                    sb.Append("f ").Append(tris[t] + 1).Append(' ').Append(tris[t + 1] + 1).Append(' ').Append(tris[t + 2] + 1).Append('\n');
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, smr.name + "_rest.obj"), sb.ToString());

                sb.Length = 0;
                sb.Append("vi,w,need,soft,hard,rawpen\n");
                for (int s = 0; s < sims.Count; s++) sims[s].DumpCsv(sb);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, smr.name + "_fields.csv"), sb.ToString());

                // active capsules
                sb.Length = 0;
                sb.Append("ax,ay,az,bx,by,bz,r\n");
                foreach (KeyValuePair<string, MeshColliderCloud> kv in colMeshCloud)
                {
                    MeshColliderCloud cl = kv.Value;
                    for (int k = 0; k < cl.nd; k++)
                        sb.Append(cl.ca[k].x.ToString("0.0000")).Append(',').Append(cl.ca[k].y.ToString("0.0000")).Append(',').Append(cl.ca[k].z.ToString("0.0000")).Append(',')
                          .Append(cl.cb[k].x.ToString("0.0000")).Append(',').Append(cl.cb[k].y.ToString("0.0000")).Append(',').Append(cl.cb[k].z.ToString("0.0000")).Append(',')
                          .Append((cl.cr[k] + cl.radius).ToString("0.0000")).Append('\n');
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, smr.name + "_capsules.csv"), sb.ToString());
                Debug.Log("[Jello] debug dump written to " + dir);
            }
            catch (System.Exception e) { Debug.LogWarning("[Jello] dump failed: " + e.Message); }
        }

        List<SquishSim> SimsAsList() { return sims; }

        // ---------- weight-paint overlay ----------
        public void SetOverlay(bool on)
        {
            overlayOn = on;
            if (!on)
            {
                if (overlayGO != null) Object.Destroy(overlayGO);
                overlayGO = null; overlayMR = null;
                if (mr != null) mr.enabled = true;            // hand rendering back to the textured mesh
                return;
            }
            if (!Alive) return;
            if (overlayGO == null)
            {
                overlayGO = new GameObject(smr.name + "_JelloOverlay");
                overlayGO.transform.SetParent(go.transform, false);
                MeshFilter omf = overlayGO.AddComponent<MeshFilter>();
                omf.sharedMesh = display;                     // same live mesh
                overlayMR = overlayGO.AddComponent<MeshRenderer>();
                if (overlayMatOverride != null) overlayMat = new Material(overlayMatOverride);
                else
                {
                    Shader sh = Shader.Find("Sprites/Default");
                    if (sh == null) sh = Shader.Find("Unlit/Color");
                    overlayMat = new Material(sh);
                }
                overlayMat.color = new Color(1f, 1f, 1f, overlayOpacity);
                overlayMR.sharedMaterial = overlayMat;
            }
            // Blender-style: the heatmap REPLACES the textured surface while painting.
            // Rendering both at the same depth z-fought, and on some meshes the textured
            // side won the front faces, leaving the overlay visible only from inside.
            if (mr != null) mr.enabled = false;
        }

        public void SetOverlayOpacity(float o)
        {
            overlayOpacity = Mathf.Clamp01(o);
            if (overlayMat != null) overlayMat.color = new Color(1f, 1f, 1f, overlayOpacity);
        }

        // paint the classic blue->cyan->green->yellow->red ramp for one region's weights
        public void RefreshOverlayColors(SquishRegion region)
        {
            if (display == null) return;
            int n = display.vertexCount;
            if (overlayColors == null || overlayColors.Length != n) overlayColors = new Color32[n];
            for (int i = 0; i < n; i++) overlayColors[i] = new Color32(40, 40, 160, 255);
            if (region != null)
                for (int i = 0; i < region.vertIndex.Count; i++)
                {
                    int vi = region.vertIndex[i]; if (vi >= n) continue;
                    overlayColors[vi] = Ramp(region.weight[i]);
                }
            display.colors32 = overlayColors;
        }

        static Color32 Ramp(float t)
        {
            t = Mathf.Clamp01(t);
            Color c;
            if (t < 0.25f) c = Color.Lerp(new Color(0.15f, 0.15f, 0.63f), Color.cyan, t / 0.25f);
            else if (t < 0.5f) c = Color.Lerp(Color.cyan, Color.green, (t - 0.25f) / 0.25f);
            else if (t < 0.75f) c = Color.Lerp(Color.green, Color.yellow, (t - 0.5f) / 0.25f);
            else c = Color.Lerp(Color.yellow, Color.red, (t - 0.75f) / 0.25f);
            return c;
        }

        // ---------- quick-select from skin weights ("vertex groups") ----------
        public List<string> BoneNamesWithWeights()
        {
            List<string> names = new List<string>();
            if (smr == null || smr.sharedMesh == null) return names;
            BoneWeight[] bw = smr.sharedMesh.boneWeights;
            bool[] used = new bool[smr.bones.Length];
            for (int i = 0; i < bw.Length; i++)
            {
                if (bw[i].weight0 > 0.01f && bw[i].boneIndex0 < used.Length) used[bw[i].boneIndex0] = true;
                if (bw[i].weight1 > 0.01f && bw[i].boneIndex1 < used.Length) used[bw[i].boneIndex1] = true;
                if (bw[i].weight2 > 0.01f && bw[i].boneIndex2 < used.Length) used[bw[i].boneIndex2] = true;
                if (bw[i].weight3 > 0.01f && bw[i].boneIndex3 < used.Length) used[bw[i].boneIndex3] = true;
            }
            for (int b = 0; b < used.Length; b++)
                if (used[b] && smr.bones[b] != null) names.Add(smr.bones[b].name);
            names.Sort();
            return names;
        }

        // Set a region's weights from a bone's skin weights (>= threshold). With
        // includeChildren, every bone further down that branch of the rig (all
        // descendants that skin this mesh) contributes too — e.g. picking a breast
        // root also grabs its tip/secondary bones.
        public void SelectFromBone(SquishRegion region, string boneName, float threshold, bool includeChildren)
        {
            SelectFromBoneOn(smr, region, boneName, threshold, includeChildren);
        }

        // Static variant usable on ANY SkinnedMeshRenderer (no proxy needed) — lets the
        // same vertex-group selection be applied across multiple meshes.
        public static void SelectFromBoneOn(SkinnedMeshRenderer smr, SquishRegion region,
                                            string boneName, float threshold, bool includeChildren)
        {
            if (smr == null || smr.sharedMesh == null) return;
            Transform rootBone = null;
            for (int b = 0; b < smr.bones.Length; b++)
                if (smr.bones[b] != null && smr.bones[b].name == boneName) { rootBone = smr.bones[b]; break; }
            if (rootBone == null) return;

            bool[] pick = new bool[smr.bones.Length];
            for (int b = 0; b < smr.bones.Length; b++)
            {
                Transform t = smr.bones[b]; if (t == null) continue;
                if (t == rootBone) { pick[b] = true; continue; }
                if (includeChildren)
                    for (Transform p = t.parent; p != null; p = p.parent)
                        if (p == rootBone) { pick[b] = true; break; }
            }

            BoneWeight[] bw = smr.sharedMesh.boneWeights;
            region.vertIndex.Clear(); region.weight.Clear();
            for (int i = 0; i < bw.Length; i++)
            {
                float wv = 0f;
                if (bw[i].boneIndex0 < pick.Length && pick[bw[i].boneIndex0]) wv += bw[i].weight0;
                if (bw[i].boneIndex1 < pick.Length && pick[bw[i].boneIndex1]) wv += bw[i].weight1;
                if (bw[i].boneIndex2 < pick.Length && pick[bw[i].boneIndex2]) wv += bw[i].weight2;
                if (bw[i].boneIndex3 < pick.Length && pick[bw[i].boneIndex3]) wv += bw[i].weight3;
                if (wv >= threshold) { region.vertIndex.Add(i); region.weight.Add(Mathf.Clamp01(wv)); }
            }
        }

        // ---------- surface transfer (project weights onto another mesh) ----------
        // Collect this proxy's painted region as WORLD-space (position, weight) samples.
        public List<Vector4> RegionWorldSamples(SquishRegion region)
        {
            List<Vector4> pts = new List<Vector4>();
            if (!Alive || region == null) return pts;
            Transform tr = go.transform;
            for (int i = 0; i < region.vertIndex.Count; i++)
            {
                int vi = region.vertIndex[i];
                if (vi >= bakedVerts.Length) continue;
                Vector3 wp = tr.TransformPoint(bakedVerts[vi]);
                pts.Add(new Vector4(wp.x, wp.y, wp.z, region.weight[i]));
            }
            return pts;
        }

        // Project world-space weight samples onto a target mesh: each target vertex takes
        // the max falloff-weighted sample within `radius`. Spatial-hashed so Body->cloth
        // transfers stay fast even on high-poly meshes. Both meshes are baked in the SAME
        // avatar pose, so overlapping surfaces line up.
        public static int TransferWeights(SkinnedMeshRenderer target, SquishRegion region,
                                          List<Vector4> samples, float radius)
        {
            if (target == null || samples == null || samples.Count == 0) return 0;
            Mesh tmp = new Mesh();
            target.BakeMesh(tmp);
            Vector3[] tv = tmp.vertices;
            Transform tt = target.transform;

            float cell = Mathf.Max(0.005f, radius);
            Dictionary<long, List<int>> hash = new Dictionary<long, List<int>>();
            for (int s = 0; s < samples.Count; s++)
            {
                long k = CellKey(samples[s], cell);
                List<int> lst; if (!hash.TryGetValue(k, out lst)) { lst = new List<int>(); hash[k] = lst; }
                lst.Add(s);
            }

            region.vertIndex.Clear(); region.weight.Clear();
            float r2 = radius * radius;
            for (int i = 0; i < tv.Length; i++)
            {
                Vector3 wp = tt.TransformPoint(tv[i]);
                int cx = Mathf.FloorToInt(wp.x / cell), cy = Mathf.FloorToInt(wp.y / cell), cz = Mathf.FloorToInt(wp.z / cell);
                float best = 0f;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    long k = Key(cx + dx, cy + dy, cz + dz);
                    List<int> lst; if (!hash.TryGetValue(k, out lst)) continue;
                    for (int j = 0; j < lst.Count; j++)
                    {
                        Vector4 sm = samples[lst[j]];
                        float ddx = wp.x - sm.x, ddy = wp.y - sm.y, ddz = wp.z - sm.z;
                        float d2 = ddx * ddx + ddy * ddy + ddz * ddz;
                        if (d2 > r2) continue;
                        float w = sm.w * (1f - Mathf.Sqrt(d2) / radius);
                        if (w > best) best = w;
                    }
                }
                if (best > 0.01f) { region.vertIndex.Add(i); region.weight.Add(best); }
            }
            Object.Destroy(tmp);
            return region.vertIndex.Count;
        }

        static long CellKey(Vector4 p, float cell)
        {
            return Key(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
        }
        static long Key(int x, int y, int z)
        {
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
        }

        // Rebuild only the sims from the current region weights (much lighter than a
        // full re-attach; used after paint strokes / blur / undo).
        public void RebuildSims(GameObject avatar, Animator anim)
        {
            if (!Alive || cfg == null) return;
            BuildSims(avatar, anim);
            ResolveColliderMeshes(avatar);
            // paint / region bones changed: "dead" garments get another chance right away
            for (int i = 0; i < folDead.Count; i++) if (folDead[i] != null) folFailed.Add(folDead[i]);
            folDead.Clear();
            shareBones = null;   // flesh-bone data is rebuilt from the new paint
        }

        // In cage mode the solver runs on a CLONE of the source region, so live slider
        // edits (which land on the source) never reach it. Copy every value-type field
        // (all the solver scalars) from source -> clone each frame; reference-type fields
        // (vertIndex/weight/colliders/name) are left alone, keeping the cage's geometry.
        static readonly System.Reflection.FieldInfo[] regionValueFields =
            System.Array.FindAll(typeof(SquishRegion).GetFields(), f => f.FieldType.IsValueType);
        static void SyncSolverParams(SquishRegion dst, SquishRegion src)
        {
            if (dst == null || src == null) return;
            for (int i = 0; i < regionValueFields.Length; i++)
                regionValueFields[i].SetValue(dst, regionValueFields[i].GetValue(src));
        }

        // one sim per region — or, in cage mode, ONE sim on a uniform remeshed
        // duplicate of all regions (bad topology never reaches the solver)
        void BuildSims(GameObject avatar, Animator anim)
        {
            seamDirty = true;
            sims.Clear(); cage = null; cageSim = null;
            DestroyCageViz();
            avatarRef = avatar; animRef = anim;
            cageToken++; cageBuilding = null;                    // abandon any in-flight build
            if (settingsRef != null && settingsRef.jello.useRemesh > 0.5f && StartCageBuild()) return;
            BuildRegionSims();
        }

        void BuildRegionSims()
        {
            sims.Clear();
            for (int r = 0; r < cfg.regions.Count; r++)
            {
                SquishSim s = new SquishSim();
                s.Build(cfg.regions[r].jello, display, weldOf, weldMembers);
                ResolveRegionRefs(s, avatarRef, animRef);
                sims.Add(s);
            }
        }

        // kick the remesh off on a background thread; physics for this mesh pauses
        // (zero displacement) until the cage arrives, VNyan itself stays responsive
        bool StartCageBuild()
        {
            SquishRegion src = null;
            for (int r = 0; r < cfg.regions.Count; r++)
                if (cfg.regions[r].jello.enabled && cfg.regions[r].jello.vertIndex.Count > 0) { src = cfg.regions[r].jello; break; }
            if (src == null) return false;
            float[] wRep = RemeshCage.UnionWeights(display.vertexCount, cfg.regions, weldOf);
            if (wRep == null) return false;
            RemeshCage c = new RemeshCage();
            c.logTag = "[Jello]";
            float L = Mathf.Clamp(settingsRef.jello.remeshSize, 0.002f, 0.15f);
            int passes = Mathf.Clamp(Mathf.RoundToInt(settingsRef.jello.remeshPasses), 5, 20);
            Vector3[] mvp = display.vertices;
            Vector3[] mnrm = display.normals;                    // rest normals for the valley gate
            int[] tris = display.triangles;
            int[] wo = weldOf; List<int>[] wm = weldMembers;
            cageBuilding = c;
            cageSw = System.Diagnostics.Stopwatch.StartNew();
            Debug.Log(c.logTag + " cage build started (edge " + L.ToString("0.0000") + " m, " + passes
                + " passes) — running in background");
            System.Threading.Thread th = new System.Threading.Thread(() =>
            {
                // NO Debug.Log in here: logging from a worker thread can deadlock
                // against the host's log handler — progress goes via c.stage instead
                try { c.buildOk = c.Build(mvp, tris, wo, wm, wRep, mnrm, L, passes); }
                catch (System.Exception e) { c.note = "exception: " + e.Message; }
                finally { c.buildDone = true; }
            });
            th.IsBackground = true;
            th.Start();
            return true;
        }

        string cageLastStage;
        void PollCageBuild()
        {
            RemeshCage c = cageBuilding;
            if (c == null) return;
            if (c.stage != cageLastStage)
            {
                cageLastStage = c.stage;
                Debug.Log(c.logTag + " cage stage: " + c.stage + " (" + (cageSw != null ? cageSw.ElapsedMilliseconds : 0) + " ms)");
            }
            if (!c.buildDone)
            {
                if (cageSw != null && cageSw.ElapsedMilliseconds > 30000)
                {
                    Debug.LogWarning("[Jello] cage build timed out — falling back to per-region sims");
                    cageToken++; cageBuilding = null;
                    BuildRegionSims(); ResolveColliderMeshes(avatarRef);
                }
                return;
            }
            cageBuilding = null;
            if (c.note.Length > 0) Debug.LogWarning(c.logTag + " cage note: " + c.note);
            if (!c.buildOk)
            {
                Debug.LogWarning("[Jello] cage remesh failed (stage: " + c.stage + ") — falling back to per-region sims");
                BuildRegionSims(); ResolveColliderMeshes(avatarRef);
                return;
            }
            FinishCage(c);
        }

        void FinishCage(RemeshCage c)
        {
            SquishRegion src = null;
            for (int r = 0; r < cfg.regions.Count; r++)
                if (cfg.regions[r].jello.enabled && cfg.regions[r].jello.vertIndex.Count > 0) { src = cfg.regions[r].jello; break; }
            if (src == null) { BuildRegionSims(); ResolveColliderMeshes(avatarRef); return; }
            // synthetic region: solver params cloned from the first enabled region,
            // vertices are the CAGE's, colliders merged from every enabled region
            SquishRegion reg = Newtonsoft.Json.JsonConvert.DeserializeObject<SquishRegion>(
                Newtonsoft.Json.JsonConvert.SerializeObject(src));
            reg.name = "(cage) " + src.name;
            reg.enabled = true;
            reg.vertIndex = new List<int>(); reg.weight = new List<float>();
            for (int i = 0; i < c.SimVertCount; i++) { reg.vertIndex.Add(i); reg.weight.Add(c.simWeight[i]); }
            for (int r = 0; r < cfg.regions.Count; r++)
            {
                if (!cfg.regions[r].jello.enabled || cfg.regions[r].jello == src) continue;
                for (int cc = 0; cc < cfg.regions[r].jello.colliders.Count; cc++)
                {
                    SquishCollider col = cfg.regions[r].jello.colliders[cc];
                    bool dup = false;
                    for (int k = 0; k < reg.colliders.Count; k++)
                        if (reg.colliders[k].bone == col.bone && reg.colliders[k].mesh == col.mesh) { dup = true; break; }
                    if (!dup) reg.colliders.Add(Newtonsoft.Json.JsonConvert.DeserializeObject<SquishCollider>(
                        Newtonsoft.Json.JsonConvert.SerializeObject(col)));
                }
            }
            if (cageMesh != null) Object.Destroy(cageMesh);
            cageMesh = new Mesh();
            if (c.SimVertCount > 65000) cageMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            cageMesh.vertices = c.simRest; cageMesh.triangles = c.simTris; cageMesh.RecalculateNormals();
            int[] idWeld = new int[c.SimVertCount];
            List<int>[] idMem = new List<int>[c.SimVertCount];
            for (int i = 0; i < c.SimVertCount; i++) { idWeld[i] = i; idMem[i] = new List<int>(1); idMem[i].Add(i); }
            SquishSim sim = new SquishSim();
            sim.Build(reg, cageMesh, idWeld, idMem);
            ResolveRegionRefs(sim, avatarRef, animRef);
            sims.Clear();
            cage = c; cageSim = sim; cageSrc = src; sims.Add(sim);
            cageHeldValid = false;   // fresh cage: don't lerp followers from the old field
            ResolveColliderMeshes(avatarRef);
            Debug.Log("[Jello] remesh cage LIVE: " + c.SimVertCount + " verts, edge=" + c.usedEdge.ToString("0.0000")
                + " m, built in " + (cageSw != null ? cageSw.ElapsedMilliseconds : 0) + " ms (union of all regions); valley gate: "
                + c.gateUsed + " gated / " + c.gateFallback + " fallback");
        }

        public void ToggleCageViz()
        {
            if (cage == null) return;
            if (cageVizGo == null)
            {
                cageVizMesh = cage.MakeVizMesh();
                cageVizGo = new GameObject(smr.name + "_CageViz");
                cageVizGo.transform.SetParent(go.transform, false);
                MeshFilter f = cageVizGo.AddComponent<MeshFilter>(); f.sharedMesh = cageVizMesh;
                MeshRenderer r2 = cageVizGo.AddComponent<MeshRenderer>();
                r2.sharedMaterial = overlayMatOverride != null ? new Material(overlayMatOverride)
                                                               : new Material(Shader.Find("Standard"));
                return;
            }
            cageVizGo.SetActive(!cageVizGo.activeSelf);
        }
        void DestroyCageViz()
        {
            if (cageVizGo != null) { Object.Destroy(cageVizGo); cageVizGo = null; }
            if (cageVizMesh != null) { Object.Destroy(cageVizMesh); cageVizMesh = null; }
        }

        // ---------- vertex welding (dupes along UV seams / hard edges) ----------
        // Game meshes duplicate vertices wherever UVs or normals split; simulating the
        // copies independently TEARS the surface. Weld by position so the sim runs one
        // node per unique point and every duplicate moves identically.
        int[] weldOf;                 // mesh vertex -> weld group id
        List<int>[] weldMembers;      // weld group id -> all duplicate vertex indices

        void BuildWeldMap()
        {
            Vector3[] v = display.vertices;
            int n = v.Length;
            weldOf = new int[n];
            Dictionary<long, int> groupOf = new Dictionary<long, int>(n);
            List<List<int>> groups = new List<List<int>>();
            for (int i = 0; i < n; i++)
            {
                long k = ((long)(Mathf.RoundToInt(v[i].x * 10000f) & 0x1FFFFF) << 42)
                       | ((long)(Mathf.RoundToInt(v[i].y * 10000f) & 0x1FFFFF) << 21)
                       | (long)(Mathf.RoundToInt(v[i].z * 10000f) & 0x1FFFFF);
                int g;
                if (!groupOf.TryGetValue(k, out g)) { g = groups.Count; groupOf[k] = g; groups.Add(new List<int>(2)); }
                groups[g].Add(i);
                weldOf[i] = g;
            }
            weldMembers = groups.ToArray();
        }

        // ---------- blur (Laplacian smoothing of the painted weights) ----------
        int[][] meshAdj;   // full-mesh vertex adjacency, built lazily
        void EnsureAdjacency()
        {
            if (meshAdj != null || display == null) return;
            int n = display.vertexCount;
            List<int>[] adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>(6);
            int[] tris = display.triangles;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], cc = tris[t + 2];
                if (!adj[a].Contains(b)) { adj[a].Add(b); adj[b].Add(a); }
                if (!adj[b].Contains(cc)) { adj[b].Add(cc); adj[cc].Add(b); }
                if (!adj[a].Contains(cc)) { adj[a].Add(cc); adj[cc].Add(a); }
            }
            meshAdj = new int[n][];
            for (int i = 0; i < n; i++) meshAdj[i] = adj[i].ToArray();
        }

        // ---------- boundary seam smoothing ----------
        // Work in WELD space so coincident UV-seam duplicates always move together
        // (per-render smoothing would tear the surface). seamDist = metric distance of
        // each weld group to the painted<->unpainted boundary (multi-source Dijkstra,
        // capped); the live range slider just reweights this cached field.
        int[][] wAdj; int[] grpRep; float[] seamDist; int[] seamBand;
        Vector3[] grpDisp, grpDisp2; bool seamDirty = true;
        const float SEAM_MAXR = 0.25f;   // cache distances out to here; slider range clamps under it

        void EnsureWeldAdj()
        {
            if (wAdj != null || display == null) return;
            int g = weldMembers.Length;
            grpRep = new int[g];
            for (int i = 0; i < g; i++) grpRep[i] = weldMembers[i].Count > 0 ? weldMembers[i][0] : 0;
            List<int>[] adj = new List<int>[g];
            for (int i = 0; i < g; i++) adj[i] = new List<int>(6);
            int[] tris = display.triangles;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = weldOf[tris[t]], b = weldOf[tris[t + 1]], c = weldOf[tris[t + 2]];
                if (a != b && !adj[a].Contains(b)) { adj[a].Add(b); adj[b].Add(a); }
                if (b != c && !adj[b].Contains(c)) { adj[b].Add(c); adj[c].Add(b); }
                if (a != c && !adj[a].Contains(c)) { adj[a].Add(c); adj[c].Add(a); }
            }
            wAdj = new int[g][];
            for (int i = 0; i < g; i++) wAdj[i] = adj[i].ToArray();
            grpDisp = new Vector3[g]; grpDisp2 = new Vector3[g];
        }

        void BuildSeamField()
        {
            seamDirty = false;
            EnsureWeldAdj();
            int g = weldMembers.Length;
            Vector3[] vp = display.vertices;
            // union paint weight per weld group
            float[] wG = new float[g];
            for (int r = 0; r < cfg.regions.Count; r++)
            {
                SquishRegion reg = cfg.regions[r].jello;
                if (!reg.enabled) continue;
                for (int i = 0; i < reg.vertIndex.Count; i++)
                {
                    int vi = reg.vertIndex[i];
                    if (vi < 0 || vi >= weldOf.Length) continue;
                    int gg = weldOf[vi];
                    if (reg.weight[i] > wG[gg]) wG[gg] = reg.weight[i];
                }
            }
            bool[] inside = new bool[g];
            for (int i = 0; i < g; i++) inside[i] = wG[i] > 0.05f;
            seamDist = new float[g];
            for (int i = 0; i < g; i++) seamDist[i] = float.MaxValue;
            // multi-source Dijkstra with lazy deletion: boundary groups (an edge crossing
            // the inside/outside line) start at distance 0
            MinHeap heap = new MinHeap(g);
            for (int a = 0; a < g; a++)
            {
                int[] nb = wAdj[a];
                for (int j = 0; j < nb.Length; j++)
                    if (inside[a] != inside[nb[j]]) { if (seamDist[a] != 0f) { seamDist[a] = 0f; heap.Push(0f, a); } break; }
            }
            while (heap.Count > 0)
            {
                float d; int a; heap.Pop(out d, out a);
                if (d > seamDist[a]) continue;
                if (d > SEAM_MAXR) continue;
                Vector3 pa = vp[grpRep[a]];
                int[] nb = wAdj[a];
                for (int j = 0; j < nb.Length; j++)
                {
                    int b = nb[j];
                    float nd = d + (pa - vp[grpRep[b]]).magnitude;
                    if (nd < seamDist[b]) { seamDist[b] = nd; if (nd <= SEAM_MAXR) heap.Push(nd, b); }
                }
            }
            List<int> band = new List<int>();
            for (int i = 0; i < g; i++) if (seamDist[i] <= SEAM_MAXR) band.Add(i);
            seamBand = band.ToArray();
            // sort by distance-to-seam so the ACTIVE band (verts within `range`, the only
            // ones the smoothing touches) is a prefix — ApplySeamSmoothing then iterates
            // only that prefix instead of the whole 0.25 m cache every frame (the lag).
            System.Array.Sort(seamBand, delegate(int x, int y) { return seamDist[x].CompareTo(seamDist[y]); });
        }

        void ApplySeamSmoothing(Vector3[] disp, int passes, float range, float maxStretch)
        {
            if (seamDirty || seamDist == null) BuildSeamField();
            if (seamBand == null || seamBand.Length == 0) return;
            range = Mathf.Min(range, SEAM_MAXR);
            float invR = 1f / range;
            // seamBand is sorted by distance-to-seam → the ACTIVE band (seamDist <= range,
            // the only verts the smoothing/limiter affect) is a prefix. Iterate ONLY that;
            // scanning the whole 0.25 m cache every frame was the lag.
            int active = seamBand.Length;
            for (int i = 0; i < seamBand.Length; i++) if (seamDist[seamBand[i]] > range) { active = i; break; }
            if (active == 0) return;
            // gather current per-group displacement (active verts + their 1-ring)
            for (int i = 0; i < active; i++)
            {
                int gg = seamBand[i];
                grpDisp[gg] = disp[grpRep[gg]];
                int[] nb = wAdj[gg];
                for (int j = 0; j < nb.Length; j++) grpDisp[nb[j]] = disp[grpRep[nb[j]]];
            }
            // Every pass reads grpDisp and writes only its own grpDisp2 slot (band groups
            // are unique), and weld groups own disjoint render verts, so all three loops
            // split across cores with exactly the serial result. This pass was 1.5 ms.
            int[] band = seamBand; float[] sDist = seamDist; int[][] adj = wAdj;
            Vector3[] gA = grpDisp, gB = grpDisp2;
            for (int pass = 0; pass < passes; pass++)
            {
                SoftBodySuite.Field.ParallelLoop.For(active, 512, delegate (int lo, int hi)
                {
                    for (int i = lo; i < hi; i++)
                    {
                        int gg = band[i];
                        float bw = 1f - sDist[gg] * invR;      // 1 at the seam, 0 at the band edge
                        if (bw <= 0f) { gB[gg] = gA[gg]; continue; }
                        int[] nb = adj[gg];
                        if (nb.Length < 2) { gB[gg] = gA[gg]; continue; }
                        Vector3 avg = Vector3.zero;
                        for (int j = 0; j < nb.Length; j++) avg += gA[nb[j]];
                        avg /= nb.Length;
                        gB[gg] = Vector3.Lerp(gA[gg], avg, bw);
                    }
                });
                SoftBodySuite.Field.ParallelLoop.For(active, 4096, delegate (int lo, int hi)
                {
                    for (int i = lo; i < hi; i++) { int gg = band[i]; gA[gg] = gB[gg]; }
                });
            }
            // scatter smoothed group displacement back to every render member (active band)
            List<int>[] members = weldMembers;
            SoftBodySuite.Field.ParallelLoop.For(active, 1024, delegate (int lo, int hi)
            {
                for (int i = lo; i < hi; i++)
                {
                    int gg = band[i];
                    List<int> mem = members[gg];
                    Vector3 d = gA[gg];
                    for (int m = 0; m < mem.Count; m++) disp[mem[m]] = d;
                }
            });
            // SEAM MAX STRETCH — displacement RAMP limit ("cone clamp"). F11 evidence: the
            // whole 0→full transition packs into the smoothing band (avg +214% edge stretch),
            // pinned between the still body and the moving interior — edge clamps inside the
            // band can only shuffle that, never remove it. Instead cap the displacement
            // MAGNITUDE by surface distance to the boundary: |disp| <= seamDist * slope with
            // slope = maxStretch / range ("one range out, at most maxStretch of motion"),
            // over the ENTIRE cached field (0.25 m), spreading the transition deep into the
            // region. One pass, no neighbour loops. 0 = off.
            if (maxStretch > 0.0001f)
            {
                float slope = maxStretch / Mathf.Max(range, 0.0005f);
                // each group reads and writes only its own members
                SoftBodySuite.Field.ParallelLoop.For(band.Length, 1024, delegate (int lo, int hi)
                {
                    for (int i = lo; i < hi; i++)
                    {
                        int gg = band[i];
                        float allowed = sDist[gg] * slope;
                        List<int> mem = members[gg];
                        if (mem.Count == 0) continue;
                        Vector3 d = disp[mem[0]];
                        float dm = d.magnitude;
                        if (dm <= allowed || dm < 1e-9f) continue;
                        d *= allowed / dm;
                        for (int m = 0; m < mem.Count; m++) disp[mem[m]] = d;
                    }
                });
            }
        }

        // tiny binary min-heap (float key, int value) with lazy deletion for Dijkstra
        class MinHeap
        {
            float[] k; int[] v; public int Count;
            public MinHeap(int cap) { cap = cap < 16 ? 16 : cap; k = new float[cap]; v = new int[cap]; Count = 0; }
            public void Push(float key, int val)
            {
                if (Count == k.Length) { System.Array.Resize(ref k, k.Length * 2); System.Array.Resize(ref v, v.Length * 2); }
                int i = Count++; k[i] = key; v[i] = val;
                while (i > 0) { int p = (i - 1) >> 1; if (k[p] <= k[i]) break; Swap(p, i); i = p; }
            }
            public void Pop(out float key, out int val)
            {
                key = k[0]; val = v[0];
                Count--; k[0] = k[Count]; v[0] = v[Count];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < Count && k[l] < k[m]) m = l;
                    if (r < Count && k[r] < k[m]) m = r;
                    if (m == i) break; Swap(m, i); i = m;
                }
            }
            void Swap(int a, int b) { float tk = k[a]; k[a] = k[b]; k[b] = tk; int tv = v[a]; v[a] = v[b]; v[b] = tv; }
        }

        // One smoothing pass: each affected vertex moves toward the average of its
        // neighbours' weights. The region EXPANDS into the one-ring around it so the
        // edge feathers outward instead of clipping.
        public void BlurRegion(SquishRegion region, float amount)
        {
            if (region == null || display == null) return;
            EnsureAdjacency();
            int n = display.vertexCount;
            float[] wFull = new float[n];
            for (int i = 0; i < region.vertIndex.Count; i++)
                if (region.vertIndex[i] < n) wFull[region.vertIndex[i]] = region.weight[i];

            // affected = region + its one-ring
            HashSet<int> touch = new HashSet<int>();
            for (int i = 0; i < region.vertIndex.Count; i++)
            {
                int vi = region.vertIndex[i]; if (vi >= n) continue;
                touch.Add(vi);
                int[] nb = meshAdj[vi];
                for (int j = 0; j < nb.Length; j++) touch.Add(nb[j]);
            }

            Dictionary<int, float> outW = new Dictionary<int, float>();
            foreach (int vi in touch)
            {
                int[] nb = meshAdj[vi];
                if (nb.Length == 0) { outW[vi] = wFull[vi]; continue; }
                float avg = 0f;
                for (int j = 0; j < nb.Length; j++) avg += wFull[nb[j]];
                avg /= nb.Length;
                outW[vi] = Mathf.Clamp01(Mathf.Lerp(wFull[vi], avg, amount));
            }

            region.vertIndex.Clear(); region.weight.Clear();
            foreach (KeyValuePair<int, float> kv in outW)
                if (kv.Value > 0.003f) { region.vertIndex.Add(kv.Key); region.weight.Add(kv.Value); }
        }

        // ---------- brush painting ----------
        // add/subtract weight around the point where the mouse ray passes the surface
        public bool PaintStroke(SquishRegion region, Ray worldRay, float radius, float strength, int mode)
        {
            if (!Alive || region == null) return false;
            Transform tr = go.transform;
            Vector3 ro = tr.InverseTransformPoint(worldRay.origin);
            Vector3 rd = tr.InverseTransformDirection(worldRay.direction).normalized;

            // nearest vertex to the ray = brush centre
            int hit = -1; float bestT = float.MaxValue; float bestD = radius;
            for (int i = 0; i < bakedVerts.Length; i++)
            {
                Vector3 v = bakedVerts[i] - ro;
                float t = Vector3.Dot(v, rd); if (t < 0f) continue;
                float d = (v - rd * t).magnitude;
                if (d < bestD || (d < bestD + 0.001f && t < bestT)) { bestD = d; hit = i; bestT = t; }
            }
            if (hit < 0) return false;

            Vector3 center = bakedVerts[hit];
            // sparse map for the region
            Dictionary<int, int> pos = new Dictionary<int, int>(region.vertIndex.Count);
            for (int i = 0; i < region.vertIndex.Count; i++) pos[region.vertIndex[i]] = i;

            float r2 = radius * radius;
            for (int i = 0; i < bakedVerts.Length; i++)
            {
                float d2 = (bakedVerts[i] - center).sqrMagnitude;
                if (d2 > r2) continue;
                float fall = 1f - Mathf.Sqrt(d2) / radius;      // linear falloff
                float delta = strength * fall * (mode == 1 ? -1f : 1f);
                int at;
                if (pos.TryGetValue(i, out at))
                {
                    float nw = Mathf.Clamp01(region.weight[at] + delta);
                    region.weight[at] = nw;
                }
                else if (delta > 0f)
                {
                    pos[i] = region.vertIndex.Count;
                    region.vertIndex.Add(i);
                    region.weight.Add(Mathf.Clamp01(delta));
                }
            }
            // prune zeros
            for (int i = region.vertIndex.Count - 1; i >= 0; i--)
                if (region.weight[i] <= 0.001f) { region.vertIndex.RemoveAt(i); region.weight.RemoveAt(i); }
            return true;
        }
    }
}
