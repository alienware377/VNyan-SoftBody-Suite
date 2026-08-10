using System.Collections.Generic;
using UnityEngine;

namespace JelloStudio
{
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
            public int[][] gAdj;                // adjacency among driven groups
            public float[] gSeam;               // metric distance to the driven-area boundary
            public int[] gBand;                 // groups sorted by gSeam (active band = prefix)
            public Vector3[] gDisp, gDisp2;     // per-frame scratch
            public Vector3[] gNrm;              // per-frame bound-tri normal (anti-clip enforcement)
            public float[] gProt;               // per-frame protected outward component (anti-clip floor)
            public float[] gDnRaw;              // full-wrap normal component (clearance reference)
            public bool[] gIsFill;              // valley-gap groups: no binding, diffused from neighbors
            public bool hasFill;
        }
        readonly List<Follower> followers = new List<Follower>();
        readonly List<SkinnedMeshRenderer> folQueue = new List<SkinnedMeshRenderer>();  // bind 1/frame
        RemeshCage folCage;            // cage the current followers/queue were built against
        bool folGenActive;             // a follower generation exists (whole mode has no cage)
        bool folLastWhole;
        int folLastBoneFilter, folLastFillMax, folLastAnchor;
        float folLastRange, folRangeT, folRetryT;
        readonly List<SkinnedMeshRenderer> folFailed = new List<SkinnedMeshRenderer>();
        // bind-generation search structures: a CURRENT-POSE grid (rest-space search mis-bound
        // verts and froze pose error into the fit)
        RemeshCage.TriGrid bindGrid;
        Vector3[] bindPts;             // positions the grid indexes (refreshed in place per bind call)
        int[] bindTris;                // triangle array the grid's results index into
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

        public void ResolveColliderMeshes(GameObject avatar)
        {
            colMeshSmr.Clear(); colMeshCloud.Clear(); allColSmrs = null;
            if (avatar == null || cfg == null) return;
            SkinnedMeshRenderer[] rends = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int r = 0; r < cfg.regions.Count; r++)
                for (int c = 0; c < cfg.regions[r].colliders.Count; c++)
                {
                    string nm = cfg.regions[r].colliders[c].mesh;
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
                SquishRegion reg = cfg.regions[r];
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

            HashSet<int> selfExclude = null;
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
                        for (int v = 0; v < cfg.regions[r].vertIndex.Count; v++)
                            if (cfg.regions[r].weight[v] > 0.15f) selfExclude.Add(cfg.regions[r].vertIndex[v]);
                }

                MeshColliderCloud cloud;
                if (!colMeshCloud.TryGetValue(kv.Key, out cloud)) { cloud = new MeshColliderCloud(); colMeshCloud[kv.Key] = cloud; }

                // skin gap: use the largest radius any collider entry asked for on this mesh
                float rad = 0.015f;
                for (int r = 0; r < cfg.regions.Count; r++)
                    for (int c = 0; c < cfg.regions[r].colliders.Count; c++)
                        if (cfg.regions[r].colliders[c].mesh == kv.Key && cfg.regions[r].colliders[c].radius > rad)
                            rad = cfg.regions[r].colliders[c].radius;
                cloud.radius = rad;

                int excluded = self && selfExclude != null ? selfExclude.Count : 0;
                if (!cloud.HasBuild(excluded))
                {
                    List<Vector3> regionWorld = new List<Vector3>();
                    for (int r = 0; r < cfg.regions.Count; r++)
                    {
                        SquishRegion reg = cfg.regions[r];
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

        // ---------- cage followers ----------
        void UpdateFollowerLifecycle()
        {
            bool whole = settingsRef != null && settingsRef.cageBindWhole;
            bool want = settingsRef != null && settingsRef.cageDrive &&
                        (whole || (cage != null && settingsRef.useRemesh > 0.5f));
            if (!want) { if (folGenActive || followers.Count > 0) DetachFollowers(); return; }
            float range = Mathf.Clamp(settingsRef.cageFollowRange, 0.005f, 0.5f);
            // cage rebuilt (cage mode) or bind-mode flipped: detach and RETURN — requeueing
            // next frame lets the pending-destroy "_JelloFollow" corpses actually die
            if (folGenActive && (folLastWhole != whole || (!whole && folCage != cage))) { DetachFollowers(); return; }
            // bind-affecting knobs: debounce the drag; followers keep running until settled
            int kFilt = settingsRef.cageBoneFilter ? 1 : 0;
            int kFill = Mathf.RoundToInt(settingsRef.cageFillMax);
            int kAnch = Mathf.RoundToInt(settingsRef.cageAnchorMin);
            if (folGenActive && (Mathf.Abs(range - folLastRange) > 0.0005f ||
                kFilt != folLastBoneFilter || kFill != folLastFillMax || kAnch != folLastAnchor))
            { folRangeT = 0.5f; folLastRange = range; folLastBoneFilter = kFilt; folLastFillMax = kFill; folLastAnchor = kAnch; }
            if (folRangeT > 0f)
            {
                folRangeT -= Time.deltaTime;
                if (folRangeT <= 0f) { DetachFollowers(); return; }   // rebind next frame at the settled range
            }
            if (!folGenActive)
            {
                folGenActive = true; folCage = cage; folLastWhole = whole; folLastRange = range;
                folLastBoneFilter = settingsRef.cageBoneFilter ? 1 : 0;
                folLastFillMax = Mathf.RoundToInt(settingsRef.cageFillMax);
                folLastAnchor = Mathf.RoundToInt(settingsRef.cageAnchorMin);
                QueueFollowerCandidates();
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
        bool EnsureBindGrid(bool whole)
        {
            if (bindGrid != null)
            {
                if (whole && bindPts != null && bakedVerts != null && bindPts.Length == bakedVerts.Length)
                    for (int i = 0; i < bindPts.Length; i++) bindPts[i] = bakedVerts[i] - disp[i];
                ComputeBindBounds();
                return true;
            }
            if (whole)
            {
                if (display == null || bakedVerts == null || bakedNormals == null) return false;
                int n = bakedVerts.Length;
                bindPts = new Vector3[n];
                for (int i = 0; i < n; i++) bindPts[i] = bakedVerts[i] - disp[i];
                bindTris = display.triangles;
            }
            else
            {
                if (cage == null || cage.simBaked == null || cage.SimVertCount < 1 ||
                    (cage.simBaked[0] == Vector3.zero && cage.simBaked[cage.SimVertCount - 1] == Vector3.zero))
                    return false;   // freshly built cage hasn't interpolated yet — wait one tick
                bindPts = cage.simBaked;
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
            if (settingsRef != null && settingsRef.cageBoneFilter && cfg != null && cfg.regions != null)
            {
                HashSet<string> names = new HashSet<string>();
                for (int ri = 0; ri < cfg.regions.Count; ri++)
                {
                    SquishRegion rg = cfg.regions[ri];
                    if (rg == null || !rg.enabled || rg.srcBones == null) continue;
                    for (int bi = 0; bi < rg.srcBones.Count; bi++)
                        if (!string.IsNullOrEmpty(rg.srcBones[bi])) names.Add(rg.srcBones[bi]);
                }
                if (names.Count > 0 && r.sharedMesh != null && r.bones != null)
                {
                    Transform[] gb = r.bones;
                    bool[] pick = new bool[gb.Length];
                    for (int b0 = 0; b0 < gb.Length; b0++)
                    {
                        Transform t0 = gb[b0];
                        if (t0 == null) continue;
                        if (names.Contains(t0.name)) { pick[b0] = true; continue; }
                        for (Transform pp = t0.parent; pp != null; pp = pp.parent)   // children of picked bones count too
                            if (names.Contains(pp.name)) { pick[b0] = true; break; }
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

            for (int gg = 0; gg < nGlobal; gg++)
            {
                if (boneOkG != null && !boneOkG[gg]) continue;
                Vector3 q = toSrc.MultiplyPoint3x4(pts[gFirst[gg]]);
                if (q.x < lo.x || q.y < lo.y || q.z < lo.z || q.x > hi.x || q.y > hi.y || q.z > hi.z) continue;
                Vector3 ns = gNormSum[gg];
                Vector3 nq = ns.sqrMagnitude > 1e-6f ? toSrc.MultiplyVector(ns).normalized : Vector3.zero;
                int bt; Vector3 cp, bar; bool viaGate;
                if (!bindGrid.NearestGatedWithin(q, nq, 0.35f, range * 1.25f, out bt, out cp, out bar, out viaGate)) continue;
                float dist = (q - cp).magnitude;
                if (dist > range) continue;
                float u = Mathf.Clamp01((dist - range * 0.5f) / (range * 0.5f));
                float fall = 1f - u * u * (3f - 2f * u);   // 1 inside half-range, smooth to 0 at range
                localOf[gg] = rep.Count;
                rep.Add(gFirst[gg]);
                A.Add(bindTris[bt]); B.Add(bindTris[bt + 1]); C.Add(bindTris[bt + 2]);
                WA.Add(bar.x); WB.Add(bar.y); WC.Add(bar.z); FF.Add(fall);
            }
            if (rep.Count < 8) { Object.Destroy(bk); folFailed.Add(r); return; }   // not covered (maybe off-pose)
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
                int fillMax = settingsRef != null ? Mathf.Max(0, Mathf.RoundToInt(settingsRef.cageFillMax)) : 600;
                int anchorMin = settingsRef != null ? Mathf.Max(1, Mathf.RoundToInt(settingsRef.cageAnchorMin)) : 150;

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

            // WRAP offsets: each group's rest position expressed in its bound tri's local
            // frame, measured on the CLEAN current-pose surface (bindPts: skinned bra vs
            // skinned body, SAME frame, sim removed). Per frame the group rides the tri
            // absolutely, so sim, blendshapes and skinning divergence are all tracked 1:1.
            f.gOff = new Vector3[G];
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
                f.gOff[g2] = new Vector3(Vector3.Dot(d3, e1), Vector3.Dot(d3, e2), Vector3.Dot(d3, n3));
            }

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
            for (int fi = followers.Count - 1; fi >= 0; fi--)
            {
                Follower f = followers[fi];
                if (f.smr == null || f.go == null) { RemoveFollower(fi); continue; }
                // hidden meanwhile (hide panel sets enabled=false): drop the follower and
                // RELEASE forceRenderingOff — enabled=false is what hides it, and leaving
                // our flag set would keep the mesh invisible forever after an un-hide
                if (!f.smr.enabled || !f.smr.gameObject.activeInHierarchy ||
                    (settingsRef != null && settingsRef.hiddenMeshes != null && settingsRef.hiddenMeshes.Contains(f.smr.name)))
                { RemoveFollower(fi, true); continue; }
                // another studio took ownership: they manage visibility now
                if (OtherStudioProxyAlive(f.smr)) { RemoveFollower(fi, false); continue; }
                f.smr.forceRenderingOff = true;   // we render this mesh while driving it
                f.smr.BakeMesh(f.baked);
                f.baked.GetVertices(scratch);
                if (scratch.Count != f.verts.Length) { RemoveFollower(fi); continue; }   // mesh swapped
                scratch.CopyTo(f.verts);
                f.baked.GetNormals(folNrmScratch);
                bool haveN = folNrmScratch.Count == f.verts.Length;
                Matrix4x4 M = f.smr.transform.worldToLocalMatrix * srcL2W;

                if (!f.whole && !cageOk) continue;   // cage followers wait out a rebuild frame
                float fit = settingsRef != null ? Mathf.Clamp(settingsRef.cageFitStrength, 0f, 3f) : 1f;
                float inf = settingsRef != null ? Mathf.Clamp(settingsRef.cageInflate, 0f, 0.1f) : 0f;
                float dyn = settingsRef != null ? Mathf.Clamp(settingsRef.cageInflateDyn, 0f, 3f) : 0f;
                int G = f.gRep.Length;
                Vector3[] cb = f.whole ? bakedVerts : cage.simBaked;
                for (int g2 = 0; g2 < G; g2++)
                {
                    if (f.gIsFill[g2]) { f.gProt[g2] = 0f; continue; }   // diffused later, warm-started
                    // WRAP: rebuild the bound tri's frame on the CURRENT surface and re-place
                    // the stored rest offset. whole mode rides Jello's FINAL output verts
                    // (post-lerp, seam-smoothed); cage mode rides skinned interp + sim mirror.
                    int ia = f.gA[g2], ib = f.gB[g2], ic = f.gC[g2];
                    Vector3 A2, B2, C2;
                    if (f.whole) { A2 = cb[ia]; B2 = cb[ib]; C2 = cb[ic]; }
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
                    Vector3 off = f.gOff[g2];
                    Vector3 wrapSrc = cp2 + e1 * off.x + e2 * off.y + n2 * off.z;
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
                    f.gNrm[g2] = nl; f.gProt[g2] = prot; f.gDnRaw[g2] = dn;
                    f.gDisp[g2] = dl;
                }

                // driven-area seam polish: SAME sliders as the painted<->unpainted seam.
                // Smooths across the boundary band of the garment and cone-clamps the ramp
                // so the driven edge can't crease or pop through.
                if (settingsRef != null && f.gBand.Length > 0)
                {
                    int slv = Mathf.Clamp(Mathf.RoundToInt(settingsRef.seamLevel), 0, 200);
                    float srange = Mathf.Min(settingsRef.seamRange, SEAM_MAXR);
                    float smax = settingsRef.seamMaxStretch;
                    if ((slv > 0 || smax > 0.0001f) && srange > 0.0002f)
                    {
                        int active = f.gBand.Length;
                        for (int i = 0; i < f.gBand.Length; i++) if (f.gSeam[f.gBand[i]] > srange) { active = i; break; }
                        float invR = 1f / srange;
                        for (int pass = 0; pass < slv; pass++)
                        {
                            for (int i = 0; i < active; i++)
                            {
                                int gg = f.gBand[i];
                                float bw2 = 1f - f.gSeam[gg] * invR;
                                int[] nb = f.gAdj[gg];
                                if (bw2 <= 0f || nb.Length < 2) { f.gDisp2[gg] = f.gDisp[gg]; continue; }
                                Vector3 avg = Vector3.zero;
                                for (int j = 0; j < nb.Length; j++) avg += f.gDisp[nb[j]];
                                avg /= nb.Length;
                                f.gDisp2[gg] = Vector3.Lerp(f.gDisp[gg], avg, bw2);
                            }
                            for (int i = 0; i < active; i++) { int gg = f.gBand[i]; f.gDisp[gg] = f.gDisp2[gg]; }
                        }
                        if (smax > 0.0001f)
                        {
                            float slope = smax / Mathf.Max(srange, 0.0005f);
                            for (int i = 0; i < f.gBand.Length; i++)
                            {
                                int gg = f.gBand[i];
                                float allowed = f.gSeam[gg] * slope;
                                float dm = f.gDisp[gg].magnitude;
                                if (dm > allowed && dm > 1e-9f) f.gDisp[gg] *= allowed / dm;
                            }
                        }
                    }
                }

                // ---- CLOTH-SIDE ANTI-CLIP (the body is never touched) ----
                // Each group knows how far it rested OUTSIDE the body (gOff.z) and what the
                // full wrap would have done (gDnRaw). A garment may drift closer to the skin
                // than its rest gap, but never nearer than `minClear` — so the flesh cannot
                // emerge through it, and the fix is applied entirely to the garment.
                if (settingsRef != null && settingsRef.cageClothOutside)
                {
                    float minClear = Mathf.Clamp(settingsRef.cageMinClear, 0f, 0.05f);
                    for (int g2 = 0; g2 < G; g2++)
                    {
                        if (f.gIsFill[g2]) continue;   // no surface reference to clamp against
                        float allow = Mathf.Max(0f, Mathf.Abs(f.gOff[g2].z) - minClear);
                        float need = f.gDnRaw[g2] - allow;
                        float cur = Vector3.Dot(f.gDisp[g2], f.gNrm[g2]);
                        if (cur >= need) continue;
                        float add = Mathf.Min(need - cur, 0.03f);   // never teleport a vert
                        f.gDisp[g2] += f.gNrm[g2] * add;
                    }
                }

                // anti-clip floor: whatever the seam smoothing/clamps did above, the protected
                // outward component survives — a close-fitting cup can never be pierced.
                // The floor still obeys the seam RAMP law near the driven edge (a boundary
                // group snapping to full amplitude against undriven neighbours is a tear).
                float pSmax = settingsRef != null ? settingsRef.seamMaxStretch : 0f;
                float pSrange = settingsRef != null ? Mathf.Min(settingsRef.seamRange, SEAM_MAXR) : 0f;
                float pSlope = (pSmax > 0.0001f && pSrange > 0.0005f) ? pSmax / pSrange : -1f;
                for (int g2 = 0; g2 < G; g2++)
                {
                    float prot = f.gProt[g2];
                    if (prot <= 0f) continue;
                    if (pSlope > 0f) prot = Mathf.Min(prot, f.gSeam[g2] * pSlope);
                    float dn2 = Vector3.Dot(f.gDisp[g2], f.gNrm[g2]);
                    if (dn2 < prot) f.gDisp[g2] += f.gNrm[g2] * (prot - dn2);
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

                for (int g2 = 0; g2 < G; g2++)
                {
                    Vector3 dl = f.gDisp[g2];
                    int e = f.gMemStart[g2 + 1];
                    for (int m = f.gMemStart[g2]; m < e; m++) f.verts[f.gMem[m]] += dl;
                }
                f.display.SetVertices(f.verts);
                if (haveN) f.display.SetNormals(folNrmScratch);
                if (!f.boundsSet)
                { f.display.bounds = new Bounds(f.display.bounds.center, f.display.bounds.size + Vector3.one * 2f); f.boundsSet = true; }
            }
        }

        readonly List<Vector3> folNrmScratch = new List<Vector3>();

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
            folQueue.Clear(); folFailed.Clear();
            folRetryT = 0f; folRangeT = 0f;
            folCage = null; folGenActive = false;
            bindGrid = null; bindPts = null; bindTris = null;
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
        Transform FindChainSource()
        {
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

        public void Frame(float dt, int substeps, Vector3 worldDown, bool simEnabled)
        {
            if (!Alive) { return; }
            PollCageBuild();
            swDbg.Restart();

            // upstream appearing/disappearing: cheap re-check 1x/second for UPGRADES, but
            // re-chain INSTANTLY while unchained or the source died (fake-null after a
            // rebind destroys it) — waiting a whole second flashed two bodies per tweak
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
            swDbg.Restart();
            System.Array.Clear(disp, 0, disp.Length);

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
                UpdateColliderClouds();
                Vector3 localDown = go.transform.InverseTransformDirection(worldDown);
                float sdt = pdt / Mathf.Max(1, substeps);
                // dynamics substepped; collision field + output written ONCE per frame
                if (cage != null && cageSim != null)
                {
                    // physics on the uniform CAGE, result projected back onto the mesh.
                    // live-sync the tuning sliders onto the cage's cloned region first
                    if (cageSrc != null) SyncSolverParams(cageSim.cfg, cageSrc);
                    cage.InterpBaked(bakedVerts, bakedNormals);
                    for (int s = 0; s < substeps; s++)
                        cageSim.StepDynamics(cage.simBaked, cage.simNormals, sdt, localDown, go.transform);
                    System.Array.Clear(cage.simDisp, 0, cage.simDisp.Length);
                    cageSim.FieldAndWrite(cage.simBaked, cage.simNormals, cage.simDisp, pdt, SimsAsList(), go.transform);
                    // snapshot the field BEFORE the body's smoothing passes: the projection
                    // averaging that makes the body buttery also blurred the CLOTH's copy down
                    // to a fraction of its amplitude (measured 24-38% at projAvg 28), which is
                    // most of why garments looked like they weren't following
                    if (cageRaw == null || cageRaw.Length != cage.simDisp.Length) cageRaw = new Vector3[cage.simDisp.Length];
                    System.Array.Copy(cage.simDisp, cageRaw, cageRaw.Length);
                    if (settingsRef != null)
                    {
                        int ts = Mathf.Clamp(Mathf.RoundToInt(settingsRef.proxySmooth), 0, 400);
                        int av = Mathf.Clamp(Mathf.RoundToInt(settingsRef.projAvg), 0, 400);
                        if (ts > 0 || av > 0) cage.SmoothDisp(ts, av);
                        // 2nd-level squish: restore the sharp dent AFTER the smoothers
                        if (settingsRef.boostStrength > 0.001f || settingsRef.slapPower > 0.001f)
                        {
                            // capture the pre-boost state so the boost's contribution can be
                            // added to the CLOTH field too — contact is exactly when the body
                            // would otherwise punch through a garment
                            if (cagePreBoost == null || cagePreBoost.Length != cage.simDisp.Length)
                                cagePreBoost = new Vector3[cage.simDisp.Length];
                            System.Array.Copy(cage.simDisp, cagePreBoost, cagePreBoost.Length);
                            cage.ContactBoost(cageSim, go.transform, settingsRef.boostStrength,
                                Mathf.Clamp(Mathf.RoundToInt(settingsRef.boostSpread), 0, 60),
                                Mathf.Clamp(settingsRef.boostMax, 0.001f, 0.2f),
                                Mathf.Max(0f, settingsRef.slapSens), settingsRef.slapPower, pdt);
                            if (cageRaw != null && cageRaw.Length == cage.simDisp.Length)
                                for (int bi = 0; bi < cageRaw.Length; bi++)
                                    cageRaw[bi] += cage.simDisp[bi] - cagePreBoost[bi];
                        }
                    }
                    // follower timing mirror: cageOut must show the SAME held/lerp state the
                    // mesh's disp does, or clothing leads/lags the body by half a tick
                    if (cageOut == null || cageOut.Length != cage.simDisp.Length)
                    {
                        cageOut = new Vector3[cage.simDisp.Length];
                        cageHeld = new Vector3[cage.simDisp.Length];
                        cagePrev = new Vector3[cage.simDisp.Length];
                        cageHeldValid = false;
                    }
                    // Cloth field = the body's own (smoothed) field blended toward the raw
                    // one. Raw carries the full contact amplitude but also its sharp local
                    // spikes: replayed per weld-group on a garment those spikes SHRED it, so
                    // the default is the body's field exactly and sharpness is opt-in.
                    Vector3[] folSrc = cage.simDisp;
                    float sharp = settingsRef != null ? Mathf.Clamp01(settingsRef.cageFolSharp) : 0f;
                    if (sharp > 0.001f && cageRaw != null && cageRaw.Length == cage.simDisp.Length)
                    {
                        int folSm = settingsRef != null ? Mathf.Clamp(Mathf.RoundToInt(settingsRef.cageFolSmooth), 0, 60) : 4;
                        if (folSm > 0) cage.SmoothArray(cageRaw, folSm);
                        for (int ci = 0; ci < cageRaw.Length; ci++)
                            cageRaw[ci] = Vector3.Lerp(cage.simDisp[ci], cageRaw[ci], sharp);
                        folSrc = cageRaw;
                    }
                    if (halfRateLerp && cageHeldValid)
                    {
                        System.Array.Copy(cageHeld, cagePrev, cageHeld.Length);
                        System.Array.Copy(folSrc, cageHeld, cageHeld.Length);
                        for (int ci = 0; ci < cageOut.Length; ci++) cageOut[ci] = (cagePrev[ci] + cageHeld[ci]) * 0.5f;
                    }
                    else
                    {
                        System.Array.Copy(folSrc, cageHeld, cageHeld.Length);
                        System.Array.Copy(folSrc, cageOut, cageOut.Length);
                    }
                    cageHeldValid = true;

                    cage.Project(disp);
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
                    int slv = Mathf.Clamp(Mathf.RoundToInt(settingsRef.seamLevel), 0, 200);
                    if ((slv > 0 || settingsRef.seamMaxStretch > 0.0001f) && settingsRef.seamRange > 0.0002f)
                        ApplySeamSmoothing(disp, slv, settingsRef.seamRange, settingsRef.seamMaxStretch);
                }
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

            for (int i = 0; i < bakedVerts.Length; i++) bakedVerts[i] += disp[i];
            display.SetVertices(bakedVerts);
            display.SetNormals(bakedNormals);
            // fixed expanded bounds once — per-frame RecalculateBounds is a full-mesh scan
            if (!dispBoundsSet) { display.bounds = new Bounds(display.bounds.center, display.bounds.size + Vector3.one * 2f); dispBoundsSet = true; }
            if (overlayOn && overlayMode == 1) RefreshSharpColors();   // live jaggedness view
            // follower lifecycle runs HERE (post-output) so binds measure against the SAME
            // frame's surface (simBaked fresh, bakedVerts final) — no one-frame skew baked in
            UpdateFollowerLifecycle();
            UpdateFollowers();   // replay this frame's surface onto the driven meshes

            msSim = Mathf.Lerp(msSim, (float)swDbg.Elapsed.TotalMilliseconds, 0.08f);
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
            if (settingsRef != null && settingsRef.useRemesh > 0.5f && StartCageBuild()) return;
            BuildRegionSims();
        }

        void BuildRegionSims()
        {
            sims.Clear();
            for (int r = 0; r < cfg.regions.Count; r++)
            {
                SquishSim s = new SquishSim();
                s.Build(cfg.regions[r], display, weldOf, weldMembers);
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
                if (cfg.regions[r].enabled && cfg.regions[r].vertIndex.Count > 0) { src = cfg.regions[r]; break; }
            if (src == null) return false;
            float[] wRep = RemeshCage.UnionWeights(display.vertexCount, cfg.regions, weldOf);
            if (wRep == null) return false;
            RemeshCage c = new RemeshCage();
            c.logTag = "[Jello]";
            float L = Mathf.Clamp(settingsRef.remeshSize, 0.002f, 0.15f);
            int passes = Mathf.Clamp(Mathf.RoundToInt(settingsRef.remeshPasses), 1, 10);
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
                if (cfg.regions[r].enabled && cfg.regions[r].vertIndex.Count > 0) { src = cfg.regions[r]; break; }
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
                if (!cfg.regions[r].enabled || cfg.regions[r] == src) continue;
                for (int cc = 0; cc < cfg.regions[r].colliders.Count; cc++)
                {
                    SquishCollider col = cfg.regions[r].colliders[cc];
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
                SquishRegion reg = cfg.regions[r];
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
            for (int pass = 0; pass < passes; pass++)
            {
                for (int i = 0; i < active; i++)
                {
                    int gg = seamBand[i];
                    float bw = 1f - seamDist[gg] * invR;      // 1 at the seam, 0 at the band edge
                    if (bw <= 0f) { grpDisp2[gg] = grpDisp[gg]; continue; }
                    int[] nb = wAdj[gg];
                    if (nb.Length < 2) { grpDisp2[gg] = grpDisp[gg]; continue; }
                    Vector3 avg = Vector3.zero;
                    for (int j = 0; j < nb.Length; j++) avg += grpDisp[nb[j]];
                    avg /= nb.Length;
                    grpDisp2[gg] = Vector3.Lerp(grpDisp[gg], avg, bw);
                }
                for (int i = 0; i < active; i++) { int gg = seamBand[i]; grpDisp[gg] = grpDisp2[gg]; }
            }
            // scatter smoothed group displacement back to every render member (active band)
            for (int i = 0; i < active; i++)
            {
                int gg = seamBand[i];
                List<int> mem = weldMembers[gg];
                Vector3 d = grpDisp[gg];
                for (int m = 0; m < mem.Count; m++) disp[mem[m]] = d;
            }
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
                for (int i = 0; i < seamBand.Length; i++)
                {
                    int gg = seamBand[i];
                    float allowed = seamDist[gg] * slope;
                    List<int> mem = weldMembers[gg];
                    if (mem.Count == 0) continue;
                    Vector3 d = disp[mem[0]];
                    float dm = d.magnitude;
                    if (dm <= allowed || dm < 1e-9f) continue;
                    d *= allowed / dm;
                    for (int m = 0; m < mem.Count; m++) disp[mem[m]] = d;
                }
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
