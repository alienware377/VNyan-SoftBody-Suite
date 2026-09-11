using System.Collections.Generic;
using Newtonsoft.Json;

namespace SoftBodySuite
{
    // ---------------------------------------------------------------------------
    // Soft Body Suite — the three studios (Wobble, Jell-o, Squish) in one plugin,
    // with clothes handled once at the very end instead of twice in the middle.
    //
    // A region is painted ONCE and carries three parameter blocks, one per stage.
    // That split is deliberate: the old plugins each kept their own copy of the
    // same field names with different tuned values (wobble jiggle 2.0 vs jello 1.0
    // vs squish 0.0 on the very same verts), and flattening them would quietly
    // throw away everything the user dialled in.
    // ---------------------------------------------------------------------------

    // ===================== per-stage region parameters =====================

    // Everything all three stages share. Values here are the stock defaults; each
    // stage's own block starts from them and is then overwritten from that stage's
    // old config file on first run.
    //
    // The four [JsonIgnore] fields at the top are POINTERS to the one painted region
    // this block belongs to, wired up by SuiteRegion.Sync(). They exist so each sim
    // can keep taking a single object that has both the paint and its own numbers —
    // exactly the shape the three sims were written against — without the paint data
    // being written to disk three times.
    public class StageRegion
    {
        [JsonIgnore] public string name = "region";
        [JsonIgnore] public bool enabled = true;
        [JsonIgnore] public List<int> vertIndex = new List<int>();
        [JsonIgnore] public List<float> weight = new List<float>();
        [JsonIgnore] public List<string> srcBones = new List<string>();
        [JsonIgnore] public List<SquishCollider> colliders = new List<SquishCollider>();

        // ----- motion -----
        public float jiggle = 1.0f;       // overall amplitude multiplier (0 = rigid)
        public float stiffness = 8.0f;    // spring back to the skinned pose (higher = tighter)
        public float damping = 0.55f;     // velocity damping 0..1
        public float bounce = 1.0f;       // inertia response to body motion (overshoot)
        public float maxOffset = 0.08f;   // hard clamp on deformation distance (metres)

        // ----- gravity -----
        public float gravity = 0.35f;     // pull toward world-down
        // These two describe the REGION, not a stage's feel, so they live on SuiteRegion
        // and are mirrored down here by Sync() for the sims to read.
        [JsonIgnore] public bool gravityPoseOnly = true;
        [JsonIgnore] public string refBone = "";

        // ----- waves / ripples -----
        public float clothRipple = 0.3f;  // neighbour-spring wave propagation (cloth-like)
        public float clothSize = 0.5f;    // spread speed/size of the cloth waves
        public float jello = 0.25f;       // whole-region resonant wobble (jell-o)
        public float jelloSize = 0.5f;    // wobble frequency scale (bigger = slower/larger)
        public float liquid = 0.0f;       // motion-spawned travelling surface waves
        public float liquidSize = 0.5f;   // wavelength of the liquid ripples
        public float waveSpeed = 1.0f;    // global wave tempo multiplier

        // ----- extra jiggle modes -----
        public float sway = 0.0f;         // lateral pendulum swing (side-to-side)
        public float twistJiggle = 0.0f;  // rotational wobble about the gravity axis
        public float pulse = 0.0f;        // ambient breathing along the normals
        public float pulseRate = 0.5f;    // breathing speed
        public float stretch = 0.0f;      // squash & stretch along the motion direction
        public float turbulence = 0.0f;   // organic noise wobble
        public float turbSize = 0.5f;     // turbulence feature size

        // ----- surface -----
        public float cellulite = 0.0f;    // static noise displacement along the normal
        public float celluliteSize = 0.5f;// noise feature size

        // ----- squish (collision) -----
        public float squish = 1.0f;       // how strongly colliders push the surface in
        public float squishDepth = 0f;    // max squish depth in metres (0 = auto from region size)
        public float bulge = 0.5f;        // volume-ish sideways bulge around a squish
        public float selfSquish = 0.5f;   // region-vs-region repulsion (e.g. cross-breast)
    }

