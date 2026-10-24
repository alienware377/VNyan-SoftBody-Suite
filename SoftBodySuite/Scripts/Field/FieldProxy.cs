using System.Collections.Generic;
using UnityEngine;

namespace SoftBodySuite.Field
{
    // the suite's shared types, under the names the neighbouring proxies use
    using FieldMesh = SoftBodySuite.SuiteMesh;
    using FieldConfig = SoftBodySuite.SuiteConfig;
    using FieldSettings = SoftBodySuite.SuiteSettings;
    using MeshColliderCloud = SoftBodySuite.Squish.MeshColliderCloud;

    // Stage 4: the soft-flesh field effect family, on its own remeshed cage.
    //
    // Same shape as SoftBodySuite.Squish.MeshProxy: bake the skinned mesh (or take the
    // previous stage's arrays directly), add a displacement field, push the result into
    // a plain MeshRenderer copy that keeps the ORIGINAL materials. What is different is
    // that the field is not one solver but a REGISTRY of sources writing typed channels
    // into one DeformField, finished once by FieldPost.
    //
    // It is a FOURTH stage, not a replacement for the other three. WobbleSim, JelloSim
    // and SquishSim are ~8,600 lines of tuned behaviour with no field counterpart;
    // replacing them would mean frame 1 is identical to nothing. So this runs AFTER
    // Squish and BEFORE the cloth passes, takes upVerts/upNormals, and crossfades its
    // own field in with sfxMix — which is 0 by default, and at 0 this file must publish
    // the bytes it was handed, unchanged. That is Milestone 1 and it is checkable:
    // FieldDiag.VerifyIdentity, maxDelta == 0.0f exactly.
    public class FieldProxy
    {
        public SkinnedMeshRenderer smr;
        public SoftBodySuite.SuiteMesh cfg;
        public static SoftBodySuite.SuiteSettings settingsRef;
        public static SoftBodySuite.SuiteConfig configRef;
        public static bool halfRate;
        public static bool halfRateLerp;
        // WIRED, FORCED FALSE. The capture/solve split is designed for it (FieldContext
        // may not hold a UnityEngine.Object precisely so the solve can move), but async
        // is not enabled until the capture half has been MEASURED: if capture is 60% of
        // the frame, moving the other 40% off-thread buys a frame of latency for
        // nothing. Do not flip this without the numbers from FieldDiag.Report().
        public static bool asyncSim;
        public static bool debugDraw;

        // ---- in-suite chain ---- the stage before this one hands its arrays straight
        // over; reading a hidden mesh back with GetVertices costs a copy each way and
        // puts the stages a frame apart.
        public Vector3[] upVerts, upNormals;

        GameObject go;
        MeshFilter mf;
        MeshRenderer mr;
        Mesh baked;                 // re-baked every frame
        Mesh display;               // what we render (topology copied once)

        Vector3[] bakedVerts;
        Vector3[] bakedNormals;
        Vector3[] disp;             // per-render-vertex displacement accumulator
        readonly List<Vector3> scratch = new List<Vector3>();

        int[] weldOf;               // mesh vertex -> weld group id
        List<int>[] weldMembers;    // weld group id -> all duplicate vertex indices

        // ---- the field: ONE SOLVER PER PAINTED REGION ----
        // Each enabled, painted region gets its own cage, field, sources, settings, motion
        // and ripple state, and their displacements ADD onto the one rendered body
        // (RemeshCage.Project adds into its target). Until 2026-10-13 there was one cage for
        // the UNION of all regions, driven by the FIRST region's sliders: adding a thighs
        // region doubled the "region size" every ripple / jiggle value is scaled by (0.23 m ->
        // 0.48 m), overran the cage build's time budget, and the thighs' own sliders did nothing.
        class RegionSolver
        {
            public SoftBodySuite.SuiteRegion reg;
            public SoftBodySuite.FieldRegion tuned;     // THIS region's sliders
            public string name;
            public int[] sampleIdx;                     // this region's painted render verts
            public SoftBodySuite.Jello.RemeshCage cage, cageBuilding;
            public System.Diagnostics.Stopwatch cageSw;
            public string cageLastStage;
            public DeformField field;
            public FieldRegistry registry;
            public FieldContext ctx;
            public CageGeodesic geo;
            public FieldCollider col;
            public FieldBlur blur;
            public float usedEdge, dens, cageArea;
            // effect family, re-resolved only when FieldRegion.effect actually changes
            public string lastEffect;
            public bool effectResolved;
            public int family = SoftBodySuite.Field.FieldEffects.Squish;
            // flesh motion is per region: thighs and breasts do not move together
            public Vector3 prevCentroidW, prevVel, prevAcc;
            public bool motionPrimed;
        }
        readonly List<RegionSolver> solvers = new List<RegionSolver>();
        RegionSolver S0 { get { return solvers.Count > 0 ? solvers[0] : null; } }
        int cageToken;
        float renderArea;

        GameObject avatarRef;
        Animator animRef;

        // ---- main-thread capture (no source may ever touch a Transform) ----
        SoftBodySuite.SquishCollider[] colCfg;
        Transform[] colTr;
        Vector3[] capA, capB;
        float[] capR;
        bool[] capValid;
        bool[] capLimb;

        // Hands, fingers and arms: the things that press INTO the flesh and move it. The
        // torso and spine capsules the breast is attached to press all the time and from
        // every side, and averaging them in is what made Lift flip between up and down.
        static bool IsLimbBone(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("hand") || n.Contains("finger") || n.Contains("thumb") || n.Contains("index")
                || n.Contains("middle") || n.Contains("ring") || n.Contains("little") || n.Contains("pinky")
                || n.Contains("palm") || n.Contains("wrist") || n.Contains("elbow") || n.Contains("arm");
        }
        int capCount;
        Vector3[] capPrevA;
        int capPrevCount;

        // ---- mesh colliders ----
        readonly Dictionary<string, SkinnedMeshRenderer> colMeshSmr = new Dictionary<string, SkinnedMeshRenderer>();
        readonly Dictionary<string, MeshColliderCloud> colMeshCloud = new Dictionary<string, MeshColliderCloud>();
        List<SkinnedMeshRenderer> allColSmrs;
        readonly List<Vector4> regBounds = new List<Vector4>();
        // painted verts this mesh must not collide with when it is its OWN collider.
        // Rebuilt in ResolveRegions (attach / rebind / repaint), never in the frame path.
        readonly HashSet<int> selfExcludeSet = new HashSet<int>();

        // ---- sleep gate ----
        int[] sampleIdx;            // painted render verts, for the bound + centroid
        Vector3 boundCenter;
        float boundRadius;
        float sleepT;
        bool asleepZeroed;

        // ---- half rate ----
        Vector3[] heldDisp, heldPrev;
        bool heldValid, frameFlip;

        // ---- diagnostics ----
        Vector3[] idInput;          // pre-field snapshot, only while identityWatch is on
        System.Diagnostics.Stopwatch swDbg = new System.Diagnostics.Stopwatch();
        System.Diagnostics.Stopwatch swPass = new System.Diagnostics.Stopwatch();
        float msBake, msField, dbgLogT;

        public bool Alive { get { return smr != null && go != null; } }
        public int VertexCount { get { return bakedVerts != null ? bakedVerts.Length : 0; } }
        public Vector3[] OutVerts { get { return bakedVerts; } }
        public Vector3[] OutNormals { get { return bakedNormals; } }
        public Vector3[] BaseVerts { get { return bakedVerts; } }
        public Vector3[] Disp { get { return disp; } }
        public Transform Root { get { return go != null ? go.transform : null; } }
        // the FIRST region's, for the callers that predate one-solver-per-region
        public SoftBodySuite.Jello.RemeshCage Cage { get { return S0 != null ? S0.cage : null; } }
        public float UsedEdge { get { return S0 != null ? S0.usedEdge : 0f; } }
        public DeformField Field { get { return S0 != null ? S0.field : null; } }
        public FieldRegistry Registry { get { return S0 != null ? S0.registry : null; } }

        // only the last stage that actually runs should be on screen
        public void SetVisible(bool v)
        {
            if (mr != null && mr.enabled != v) mr.enabled = v;
        }

        // ==================================================================
        // attach / detach
        // ==================================================================

        public void Attach(SkinnedMeshRenderer target, SoftBodySuite.SuiteMesh meshCfg,
                           GameObject avatar, Animator anim)
        {
            Detach();
            smr = target; cfg = meshCfg;
            if (smr == null || smr.sharedMesh == null) return;

            baked = new Mesh(); baked.MarkDynamic();
            smr.BakeMesh(baked);
            display = Object.Instantiate(baked);
            display.MarkDynamic();
            display.name = smr.name + "_field";

            int n = display.vertexCount;
            bakedVerts = new Vector3[n];
            bakedNormals = new Vector3[n];
            disp = new Vector3[n];
            BuildWeldMap();

            go = new GameObject(smr.name + "_FieldProxy");
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

            avatarRef = avatar; animRef = anim;
            meshNameCached = target != null ? target.name : "?";
            renderArea = MeasureRenderArea();
            ResolveRegions();
            ResolveColliderMeshes(avatar);

            Debug.Log("[SfxField] attach '" + smr.name + "' verts=" + n
                + " regions=" + (cfg != null && cfg.regions != null ? cfg.regions.Count : 0)
                + " field regions=" + solvers.Count
                + " renderArea=" + renderArea.ToString("0.0000") + " m^2"
                + " lossyScale=" + smr.transform.lossyScale.ToString("0.###"));

            StartCageBuilds();
        }

        public void Detach()
        {
            JoinWorker();
            if (smr != null) smr.forceRenderingOff = false;
            if (go != null) { go.SetActive(false); Object.Destroy(go); }   // hide NOW (Destroy is end-of-frame)
            if (baked != null) Object.Destroy(baked);
            if (display != null) Object.Destroy(display);
            go = null; mf = null; mr = null; baked = null; display = null; smr = null;
            dbgPool.Clear();   // the pool lived under go and went with it
            dotPool.Clear();
            solvers.Clear(); cageToken++;
            colMeshSmr.Clear(); colMeshCloud.Clear(); allColSmrs = null;
            colCfg = null; colTr = null; capCount = 0; capPrevCount = 0;
            heldValid = false;
            sampleIdx = null; boundRadius = 0f; sleepT = 0f; asleepZeroed = false;
        }

