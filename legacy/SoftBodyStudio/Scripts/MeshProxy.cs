using System.Collections.Generic;
using UnityEngine;

namespace SoftBodyStudio
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
        public RemeshCage cage; public SquishSim cageSim; public SquishRegion cageSrc;
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

            go = new GameObject(smr.name + "_SoftBodyProxy");
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
            Debug.Log("[SoftBody] attach '" + smr.name + "' verts=" + n
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
                    Debug.Log("[SoftBody] built " + (all ? "(all meshes)" : kv.Key) + " capsule colliders");
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
            if (wobbleMR != null) wobbleMR.enabled = true;   // hand rendering back to Wobble
            wobbleSrc = null; wobbleMR = null;
            // keep the original hidden if ANY other plugin's proxy still drives this mesh
            if (smr != null)
                smr.forceRenderingOff = ProxyAlive("_WobbleProxy") || ProxyAlive("_JelloProxy") || ProxyAlive("_SquishProxy");
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

        // upstream source: Wobble only (order 19000). Jello (20600) and Squish (20700)
        // run AFTER us — never chain onto their proxies (cycle).
        Transform FindChainSource()
        {
            if (smr == null) return null;
            Transform t = smr.transform.Find(smr.name + "_WobbleProxy");
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
            Debug.Log("[SoftBody] chaining (source: " + t.name + ") on '" + smr.name + "'");
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
                Debug.LogWarning("[SoftBody] '" + smr.name + "' vertex count changed ("
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
                    if (settingsRef != null)
                    {
                        int ts = Mathf.Clamp(Mathf.RoundToInt(settingsRef.proxySmooth), 0, 400);
                        int av = Mathf.Clamp(Mathf.RoundToInt(settingsRef.projAvg), 0, 400);
                        if (ts > 0 || av > 0) cage.SmoothDisp(ts, av);
                        // 2nd-level squish: restore the sharp dent AFTER the smoothers
                        if (settingsRef.boostStrength > 0.001f || settingsRef.slapPower > 0.001f)
                            cage.ContactBoost(cageSim, go.transform, settingsRef.boostStrength,
                                Mathf.Clamp(Mathf.RoundToInt(settingsRef.boostSpread), 0, 60),
                                Mathf.Clamp(settingsRef.boostMax, 0.001f, 0.2f),
                                Mathf.Max(0f, settingsRef.slapSens), settingsRef.slapPower, pdt);
                    }
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

            msSim = Mathf.Lerp(msSim, (float)swDbg.Elapsed.TotalMilliseconds, 0.08f);
            DrawDebug();
            if (debugDraw && (dbgLogT += dt) > 2f)
            {
                dbgLogT = 0f;
                int act = 0; foreach (KeyValuePair<string, MeshColliderCloud> kv in colMeshCloud) act += kv.Value.nd;
                Debug.Log("[SoftBody] perf '" + (smr != null ? smr.name : "?") + "': bake+chain=" + msBake.ToString("0.00")
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
                Debug.Log("[SoftBody] colliders from '" + kv.Key + "': " + kv.Value.buildInfo);
        }

        // F11: dump displaced mesh + rest mesh + per-node solver fields for offline analysis
        public void DumpDebug()
        {
            if (!Alive) return;
            try
            {
                string dir = System.IO.Path.Combine(Application.persistentDataPath, "softbodydebug");
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
                Debug.Log("[SoftBody] debug dump written to " + dir);
            }
            catch (System.Exception e) { Debug.LogWarning("[SoftBody] dump failed: " + e.Message); }
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
                overlayGO = new GameObject(smr.name + "_SoftBodyOverlay");
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
            c.logTag = "[SoftBody]";
            float L = Mathf.Clamp(settingsRef.remeshSize, 0.002f, 0.05f);
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
                    Debug.LogWarning("[SoftBody] cage build timed out — falling back to per-region sims");
                    cageToken++; cageBuilding = null;
                    BuildRegionSims(); ResolveColliderMeshes(avatarRef);
                }
                return;
            }
            cageBuilding = null;
            if (c.note.Length > 0) Debug.LogWarning(c.logTag + " cage note: " + c.note);
            if (!c.buildOk)
            {
                Debug.LogWarning("[SoftBody] cage remesh failed (stage: " + c.stage + ") — falling back to per-region sims");
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
            ResolveColliderMeshes(avatarRef);
            Debug.Log("[SoftBody] remesh cage LIVE: " + c.SimVertCount + " verts, edge=" + c.usedEdge.ToString("0.0000")
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
