using System.Collections.Generic;

namespace WobbleStudio
{
    // ---------------------------------------------------------------------------
    // Wobble Studio — mesh-level soft-body deformation for VNyan avatars.
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

        // ----- native bone physics override -----
        // Spring/dynamic bones fight the mesh-level squish when they drive the same
        // body parts. Optionally disable them (restored when turned off / unbound).
        public bool nativeDisable = false;   // VRM SpringBone / DynamicBone / MagicaCloth / SPCR
        public bool nativeScoped = true;     // true = only solvers whose bones skin painted regions
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
        public float jelloRandom = 0f;    // wanders the jell-o wobble centre around (random-looking)
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