        public void ResetState()
        {
            for (int i = 0; i < solvers.Count; i++)
            {
                RegionSolver r = solvers[i];
                r.field.ClearAll(); r.registry.ResetAll(); r.geo.Invalidate(); r.motionPrimed = false;
            }
            if (disp != null) System.Array.Clear(disp, 0, disp.Length);
            heldValid = false;
            sleepT = 0f; asleepZeroed = false;
            FieldDiag.ResetIdentityWatch();
            FieldDiag.ResetTimers();
        }

        public void RebuildSims(GameObject avatar, Animator anim)
        {
            avatarRef = avatar; animRef = anim;
            cageToken++;
            ResolveRegions();   // builds a fresh solver per region
            ResolveColliderMeshes(avatar);
            StartCageBuilds();
        }

        // ONE SOLVER PER PAINTED REGION (see RegionSolver). Colliders are still merged across
        // every enabled region, so a hand added on the breasts also dents the thighs.
        void ResolveRegions()
        {
            JoinWorker();
            solvers.Clear();
            List<SoftBodySuite.SquishCollider> merged = new List<SoftBodySuite.SquishCollider>();
            if (cfg != null && cfg.regions != null)
            {
                for (int r = 0; r < cfg.regions.Count; r++)
                {
                    SoftBodySuite.SuiteRegion reg = cfg.regions[r];
                    if (reg == null || !reg.enabled) continue;
                    if (reg.vertIndex.Count > 0 && reg.field != null) solvers.Add(NewSolver(reg));
                    for (int c = 0; c < reg.colliders.Count; c++)
                    {
                        SoftBodySuite.SquishCollider sc = reg.colliders[c];
                        bool dup = false;
                        for (int k = 0; k < merged.Count; k++)
                            if (merged[k].bone == sc.bone && merged[k].mesh == sc.mesh) { dup = true; break; }
                        if (!dup) merged.Add(sc);
                    }
                }
            }
            if (solvers.Count > 1)
            {
                string nm = "";
                for (int i = 0; i < solvers.Count; i++) nm += (i > 0 ? ", " : "") + "'" + solvers[i].name + "'";
                Debug.Log("[SfxField] '" + (smr != null ? smr.name : "?") + "': " + solvers.Count
                    + " field regions, each with its own cage and settings: " + nm);
            }

            colCfg = merged.ToArray();
            colTr = new Transform[colCfg.Length];
            for (int c = 0; c < colCfg.Length; c++)
                if (string.IsNullOrEmpty(colCfg[c].mesh))
                    colTr[c] = SoftBodySuite.Squish.MeshProxy.FindBone(avatarRef, animRef, colCfg[c].bone);
            FindNippleBones();

            // painted render verts, for the sleep bound and the flesh centroid
            List<int> samples = new List<int>();
            if (cfg != null && cfg.regions != null)
                for (int r = 0; r < cfg.regions.Count; r++)
                {
                    SoftBodySuite.SuiteRegion reg = cfg.regions[r];
                    if (reg == null || !reg.enabled) continue;
                    for (int v = 0; v < reg.vertIndex.Count; v++)
                        if (reg.weight[v] > 0.05f && reg.vertIndex[v] < bakedVerts.Length)
                            samples.Add(reg.vertIndex[v]);
                }
            sampleIdx = samples.ToArray();

            // self-collider exclusions, built here so the frame path never allocates. Note it
            // spans DISABLED regions too: a region that is switched off is still flesh, and
            // letting the cloud fit a capsule through it is what makes the mesh collide with
            // itself the moment somebody toggles a region back on.
            selfExcludeSet.Clear();
            if (cfg != null && cfg.regions != null)
                for (int r = 0; r < cfg.regions.Count; r++)
                {
                    SoftBodySuite.SuiteRegion reg = cfg.regions[r];
                    if (reg == null) continue;
                    for (int v = 0; v < reg.vertIndex.Count; v++)
                        if (reg.weight[v] > 0.15f) selfExcludeSet.Add(reg.vertIndex[v]);
                }

            // The sleep bound GROWS and never shrinks, on purpose. Across a repaint that is
            // wrong: a region shrunk to a quarter of its old size keeps the old radius, which
            // both wakes the proxy early and makes CaptureFrameInputs call the flesh
            // "overlapping" — which charges the impulse envelope off nothing.
            boundRadius = 0f;
        }

        // A collider was added or removed: re-merge the collider list and refit the mesh
        // colliders. Cheap; the cages and everything the solvers have built are kept.
        public void RefreshColliders(GameObject avatar)
        {
            if (!Alive) return;
            JoinWorker();
            List<SoftBodySuite.SquishCollider> merged = new List<SoftBodySuite.SquishCollider>();
            if (cfg != null && cfg.regions != null)
                for (int r = 0; r < cfg.regions.Count; r++)
                {
                    SoftBodySuite.SuiteRegion reg = cfg.regions[r];
                    if (reg == null || !reg.enabled) continue;
                    for (int c = 0; c < reg.colliders.Count; c++)
                    {
                        SoftBodySuite.SquishCollider sc = reg.colliders[c];
                        bool dup = false;
                        for (int k = 0; k < merged.Count; k++)
                            if (merged[k].bone == sc.bone && merged[k].mesh == sc.mesh) { dup = true; break; }
                        if (!dup) merged.Add(sc);
                    }
                }
            colCfg = merged.ToArray();
            colTr = new Transform[colCfg.Length];
            for (int c = 0; c < colCfg.Length; c++)
                if (string.IsNullOrEmpty(colCfg[c].mesh))
                    colTr[c] = SoftBodySuite.Squish.MeshProxy.FindBone(avatar != null ? avatar : avatarRef, animRef, colCfg[c].bone);
            capPrevCount = 0;   // slots shift: no fake velocity on the next frame
            ResolveColliderMeshes(avatar != null ? avatar : avatarRef);
        }

        RegionSolver NewSolver(SoftBodySuite.SuiteRegion reg)
        {
            RegionSolver r = new RegionSolver();
            r.reg = reg; r.tuned = reg.field; r.name = reg.name;
            r.field = new DeformField();
            r.registry = new FieldRegistry();
            r.geo = new CageGeodesic();
            r.col = new FieldCollider();
            r.blur = new FieldBlur();
            r.ctx = new FieldContext();
            // THE SINGLE WIRING POINT. FieldRegistry never constructs a concrete source, and
            // every region gets its OWN instances, so one region's impulse envelope, events
            // and nipple finder never leak into another's.
            FieldPresets.RegisterAll(r.registry);
            r.registry.SortByOrder();
            List<int> smp = new List<int>();
            for (int v = 0; v < reg.vertIndex.Count; v++)
                if (reg.weight[v] > 0.05f && reg.vertIndex[v] >= 0 && reg.vertIndex[v] < bakedVerts.Length)
                    smp.Add(reg.vertIndex[v]);
            r.sampleIdx = smp.ToArray();
            return r;
        }

        int TotalNodes()
        {
            int t = 0;
            for (int i = 0; i < solvers.Count; i++) if (solvers[i].field != null) t += solvers[i].field.n;
            return t;
        }

        bool AnyFieldActive()
        {
            for (int i = 0; i < solvers.Count; i++)
                if (solvers[i].field != null && solvers[i].field.AnyActive) return true;
            return false;
        }