    // Stage 1 — Wobble: the springy jiggle, sway, twist and rope pull.
    public class WobbleRegion : StageRegion
    {
        public float ropePull = 0f;          // the area trails the body like a weight on a rope
        public float ropePullEase = 1f;      // how quickly it catches back up
        public float jelloSpeed = 1f;        // how fast the jell-o wobble oscillates
        public float jelloRandomSize = 0f;   // how far the wobble centre wanders
        public float jelloRandomSpeed = 0.5f;// how quickly it wanders
        public float jelloRandom = 0f;       // legacy single randomiser, kept so old files load
        public float swaySpeed = 1f;         // pendulum swing rate
        public float swayDamp = 0.35f;       // how quickly the swing settles
        public float twistSpeed = 1f;        // twist oscillation rate
        public float twistDamp = 0.35f;      // how quickly the twisting settles
    }

    // Stage 2 — Jell-o: the XPBD solver on the remeshed cage.
    public class JelloRegion : StageRegion
    {
        public float maxDent = 0.05f;     // penetration limiter: dent depth where the flesh "gives way"
        public float evacBone = 0.5f;     // beyond maxDent: translate the region's driver bones away
        public float evacBlob = 0.5f;     // beyond maxDent: shift + squash the whole painted blob

        // ----- XPBD solver -----
        public float xIter = 5f;          // constraint iterations per substep
        public float xStretch = 0.9f;     // distance-constraint stiffness (surface integrity)
        public float xAttach = 0.25f;     // soft pull back to the skinned shape
        public float xMaxStretch = 0.1f;  // hard leash from the skinned position (m at weight 1)
        public float xPressure = 1.2f;    // displaced volume re-inflates the rest of the region
        public float xGrid = 0.006f;      // solver grid size (m) — node coarsening cell
        public float xGridMin = 0.002f;   // adaptive grid: cell size where edges are SMALLEST
        public float xGridMax = 0.02f;    // adaptive grid: cell size where edges are LARGEST
        public float xGridAuto = 1f;      // 1 = sync min/max to the region's measured edge range on rebuild
        public float xSigma = 0.008f;     // write-back blend width (m)
        public float xCorr = 0.004f;      // per-iteration correction clamp (m)
        public float xColRelax = 0.6f;    // collision projection strength per iteration
        public float xCompress = 0.12f;   // compression softness (0 = free squash, 1 = cloth)
        public float xBend = 0.5f;        // bending stiffness multiplier
        public float xTension = 0.3f;     // skin tension: smooths the DEFORMATION field (anti-crinkle)
        public float xSmoothPasses = 8f;  // Taubin (shrink-free) smoothing passes on the output
    }

    // Stage 3 — Squish: contact, marshmallow dent and bone evacuation.
    public class SquishRegion : StageRegion
    {
        public float maxDent = 0.05f;     // penetration limiter: dent depth where the flesh "gives way"
        public float evacBone = 0.5f;     // beyond maxDent: translate the region's driver bones away
        public float evacAllBones = 0f;   // ALSO translate nested/child driver bones individually
                                          // (multiplier on evacBone; 0 = topmost-only, the safe default)
        public float evacBlob = 0.5f;     // beyond maxDent: shift + squash the whole painted blob
    }

    // ===================== colliders / regions / meshes =====================

    // A sphere/capsule collider attached to any bone/transform (by name). Regions
    // squish against these — add hand bones to poke, or another mesh's root to let
    // that mesh squeeze the region.
    public class SquishCollider
    {
        public string bone = "";          // transform name (humanoid enum or raw name)
        public string mesh = "";          // if set: use this WHOLE skinned mesh as the collider
                                          // (baked every frame into a point cloud of `radius`
                                          // spheres, so it follows the animation; `bone` ignored)
        public float radius = 0.05f;      // metres (sphere radius / mesh-cloud sample radius)
        public float length = 0f;         // 0 = sphere; >0 = capsule along the bone's forward
        public bool enabled = true;
    }

