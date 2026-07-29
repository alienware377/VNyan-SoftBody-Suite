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