        public void ResolveColliderMeshes(GameObject avatar)
        {
            colMeshSmr.Clear(); colMeshCloud.Clear(); allColSmrs = null;
            if (avatar == null || cfg == null) return;
            SkinnedMeshRenderer[] rends = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int c = 0; colCfg != null && c < colCfg.Length; c++)
            {
                string nm = colCfg[c].mesh;
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

        // ==================================================================
        // cage
        // ==================================================================

        // Kicked off on a background thread; the field is inert (zero displacement)
        // until the cage arrives, VNyan itself stays responsive. Mirrors
        // JelloProxy.StartCageBuild exactly — including the rule about logging.
        void StartCageBuilds()
        {
            for (int i = 0; i < solvers.Count; i++) StartCageBuild(solvers[i]);
        }

        bool StartCageBuild(RegionSolver s)
        {
            if (display == null || s == null || s.reg == null) return false;
            // THIS region's paint only
            List<SoftBodySuite.SuiteRegion> one = new List<SoftBodySuite.SuiteRegion>(1);
            one.Add(s.reg);
            float[] wRep = SoftBodySuite.Jello.RemeshCage.UnionWeights(display.vertexCount, one, weldOf);
            if (wRep == null) return false;
            float sz = settingsRef != null ? settingsRef.field.remeshSize : 0.008f;
            float ps = settingsRef != null ? settingsRef.field.remeshPasses : 8f;
            SoftBodySuite.Jello.RemeshCage c = new SoftBodySuite.Jello.RemeshCage();
            c.logTag = "[SfxField]";
            float L = Mathf.Clamp(sz, 0.002f, 0.15f);
            int passes = Mathf.Clamp(Mathf.RoundToInt(ps), 5, 20);
            Vector3[] mvp = display.vertices;
            Vector3[] mnrm = display.normals;              // rest normals for the valley gate
            int[] tris = display.triangles;
            int[] wo = weldOf; List<int>[] wm = weldMembers;
            s.cageBuilding = c;
            s.cageLastStage = null;
            s.cageSw = System.Diagnostics.Stopwatch.StartNew();
            Debug.Log("[SfxField] '" + s.name + "' cage build started (edge " + L.ToString("0.0000") + " m, " + passes
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

        void PollCageBuilds()
        {
            for (int i = 0; i < solvers.Count; i++) PollCageBuild(solvers[i]);
        }

        void PollCageBuild(RegionSolver s)
        {
            SoftBodySuite.Jello.RemeshCage c = s.cageBuilding;
            if (c == null) return;
            if (c.stage != s.cageLastStage)
            {
                s.cageLastStage = c.stage;
                Debug.Log("[SfxField] '" + s.name + "' cage stage: " + c.stage + " ("
                    + (s.cageSw != null ? s.cageSw.ElapsedMilliseconds : 0) + " ms)");
            }
            if (!c.buildDone)
            {
                if (s.cageSw != null && s.cageSw.ElapsedMilliseconds > 30000)
                {
                    Debug.LogWarning("[SfxField] '" + s.name + "' cage build timed out — the field stays inert on this mesh");
                    cageToken++; s.cageBuilding = null;
                }
                return;
            }
            s.cageBuilding = null;
            if (c.note.Length > 0) Debug.LogWarning("[SfxField] '" + s.name + "' cage note: " + c.note);
            if (!c.buildOk)
            {
                Debug.LogWarning("[SfxField] '" + s.name + "' cage remesh failed (stage: " + c.stage
                    + ") — the field stays inert on this mesh");
                return;
            }
            FinishCage(s, c);
        }

        void FinishCage(RegionSolver s, SoftBodySuite.Jello.RemeshCage c)
        {
            s.cage = c;
            s.usedEdge = c.usedEdge;
            // The reference edge is 8 mm: the density every default was tuned at.
            // dens = clamp(0.008 / usedEdge, 1, 6).
            s.dens = Mathf.Clamp(0.008f / Mathf.Max(s.usedEdge, 1e-5f), 1f, 6f);

            int nn = c.SimVertCount;
            s.field.Ensure(nn);
            // Ensure REUSES the arrays when the node count happens to land the same, and a new
            // cage means new topology under the same indices: excite, the event ring and the
            // accumulators would carry the old cage's values onto different flesh. Clear here
            // rather than rely on every caller having cleared first.
            s.field.ClearAll();
            if (s.registry != null) s.registry.ResetAll();
            for (int i = 0; i < nn; i++) s.field.mask[i] = c.simWeight[i];
            s.field.usedEdge = s.usedEdge;
            s.field.dens = s.dens;
            s.cageArea = TriArea(c.simRest, c.simTris);
            s.field.cageArea = s.cageArea;
            s.field.renderArea = renderArea;

            CageGraph g = CageGeodesic.BuildGraph(c.simRest, c.simTris);
            s.geo.SetGraph(g);
            s.blur.SetGraph(g);

            s.ctx.field = s.field; s.ctx.geo = s.geo; s.ctx.col = s.col; s.ctx.blur = s.blur;
            s.ctx.n = nn; s.ctx.pos = s.field.pos; s.ctx.nrm = s.field.nrm; s.ctx.mask = s.field.mask;
            s.ctx.cageTris = c.simTris; s.ctx.usedEdge = s.usedEdge; s.ctx.dens = s.dens;
            s.ctx.regionRadius = PaintRadius(c.simRest, c.simWeight, nn);
            Debug.Log("[SfxField] '" + s.name + "' painted region radius " + s.ctx.regionRadius.ToString("0.0000")
                + " m (rest pose) - Ripple and Fat Jiggle amplitudes are a fraction of this"
                + " unless amplitudeRelative is turned off");

            // A DRIFTED usedEdge SILENTLY WIDENS EVERY TUNED FOOTPRINT. The remesher
            // caps the edge against the source mesh, so asking for 8 mm does not always
            // get 8 mm, and every Spread / Shape Smoothing / Corrective Smooth number in
            // the presets assumes it did. Say so out loud rather than letting the whole
            // preset library quietly read wrong.
            bool warn = settingsRef == null || settingsRef.field.warnOnEdgeDrift;
            if (warn && Mathf.Abs(s.usedEdge - 0.008f) > 0.008f * 0.05f)
                Debug.LogWarning("[SfxField] cage edge drifted: asked 0.0080 m, got "
                    + s.usedEdge.ToString("0.00000") + " m ("
                    + ((s.usedEdge / 0.008f - 1f) * 100f).ToString("0.0") + "%) — dens="
                    + s.dens.ToString("0.00") + "; every tuned footprint scales with this");
            if (warn && c.note.Length > 0)
                Debug.LogWarning("[SfxField] cage built WITH A NOTE — treat tuned numbers as suspect: " + c.note);

            Debug.Log("[SfxField] '" + s.name + "' cage LIVE: " + nn + " verts, edge=" + s.usedEdge.ToString("0.00000")
                + " m, dens=" + s.dens.ToString("0.00")
                + ", cageArea=" + s.cageArea.ToString("0.00000") + " m^2"
                + ", renderArea=" + renderArea.ToString("0.00000") + " m^2"
                + ", areaRatio=" + (s.cageArea / Mathf.Max(renderArea, 1e-9f)).ToString("0.0000")
                + ", built in " + (s.cageSw != null ? s.cageSw.ElapsedMilliseconds : 0) + " ms");
        }

        static float TriArea(Vector3[] v, int[] t)
        {
            if (v == null || t == null) return 0f;
            float a = 0f;
            for (int i = 0; i + 2 < t.Length; i += 3)
                a += Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]).magnitude * 0.5f;
            return a;
        }

        // The painted region's own radius, in metres: paint-weighted centroid first, then
        // the furthest painted node from it. Rest pose and once per cage build, because a
        // wave height that breathed with the animation would beat against the wave itself.
        //
        // WEIGHTED centroid, unweighted max: a feathered edge should pull the centre
        // towards the solid middle of the paint but must not shrink the radius, or the
        // amplitude would depend on how softly the user feathered rather than on how big
        // the region is. The 1 cm floor is there so a single-vertex dab cannot make every
        // relative amplitude collapse to zero.
        static float PaintRadius(Vector3[] rest, float[] w, int n)
        {
            if (rest == null || w == null || n <= 0) return 0.06f;
            if (n > rest.Length) n = rest.Length;
            if (n > w.Length) n = w.Length;

            Vector3 cen = Vector3.zero;
            float wsum = 0f;
            for (int i = 0; i < n; i++)
            {
                float wi = w[i];
                if (wi <= 0f) continue;
                cen += rest[i] * wi;
                wsum += wi;
            }
            if (wsum <= 0f) return 0.06f;
            cen /= wsum;

            float r2 = 0f;
            for (int i = 0; i < n; i++)
            {
                if (w[i] <= 0f) continue;
                float d2 = (rest[i] - cen).sqrMagnitude;
                if (d2 > r2) r2 = d2;
            }
            return Mathf.Max(Mathf.Sqrt(r2), 0.01f);
        }

        // The WHOLE renderer's rest area, not just the painted part: InflateSource needs
        // cageArea/renderArea because the pressure statistic is defined as a
        // mean over the ENTIRE input geometry
        // while our cage only covers the paint.
        float MeasureRenderArea()
        {
            if (smr == null || smr.sharedMesh == null) return 0f;
            Mesh m = smr.sharedMesh;
            return TriArea(m.vertices, m.triangles);
        }

        // A channel toggle that the current effect family does not include would
        // otherwise do nothing with no explanation - the exact defect class this fork
        // is being cleaned of. Say it out loud instead, once per family change.
        void WarnGatedChannels(RegionSolver s)
        {
            if (s.tuned == null) return;
            string off = "";
            if (s.tuned.inflateEnable > 0.5f && !SoftBodySuite.Field.FieldEffects.Allows(s.family, "inflate", s.tuned)) off += " Inflate";
            if (s.tuned.organicEnable > 0.5f && !SoftBodySuite.Field.FieldEffects.Allows(s.family, "organic", s.tuned)) off += " Organic";
            if (s.tuned.rippleEnable > 0.5f && !SoftBodySuite.Field.FieldEffects.Allows(s.family, "ripple", s.tuned)) off += " Ripple";
            if (s.tuned.jiggleEnable > 0.5f && !SoftBodySuite.Field.FieldEffects.Allows(s.family, "jiggle", s.tuned)) off += " FatJiggle";
            if (s.tuned.penEnable > 0.5f && !SoftBodySuite.Field.FieldEffects.Allows(s.family, "penetration", s.tuned)) off += " Penetration";
            if (off.Length == 0) return;
            Debug.LogWarning("[SfxField] '" + meshNameCached + "/" + s.name + "' has channels switched on that the "
                + SoftBodySuite.Field.FieldEffects.Name(s.family) + " family does not contain and will NOT run:"
                + off + ". Each effect family is its own effect; press the preset button for the "
                + "family you want (that is what sets it) rather than stacking channels.");
        }

        // ==================================================================
        // frame
        // ==================================================================

        // NO substeps PARAMETER, deliberately - see the note on RunField below.
        public void Frame(float dt, Vector3 worldDown, bool simEnabled)
        {
            if (!Alive) return;
            passThrough = false;
            JoinWorker();   // the cage, the collider and the field are about to be touched
            DrawColliders();
            DrawOrigins();
            swDbg.Reset(); swDbg.Start();
            PollCageBuilds();

            // FINAL stage while it is on screen: assert it EVERY frame. An upstream
            // detach re-shows the original for its own fallback and that must never win.
            smr.forceRenderingOff = true;

            // handed to us directly by the stage before this one
            if (upVerts != null && upVerts.Length == bakedVerts.Length)
            {
                System.Array.Copy(upVerts, bakedVerts, bakedVerts.Length);
                if (upNormals != null && upNormals.Length == bakedNormals.Length)
                    System.Array.Copy(upNormals, bakedNormals, bakedNormals.Length);
                RunField(dt, worldDown, simEnabled);
                return;
            }

            smr.BakeMesh(baked);
            baked.GetVertices(scratch);
            if (scratch.Count != bakedVerts.Length)
            {
                // the renderer's mesh was swapped out from under us (outfit systems etc.)
                Debug.LogWarning("[SfxField] '" + smr.name + "' vertex count changed ("
                    + bakedVerts.Length + " -> " + scratch.Count + ") — detaching, will rebind");
                Detach();
                return;
            }
            scratch.CopyTo(bakedVerts);
            baked.GetNormals(scratch);
            if (scratch.Count == bakedVerts.Length) scratch.CopyTo(bakedNormals);

            RunField(dt, worldDown, simEnabled);
        }

        // WHY THERE IS NO SUBSTEP LOOP HERE.
        // The other three stages are Verlet integrators: halving their step changes the
        // answer, so Substeps is a real knob for them. The field is not an integrator. Every
        // source is a pure function of THIS frame's pose (conform, blur, geodesic falloff,
        // noise), so running the pipeline N times per frame would produce the identical
        // displacement N times over - DeformField.BeginFrame clears the accumulators at the
        // top of each pass, so the last one would simply win. The only state the field carries
        // between frames is temporal, and all of it advances in CLOSED FORM:
        //   MotionExciter.cs:142  energy *= Exp(-dt / relaxTime)
        //   MotionExciter.cs:110  a = 1 - Exp(-dt / tau)   (the EMA chain)
        //   DeformField.AgeEvents age += dt
        // Exp(-dt/T) == Exp(-dt/N/T)^N exactly, so subdividing those changes nothing either.
        // A substep count here could therefore only ever multiply the frame cost and never
        // move a vertex, which is why the fld_substeps row and its config write are gone
        // rather than kept as decoration.
        void RunField(float dt, Vector3 worldDown, bool simEnabled)
        {
            msBake = Mathf.Lerp(msBake, (float)swDbg.Elapsed.TotalMilliseconds, 0.08f);
            swDbg.Reset(); swDbg.Start();
            System.Array.Clear(disp, 0, disp.Length);

            if (FieldDiag.identityWatch)
            {
                if (idInput == null || idInput.Length != bakedVerts.Length) idInput = new Vector3[bakedVerts.Length];
                System.Array.Copy(bakedVerts, idInput, bakedVerts.Length);
            }

            // any region with a cage, a mix above 0 and sources to run?
            bool anyLive = false;
            string idleWhy = solvers.Count == 0 ? "no painted region" : "no cage";
            for (int i = 0; i < solvers.Count; i++)
            {
                RegionSolver r = solvers[i];
                if (r.cage == null) continue;
                if (r.tuned == null || r.tuned.sfxMix <= 0f) { idleWhy = "Field Mix is 0"; continue; }
                if (r.registry == null || r.registry.Count == 0) { idleWhy = "no sources"; continue; }
                anyLive = true;
            }

            // ---- THE M1 PATH ----
            // No sim, no mix, no cage or no sources: publish what arrived, untouched.
            // Straight copy, no arithmetic. Not "add a zero vector", not "Lerp with
            // t = 0" — 0.0f + -0.0f is +0.0f, a different bit pattern, and a Lerp
            // rounds. The only bit-exact publish is the one that does nothing.
            if (!simEnabled || !anyLive)
            {
                FieldTelemetry(false, !simEnabled ? "stage off" : idleWhy);
                Publish(false);
                return;
            }

            frameFlip = !frameFlip;
            bool hrAny = halfRate || halfRateLerp;
            bool computeFrame = !hrAny || frameFlip;

            Transform tr = go.transform;
            bool awake = debugDraw || CheckAwake(dt);
            if (!awake)
            {
                // ASLEEP: no capture, no InterpBaked, no source, no FieldPost. After the
                // 0.35 s grace this costs one bound refresh and nothing else — "a
                // sleeping avatar must cost zero" is a hard requirement, and a sleeping
                // proxy that still ran cage.InterpBaked would not meet it.
                if (!asleepZeroed)
                {
                    for (int i = 0; i < solvers.Count; i++)
                    {
                        RegionSolver r = solvers[i];
                        r.field.ClearAll();
                        if (r.cage != null) System.Array.Clear(r.cage.simDisp, 0, r.cage.simDisp.Length);
                    }
                    asleepZeroed = true;
                }
                heldValid = false;
                FieldTelemetry(false, "asleep (no motion)");
                Publish(false);
                return;
            }
            asleepZeroed = false;

            if (asyncSim && !asyncBroken)
            {
                // async replaces half-rate: it already takes the solve off the frame
                KickAsync(tr, dt);
                return;
            }

            if (!computeFrame && heldValid && heldDisp != null && heldDisp.Length == disp.Length)
            {
                // HELD frame: reuse the last computed field (fresh skinning still flows
                // through — only the offset field is one frame old)
                System.Array.Copy(heldDisp, disp, disp.Length);
                PublishWithDisp();
                return;
            }

            float pdt = hrAny ? Mathf.Min(dt * 2f, 0.05f) : dt;

            // (1) MAIN-THREAD CAPTURE — the only place a Transform is read.
            swPass.Reset(); swPass.Start();
            UpdateColliderClouds(tr);
            CaptureFrameInputs(tr, pdt);
            swPass.Stop(); FieldDiag.Mark("capture", (float)swPass.Elapsed.TotalMilliseconds);

            SolveAll(pdt, Time.time, bakedVerts, bakedNormals, disp);

            if (heldDisp == null || heldDisp.Length != disp.Length)
            { heldDisp = new Vector3[disp.Length]; heldPrev = new Vector3[disp.Length]; }
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

            System.Diagnostics.Stopwatch swTel = System.Diagnostics.Stopwatch.StartNew();
            FieldTelemetry(true, "");
            FieldDiag.Mark("telemetry", (float)swTel.Elapsed.TotalMilliseconds);
            PublishWithDisp();
        }

        // ---- DISPLACEMENT TELEMETRY, ALWAYS ON -------------------------------------
        // Every 2 s, say how far the field ACTUALLY moved the mesh, and say it even when
        // the answer is "not at all". Without this the only evidence available is a
        // screenshot, and a screenshot of a stage doing nothing looks exactly like a stage
        // doing something gently - smooth, clean, no artefacts. That reading cost a whole
        // round of "it looks great" about a field that was publishing the body untouched
        // because every region's mix was 0. Clean is not a result; a number is.
        float telT; int telFrames, telSkipped; float telMax, telMeanAcc; int telMovers; int telTick, telSampled;
        float telCageMax, telCageMeanAcc; int telCageN;
        float telC, telW, telG; int telClamped, telCapped;

        static float BucketMax(Vector3[] a, float cur)
        {
            if (a == null) return cur;
            for (int i = 0; i < a.Length; i++)
            {
                float m = a[i].magnitude;
                if (m > cur) cur = m;
            }
            return cur;
        }

        string telWhy = "";

        string telQuietWhy;

        void FieldTelemetry(bool computed, string why)
        {
            if (!FieldDiag.verbose)
            {
                string w = computed ? "" : (why ?? "");
                if (w != telQuietWhy)
                {
                    telQuietWhy = w;
                    // (falling asleep between movements, or the cage still building, is
                    // normal and not worth a line)
                    if (w.Length > 0 && w.IndexOf("asleep") < 0 && w != "no cage") Debug.Log("[SfxField] '" + meshNameCached + "': the field is idle (" + w + "), so it passes the body through unchanged.");
                }
                return;
            }
            telFrames++;
            // The full sweep (render + cage + three buckets) costs about 1 ms on a big cage,
            // every frame, for a line printed every 2 s. Sample every 4th computed frame.
            bool sample = (++telTick & 3) == 0;
            if (!computed) { telSkipped++; telWhy = why; }
            else if (disp != null && sample)
            {
                float mx = 0f, sum = 0f; int mov = 0;
                for (int i = 0; i < disp.Length; i++)
                {
                    float m = disp[i].magnitude;
                    sum += m;
                    if (m > mx) mx = m;
                    if (m > 0.001f) mov++;
                }
                if (mx > telMax) telMax = mx;
                telMeanAcc += sum / Mathf.Max(1, disp.Length);
                telMovers += mov;
                telSampled++;

                // CAGE-LEVEL vs RENDER-LEVEL, measured separately and on purpose.
                // The solver runs on the ~3.9k uniform cage and the result is projected
                // onto the 38.5k render mesh. A shard mess is one of exactly two faults and
                // these two numbers tell them apart without argument:
                //   cage max ~= render max  -> the SOLVER is producing the spike, and the
                //                              projection is faithfully carrying it over.
                //   cage max << render max  -> the solver is smooth and the PROJECTION is
                //                              amplifying, i.e. a binding/interpolation bug.
                // PER-BUCKET ATTRIBUTION. The field keeps three accumulators and they are
                // combined before anything downstream can see them, so a 130 mm spike in the
                // total says nothing about WHICH produced it - and four config bisects in a
                // row failed to move it, because none of the knobs I could reach touch the
                // bucket it is actually in. dispGuard in particular is the anti-clip, which
                // is applied last and DELIBERATELY never smoothed ("a smoothed guarantee is
                // not a guarantee"), so a large value there is unsmoothed by construction -
                // i.e. shards. Name the bucket, then argue about the fix.
                telClamped += ContactSource.clampedThisFrame;
                ContactSource.clampedThisFrame = 0;
                float cmx = 0f, csum = 0f; int cn = 0;
                for (int si = 0; si < solvers.Count; si++)
                {
                    RegionSolver r = solvers[si];
                    if (r.field != null)
                    {
                        telC = BucketMax(r.field.dispContact, telC);
                        telW = BucketMax(r.field.dispWave, telW);
                        telG = BucketMax(r.field.dispGuard, telG);
                    }
                    if (r.cage == null || r.cage.simDisp == null) continue;
                    for (int k = 0; k < r.cage.simDisp.Length; k++)
                    {
                        float cm = r.cage.simDisp[k].magnitude;
                        csum += cm;
                        if (cm > cmx) cmx = cm;
                    }
                    cn += r.cage.simDisp.Length;
                }
                if (cn > 0)
                {
                    if (cmx > telCageMax) telCageMax = cmx;
                    telCageMeanAcc += csum / cn;
                    telCageN = cn;
                }
            }
            telT += Time.deltaTime;
            if (telT < 2f) return;
            int ran = telFrames - telSkipped;
            int smp = Mathf.Max(1, telSampled);
            if (ran <= 0)
            {
                Debug.LogWarning("[SfxField] MOVED NOTHING for " + telFrames + " frames - "
                    + (telWhy.Length > 0 ? telWhy : "identity path")
                    + ". The field published the body untouched.");
            }
            else
            {
                Debug.Log("[SfxField] disp: ran " + ran + "/" + telFrames + " frames"
                    + "  mean=" + (telMeanAcc / smp * 1000f).ToString("0.00") + "mm"
                    + "  max=" + (telMax * 1000f).ToString("0.0") + "mm"
                    + "  moving>1mm=" + (telMovers / smp) + " of " + disp.Length
                    + "  | cage mean=" + (telCageMeanAcc / smp * 1000f).ToString("0.00") + "mm"
                    + " max=" + (telCageMax * 1000f).ToString("0.0") + "mm of " + telCageN
                    + "  selfRangeRejects=" + FieldCollider.selfRangeRejects
                    + "  | buckets contact=" + (telC * 1000f).ToString("0.0")
                    + "mm wave=" + (telW * 1000f).ToString("0.0")
                    + "mm GUARD=" + (telG * 1000f).ToString("0.0") + "mm"
                    + "  dentClamped=" + telClamped + " capped=" + telCapped
                    + "  capsules=" + capCount
                    + MotionExciter.DiagLine()
                    + LiftSource.DiagLine()
                    + (telSkipped > 0 ? ("  [" + telSkipped + " skipped: " + telWhy + "]") : ""));
            }
            telT = 0f; telFrames = 0; telSkipped = 0; telMax = 0f; telMeanAcc = 0f; telMovers = 0; telWhy = ""; telSampled = 0;
            telCageMax = 0f; telCageMeanAcc = 0f; FieldCollider.selfRangeRejects = 0;
            telC = 0f; telW = 0f; telG = 0f; telClamped = 0; telCapped = 0;
        }

        void PublishWithDisp()
        {
            // overall jiggle strength: the field's contribution, scaled as it is published
            float js = SoftBodySuite.JiggleGlobal.strength;
            if (js != 1f) for (int i = 0; i < bakedVerts.Length; i++) bakedVerts[i] += disp[i] * js;
            else
            for (int i = 0; i < bakedVerts.Length; i++) bakedVerts[i] += disp[i];
            Publish(true);
        }

        // `contributed` is the whole story. False = the array handed in is the array
        // handed out, byte for byte, and RecalculateBounds is skipped too because the
        // bake's own bounds already describe these exact vertices.
        // true right after a frame that handed its input out byte for byte
        public bool passThrough;

        void Publish(bool contributed)
        {
            passThrough = !contributed;
            display.SetVertices(bakedVerts);
            display.SetNormals(bakedNormals);
            if (contributed) display.RecalculateBounds();
            msField = Mathf.Lerp(msField, (float)swDbg.Elapsed.TotalMilliseconds, 0.08f);
            // only meaningful on an M1 frame: on a contributing frame the delta IS the
            // field, and watching it would cry wolf on every dent
            if (!contributed && FieldDiag.identityWatch && idInput != null)
                FieldDiag.WatchFrame(smr.name, idInput, bakedVerts);
            if (debugDraw && (dbgLogT += Time.deltaTime) > 2f)
            {
                dbgLogT = 0f;
                Debug.Log("[SfxField] perf '" + (smr != null ? smr.name : "?") + "': bake="
                    + msBake.ToString("0.00") + "ms field=" + msField.ToString("0.00")
                    + "ms capsules=" + capCount + " nodes=" + TotalNodes() + " regions=" + solvers.Count);
                Debug.Log(FieldDiag.Report());
            }
        }

        // ---- collider visualisation -----------------------------------------
        // The Show/hide colliders button only ever drove the SQUISH stage's drawing, so with
        // the field running alone nothing appeared and the colliders looked missing. These
        // are the field's own capsules - mesh-collider clouds and bone capsules, skin gap
        // included - exactly as the solver sees them on its last captured frame.
        readonly List<Transform> dbgPool = new List<Transform>();
        static Material dbgMatF;

        void DrawColliders()
        {
            int used = 0;
            if (debugDraw && capA != null && go != null)
            {
                for (int k = 0; k < capCount; k++)
                {
                    if (capValid != null && k < capValid.Length && !capValid[k]) continue;
                    if (used >= dbgPool.Count) dbgPool.Add(MakeDbgCapsule());
                    Transform t = dbgPool[used++];
                    if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                    Vector3 a = capA[k], b = capB[k];
                    float r = capR[k];
                    Vector3 ab = b - a; float len = ab.magnitude;
                    t.localPosition = (a + b) * 0.5f;
                    t.localRotation = len > 1e-5f ? Quaternion.FromToRotation(Vector3.up, ab) : Quaternion.identity;
                    t.localScale = new Vector3(r * 2f, (len + 2f * r) * 0.5f, r * 2f);
                }
            }
            for (int i = used; i < dbgPool.Count; i++)
                if (dbgPool[i] != null && dbgPool[i].gameObject.activeSelf) dbgPool[i].gameObject.SetActive(false);
        }

        Transform MakeDbgCapsule()
        {
            GameObject g = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Collider c = g.GetComponent<Collider>();
            if (c != null) Object.Destroy(c);
            if (dbgMatF == null)
            {
                dbgMatF = new Material(Shader.Find("Sprites/Default"));
                dbgMatF.color = new Color(1f, 0.55f, 0.1f, 0.4f);   // orange: the field's own
            }
            g.GetComponent<MeshRenderer>().sharedMaterial = dbgMatF;
            g.name = "FieldColDbg";
            g.transform.SetParent(go.transform, false);
            return g.transform;
        }

        public int CapsuleCount { get { return capCount; } }

        // ---- ripple start points: breast bones, marker dots, picking ---------------
        Transform[] nipTip, nipBase;
        static readonly string[] BreastWords = { "breast", "bust", "boob", "oppai", "mune", "chichi", "titty" };

        static bool IsBreastName(string s, bool nippleOnly)
        {
            string l = s.ToLowerInvariant();
            if (l.Contains("nipple")) return true;
            if (nippleOnly) return false;
            for (int i = 0; i < BreastWords.Length; i++) if (l.Contains(BreastWords[i])) return true;
            return false;
        }

        // The END of each breast chain (Breast1 -> Breast2 -> ...), or bones named for the
        // nipple if the rig has them. Mesh objects ("Breast Ribbon") are not bones: skipped.
        void FindNippleBones()
        {
            nipTip = null; nipBase = null;
            if (avatarRef == null) return;
            Transform[] all = avatarRef.GetComponentsInChildren<Transform>(true);
            List<Transform> tips = new List<Transform>(), bases = new List<Transform>();
            // best chain per SIDE of the body (deepest, then longest): a rig with a separate
            // "BreastUpper" helper off the chest otherwise got a third and fourth "nipple"
            Transform av = avatarRef.transform;
            // only bones that skin THIS body mesh: a bow's "Breast Ribbon L" chain is longer
            // than the real breast chain and won the left side
            // Merged avatars list EVERY bone on every mesh, so membership proves nothing: the
            // bone has to carry real weight on this region's painted verts.
            HashSet<Transform> bodyBones = new HashSet<Transform>();
            if (smr != null && smr.bones != null && smr.sharedMesh != null && cfg != null && cfg.regions != null)
            {
                BoneWeight[] bw = null;
                try { bw = smr.sharedMesh.boneWeights; } catch (System.Exception) { bw = null; }
                Transform[] sb = smr.bones;
                if (bw != null && bw.Length > 0)
                    for (int r = 0; r < cfg.regions.Count; r++)
                    {
                        SoftBodySuite.SuiteRegion reg = cfg.regions[r];
                        if (reg == null || !reg.enabled) continue;
                        for (int v = 0; v < reg.vertIndex.Count; v++)
                        {
                            int vi = reg.vertIndex[v];
                            if (reg.weight[v] < 0.05f || vi < 0 || vi >= bw.Length) continue;
                            BoneWeight w = bw[vi];
                            if (w.weight0 > 0.02f && w.boneIndex0 < sb.Length && sb[w.boneIndex0] != null) bodyBones.Add(sb[w.boneIndex0]);
                            if (w.weight1 > 0.02f && w.boneIndex1 < sb.Length && sb[w.boneIndex1] != null) bodyBones.Add(sb[w.boneIndex1]);
                            if (w.weight2 > 0.02f && w.boneIndex2 < sb.Length && sb[w.boneIndex2] != null) bodyBones.Add(sb[w.boneIndex2]);
                            if (w.weight3 > 0.02f && w.boneIndex3 < sb.Length && sb[w.boneIndex3] != null) bodyBones.Add(sb[w.boneIndex3]);
                        }
                    }
            }
            Transform[] sideTip = new Transform[2], sideBase = new Transform[2];
            int[] sideD = { -1, -1 }; float[] sideLen = { 0f, 0f };
            for (int pass = 0; pass < 2 && sideTip[0] == null && sideTip[1] == null; pass++)
            {
                bool nippleOnly = pass == 0;
                HashSet<Transform> named = new HashSet<Transform>();
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].GetComponent<Renderer>() == null && IsBreastName(all[i].name, nippleOnly)
                        && (bodyBones.Count == 0 || bodyBones.Contains(all[i])))
                        named.Add(all[i]);
                // ONE ray per chain: from each chain's top bone, its DEEPEST named descendant
                // (ties: the one furthest from the top). A rig with side bones (BustRoot ->
                // Bust1.1 -> Bust2, plus Bust1.2/1.3/1.4) otherwise gave four rays on one side.
                foreach (Transform root in named)
                {
                    if (root.parent != null && named.Contains(root.parent)) continue;   // not a chain top
                    Transform deep = root; int deepD = 0; float deepDist = 0f;
                    List<Transform> stack = new List<Transform>(); List<int> depth = new List<int>();
                    stack.Add(root); depth.Add(0);
                    while (stack.Count > 0)
                    {
                        Transform t = stack[stack.Count - 1]; int d = depth[depth.Count - 1];
                        stack.RemoveAt(stack.Count - 1); depth.RemoveAt(depth.Count - 1);
                        float dist = (t.position - root.position).sqrMagnitude;
                        if (d > deepD || (d == deepD && dist > deepDist)) { deep = t; deepD = d; deepDist = dist; }
                        for (int ch = 0; ch < t.childCount; ch++)
                            if (named.Contains(t.GetChild(ch))) { stack.Add(t.GetChild(ch)); depth.Add(d + 1); }
                    }
                    Transform tip = deep, bas = deep.parent;
                    if (deep.childCount > 0 && deep.GetChild(0).GetComponent<Renderer>() == null) { tip = deep.GetChild(0); bas = deep; }
                    if (bas == null) continue;
                    int side = Vector3.Dot(tip.position - av.position, av.right) >= 0f ? 1 : 0;
                    float len = (tip.position - root.position).sqrMagnitude;
                    if (deepD > sideD[side] || (deepD == sideD[side] && len > sideLen[side]))
                    { sideTip[side] = tip; sideBase[side] = bas; sideD[side] = deepD; sideLen[side] = len; }
                }
            }
            for (int sd = 0; sd < 2; sd++) if (sideTip[sd] != null) { tips.Add(sideTip[sd]); bases.Add(sideBase[sd]); }
            if (tips.Count == 0) { Debug.Log("[SfxField] ripple nipple finder: no breast bones, will go by shape"); return; }
            nipTip = tips.ToArray(); nipBase = bases.ToArray();
            string s = "";
            for (int i = 0; i < nipTip.Length; i++) s += (i > 0 ? ", " : "") + nipBase[i].name + " -> " + nipTip[i].name;
            Debug.Log("[SfxField] ripple nipple finder: breast bones " + s);
        }

