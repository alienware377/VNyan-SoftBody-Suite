using System.Collections.Generic;

namespace JelloStudio
{
    // ---------------------------------------------------------------------------
    // Jello Studio — mesh-level soft-body deformation for VNyan avatars.
    //
    // Instead of bending bones, the plugin bakes the skinned mesh every frame,
    // simulates the PAINTED vertices as point masses on top of the baked pose, and
    // renders the result through a live mesh copy. Rig-agnostic: works on any mesh
    // however messy its skeleton is, and blendshapes/face tracking keep working
    // because the bake happens after them.
    // ---------------------------------------------------------------------------

    public class SquishSettings
    {
        public bool enabled = true;
        public int substeps = 2;          // physics substeps per frame
        public bool halfRate = false;     // compute physics every 2nd frame (held between) — lighter on slow PCs
        public bool halfRateLerp = false; // half-rate, but held frames BLEND between ticks (smoother)
        public float maxDeltaTime = 0.033f;

        // ----- remeshed sim cage -----
        // Physics runs on a UNIFORMLY REMESHED duplicate of all enabled regions and
        // the deformation is projected back onto the original mesh, so the solver
        // never sees the original's mixed sub-mm topology.
        public float useRemesh = 0f;
        public float remeshSize = 0.008f;   // cage target edge length (m)
        public float remeshPasses = 4f;     // isotropic remesh iterations
        public float projAvg = 0f;          // projection averaging RANGE: Laplacian passes on the
                                            // cage displacement before projecting to the mesh (widens/softens)
        public float proxySmooth = 0f;      // peak/sharp-edge smoothing (Taubin, shrink-free) on the
                                            // cage displacement before projecting

        // ----- boundary seam smoothing (painted <-> unpainted edge) -----
        // The seam where the deformed region meets the still body is often sharp no
        // matter what the interior does. These smooth the FINAL displacement in a
        // metric band straddling that boundary (works with or without the cage).
        public float seamLevel = 0f;        // smoothing strength (Laplacian passes in the band)
        public float seamRange = 0f;        // band half-width (m) — how far the smoothing reaches either side
        public float seamMaxStretch = 0f;   // cap (m) on how far seam smoothing may drag a vert from its
                                            // pre-smoothing spot (0 = no limit) — stops over-stretched seam tris

        // ----- contact boost / slap (2nd-level squish, cage mode) -----
        // The projection smoothers that make everything buttery also dilute the sharp
        // local dent a collider should make. This stage re-measures the SMOOTHED cage
        // against the colliders and adds the missing push-out, after smoothing.
        public float boostStrength = 0f;    // extra push-out = residual penetration * strength (0 = off)
        public float boostSpread = 6f;      // diffusion passes over the cage (soft dent shoulder)
        public float boostMax = 0.05f;      // cap on added depth (m)
        public float slapSens = 0.6f;       // approach-speed threshold (m/s of penetration growth)
        public float slapPower = 0f;        // impulse strength for fast hits (0 = off)

        // ----- native bone physics override -----
        // Spring/dynamic bones fight the mesh-level squish when they drive the same
        // body parts. Optionally disable them (restored when turned off / unbound).
        public bool nativeDisable = false;   // VRM SpringBone / DynamicBone / MagicaCloth / SPCR
        public bool nativeScoped = true;
        public List<string> hiddenMeshes = new List<string>();   // meshes hidden via the Hide panel     // true = only solvers whose bones skin painted regions
    }

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

    // One deformable region on a mesh: painted vertex weights + all sim parameters.
    public class SquishRegion
    {
        public string name = "region";
        public bool enabled = true;

        // painted weights, sparse: vertIndex[i] has weight[i] (0..1)
        public List<int> vertIndex = new List<int>();
        public List<float> weight = new List<float>();

        // ----- motion -----
        public float jiggle = 1.0f;       // overall amplitude multiplier (0 = rigid)
        public float stiffness = 8.0f;    // spring back to the skinned pose (higher = tighter)
        public float damping = 0.55f;     // velocity damping 0..1
        public float bounce = 1.0f;       // inertia response to body motion (overshoot)
        public float maxOffset = 0.08f;   // hard clamp on deformation distance (metres)

        // ----- gravity -----
        public float gravity = 0.35f;     // pull toward world-down
        public bool gravityPoseOnly = true; // only when the reference bone leaves its rest pose
        public string refBone = "";       // pose reference; empty = auto (highest skin weight)

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
        public float xSmoothPasses = 8f;  // Taubin (shrink-free) smoothing passes on the output — kills peaks, keeps shape
        public List<SquishCollider> colliders = new List<SquishCollider>();
    }

    // All regions for one SkinnedMeshRenderer (matched by renderer name).
    public class SquishMesh
    {
        public string mesh = "";
        public bool enabled = true;
        public List<SquishRegion> regions = new List<SquishRegion>();
    }

    public class SquishConfig
    {
        public SquishSettings settings = new SquishSettings();
        public List<SquishMesh> meshes = new List<SquishMesh>();
    }
}