    // One deformable region on a mesh: painted once, simulated three times.
    public class SuiteRegion
    {
        public string name = "region";
        public bool enabled = true;

        // painted weights, sparse: vertIndex[i] has weight[i] (0..1)
        public List<int> vertIndex = new List<int>();
        public List<float> weight = new List<float>();

        // bones this region was selected from (the vertex-group picker / bone-based apply).
        // The cloth pass uses it to filter garment verts to the same bones.
        public List<string> srcBones = new List<string>();

        public List<SquishCollider> colliders = new List<SquishCollider>();

        public bool gravityPoseOnly = true; // gravity only once the reference bone leaves rest
        public string refBone = "";         // pose reference; empty = auto (highest skin weight)

        // one tuned block per stage — see the header note on why these are not merged
        public WobbleRegion wobble = new WobbleRegion();
        public JelloRegion jello = new JelloRegion();
        public SquishRegion squish = new SquishRegion();

        // Point every stage block at THIS region's paint. Call after loading, after
        // painting, and after adding or removing a collider — the blocks hold
        // references, not copies, so a later edit to the lists is seen by all three.
        public void Sync()
        {
            Wire(wobble); Wire(jello); Wire(squish);
        }

        void Wire(StageRegion s)
        {
            s.name = name;
            s.enabled = enabled;
            s.vertIndex = vertIndex;
            s.weight = weight;
            s.srcBones = srcBones;
            s.colliders = colliders;
            s.gravityPoseOnly = gravityPoseOnly;
            s.refBone = refBone;
        }
    }

    // All regions for one SkinnedMeshRenderer (matched by renderer name).
    public class SuiteMesh
    {
        public string mesh = "";
        public bool enabled = true;
        public List<SuiteRegion> regions = new List<SuiteRegion>();
    }

    // ===================== settings =====================

    // Shared by every stage: whether it runs at all and how it is allowed to cheat
    // for framerate.
    public class StageSettings
    {
        public bool enabled = true;
        public int substeps = 2;          // physics substeps per frame — kept per stage
                                          // because it changes the answer, not just the cost
        public float maxDeltaTime = 0.033f;
        public bool halfRate = false;     // compute physics every 2nd frame (held between)
        public bool halfRateLerp = false; // half-rate, but held frames BLEND between ticks
        public bool asyncSim = false;     // run the physics on a worker thread (1 frame latency)
    }

    // Stage 2 also owns the remeshed cage and the smoothing that made it usable.
    public class JelloStageSettings : StageSettings
    {
        // ----- remeshed sim cage -----
        public float useRemesh = 0f;
        public float remeshSize = 0.008f;   // cage target edge length (m)
        public float remeshPasses = 4f;     // isotropic remesh iterations
        public float projAvg = 0f;          // Laplacian passes on the cage displacement before
                                            // projecting to the mesh (widens/softens)
        public float proxySmooth = 0f;      // peak/sharp-edge smoothing (Taubin) before projecting

        // ----- boundary seam smoothing (painted <-> unpainted edge) -----
        public float seamLevel = 0f;        // smoothing strength (Laplacian passes in the band)
        public float seamRange = 0f;        // band half-width (m)
        public float seamMaxStretch = 0f;   // cap (m) on how far seam smoothing may drag a vert

        // ----- contact boost / slap (2nd-level squish, cage mode) -----
        public float boostStrength = 0f;    // extra push-out = residual penetration * strength
        public float boostSpread = 6f;      // diffusion passes over the cage (soft dent shoulder)
        public float boostMax = 0.05f;      // cap on added depth (m)
        public float slapSens = 0.6f;       // approach-speed threshold (m/s of penetration growth)
        public float slapPower = 0f;        // impulse strength for fast hits (0 = off)
    }

    // ----- clothes -----
    // Two different fixes for the same problem, which used to live in two different
    // plugins and fight each other one frame apart:
    //   FOLLOW moves the CLOTHES onto the finished body (was Jell-o Studio's cage drive).
    //   GUARD  moves the BODY back inside the clothes (was Squish Studio's clip guard).
    // Both now run at the very end, in that order, on the true final body.
    public enum ClothMode { Off = 0, Follow = 1, Guard = 2, Both = 3 }