        public static bool showOrigins;
        readonly List<Transform> dotPool = new List<Transform>();
        static Material dotMatPick, dotMatNip;

        void DrawOrigins()
        {
            int used = 0;
            if ((showOrigins || debugDraw) && go != null)
            {
                float s = 0.014f / Mathf.Max(1e-4f, go.transform.lossyScale.x);
                for (int ri = 0; ri < solvers.Count; ri++)
                {
                    FieldContext cx = solvers[ri].ctx;
                    DeformField fd = solvers[ri].field;
                    if (cx == null) continue;
                    for (int k = 0; k < cx.pickCount; k++) PlaceDot(ref used, cx.pickPts[k], s, true);
                    if (fd != null && fd.pos != null)
                        for (int k = 0; k < cx.markNipCount; k++)
                        {
                            int nd = cx.markNip[k];
                            if (nd >= 0 && nd < fd.pos.Length) PlaceDot(ref used, fd.pos[nd], s, false);
                        }
                }
            }
            for (int i = used; i < dotPool.Count; i++)
                if (dotPool[i] != null && dotPool[i].gameObject.activeSelf) dotPool[i].gameObject.SetActive(false);
        }

        void PlaceDot(ref int used, Vector3 p, float s, bool picked)
        {
            if (used >= dotPool.Count) dotPool.Add(MakeDot());
            Transform t = dotPool[used++];
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            t.localPosition = p;
            t.localScale = Vector3.one * s;
            t.GetComponent<MeshRenderer>().sharedMaterial = picked ? dotMatPick : dotMatNip;
        }

