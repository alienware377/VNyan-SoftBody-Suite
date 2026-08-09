using System.Collections.Generic;
using UnityEngine;

namespace SquishStudio
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
        public bool overlayOn;
        public float overlayOpacity = 0.75f;

        // ================= CLIP GUARD =================
        // Garments that cover this body's painted regions. Each painted body vertex binds
        // ONCE to the nearest garment triangle (barycentric + signed offset along the garment
        // normal). Every frame, at the FINAL write of the whole pipeline, any body vertex
        // that has reached or crossed its garment shell is pushed back inside. Because it
        // runs on the array that becomes the rendered mesh, nothing downstream can re-open
        // a poke-through, and it does not care how well the cloth tracks the body.
        class ClipTarget
        {
            public SkinnedMeshRenderer smr;
            public Mesh bake;                       // reused garment bake
            public MeshFilter follow;               // a deformer's display copy, if one drives it
            public Vector3[] gv, gn;                // garment verts / normals (current frame)
            public int[] gt;                        // garment triangles
            public int[] bV, bA, bB, bC;            // body vert -> garment tri verts
            public float[] bwA, bwB, bwC, bW;       // barycentric + guard weight (paint * rim fade)
            public float[] bRest;                   // signed distance at bind time (the fit to preserve)
            public bool bound;                      // bindings computed (binding is amortised)
            public int pen; public float penMax;    // measured penetration this window
        }
        readonly List<Vector3> clipScratch = new List<Vector3>();
        readonly List<ClipTarget> clipTargets = new List<ClipTarget>();
        bool clipBound; float clipBindT;
        int clipLastRange, clipLastClear;
        public static SquishSettings settingsRef;   // plugin-global settings (clip guard options)
        public static SquishConfig configRef;
        GameObject avatarRef;
        Vector3[] clipPush, clipPrev;               // per-frame correction (+ last frame, rate limit)

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

            go = new GameObject(smr.name + "_SquishProxy");
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
            Debug.Log("[Squish] attach '" + smr.name + "' verts=" + n
                + " lossyScale=" + smr.transform.lossyScale.ToString("0.###")
                + " bakeBounds=" + baked.bounds.size.ToString("0.###")
                + " smrLocalBounds=" + smr.localBounds.size.ToString("0.###"));

            avatarRef = avatar;
            // build sims
            sims.Clear();
            for (int r = 0; r < cfg.regions.Count; r++)
            {
                SquishSim s = new SquishSim();
                s.Build(cfg.regions[r], display, weldOf, weldMembers);
                BuildEvacClusters(s, cfg.regions[r]);
                ResolveRegionRefs(s, avatar, anim);
                sims.Add(s);
            }
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
                regBounds.Add(new Vector4(cen.x, cen.y, cen.z, Mathf.Sqrt(rr) + 0.14f));   // slack >= the sleep gate's pre-wake margin
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
                    Debug.Log("[Squish] built " + (all ? "(all meshes)" : kv.Key) + " capsule colliders");
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

        // Derive which bones "own" a painted region: accumulate each bone's skin weight
        // across the painted verts (scaled by paint weight), keep bones holding at least
        // `shareCutoff` of the total. Uses the FULL multi-weight arrays (not the legacy
        // 4-wide API) so >4-influence meshes count every influence. Strongest first.
        public List<string> DeriveRegionBones(SquishRegion region, float shareCutoff)
        {
            List<string> outNames = new List<string>();
            if (smr == null || smr.sharedMesh == null || region == null || region.vertIndex.Count == 0) return outNames;
            Mesh mesh = smr.sharedMesh;
            var bpv = mesh.GetBonesPerVertex();
            var all = mesh.GetAllBoneWeights();
            Dictionary<int, float> tally = new Dictionary<int, float>();
            float total = 0f;
            if (bpv.Length > 0)
            {
                // prefix offsets so the sparse painted verts can index their weight runs
                int[] start = new int[bpv.Length];
                int run = 0;
                for (int v = 0; v < bpv.Length; v++) { start[v] = run; run += bpv[v]; }
                for (int i = 0; i < region.vertIndex.Count; i++)
                {
                    int vi = region.vertIndex[i];
                    if (vi < 0 || vi >= bpv.Length) continue;
                    float pw = region.weight[i];
                    int s = start[vi], n = bpv[vi];
                    for (int j = 0; j < n; j++)
                    {
                        BoneWeight1 b1 = all[s + j];
                        float c = b1.weight * pw;
                        if (c <= 0f) continue;
                        float cur; tally.TryGetValue(b1.boneIndex, out cur); tally[b1.boneIndex] = cur + c;
                        total += c;
                    }
                }
            }
            else
            {
                BoneWeight[] bw = mesh.boneWeights;
                for (int i = 0; i < region.vertIndex.Count; i++)
                {
                    int vi = region.vertIndex[i];
                    if (vi < 0 || vi >= bw.Length) continue;
                    float pw = region.weight[i];
                    Acc(tally, bw[vi].boneIndex0, bw[vi].weight0 * pw);
                    Acc(tally, bw[vi].boneIndex1, bw[vi].weight1 * pw);
                    Acc(tally, bw[vi].boneIndex2, bw[vi].weight2 * pw);
                    Acc(tally, bw[vi].boneIndex3, bw[vi].weight3 * pw);
                }
                foreach (KeyValuePair<int, float> kv in tally) total += kv.Value;
            }
            if (total <= 0f) return outNames;
            List<KeyValuePair<int, float>> ranked = new List<KeyValuePair<int, float>>(tally);
            ranked.Sort(delegate(KeyValuePair<int, float> a, KeyValuePair<int, float> b) { return b.Value.CompareTo(a.Value); });
            for (int i = 0; i < ranked.Count; i++)
            {
                if (ranked[i].Value / total < shareCutoff) break;   // sorted, safe to stop
                int bi = ranked[i].Key;
                if (bi >= 0 && bi < smr.bones.Length && smr.bones[bi] != null) outNames.Add(smr.bones[bi].name);
            }
            return outNames;
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

        public void Detach()
        {
            CollectAsync();   // never tear arrays out from under a running worker
            RestoreEvacBones();
            if (wobbleMR != null) wobbleMR.enabled = true;   // hand rendering back to Wobble
            wobbleSrc = null; wobbleMR = null;
            // keep the original hidden if Wobble Studio still drives this mesh
            if (smr != null)
                smr.forceRenderingOff = FindChainSource() != null;   // any LIVE upstream proxy keeps it hidden
            if (go != null) { go.SetActive(false); Object.Destroy(go); }        // hide NOW (Destroy is end-of-frame)
            if (overlayGO != null) { overlayGO.SetActive(false); Object.Destroy(overlayGO); }
            if (baked != null) Object.Destroy(baked);
            if (display != null) Object.Destroy(display);
            if (colBakeScratch != null) Object.Destroy(colBakeScratch);
            ClipGuardInvalidate();   // destroys the per-garment bake meshes
            go = null; overlayGO = null; baked = null; display = null; smr = null; colBakeScratch = null;
            sims.Clear(); colMeshSmr.Clear(); colMeshCloud.Clear();
            for (int i = 0; i < dbgPool.Count; i++) if (dbgPool[i] != null) Object.Destroy(dbgPool[i].gameObject);
            dbgPool.Clear();
        }

        // Squish is the FINAL chain stage: take the most-processed upstream output
        // (Jello > SoftBody > Wobble) so the marshmallow squish lands on top of the jiggle
        Transform FindChainSource()
        {
            if (smr == null) return null;
            Transform t = smr.transform.Find(smr.name + "_JelloProxy");
            if (t != null && t.gameObject.activeSelf) return t;
            t = smr.transform.Find(smr.name + "_SoftBodyProxy");
            if (t != null && t.gameObject.activeSelf) return t;
            t = smr.transform.Find(smr.name + "_WobbleProxy");
            return (t != null && t.gameObject.activeSelf) ? t : null;   // pending-destroy corpses are inactive
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
            Debug.Log("[Squish] chaining to " + t.name + " output on '" + smr.name + "'");
        }

        // ---------- troubleshooting: F10 toggles collider draw + perf log ----------
        public static bool debugDraw;
        List<Transform> dbgPool = new List<Transform>();
        static Material dbgMat;
        float msBake, msSim, dbgLogT;
        bool dispBoundsSet;
        public static bool halfRate;                 // set by the plugin from settings
        public static bool halfRateLerp;
        public static bool asyncSim;
        bool frameFlip; Vector3[] heldDisp, heldPrev; bool heldValid;
        // async worker state: inputs captured main-side, worker writes asyncOut, main
        // applies it NEXT frame (1 frame of physics latency). Joined in Frame() before
        // anything touches sim arrays or clouds.
        readonly System.Threading.ManualResetEventSlim asyncDone = new System.Threading.ManualResetEventSlim(true);
        Vector3[] asyncBaked, asyncNormals, asyncOut;
        bool[] asyncAwake; bool asyncKicked, asyncBroken;
        float asyncPdt; Vector3 asyncLocalDown; int asyncSubsteps;

        void CollectAsync()
        {
            if (!asyncKicked) return;
            if (!asyncDone.Wait(100))
            {
                asyncBroken = true;
                Debug.LogWarning("[Squish] async physics worker timed out — reverting to synchronous");
            }
            asyncKicked = false;
            for (int r = 0; r < sims.Count; r++)
                if (sims[r].pendingBlewUp)
                {
                    sims[r].pendingBlewUp = false;
                    Debug.LogWarning("[Squish] region '" + sims[r].cfg.name + "' destabilised (NaN/huge offset) — state reset");
                }
        }

        void AsyncJob(object state)
        {
            try
            {
                float sdt = asyncPdt / Mathf.Max(1, asyncSubsteps);
                for (int r = 0; r < sims.Count; r++)
                {
                    SquishSim sim = sims[r];
                    if (sim.cfg == null || !sim.cfg.enabled) continue;
                    if (asyncAwake != null && r < asyncAwake.Length && asyncAwake[r])
                    {
                        for (int s2 = 0; s2 < asyncSubsteps; s2++)
                            sim.StepDynamics(asyncBaked, asyncNormals, sdt, asyncLocalDown, null);
                        sim.FieldAndWrite(asyncBaked, asyncNormals, asyncOut, asyncPdt, sims, null);
                    }
                    else sim.SleepFrame(asyncOut, asyncPdt);
                }
            }
            catch (System.Exception) { }
            finally { asyncDone.Set(); }
        }
        System.Diagnostics.Stopwatch swDbg = new System.Diagnostics.Stopwatch();

        // ---------- evacuation driver bones ----------
        // Bones dedicated to a region (breast bones etc.) get TRANSLATED away when a press
        // exceeds the dent limiter. Messy-rig rules: pick only bones whose influence is
        // mostly inside the region (>60%), then drive only the TOPMOST bone of each chain —
        // nested/overlapping children just ride along, so bones can never fight each other.
        // No scaling is applied to bones at all (squash lives in the mesh blob layer).
        void BuildEvacClusters(SquishSim sim, SquishRegion region)
        {
            sim.evacBones = null; sim.evacClusterCount = 0;
            sim.evacBones2 = null; sim.evacCluster2Count = 0;
            Mesh mesh = smr != null ? smr.sharedMesh : null;
            BoneWeight[] bw = mesh != null ? mesh.boneWeights : null;
            Transform[] bones = smr != null ? smr.bones : null;
            if (bw == null || bw.Length == 0 || bones == null || bones.Length == 0) return;
            int nb = bones.Length;
            float[] regW = new float[nb]; float[] totW = new float[nb];
            for (int i = 0; i < bw.Length; i++)
            {
                BoneWeight b4 = bw[i];
                if (b4.boneIndex0 >= 0 && b4.boneIndex0 < nb) totW[b4.boneIndex0] += b4.weight0;
                if (b4.boneIndex1 >= 0 && b4.boneIndex1 < nb) totW[b4.boneIndex1] += b4.weight1;
                if (b4.boneIndex2 >= 0 && b4.boneIndex2 < nb) totW[b4.boneIndex2] += b4.weight2;
                if (b4.boneIndex3 >= 0 && b4.boneIndex3 < nb) totW[b4.boneIndex3] += b4.weight3;
            }
            for (int v = 0; v < region.vertIndex.Count; v++)
            {
                int vi = region.vertIndex[v];
                if (vi >= bw.Length || region.weight[v] < 0.2f) continue;
                BoneWeight b4 = bw[vi];
                if (b4.boneIndex0 >= 0 && b4.boneIndex0 < nb) regW[b4.boneIndex0] += b4.weight0;
                if (b4.boneIndex1 >= 0 && b4.boneIndex1 < nb) regW[b4.boneIndex1] += b4.weight1;
                if (b4.boneIndex2 >= 0 && b4.boneIndex2 < nb) regW[b4.boneIndex2] += b4.weight2;
                if (b4.boneIndex3 >= 0 && b4.boneIndex3 < nb) regW[b4.boneIndex3] += b4.weight3;
            }
            float regTotal = 0f;
            for (int b = 0; b < nb; b++) regTotal += regW[b];
            if (regTotal < 1f) return;
            List<int> cand = new List<int>();
            for (int b = 0; b < nb; b++)
                if (bones[b] != null && regW[b] > regTotal * 0.03f && regW[b] > totW[b] * 0.6f)
                    cand.Add(b);
            List<Transform> roots = new List<Transform>();
            for (int ci = 0; ci < cand.Count; ci++)
            {
                int b = cand[ci];
                bool hasAnc = false;
                for (int ai = 0; ai < cand.Count && !hasAnc; ai++)
                {
                    if (cand[ai] == b) continue;
                    for (Transform pp = bones[b].parent; pp != null; pp = pp.parent)
                        if (pp == bones[cand[ai]]) { hasAnc = true; break; }
                }
                if (!hasAnc && !roots.Contains(bones[b])) roots.Add(bones[b]);
            }
            if (roots.Count == 0) return;
            sim.evacBones = roots.ToArray();
            sim.evacClusterCount = roots.Count;
            sim.evacClusterPos = new Vector3[roots.Count];
            sim.evacClusterDir = new Vector3[roots.Count];
            sim.evacClusterExc = new float[roots.Count];
            sim.evacCurW = new Vector3[roots.Count];
            sim.evacAppliedW = new Vector3[roots.Count];
            string[] names = new string[roots.Count];
            for (int i = 0; i < roots.Count; i++) names[i] = roots[i].name;
            Debug.Log("[Squish] evac driver bones for '" + region.name + "': " + string.Join(", ", names));
            // "evacuate ALL bones": every candidate that is NOT a chain root becomes a
            // second-set driver (nested breast bones etc.). Always built — the slider
            // gates the gain live, so 0 keeps today's exact behaviour with no rebuild.
            List<Transform> kids = new List<Transform>();
            for (int ci = 0; ci < cand.Count; ci++)
                if (!roots.Contains(bones[cand[ci]]) && !kids.Contains(bones[cand[ci]]))
                    kids.Add(bones[cand[ci]]);
            if (kids.Count > 0)
            {
                sim.evacBones2 = kids.ToArray();
                sim.evacCluster2Count = kids.Count;
                sim.evacCluster2Pos = new Vector3[kids.Count];
                sim.evacCluster2Dir = new Vector3[kids.Count];
                sim.evacCluster2Exc = new float[kids.Count];
                sim.evacCur2W = new Vector3[kids.Count];
                sim.evacApplied2W = new Vector3[kids.Count];
                string[] kn = new string[kids.Count];
                for (int i = 0; i < kids.Count; i++) kn[i] = kids[i].name;
                Debug.Log("[Squish] evac CHILD bones for '" + region.name + "': " + string.Join(", ", kn));
            }
        }

        // per frame BEFORE baking: undo last frame's offsets, spring toward the sim's
        // latest per-cluster targets, apply. Purely additive translation per frame, so it
        // composes with animation/tracking and never accumulates.
        void ApplyEvacBones(float dt)
        {
            float ease = 1f - Mathf.Exp(-8f * dt);
            for (int s = 0; s < sims.Count; s++)
            {
                SquishSim sim = sims[s];
                if (sim.evacBones == null) continue;
                float gain = Mathf.Clamp(sim.cfg.evacBone, 0f, 2f);
                for (int k = 0; k < sim.evacBones.Length; k++)
                {
                    Transform b = sim.evacBones[k];
                    if (b == null) continue;
                    // undo in PARENT-LOCAL space. A world-space undo leaks (R−I)·offset into
                    // localPosition every frame an ancestor ROTATES (dance anims) — offsets
                    // ratcheted up over loops and the breasts slowly extended. Local
                    // add/subtract is exact under any ancestor motion.
                    b.localPosition -= sim.evacAppliedW[k];
                    sim.evacClusterPos[k] = go.transform.InverseTransformPoint(b.position);
                    Vector3 tgtL = sim.cfg.enabled ? sim.evacClusterDir[k] * (sim.evacClusterExc[k] * gain * 3f) : Vector3.zero;
                    float tm = tgtL.magnitude;
                    if (tm > 0.2f) tgtL *= 0.2f / tm;
                    sim.evacCurW[k] = Vector3.Lerp(sim.evacCurW[k], go.transform.TransformVector(tgtL), ease);
                    Vector3 loc = b.parent != null ? b.parent.InverseTransformVector(sim.evacCurW[k]) : sim.evacCurW[k];
                    b.localPosition += loc;
                    sim.evacAppliedW[k] = loc;   // parent-local from here on
                }
                // CHILD set second (roots first: children inherit the parent's fresh shift
                // through the hierarchy, then add their own on top). World-space per-bone
                // undo/apply composes exactly through nesting; feedback keeps it stable.
                if (sim.evacBones2 != null)
                {
                    float gain2 = gain * Mathf.Clamp(sim.cfg.evacAllBones, 0f, 2f);
                    for (int k = 0; k < sim.evacBones2.Length; k++)
                    {
                        Transform b = sim.evacBones2[k];
                        if (b == null) continue;
                        b.localPosition -= sim.evacApplied2W[k];   // parent-local undo (see roots)
                        sim.evacCluster2Pos[k] = go.transform.InverseTransformPoint(b.position);
                        Vector3 tgtL = (sim.cfg.enabled && gain2 > 0.001f)
                            ? sim.evacCluster2Dir[k] * (sim.evacCluster2Exc[k] * gain2 * 3f) : Vector3.zero;
                        float tm = tgtL.magnitude;
                        if (tm > 0.2f) tgtL *= 0.2f / tm;
                        sim.evacCur2W[k] = Vector3.Lerp(sim.evacCur2W[k], go.transform.TransformVector(tgtL), ease);
                        Vector3 loc = b.parent != null ? b.parent.InverseTransformVector(sim.evacCur2W[k]) : sim.evacCur2W[k];
                        b.localPosition += loc;
                        sim.evacApplied2W[k] = loc;   // parent-local
                    }
                }
            }
        }

        void RestoreEvacBones()
        {
            for (int s = 0; s < sims.Count; s++)
            {
                SquishSim sim = sims[s];
                if (sim.evacBones == null) continue;
                for (int k = 0; k < sim.evacBones.Length; k++)
                {
                    if (sim.evacBones[k] != null) sim.evacBones[k].localPosition -= sim.evacAppliedW[k];
                    sim.evacAppliedW[k] = Vector3.zero; sim.evacCurW[k] = Vector3.zero;
                }
                if (sim.evacBones2 != null)
                    for (int k = 0; k < sim.evacBones2.Length; k++)
                    {
                        if (sim.evacBones2[k] != null) sim.evacBones2[k].localPosition -= sim.evacApplied2W[k];
                        sim.evacApplied2W[k] = Vector3.zero; sim.evacCur2W[k] = Vector3.zero;
                    }
            }
        }

        public void Frame(float dt, int substeps, Vector3 worldDown, bool simEnabled)
        {
            if (!Alive) { return; }
            swDbg.Restart();
            CollectAsync();   // join last frame's worker before touching sim state
            if (simEnabled) ApplyEvacBones(dt);   // move driver bones BEFORE baking

            // FINAL stage: we own the rendered image — assert it EVERY frame (an upstream
            // detach re-shows the original for its own fallback; that must never win)
            smr.forceRenderingOff = true;
            // re-chain INSTANTLY while unchained/lost; 1x/second only for upgrade checks
            if (wobbleSrc == null) FindWobbleProxy();
            if (++chainCheck >= 60)
            {
                chainCheck = 0;
                Transform best = FindChainSource();
                if (best == null) { wobbleSrc = null; wobbleMR = null; }
                else if (wobbleSrc == null || wobbleSrc.transform != best) FindWobbleProxy();
            }

            if (wobbleSrc != null)
            {
                if (wobbleMR != null && wobbleMR.enabled) wobbleMR.enabled = false;
                wobbleSrc.sharedMesh.GetVertices(scratch);   // base = Wobble's jiggled output
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
                Debug.LogWarning("[Squish] '" + smr.name + "' vertex count changed ("
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

        float dtLast = 0.016f;

        void RunSim(float dt, int substeps, Vector3 worldDown, bool simEnabled)
        {
            msBake = Mathf.Lerp(msBake, (float)swDbg.Elapsed.TotalMilliseconds, 0.08f);
            swDbg.Restart();
            System.Array.Clear(disp, 0, disp.Length);

            frameFlip = !frameFlip;
            bool useAsync = asyncSim && !debugDraw && !asyncBroken && simEnabled;
            bool hrAny = !useAsync && (halfRate || halfRateLerp);
            if (useAsync)
            {
                // ASYNC: show LAST frame's worker result (1 frame of physics latency),
                // capture this frame's inputs, kick the next job. Worker was joined at
                // Frame() start, so clouds and sim arrays are safe to touch here.
                if (asyncOut != null && asyncOut.Length == disp.Length)
                    System.Array.Copy(asyncOut, disp, disp.Length);
                UpdateColliderClouds();
                Vector3 aDown = go.transform.InverseTransformDirection(worldDown);
                if (asyncBaked == null || asyncBaked.Length != bakedVerts.Length)
                { asyncBaked = new Vector3[bakedVerts.Length]; asyncNormals = new Vector3[bakedVerts.Length]; }
                if (asyncOut == null || asyncOut.Length != disp.Length) asyncOut = new Vector3[disp.Length];
                System.Array.Copy(bakedVerts, asyncBaked, bakedVerts.Length);
                System.Array.Copy(bakedNormals, asyncNormals, bakedNormals.Length);
                if (asyncAwake == null || asyncAwake.Length != sims.Count) asyncAwake = new bool[sims.Count];
                for (int r = 0; r < sims.Count; r++)
                {
                    SquishSim sim = sims[r];
                    sim.CaptureFrameInputs(go.transform);
                    asyncAwake[r] = sim.cfg.enabled && sim.CheckAwake(bakedVerts, go.transform, SimsAsList(), dt);
                }
                asyncPdt = dt; asyncLocalDown = aDown; asyncSubsteps = substeps;
                System.Array.Clear(asyncOut, 0, asyncOut.Length);
                asyncDone.Reset(); asyncKicked = true;
                System.Threading.ThreadPool.QueueUserWorkItem(AsyncJob);
            }
            else if (simEnabled && hrAny && !frameFlip && heldValid && heldDisp != null && heldDisp.Length == disp.Length)
            {
                // HELD frame: reuse last computed displacement (fresh skinning still flows
                // through — only the offset field is one frame old)
                System.Array.Copy(heldDisp, disp, disp.Length);
            }
            else if (simEnabled)
            {
                float pdt = hrAny ? Mathf.Min(dt * 2f, 0.05f) : dt;   // physics dt spans the held frame
                UpdateColliderClouds();
                Vector3 localDown = go.transform.InverseTransformDirection(worldDown);
                float sdt = pdt / Mathf.Max(1, substeps);
                // dynamics substepped; collision field + output written ONCE per frame.
                // SLEEP GATE: the expensive field pipeline only runs while a collider is
                // near (12cm pre-wake margin) or residual dent/evac energy exists —
                // measured 17-19ms/frame ALWAYS without it, ~0 idle with it.
                for (int r = 0; r < sims.Count; r++)
                {
                    SquishSim sim = sims[r];
                    if (!sim.cfg.enabled) continue;
                    sim.CaptureFrameInputs(go.transform);
                    if (debugDraw || sim.CheckAwake(bakedVerts, go.transform, SimsAsList(), pdt))
                    {
                        for (int s = 0; s < substeps; s++)
                            sim.StepDynamics(bakedVerts, bakedNormals, sdt, localDown, go.transform);
                        sim.FieldAndWrite(bakedVerts, bakedNormals, disp, pdt, SimsAsList(), go.transform);
                    }
                    else sim.SleepFrame(disp, pdt);
                    if (sim.pendingBlewUp)
                    {
                        sim.pendingBlewUp = false;
                        Debug.LogWarning("[Squish] region '" + sim.cfg.name + "' destabilised (NaN/huge offset) — state reset");
                    }
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
            // FINAL guarantee, on the very array that becomes the rendered body: the flesh
            // cannot cross the clothes covering it, no matter what any earlier stage did.
            dtLast = dt;
            EnforceClipGuard();
            display.SetVertices(bakedVerts);
            display.SetNormals(bakedNormals);
            // fixed expanded bounds once — per-frame RecalculateBounds is a full-mesh scan
            if (!dispBoundsSet) { display.bounds = new Bounds(display.bounds.center, display.bounds.size + Vector3.one * 2f); dispBoundsSet = true; }

            msSim = Mathf.Lerp(msSim, (float)swDbg.Elapsed.TotalMilliseconds, 0.08f);
            DrawDebug();
            if (debugDraw && (dbgLogT += dt) > 2f)
            {
                dbgLogT = 0f;
                int act = 0; foreach (KeyValuePair<string, MeshColliderCloud> kv in colMeshCloud) act += kv.Value.nd;
                Debug.Log("[Squish] perf '" + (smr != null ? smr.name : "?") + "': bake+chain=" + msBake.ToString("0.00")
                    + "ms sim+write=" + msSim.ToString("0.00") + "ms activeCapsules=" + act);
                // clip guard telemetry: the SYMPTOM itself (how often and how deep the flesh
                // reached the cloth before correction) — the only number that says "it worked"
                for (int ci = 0; ci < clipTargets.Count; ci++)
                {
                    ClipTarget ct = clipTargets[ci];
                    if (ct.bV == null) continue;
                    Debug.Log("[Squish] clip '" + (ct.smr != null ? ct.smr.name : "?") + "': bound=" + ct.bV.Length
                        + " corrected=" + ct.pen + " maxDepth=" + (ct.penMax * 1000f).ToString("0.0") + "mm"
                        + (ct.follow != null ? " (vs deformed cloth)" : " (vs skinned cloth)"));
                    ct.pen = 0; ct.penMax = 0f;
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

        public void LogColliderInfo()
        {
            foreach (KeyValuePair<string, MeshColliderCloud> kv in colMeshCloud)
                Debug.Log("[Squish] colliders from '" + kv.Key + "': " + kv.Value.buildInfo);
        }

        // F11: dump displaced mesh + rest mesh + per-node solver fields for offline analysis
        public void DumpDebug()
        {
            if (!Alive) return;
            try
            {
                string dir = System.IO.Path.Combine(Application.persistentDataPath, "squishdebug");
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
                Debug.Log("[Squish] debug dump written to " + dir);
            }
            catch (System.Exception e) { Debug.LogWarning("[Squish] dump failed: " + e.Message); }
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
                overlayGO = new GameObject(smr.name + "_SquishOverlay");
                overlayGO.transform.SetParent(go.transform, false);
                MeshFilter omf = overlayGO.AddComponent<MeshFilter>();
                omf.sharedMesh = display;                     // same live mesh
                overlayMR = overlayGO.AddComponent<MeshRenderer>();
                Shader sh = Shader.Find("Sprites/Default");   // vertex colors + Cull Off (double-sided)
                if (sh == null) sh = Shader.Find("Unlit/Color");
                overlayMat = new Material(sh);
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
        // Fresh-bakes the source SMR rather than reading bakedVerts: by this point in the
        // frame bakedVerts holds the SIM-DISPLACED surface (jiggle/sag/chained wobble),
        // while TransferWeights raw-bakes the target — sampling the same raw skinned pose
        // on both sides keeps overlapping surfaces aligned within the search radius.
        public List<Vector4> RegionWorldSamples(SquishRegion region)
        {
            List<Vector4> pts = new List<Vector4>();
            if (!Alive || region == null) return pts;
            Mesh tmp = new Mesh();
            smr.BakeMesh(tmp);
            Vector3[] verts = tmp.vertices;
            Transform tr = smr.transform;
            for (int i = 0; i < region.vertIndex.Count; i++)
            {
                int vi = region.vertIndex[i];
                if (vi >= verts.Length) continue;
                Vector3 wp = tr.TransformPoint(verts[vi]);
                pts.Add(new Vector4(wp.x, wp.y, wp.z, region.weight[i]));
            }
            Object.Destroy(tmp);
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
            sims.Clear();
            for (int r = 0; r < cfg.regions.Count; r++)
            {
                SquishSim s = new SquishSim();
                s.Build(cfg.regions[r], display, weldOf, weldMembers);
                BuildEvacClusters(s, cfg.regions[r]);
                ResolveRegionRefs(s, avatar, anim);
                sims.Add(s);
            }
            ResolveColliderMeshes(avatar);
        }

        // ---------- vertex welding (dupes along UV seams / hard edges) ----------
        // Game meshes duplicate vertices wherever UVs or normals split; simulating the
        // copies independently TEARS the surface. Weld by position so the sim runs one
        // node per unique point and every duplicate moves identically.
        // ---------------- CLIP GUARD ----------------
        static Vector3 ClosestOnTri(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
            Vector3 cp2 = p - c;
            float d5 = Vector3.Dot(ab, cp2), d6 = Vector3.Dot(ac, cp2);
            if (d6 >= 0f && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
                return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            float denom = 1f / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }

        static Vector3 Bary(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 v0 = b - a, v1 = c - a, v2 = p - a;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
            float den = d00 * d11 - d01 * d01;
            if (Mathf.Abs(den) < 1e-16f) return new Vector3(1f, 0f, 0f);
            float v = (d11 * d20 - d01 * d21) / den;
            float w = (d00 * d21 - d01 * d20) / den;
            return new Vector3(1f - v - w, v, w);
        }

        public void ClipGuardInvalidate()
        {
            clipBound = false;
            for (int i = 0; i < clipTargets.Count; i++)
                if (clipTargets[i].bake != null) { Object.Destroy(clipTargets[i].bake); clipTargets[i].bake = null; }
            clipTargets.Clear();
            clipPrev = null;
        }

        // Which meshes count as garments over this body: every other skinned mesh that is
        // visible and is not itself a configured soft-body mesh.
        Bounds paintBoundsWorld;
        float[] clipPaint;      // max paint weight per body vert (weld-aware)

        // union paint weight per body vert, taken as the MAX over each weld group so seam
        // duplicates can't leave a group unprotected; plus a world AABB of the painted flesh
        void BuildClipPaint(float range)
        {
            int n = bakedVerts.Length;
            if (clipPaint == null || clipPaint.Length != n) clipPaint = new float[n];
            else System.Array.Clear(clipPaint, 0, n);
            for (int r = 0; r < cfg.regions.Count; r++)
            {
                SquishRegion reg = cfg.regions[r];
                if (!reg.enabled) continue;
                for (int i = 0; i < reg.vertIndex.Count; i++)
                {
                    int vi = reg.vertIndex[i];
                    if (vi >= 0 && vi < n && reg.weight[i] > clipPaint[vi]) clipPaint[vi] = reg.weight[i];
                }
            }
            for (int g = 0; g < weldMembers.Length; g++)
            {
                List<int> mem = weldMembers[g];
                float mx = 0f;
                for (int m = 0; m < mem.Count; m++) if (clipPaint[mem[m]] > mx) mx = clipPaint[mem[m]];
                if (mx <= 0f) continue;
                for (int m = 0; m < mem.Count; m++) clipPaint[mem[m]] = mx;
            }
            bool first = true;
            Bounds b = new Bounds(Vector3.zero, Vector3.zero);
            Transform tr = go.transform;
            for (int i = 0; i < n; i++)
            {
                if (clipPaint[i] <= 0.02f) continue;
                Vector3 wp = tr.TransformPoint(bakedVerts[i]);
                if (first) { b = new Bounds(wp, Vector3.zero); first = false; } else b.Encapsulate(wp);
            }
            b.Expand(range * 2f);
            paintBoundsWorld = b;
        }

        void CollectClipTargets()
        {
            clipTargets.Clear();
            if (avatarRef == null || smr == null) return;
            SkinnedMeshRenderer[] rends = avatarRef.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < rends.Length; i++)
            {
                SkinnedMeshRenderer r = rends[i];
                if (r == null || r == smr || r.sharedMesh == null) continue;
                if (!r.gameObject.activeInHierarchy) continue;
                if (!r.enabled && !r.forceRenderingOff) continue;   // genuinely hidden outfit piece
                bool isBody = false;
                if (configRef != null && configRef.meshes != null)
                    for (int m = 0; m < configRef.meshes.Count; m++)
                        if (configRef.meshes[m] != null && configRef.meshes[m].enabled &&
                            configRef.meshes[m].mesh == r.name) { isBody = true; break; }
                if (isBody) continue;
                // any studio's body proxy lives on the mesh it deforms — that's a body, not cloth
                if (r.transform.Find(r.name + "_SquishProxy") != null ||
                    r.transform.Find(r.name + "_JelloProxy") != null ||
                    r.transform.Find(r.name + "_WobbleProxy") != null ||
                    r.transform.Find(r.name + "_SoftBodyProxy") != null) continue;
                // cheap reject: nothing near the painted flesh (hair, shoes, props stay free)
                if (!r.bounds.Intersects(paintBoundsWorld)) continue;
                ClipTarget t = new ClipTarget();
                t.smr = r; t.bake = new Mesh(); t.bake.MarkDynamic();
                clipTargets.Add(t);
            }
        }

        // Refresh a garment's current surface: prefer a deformer's display copy (so the guard
        // measures against the cloth the viewer actually sees), else its own skinned bake.
        // Everything is expressed in THIS proxy's local space.
        bool RefreshClipTarget(ClipTarget t)
        {
            if (t.smr == null || go == null) return false;
            if (t.follow == null)
            {
                Transform ft = t.smr.transform.Find(t.smr.name + "_JelloFollow");
                if (ft != null && ft.gameObject.activeSelf) t.follow = ft.GetComponent<MeshFilter>();
            }
            else if (t.follow.gameObject == null || !t.follow.gameObject.activeSelf) t.follow = null;

            Mesh src;
            Transform space;
            if (t.follow != null && t.follow.sharedMesh != null)
            { src = t.follow.sharedMesh; space = t.follow.transform; }
            else
            {
                t.smr.BakeMesh(t.bake);
                src = t.bake; space = t.smr.transform;
            }
            src.GetVertices(clipScratch);
            int n = clipScratch.Count;
            if (n < 3) return false;
            if (t.gv == null || t.gv.Length != n) { t.gv = new Vector3[n]; t.gn = new Vector3[n]; t.bV = null; }
            Matrix4x4 M = go.transform.worldToLocalMatrix * space.localToWorldMatrix;
            for (int i = 0; i < n; i++) t.gv[i] = M.MultiplyPoint3x4(clipScratch[i]);
            src.GetNormals(clipScratch);
            if (clipScratch.Count == n) for (int i = 0; i < n; i++) t.gn[i] = M.MultiplyVector(clipScratch[i]);
            if (t.gt == null || t.gt.Length == 0) t.gt = src.triangles;
            return true;
        }

        // Bind every painted body vertex to its nearest garment triangle (one-time, per
        // garment). Spatial hash over garment tris; rim triangles fade out so the guard can't
        // build a ridge at a hem.
        void BindClipTarget(ClipTarget t, float range, float rimFade)
        {
            if (!RefreshClipTarget(t) || t.gt.Length < 3) return;

            float cell = Mathf.Max(0.01f, range);
            Dictionary<long, List<int>> hash = new Dictionary<long, List<int>>();
            for (int tri = 0; tri < t.gt.Length; tri += 3)
            {
                Vector3 c = (t.gv[t.gt[tri]] + t.gv[t.gt[tri + 1]] + t.gv[t.gt[tri + 2]]) / 3f;
                long k = Key(Mathf.FloorToInt(c.x / cell), Mathf.FloorToInt(c.y / cell), Mathf.FloorToInt(c.z / cell));
                List<int> l; if (!hash.TryGetValue(k, out l)) { l = new List<int>(); hash[k] = l; }
                l.Add(tri);
            }
            // rim detection: an edge used by a single triangle is a boundary edge
            Dictionary<long, int> edge = new Dictionary<long, int>();
            for (int tri = 0; tri < t.gt.Length; tri += 3)
                for (int e = 0; e < 3; e++)
                {
                    int v0 = t.gt[tri + e], v1 = t.gt[tri + (e + 1) % 3];
                    long ek = v0 < v1 ? ((long)v0 << 32) | (uint)v1 : ((long)v1 << 32) | (uint)v0;
                    int c2; edge.TryGetValue(ek, out c2); edge[ek] = c2 + 1;
                }
            List<Vector3> rimPts = new List<Vector3>();
            {
                HashSet<int> rim = new HashSet<int>();
                foreach (KeyValuePair<long, int> kv in edge)
                    if (kv.Value == 1) { rim.Add((int)(kv.Key >> 32)); rim.Add((int)(kv.Key & 0xFFFFFFFF)); }
                foreach (int rv in rim) if (rv >= 0 && rv < t.gv.Length) rimPts.Add(t.gv[rv]);
            }
            float[] paint = clipPaint;

            List<int> V = new List<int>(), A = new List<int>(), B = new List<int>(), C = new List<int>();
            List<float> WA = new List<float>(), WB = new List<float>(), WC = new List<float>(), GW = new List<float>();
            List<float> RS = new List<float>();
            float r2 = range * range;
            for (int vi = 0; vi < bakedVerts.Length; vi++)
            {
                if (paint[vi] <= 0.02f) continue;
                if (weldMembers[weldOf[vi]][0] != vi) continue;   // one binding per weld group
                // measure the fit against the CLEAN skinned body: bakedVerts already carries
                // this frame's displacement, so binding mid-squish would freeze the dent in
                Vector3 p = bakedVerts[vi] - disp[vi];
                int cx = Mathf.FloorToInt(p.x / cell), cy = Mathf.FloorToInt(p.y / cell), cz = Mathf.FloorToInt(p.z / cell);
                float best = float.MaxValue; int bt = -1; Vector3 bcp = p;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            List<int> l;
                            if (!hash.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out l)) continue;
                            for (int j = 0; j < l.Count; j++)
                            {
                                int tri = l[j];
                                Vector3 cp = ClosestOnTri(p, t.gv[t.gt[tri]], t.gv[t.gt[tri + 1]], t.gv[t.gt[tri + 2]]);
                                float d = (p - cp).sqrMagnitude;
                                if (d < best) { best = d; bt = tri; bcp = cp; }
                            }
                        }
                if (bt < 0 || best > r2) continue;
                Vector3 a = t.gv[t.gt[bt]], b = t.gv[t.gt[bt + 1]], c3 = t.gv[t.gt[bt + 2]];
                Vector3 bar = Bary(bcp, a, b, c3);
                Vector3 nRest = t.gn[t.gt[bt]] * bar.x + t.gn[t.gt[bt + 1]] * bar.y + t.gn[t.gt[bt + 2]] * bar.z;
                float nrm2 = nRest.magnitude;
                if (nrm2 < 1e-6f) continue;
                nRest /= nrm2;
                // ORIENTATION: a garment's stored normals may point at the body (modelled
                // thickness, inner shells, flipped/mirrored normals). Canonicalize against the
                // BODY's own outward normal so "+N" always means "out of the flesh" — without
                // this the constraint inverts and drags flesh INTO the cloth.
                if (Vector3.Dot(nRest, bakedNormals[vi]) < 0f) nRest = -nRest;
                float restSd = Vector3.Dot(p - bcp, nRest);
                // only guard flesh that rests INSIDE this garment; skin sitting outside the
                // shell (next to a hem, through a cut-out) must stay free to move
                if (restSd > -0.0002f) continue;

                float w = paint[vi];
                // rim fade: taper smoothly with distance to the garment's hem so the guard
                // can't build a ridge where the cloth ends
                if (rimFade > 0.0005f && rimPts.Count > 0)
                {
                    float dr2 = float.MaxValue;
                    for (int q = 0; q < rimPts.Count; q++)
                    {
                        float dd = (bcp - rimPts[q]).sqrMagnitude;
                        if (dd < dr2) dr2 = dd;
                    }
                    float u = Mathf.Clamp01(Mathf.Sqrt(dr2) / rimFade);
                    w *= u * u * (3f - 2f * u);
                }
                V.Add(vi); A.Add(t.gt[bt]); B.Add(t.gt[bt + 1]); C.Add(t.gt[bt + 2]);
                WA.Add(bar.x); WB.Add(bar.y); WC.Add(bar.z); GW.Add(w); RS.Add(restSd);
            }
            t.bV = V.ToArray(); t.bA = A.ToArray(); t.bB = B.ToArray(); t.bC = C.ToArray();
            t.bwA = WA.ToArray(); t.bwB = WB.ToArray(); t.bwC = WC.ToArray(); t.bW = GW.ToArray();
            t.bRest = RS.ToArray();
            t.bound = true;
            if (t.bV.Length > 0)
                Debug.Log("[Squish] clip guard '" + smr.name + "' vs '" + t.smr.name + "': "
                    + t.bV.Length + " body verts bound (range " + range.ToString("0.000") + " m"
                    + (t.follow != null ? ", measuring the deformed cloth" : ", measuring the skinned cloth") + ")");
        }

        // The guarantee: push any body vertex that reached its garment shell back inside.
        void EnforceClipGuard()
        {
            if (settingsRef == null || !settingsRef.clipGuard) { if (clipBound) ClipGuardInvalidate(); return; }
            if (bakedVerts == null || weldOf == null) return;
            float range = Mathf.Clamp(settingsRef.clipRange, 0.005f, 0.3f);
            float clear = Mathf.Clamp(settingsRef.clipClearance, 0f, 0.05f);
            float strength = Mathf.Clamp01(settingsRef.clipStrength);
            float rimFade = Mathf.Max(0f, settingsRef.clipRimFade);
            // re-bind when the shape of the problem changes (slider moves are quantised so a
            // drag doesn't rebind every tick)
            int qr = Mathf.RoundToInt(range * 1000f), qc = Mathf.RoundToInt(clear * 1000f);
            if (clipBound && (qr != clipLastRange || qc != clipLastClear)) { clipBindT = 0.4f; clipLastRange = qr; clipLastClear = qc; }
            if (clipBindT > 0f) { clipBindT -= Time.deltaTime; if (clipBindT <= 0f) ClipGuardInvalidate(); }
            if (!clipBound)
            {
                BuildClipPaint(range);
                CollectClipTargets();
                clipLastRange = qr; clipLastClear = qc;
                clipBound = true;
            }
            // bind at most ONE garment per frame: binding several thousand verts against
            // several meshes in a single frame is a visible hitch
            for (int i = 0; i < clipTargets.Count; i++)
                if (!clipTargets[i].bound) { BindClipTarget(clipTargets[i], range, rimFade); break; }

            if (clipPush == null || clipPush.Length != bakedVerts.Length)
            { clipPush = new Vector3[bakedVerts.Length]; clipPrev = new Vector3[bakedVerts.Length]; }
            else System.Array.Clear(clipPush, 0, clipPush.Length);
            if (clipPrev == null || clipPrev.Length != clipPush.Length) clipPrev = new Vector3[clipPush.Length];
            bool any = false;
            float maxCorr = Mathf.Max(0.005f, range);   // never teleport a vertex

            for (int ti = clipTargets.Count - 1; ti >= 0; ti--)
            {
                ClipTarget t = clipTargets[ti];
                if (t.smr == null) { clipTargets.RemoveAt(ti); continue; }
                if (!t.bound || t.bV == null || t.bV.Length == 0) continue;
                int hadVerts = t.gv != null ? t.gv.Length : 0;
                if (!RefreshClipTarget(t)) continue;
                if (t.gv.Length != hadVerts || t.bV == null) { ClipGuardInvalidate(); return; }   // outfit swapped

                for (int k = 0; k < t.bV.Length; k++)
                {
                    int vi = t.bV[k];
                    int ia = t.bA[k], ib = t.bB[k], ic = t.bC[k];
                    Vector3 P = t.gv[ia] * t.bwA[k] + t.gv[ib] * t.bwB[k] + t.gv[ic] * t.bwC[k];
                    Vector3 N = t.gn[ia] * t.bwA[k] + t.gn[ib] * t.bwB[k] + t.gn[ic] * t.bwC[k];
                    float nm = N.magnitude;
                    if (nm < 1e-6f) continue;
                    N /= nm;
                    // signed distance along the garment's outward normal, compared to the
                    // RELATIONSHIP AT BIND TIME: the flesh may never come closer to the cloth
                    // than it rested (minus an optional extra margin). At rest this is a no-op;
                    // it only bites when a sim stage pushes the flesh toward/through the cloth.
                    if (Vector3.Dot(N, bakedNormals[vi]) < 0f) N = -N;   // same canonicalisation as bind
                    float sd = Vector3.Dot(bakedVerts[vi] - P, N);
                    float limit = t.bRest[k] - clear;
                    if (sd <= limit) continue;
                    float depth = sd - limit;
                    if (depth > t.penMax) t.penMax = depth;
                    t.pen++;
                    // CLAMP rather than abandon: an absolute give-up test switched the guard
                    // off exactly when penetration was deepest, which popped
                    if (depth > maxCorr) depth = maxCorr;
                    Vector3 push = N * (-depth * t.bW[k] * strength);
                    if (push.sqrMagnitude > clipPush[vi].sqrMagnitude) clipPush[vi] = push;   // deepest garment wins
                    any = true;
                }
            }
            // rate limit: ease each correction toward its target instead of snapping, so a
            // vertex entering or leaving the guard can't pop for a frame
            float rate = Mathf.Clamp01(12f * Mathf.Max(dtLast, 0.001f));
            for (int i = 0; i < clipPush.Length; i++)
            {
                if (clipPush[i].sqrMagnitude < 1e-12f && clipPrev[i].sqrMagnitude < 1e-12f) continue;
                clipPrev[i] = Vector3.Lerp(clipPrev[i], clipPush[i], rate);
                if (clipPrev[i].sqrMagnitude > 1e-12f) any = true;
            }
            if (!any) return;
            // scatter over weld groups so UV seams can't tear
            for (int g = 0; g < weldMembers.Length; g++)
            {
                List<int> mem = weldMembers[g];
                if (mem.Count == 0) continue;
                Vector3 d = clipPrev[mem[0]];
                if (d.sqrMagnitude < 1e-12f) continue;
                for (int m = 0; m < mem.Count; m++) bakedVerts[mem[m]] += d;
            }
        }

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