    public class ClothFollow
    {
        public bool enabled = false;       // was Jell-o Studio's "cage drive"
        public float range = 0.04f;        // max distance (m) from the body for a garment vert to
                                           // be driven; influence tapers to 0 at this range
        public float fitStrength = 1f;     // multiplier on the displacement the clothes replay
        public float inflate = 0f;         // static outward clearance (m) added to the driven area
        public float inflateDyn = 0f;      // extra outward push proportional to the OUTWARD part of
                                           // the motion — cloth leads the body out, never digs in
        public bool bindWhole = false;     // bind to the WHOLE body surface, not just painted regions
        public bool boneFilter = true;     // garments only bind on verts weighted to the region's
                                           // picked bones (srcBones)
        public float fillMax = 600f;       // gap infill: largest unbound island (in weld groups)
                                           // that may inherit motion from neighbours
        public float anchorMin = 150f;     // gap infill: smallest bound area that counts as an anchor
        public bool clothOutside = true;   // driven clothes may never come nearer to the skin
                                           // than `minClear`
        public float minClear = 0.002f;    // minimum garment-to-skin gap (m)
        public float folSharp = 0f;        // 0 = clothes ride the body's smoothed field (safe);
                                           // 1 = the raw, full-amplitude field
        public float folSmooth = 4f;       // smoothing passes on the CLOTH's copy of the field
        public float seamLevel = 0f;       // the follower's own copy of the seam smoother
        public float seamRange = 0f;
        public float seamMaxStretch = 0f;
    }

    public class ClothGuard
    {
        public bool enabled = false;       // was Squish Studio's "clip guard"
        public float clearance = 0.001f;   // EXTRA metres beyond the rest fit (0 = preserve the
                                           // exact rest relationship, never squash at rest)
        public float range = 0.05f;        // bind range: body verts within this of a garment
        public float strength = 1f;        // 0..1 blend of the correction
        public float rimFade = 0.02f;      // fade the guard out this far from a garment's rim/hem
    }

    // Per-garment override. Absent = use the suite default mode.
    public class ClothGarment
    {
        public string mesh = "";
        public ClothMode mode = ClothMode.Follow;
    }

    public class ClothSettings
    {
        public bool enabled = false;
        public ClothMode defaultMode = ClothMode.Follow;
        public ClothFollow follow = new ClothFollow();
        public ClothGuard guard = new ClothGuard();
        public List<ClothGarment> garments = new List<ClothGarment>();
    }

    public class SuiteSettings
    {
        public int version = 2;           // 2 = the merged suite; 1 = the three old files
        public bool enabled = true;

        public StageSettings wobble = new StageSettings();
        public JelloStageSettings jello = new JelloStageSettings();
        public StageSettings squish = new StageSettings();
        public ClothSettings cloth = new ClothSettings();

        // ----- native bone physics override -----
        // Spring/dynamic bones fight the mesh-level squish when they drive the same
        // body parts. Optionally disable them (restored when turned off / unbound).
        public bool nativeDisable = false;   // VRM SpringBone / DynamicBone / MagicaCloth / SPCR
        public bool nativeScoped = true;     // true = only solvers whose bones skin painted regions

        public List<string> hiddenMeshes = new List<string>();   // meshes hidden via the Hide panel
    }

    public class SuiteConfig
    {
        public SuiteSettings settings = new SuiteSettings();
        public List<SuiteMesh> meshes = new List<SuiteMesh>();

        // Always call this straight after loading or editing — the stage blocks are
        // empty of paint until it runs.
        public void Sync()
        {
            if (meshes == null) return;
            for (int m = 0; m < meshes.Count; m++)
            {
                SuiteMesh mesh = meshes[m];
                if (mesh == null || mesh.regions == null) continue;
                for (int r = 0; r < mesh.regions.Count; r++)
                    if (mesh.regions[r] != null) mesh.regions[r].Sync();
            }
        }
    }
}