        Transform MakeDot()
        {
            GameObject g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Collider cl = g.GetComponent<Collider>();
            if (cl != null) Object.Destroy(cl);
            if (dotMatPick == null)
            {
                dotMatPick = new Material(Shader.Find("Sprites/Default"));
                dotMatPick.color = new Color(0.2f, 0.95f, 1f, 0.9f);   // cyan: picked by hand
                dotMatNip = new Material(Shader.Find("Sprites/Default"));
                dotMatNip.color = new Color(1f, 0.35f, 0.75f, 0.9f);   // pink: found nipples
            }
            g.name = "FieldOriginDot";
            g.transform.SetParent(go.transform, false);
            return g.transform;
        }

        // The render vert under the mouse: nearest the ray within ~12 mm, front-most first.
        public bool PickVertex(Ray worldRay, out int vert)
        {
            vert = -1;
            if (go == null || bakedVerts == null) return false;
            Transform t = go.transform;
            Vector3 ro = t.InverseTransformPoint(worldRay.origin);
            Vector3 rd = t.InverseTransformDirection(worldRay.direction).normalized;
            float tol = 0.012f / Mathf.Max(1e-4f, t.lossyScale.x);
            float tol2 = tol * tol;
            int best = -1, bestAny = -1; float bestT = float.MaxValue, bestPerp = float.MaxValue;
            for (int i = 0; i < bakedVerts.Length; i++)
            {
                Vector3 v = bakedVerts[i] - ro;
                float tt = Vector3.Dot(v, rd);
                if (tt < 0f) continue;
                float perp = (v - rd * tt).sqrMagnitude;
                if (perp < tol2 && tt < bestT) { bestT = tt; best = i; }
                if (perp < bestPerp) { bestPerp = perp; bestAny = i; }
            }
            if (best < 0 && bestAny >= 0 && bestPerp < 9f * tol2) best = bestAny;
            vert = best;
            return best >= 0;
        }

        // index in `pts` of a picked vert within `within` metres of vert v, else -1
        public int NearestPicked(List<int> pts, int v, float within)
        {
            if (go == null || bakedVerts == null || pts == null || v < 0 || v >= bakedVerts.Length) return -1;
            float w = within / Mathf.Max(1e-4f, go.transform.lossyScale.x);
            int best = -1; float bd = w * w;
            for (int k = 0; k < pts.Count; k++)
            {
                if (pts[k] < 0 || pts[k] >= bakedVerts.Length) continue;
                float d2 = (bakedVerts[pts[k]] - bakedVerts[v]).sqrMagnitude;
                if (d2 <= bd) { bd = d2; best = k; }
            }
            return best;
        }

        // ==================================================================
        // THE SOLVE: steps (2)-(6), everything after the capture and before the publish.
        // Pure array maths - no Transform, no Time, no Mesh - which is what lets it run on
        // a worker thread when Async Sim is on. `bk`/`bn` are the skinned surface it reads
        // and `outDisp` the render-mesh displacement it writes; in async mode those are
        // private snapshots, so the main thread can move on to the next frame meanwhile.
        // ==================================================================
        readonly System.Diagnostics.Stopwatch swSolve = new System.Diagnostics.Stopwatch();

        // Every live region solves on its own cage and ADDS its result into outDisp.
        void SolveAll(float pdt, float now, Vector3[] bk, Vector3[] bn, Vector3[] outDisp)
        {
            // Project ADDS into its target. The async buffer was once never cleared and summed
            // every frame since startup (measured: 44 m on the render mesh). Clear ONCE, here,
            // and let each region add its own share.
            System.Array.Clear(outDisp, 0, outDisp.Length);
            for (int i = 0; i < solvers.Count; i++)
            {
                RegionSolver r = solvers[i];
                if (r.cage == null || r.tuned == null || r.tuned.sfxMix <= 0f || r.registry.Count == 0) continue;
                SolveOne(r, pdt, now, bk, bn, outDisp);
            }
        }

        void SolveOne(RegionSolver s, float pdt, float now, Vector3[] bk, Vector3[] bn, Vector3[] outDisp)
        {
            float mix = Mathf.Clamp01(s.tuned.sfxMix);
            // (2) pull the skinned surface onto the cage
            swSolve.Reset(); swSolve.Start();
            s.cage.InterpBaked(bk, bn);
            System.Array.Copy(s.cage.simBaked, s.field.pos, s.field.n);
            System.Array.Copy(s.cage.simNormals, s.field.nrm, s.field.n);
            for (int i = 0; i < s.field.n; i++) s.field.mask[i] = s.cage.simWeight[i];
            swSolve.Stop(); FieldDiag.Mark("interp", (float)swSolve.Elapsed.TotalMilliseconds);

            // (3) frame reset; events age BEFORE the sources so every source in this
            //     frame sees the same ages
            s.field.BeginFrame(pdt, now);
            s.field.AgeEvents(pdt, 2.5f);

            s.ctx.p = s.tuned;
            s.ctx.dt = pdt; s.ctx.time = now;
            s.ctx.n = s.field.n;
            s.ctx.pos = s.field.pos; s.ctx.nrm = s.field.nrm; s.ctx.mask = s.field.mask;
            s.ctx.cageTris = s.cage.simTris;
            s.ctx.usedEdge = s.usedEdge; s.ctx.dens = s.dens;
            s.ctx.coreOverride = false; s.ctx.ovMask = null; s.ctx.ovIntensity = 0f;
            s.ctx.contactActive = false; s.ctx.axisQuality = 0f;

            // AMPLITUDES -> METRES, once a frame, never written back into `tuned`. The
            // config keeps the fraction the user typed; the sources read only these.
            float ampScale = s.tuned.amplitudeRelative ? Mathf.Max(s.ctx.regionRadius, 1e-4f) : 1f;
            s.ctx.rippleAmpM = s.tuned.rippleAmplitude * ampScale;
            // The wave's SPEED, WIDTH, RANGE and DECAY get the same treatment the amplitude
            // already had. In metres, Wave Speed 4 crossed a 46 cm breast in about 0.1 s,
            // which reads as the whole ball pulsing at once - resizing, not rippling.
            s.ctx.waveScale = ampScale;
            s.ctx.wobbleAmpM = s.tuned.wobbleAmplitude * ampScale;

            // Effect family. Resolved only when the STRING changes: FieldEffects.Resolve
            // lower-cases, which allocates, and this is the per-frame path.
            if (!s.effectResolved || !string.Equals(s.lastEffect, s.tuned.effect))
            {
                s.effectResolved = true;
                s.lastEffect = s.tuned.effect;
                s.family = SoftBodySuite.Field.FieldEffects.Resolve(s.lastEffect);
                Debug.Log("[SfxField] '" + meshNameCached + "/" + s.name + "' effect family: "
                          + SoftBodySuite.Field.FieldEffects.Name(s.family)
                          + " (from \"" + (s.lastEffect == null ? "" : s.lastEffect) + "\")");
                WarnGatedChannels(s);
                // Switching family is switching to a different effect. Every source that is about to be
                // gated OUT stops being prepared, so anything it was integrating would freeze
                // at its last value instead of decaying - MotionExciter's energy envelope in
                // particular, which feeds f.excite, which DeformField.AnyActive treats as "not
                // finished yet" and which would therefore have kept the proxy awake forever.
                // Reset the whole registry and the shared field so the new family starts clean.
                s.registry.ResetAll();
                s.field.ClearAll();
            }
            s.ctx.family = s.family;
            s.ctx.contactFootprint = SoftBodySuite.Field.FieldEffects.FootprintOnly(s.family);
            s.ctx.contactFootprintDent = SoftBodySuite.Field.FieldEffects.FootprintUsesDent(s.family);

            // The self hull is built by FieldPost, from the DEFORMED cage, because that is the
            // surface the rays are actually shot against (FieldPost.cs:109-111). This used to
            // hand SetCageForSelf the UNDEFORMED field.pos here, every frame, before any source
            // had run - and because SetCageForSelf only rebuilds its grid once per SelfRebuild
            // (0.2 s) and this call always got there first, FieldPost's own rebuild request was
            // ALWAYS refused by the timer. The triangles RayCage read were the deformed ones
            // (selfP is an alias) but the spatial buckets they were sorted into were the rest
            // pose's, so the DDA walked cells listing triangles that had moved out of them.
            // Nothing is armed from here now; all this does is RELEASE the hull when the
            // channel is off, so a stale alias cannot outlive the toggle.
            // (The old comment claiming SetCageForSelf reads Time.realtimeSinceStartup was
            // wrong: it takes `now` as a parameter - FieldCollider.cs:374-378 says so itself.)
            if (s.tuned == null || s.tuned.selfCollision <= 0.5f) s.col.SetCageForSelf(null, null, now);

            // (4) Prepare pass, then Write pass — two passes over the whole registry,
            //     never merged. A reduction one source computes in Prepare has to be
            //     visible to every source's Write, not just the ones that sort later.
            swSolve.Reset(); swSolve.Start();
            s.registry.PrepareAll(s.ctx);
            swSolve.Stop(); FieldDiag.Mark("prepare", (float)swSolve.Elapsed.TotalMilliseconds);

            for (int si = 0; si < s.registry.Count; si++)
            {
                FieldSource src = s.registry.Get(si);
                // WasActive, not IsActive: OrganicSource publishes ctx.coreOverride from
                // IsActive, and asking again mid-frame would answer a different question.
                if (!s.registry.WasActive(si)) continue;
                swSolve.Reset(); swSolve.Start();
                src.Write(s.ctx, s.field);
                swSolve.Stop(); FieldDiag.Mark(src.Id, (float)swSolve.Elapsed.TotalMilliseconds);
            }

            // (5) one shared finisher, each pass gated on postFlags
            swSolve.Reset(); swSolve.Start();
            FieldPost.Run(s.ctx, s.field);
            swSolve.Stop(); FieldDiag.Mark("post", (float)swSolve.Elapsed.TotalMilliseconds);

            // (6) sum the three accumulators, scale by the crossfade, project to render
            swSolve.Reset(); swSolve.Start();
            Vector3[] sd = s.cage.simDisp;
            // ---- THE LIMITER, MADE AN ACTUAL GUARANTEE ----------------------------------
            // Clamping the contact SEED to maxDent was not enough: the blur-and-pile pass
            // that follows roughly doubles it, so a 50 mm cap still produced a 95 mm contact
            // bucket. Measured. The cap has to be applied where the number is final.
            //
            // The CONTACT bucket is clamped and the other two are not, on purpose:
            //   * dispWave is the ripple/fat crest - an effect, sized by its own amplitude
            //     slider, and already a fraction of the region radius.
            //   * dispGuard is the anti-clip. It is the guarantee that flesh does not end up
            //     inside the clothes, and clamping a guarantee turns it into a suggestion.
            //     It shrinks on its own as the dent shrinks, which is the right dependency:
            //     measured 74 mm -> 42 mm from the seed clamp alone.
            float dentCap = 0f;
            if (s.tuned != null && s.tuned.maxDent > 0f) dentCap = s.tuned.maxDent;
            if (dentCap > 0f)
            {
                for (int i = 0; i < s.field.n; i++)
                {
                    float m = s.field.dispContact[i].magnitude;
                    if (m > dentCap) { s.field.dispContact[i] *= dentCap / m; telCapped++; }
                }
            }
            for (int i = 0; i < s.field.n; i++)
                sd[i] = (s.field.dispContact[i] + s.field.dispWave[i] + s.field.dispGuard[i]) * mix;
            float projAvg = settingsRef != null ? settingsRef.field.projAvg : 0f;
            float proxySmooth = settingsRef != null ? settingsRef.field.proxySmooth : 0f;
            if (projAvg > 0.5f || proxySmooth > 0.5f)
                s.cage.SmoothDisp(Mathf.RoundToInt(proxySmooth), Mathf.RoundToInt(projAvg));
            s.cage.Project(outDisp);
            swSolve.Stop(); FieldDiag.Mark("project", (float)swSolve.Elapsed.TotalMilliseconds);
        }

        // ---- ASYNC SIM ---------------------------------------------------------
        // Frame N: join the solve kicked in frame N-1, publish its result, capture this
        // frame's colliders and pose on the MAIN thread, snapshot the skinned surface,
        // and kick the next solve. It runs while Unity renders and the other plugins
        // update, and its result shows one frame later (Squish's async mode does the
        // same). Any exception turns async off for the session and says why.
        System.Threading.Tasks.Task solveTask;
        // Unity object names may only be read on the main thread; the solve logs this copy
        string meshNameCached = "?";
        Vector3[] workBaked, workNormals, dispWork;
        bool asyncHaveResult, asyncBroken;

        void JoinWorker()
        {
            if (solveTask == null) return;
            string err = null;
            try { solveTask.Wait(); }
            catch (System.Exception e) { err = e.GetBaseException().ToString(); }
            if (err == null && solveTask.IsFaulted && solveTask.Exception != null)
                err = solveTask.Exception.GetBaseException().ToString();
            solveTask = null;
            if (err != null)
            {
                asyncBroken = true; asyncHaveResult = false;
                Debug.LogWarning("[SfxField] async solve failed, falling back to the main thread: " + err);
                return;
            }
            asyncHaveResult = true;
        }

        void KickAsync(Transform tr, float pdt)
        {
            UpdateColliderClouds(tr);
            CaptureFrameInputs(tr, pdt);
            int nv = bakedVerts.Length;
            if (workBaked == null || workBaked.Length != nv)
            {
                workBaked = new Vector3[nv]; workNormals = new Vector3[nv];
                dispWork = new Vector3[disp.Length]; asyncHaveResult = false;
            }
            System.Array.Copy(bakedVerts, workBaked, nv);
            if (bakedNormals != null && bakedNormals.Length == nv) System.Array.Copy(bakedNormals, workNormals, nv);
            bool had = asyncHaveResult;
            if (had) System.Array.Copy(dispWork, disp, disp.Length);
            float now = Time.time;
            Vector3[] wb = workBaked, wn = workNormals, wd = dispWork;
            solveTask = System.Threading.Tasks.Task.Run(delegate { SolveAll(pdt, now, wb, wn, wd); });
            if (had) { FieldTelemetry(true, ""); PublishWithDisp(); }
            else Publish(false);
        }

        // ==================================================================
        // capture (the ONLY code here that reads a Transform)
        // ==================================================================

        void CaptureFrameInputs(Transform tr, float dt)
        {
            float ps = Mathf.Max(1e-4f, tr.lossyScale.x);
            int need = colCfg != null ? colCfg.Length : 0;
            foreach (KeyValuePair<string, MeshColliderCloud> kv in colMeshCloud) need += kv.Value.nd;
            if (capA == null || capA.Length < need)
            {
                int cap = Mathf.Max(need, 16);
                capA = new Vector3[cap]; capB = new Vector3[cap];
                capR = new float[cap]; capValid = new bool[cap]; capLimb = new bool[cap];
                capPrevA = new Vector3[cap];
                // the new capPrevA is all zeroes; leaving capPrevCount alone would make the
                // next relSpeed read (capA[c] - 0)/dt, i.e. a several-hundred-m/s phantom slap
                // that charges the impulse envelope and pushes a ripple event out of nothing
                capPrevCount = 0;
            }

            int cnt = 0;
            for (int c = 0; colCfg != null && c < colCfg.Length; c++)
            {
                if (!colCfg[c].enabled || !string.IsNullOrEmpty(colCfg[c].mesh)) continue;
                Transform ct = colTr != null ? colTr[c] : null;
                if (ct == null) continue;
                capA[cnt] = tr.InverseTransformPoint(ct.position);
                capR[cnt] = colCfg[c].radius / ps;
                capB[cnt] = colCfg[c].length > 0f
                    ? tr.InverseTransformPoint(ct.position + ct.forward * colCfg[c].length) : capA[cnt];
                capValid[cnt] = true;
                capLimb[cnt] = true;   // a collider the user placed on purpose counts as a presser
                cnt++;
            }
            // mesh colliders arrive as capsules too — MeshColliderCloud already reduces
            // a whole skinned mesh to auto-fitted bone capsules, so there is nothing for
            // a sphere path to do that this does not do better.
            foreach (KeyValuePair<string, MeshColliderCloud> kv in colMeshCloud)
            {
                MeshColliderCloud cl = kv.Value;
                for (int k = 0; k < cl.nd && cnt < capA.Length; k++)
                {
                    capA[cnt] = cl.ca[k]; capB[cnt] = cl.cb[k];
                    capR[cnt] = cl.cr[k] + cl.radius;
                    capValid[cnt] = true;
                    capLimb[cnt] = cl.cbone != null && k < cl.cbone.Length && IsLimbBone(cl.cbone[k]);
                    cnt++;
                }
            }
            capCount = cnt;
            for (int si = 0; si < solvers.Count; si++)
                solvers[si].col.SetCapsules(capA, capB, capR, capValid, capLimb, capCount);

            float idt = dt > 1e-6f ? 1f / dt : 0f;
            // A CHANGED capsule count means slot c is not the same collider it was last frame
            // (a disabled collider shifts every later one down), so the difference is not a
            // velocity. Skip the derivative for that one frame rather than fire a fake impact.
            bool prevOk = capPrevCount == cnt;
            for (int si = 0; si < solvers.Count; si++) CaptureRegion(solvers[si], tr, dt, idt, prevOk);
            for (int c = 0; c < capCount; c++) capPrevA[c] = capA[c];
            capPrevCount = capCount;
        }

        // Per region: its own flesh motion, its own picked ripple points, the nipple rays
        // near it, and whether a collider is touching IT.
        void CaptureRegion(RegionSolver s, Transform tr, float dt, float idt, bool prevOk)
        {
            FieldContext ctx = s.ctx;
            int[] si = s.sampleIdx;
            // flesh motion, proxy-local. The proxy is parented to the renderer, so a
            // proxy-local centroid does not move when the body does - the derivative has
            // to be taken in WORLD space and brought back into local axes, or every
            // motion-driven effect goes dead the moment the avatar is standing still
            // relative to its own mesh.
            Vector3 lc = Vector3.zero;
            float rr = 0f;
            if (si != null && si.Length > 0)
            {
                for (int i = 0; i < si.Length; i++) lc += bakedVerts[si[i]];
                lc /= si.Length;
                int stride = Mathf.Max(1, si.Length / 128);
                for (int i = 0; i < si.Length; i += stride)
                {
                    float d2 = (bakedVerts[si[i]] - lc).sqrMagnitude;
                    if (d2 > rr) rr = d2;
                }
            }
            Vector3 wc = tr.TransformPoint(lc);
            Vector3 velW = s.motionPrimed ? (wc - s.prevCentroidW) * idt : Vector3.zero;
            Vector3 accW = s.motionPrimed ? (velW - s.prevVel) * idt : Vector3.zero;
            Vector3 jerkW = s.motionPrimed ? (accW - s.prevAcc) * idt : Vector3.zero;
            s.prevCentroidW = wc; s.prevVel = velW; s.prevAcc = accW; s.motionPrimed = true;
            ctx.fleshCentroid = lc;

            // ripple start points: THIS region's picks, and the breast-bone rays near it
            int pc = 0;
            List<int> rpts = s.tuned != null ? s.tuned.ripplePoints : null;
            if (rpts != null)
                for (int k = 0; k < rpts.Count && pc < ctx.pickPts.Length; k++)
                    if (rpts[k] >= 0 && rpts[k] < bakedVerts.Length) ctx.pickPts[pc++] = bakedVerts[rpts[k]];
            ctx.pickCount = pc;
            int nc = 0;
            if (nipTip != null)
                for (int k = 0; k < nipTip.Length && nc < ctx.nipO.Length; k++)
                {
                    if (nipTip[k] == null || nipBase[k] == null) continue;
                    Vector3 o = tr.InverseTransformPoint(nipTip[k].position);
                    Vector3 d = tr.InverseTransformDirection(nipTip[k].position - nipBase[k].position);
                    if (d.sqrMagnitude < 1e-10f) continue;
                    // bones far from this paint belong to another body part
                    if ((o - lc).magnitude > 3f * Mathf.Max(ctx.regionRadius, 0.02f)) continue;
                    ctx.nipO[nc] = o; ctx.nipD[nc] = d.normalized; nc++;
                }
            ctx.nipCount = nc;
            ctx.fleshVel = tr.InverseTransformVector(velW);
            ctx.upLocal = tr.InverseTransformDirection(Vector3.up).normalized;
            ctx.fwdLocal = avatarRef != null
                ? tr.InverseTransformDirection(avatarRef.transform.forward).normalized : Vector3.forward;
            ctx.fleshAcc = tr.InverseTransformVector(accW);
            ctx.fleshJerk = tr.InverseTransformVector(jerkW);
            // how far the flesh would lag this frame if it did not follow at all: the
            // direction soft tissue drags toward, in metres, ready to scale by a slider
            ctx.dragVec = -ctx.fleshVel * dt;

            // relative collider/flesh speed while overlapping THIS region - the "was that a
            // slap or a rest" number. Capsule A-ends are enough: a capsule that rotates about
            // its own axis is not a hit.
            float rel = 0f;
            bool overlap = false;
            float reach = Mathf.Sqrt(rr) * 1.15f;
            for (int c = 0; c < capCount; c++)
            {
                Vector3 cp = ClosestOnSegment(capA[c], capB[c], lc);
                if ((lc - cp).magnitude < capR[c] + reach) overlap = true;
                if (prevOk && c < capPrevCount)
                {
                    Vector3 cv = (capA[c] - capPrevA[c]) * idt;
                    float rs = (cv - ctx.fleshVel).magnitude;
                    if (rs > rel) rel = rs;
                }
            }
            ctx.relSpeed = overlap ? rel : 0f;
            ctx.overlapping = overlap;
        }

        // Reduce every referenced collider mesh ONCE to auto-fitted bone capsules — no
        // per-frame baking. When the collider is this mesh itself, painted vertices are
        // excluded so the region does not collide with its own flesh; hands on the same
        // mesh still squash it.
        void UpdateColliderClouds(Transform tr)
        {
            if (colMeshSmr.Count == 0) return;
            regBounds.Clear();
            if (sampleIdx != null && sampleIdx.Length > 0)
            {
                Vector3 cen = Vector3.zero;
                for (int i = 0; i < sampleIdx.Length; i++) cen += bakedVerts[sampleIdx[i]];
                cen /= sampleIdx.Length;
                float rr = 0f;
                for (int i = 0; i < sampleIdx.Length; i++)
                {
                    float d = (bakedVerts[sampleIdx[i]] - cen).sqrMagnitude;
                    if (d > rr) rr = d;
                }
                regBounds.Add(new Vector4(cen.x, cen.y, cen.z, Mathf.Sqrt(rr) + 0.14f));
            }

            // Built ONCE per paint in ResolveRegions, not per frame. MeshProxy rebuilds this
            // set inside its own frame loop; here that is a HashSet plus one Add per painted
            // vertex every single frame that a self mesh collider is configured, and
            // allocation in the per-frame path is not allowed.
            HashSet<int> selfExclude = selfExcludeSet;
            foreach (KeyValuePair<string, SkinnedMeshRenderer> kv in colMeshSmr)
            {
                SkinnedMeshRenderer csmr = kv.Value;
                bool all = kv.Key == "*";
                if (csmr == null && !all) continue;
                bool self = all || ReferenceEquals(csmr, smr);

                MeshColliderCloud cloud;
                if (!colMeshCloud.TryGetValue(kv.Key, out cloud))
                { cloud = new MeshColliderCloud(); colMeshCloud[kv.Key] = cloud; }

                float rad = 0.015f;
                for (int c = 0; colCfg != null && c < colCfg.Length; c++)
                    if (colCfg[c].mesh == kv.Key && colCfg[c].radius > rad) rad = colCfg[c].radius;
                cloud.radius = rad;

                int excluded = self && selfExclude != null ? selfExclude.Count : 0;
                if (!cloud.HasBuild(excluded))
                {
                    List<Vector3> regionWorld = new List<Vector3>();
                    if (sampleIdx != null)
                        for (int i = 0; i < sampleIdx.Length; i++)
                            regionWorld.Add(tr.TransformPoint(bakedVerts[sampleIdx[i]]));
                    if (all) cloud.BuildFromSkinMulti(allColSmrs, smr, selfExclude, regionWorld);
                    else cloud.BuildFromSkin(csmr, self ? selfExclude : null, regionWorld);
                    Debug.Log("[SfxField] built " + (all ? "(all meshes)" : kv.Key) + " capsule colliders");
                }
                cloud.UpdateFrame(tr, regBounds);
            }
        }

        // ==================================================================
        // sleep gate
        // ==================================================================

        // Mirrors SquishSim.CheckAwake (SquishSim.cs:434-484): a sparse-stride bound
        // centre refreshed every frame (the region rides the animation even asleep) and
        // a bound radius that GROWS with the sample and never shrinks — a frozen radius
        // under blendshape growth would quietly erode the pre-wake margin.
        bool CheckAwake(float dt)
        {
            if (sampleIdx == null || sampleIdx.Length == 0) return false;
            int stride = Mathf.Max(1, sampleIdx.Length / 128);
            Vector3 acc = Vector3.zero; int cnt = 0;
            for (int i = 0; i < sampleIdx.Length; i += stride) { acc += bakedVerts[sampleIdx[i]]; cnt++; }
            if (cnt > 0) boundCenter = acc / cnt;
            float rMaxS = 0f;
            for (int i = 0; i < sampleIdx.Length; i += stride)
            {
                float rd2 = (bakedVerts[sampleIdx[i]] - boundCenter).sqrMagnitude;
                if (rd2 > rMaxS) rMaxS = rd2;
            }
            boundRadius = Mathf.Max(boundRadius, Mathf.Sqrt(rMaxS) * 1.15f);
            float reach = boundRadius + 0.12f;   // wake ~12 cm BEFORE contact: no first-touch lag

            bool near = false;
            Transform tr = go.transform;
            float ps = Mathf.Max(1e-4f, tr.lossyScale.x);
            for (int c = 0; colCfg != null && c < colCfg.Length && !near; c++)
            {
                if (!colCfg[c].enabled) continue;
                if (!string.IsNullOrEmpty(colCfg[c].mesh))
                {
                    MeshColliderCloud cl;
                    if (colMeshCloud.TryGetValue(colCfg[c].mesh, out cl) && cl.SphereNear(boundCenter, reach))
                        near = true;
                }
                else
                {
                    Transform ct = colTr != null ? colTr[c] : null;
                    if (ct == null) continue;
                    Vector3 cp0 = tr.InverseTransformPoint(ct.position);
                    float rad = colCfg[c].radius / ps;
                    Vector3 cp = cp0;
                    if (colCfg[c].length > 0f)
                    {
                        Vector3 b3 = tr.InverseTransformPoint(ct.position + ct.forward * colCfg[c].length);
                        cp = ClosestOnSegment(cp0, b3, boundCenter);
                    }
                    if ((boundCenter - cp).magnitude < rad + reach) near = true;
                }
            }

            if (near || AnyFieldActive()) { sleepT = 0f; return true; }
            sleepT += dt;
            return sleepT < 0.35f;   // grace: let residual fields settle before going quiet
        }

        static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float t = Vector3.Dot(p - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude);
            return a + ab * Mathf.Clamp01(t);
        }

        // ==================================================================
        // weld map
        // ==================================================================

        // Game meshes duplicate vertices wherever UVs or normals split; the cage build
        // depends on this hash MATCHING SquishProxy's (SquishProxy.cs:1676-1687) — a
        // 21-bit-per-axis packed long at 0.1 mm. Do not 'improve' it.
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
                       | (long)(uint)(Mathf.RoundToInt(v[i].z * 10000f) & 0x1FFFFF);
                int g;
                if (!groupOf.TryGetValue(k, out g)) { g = groups.Count; groupOf[k] = g; groups.Add(new List<int>(2)); }
                groups[g].Add(i);
                weldOf[i] = g;
            }
            weldMembers = groups.ToArray();
        }

        // ==================================================================
        // diagnostics
        // ==================================================================

        public void LogFieldInfo()
        {
            if (solvers.Count == 0)
                Debug.Log("[SfxField] '" + (smr != null ? smr.name : "?") + "': no painted region, no field");
            for (int ri = 0; ri < solvers.Count; ri++)
            {
                RegionSolver s = solvers[ri];
                Debug.Log("[SfxField] '" + (smr != null ? smr.name : "?") + "/" + s.name + "': cage="
                    + (s.cage != null ? s.cage.SimVertCount.ToString() : "none")
                    + " edge=" + s.usedEdge.ToString("0.00000")
                    + " dens=" + s.dens.ToString("0.00")
                    + " cageArea=" + s.cageArea.ToString("0.00000")
                    + " renderArea=" + renderArea.ToString("0.00000")
                    + " radius=" + (s.ctx != null ? s.ctx.regionRadius.ToString("0.0000") : "-")
                    + " sources=" + (s.registry != null ? s.registry.Count : 0)
                    + " effect=" + SoftBodySuite.Field.FieldEffects.Name(s.family)
                    + " sfxMix=" + (s.tuned != null ? s.tuned.sfxMix.ToString("0.00") : "n/a")
                    + " asyncSim=" + asyncSim);
                if (s.registry != null)
                    for (int i = 0; i < s.registry.Count; i++)
                    {
                        FieldSource src = s.registry.Get(i);
                        Debug.Log("[SfxField]   source " + src.Order + " " + src.Id
                            + " inFamily=" + SoftBodySuite.Field.FieldEffects.Allows(s.family, src.Id)
                            + " active=" + s.registry.WasActive(i));
                    }
            }
            Debug.Log(FieldDiag.Report());
        }

        // F11: per-node field dump for offline analysis
        public void DumpDebug()
        {
            if (!Alive) return;
            for (int i = 0; i < solvers.Count; i++)
            {
                if (solvers[i].field == null) continue;
                string p = FieldDiag.WriteCsv(smr.name + "_" + solvers[i].name, solvers[i].ctx, solvers[i].field);
                if (p.Length > 0) Debug.Log("[SfxField] dumped " + p);
            }
        }

        // One-shot identity check against the arrays that arrived this frame. Only
        // meaningful on a frame that ran the M1 path; on a contributing frame the
        // delta is the field, which is the point.
        public bool VerifyIdentityNow(out float maxDelta)
        {
            maxDelta = 0f;
            if (!Alive || idInput == null) return false;
            uint ck;
            FieldDiag.VerifyIdentity(idInput, bakedVerts, out maxDelta, out ck);
            return maxDelta == 0f;
        }
    }
}
