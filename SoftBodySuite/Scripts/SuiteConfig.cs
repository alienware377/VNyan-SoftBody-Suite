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

    // Stage 4 - Field: the soft-flesh field effect family on its own cage.
    // Inherits StageRegion for the [JsonIgnore] paint/collider pointers that
    // SuiteRegion.Sync() wires; the inherited motion numbers are unused here.
    // Names that would collide with StageRegion (stiffness, waveSpeed, jiggle,
    // bulge, squish, stretch) are deliberately suffixed.
    public class FieldRegion : StageRegion
    {
        public float sfxMix = 0f;                 // 0 = shipped look only, 1 = full field
        public string effect = "Squish";          // preset family this region is tuned as

        // ----- contact core -----
        public float intensity = 2.0f;            // how hard a collider pushes the flesh in
        public float shapeSmoothing = 15f;        // blur passes on the contact footprint
        public float spread = 1f;                 // how far the halo reaches past the contact
        public float volumePreservation = 0.21f;  // squashed volume pushed back out elsewhere
        public float lateralFlow = 0.291667f;     // how much flesh slides sideways out of the dent
        public float contactHardness = 0.518519f; // 0 = soft mush, 1 = firm skin at the contact
        public float correctiveSmooth = 4f;       // anti-crinkle pass over the contact channel
        public float collisionOffset = 0f;        // extra skin thickness held off the collider (m)
        public float splay = 0f;                  // fans the escape direction outward at the rim
        public float selfCollision = 0f;          // region-vs-itself push apart

        // ----- inflate -----
        public float inflateEnable = 0f;          // 0/1 toggle for the inflate channel
        public float inflatePressure = 1.0f;      // outward pressure along the normals

        // ----- organic -----
        public float organicEnable = 0f;          // 0/1 toggle for the organic channel
        public float squishIntensity = 1.2f;      // organic squash amount
        public float directionInfluence = 0.7f;   // how much motion direction steers the squash
        public float collisionStrength = 1.2f;    // drives the core's intensity in organic mode
        public float collisionSoftness = 0.55f;   // softness field width around the contact
        public float stiffness01 = 0.6f;          // 0 = loose flesh, 1 = firm (organic only)
        public float velocityInfluence = 1.0f;    // how much body speed squashes the region
        public float organicJiggle = 0.5f;        // secondary wobble riding the organic squash

        // ----- impulse envelope -----
        public float impactSensitivity = 1.5f;    // how much a hit charges the shared envelope
        public float relaxTime = 0.6f;            // seconds for that envelope to fall back to 0
        public float motionSensitivity = 0f;      // 0 = contact only (no motion trigger)
        public float jerkFilter = 0.15f;          // EMA time constant on the jerk gate, seconds
        public float accRef = 20f;                // m/s^2 that maps to gate 1

        // Wave heights are a FRACTION OF THE REGION'S OWN SIZE, not metres.
        //
        // rippleAmplitude and wobbleAmplitude came across as absolute metres, tuned by
        // against demo blobs - objects far larger than any feature on a
        // VTuber. The built-in Ripple presets are 0.25 and 0.35 METRES (FieldPresets
        // .cs), and this fork's own default is 0.23. Measured on a real avatar at 0.15,
        // the same family of effect produced 7.45 cm of displacement on a chest region,
        // raised the 99th-percentile angle between adjacent face normals from 62.6 to
        // 73.7 deg, and took the count of edges over 90 deg from 449 to 664 - visible
        // polygon shards. At 0.05 and below the deformed mesh is indistinguishable from
        // the rest mesh. A magnitude-vs-gradient split showed the displacement field is
        // SMOOTH (2.6 mm mean change across a sharpened edge; only 2.6% of them sit on a
        // mask boundary), so it is NOT a discontinuity - the surface is simply pushed far
        // enough to fold through itself.
        //
        // The 8 mm cage argument makes the BLUR / SMOOTHING counts transfer unscaled,
        // because their radius is usedEdge*sqrt(iters) and the edge term cancels at
        // 0.008. It says nothing about amplitude, which scales with the FEATURE, not with
        // the cage density. So both amplitudes are read as a fraction of the region's own
        // radius (FieldContext.regionRadius) and turned into metres at solve time.
        //
        // waveWidth and waveRange are left ABSOLUTE on purpose: they are distances the
        // wave travels, already bounded by the geodesic field, and the Ricker reconstruction
        // was documented in metres. Only the HEIGHTS fold the surface through itself.
        //
        // Set false in the config file to go back to typing metres.
        public bool amplitudeRelative = true;

        // ----- ripple -----
        public float rippleEnable = 0f;           // 0/1 toggle for the ripple channel
        public float rippleMode = 1f;             // 0 = sine train, 1 = single-wave Ricker
        public float rippleAmplitude = 0.23f;     // wave height
        public float rippleFrequency = 8f;        // waves per metre (sine train only)
        public float waveSpeedFx = 4f;            // how fast the wave travels outward (m/s)
        public float rippleDecay = 1.5f;          // how quickly the wave dies with distance
        public float waveWidth = 0.10f;           // crest width of a single wave (m)
        public float waveRange = 0.5f;            // furthest the wave is evaluated (m)
        public float dentDepth = 1f;              // multiplier on the contact dent itself

        // PENETRATION LIMITER. The shipped Wobble and Squish stages have had one of these
        // since v1 (SuiteConfig.maxDent, 50 mm, with the excess handed to the evacuation
        // layers); the field stage shipped with NONE, and it shows. Measured on this avatar:
        // the contact bucket alone reached 139 mm on a region 193 mm across, driving flesh
        // deep inside the avatar's own arm, whereupon the anti-clip guard shoved it 74 mm
        // back out in a single UNSMOOTHED step - which is what a shard actually is.
        //
        // 0 disables the limiter and restores the old behaviour for an A/B.
        public float maxDent = 0.05f;

        // Push-out ring and Lift (Field/CarrySources.cs). Extras; off by default.
        public float ringEnable = 0f;
        public float ringAmount = 0.6f;
        public float ringDistance = 0.12f;   // x painted-region radius, from the dent's edge
        public float ringWidth = 0.12f;      // x painted-region radius
        public float liftEnable = 0f;
        public float liftAmount = 1f;
        public float liftSoftness = 0.4f;
        // how much of the push Lift follows along each body axis (up = world up)
        public float liftUp = 1f, liftForward = 0.2f, liftSide = 0.5f;
        public float rippleSmoothing = 10f;       // blur passes on the ripple crests
        // WHERE a ripple starts. Any mix may be on; each one that applies sends its own wave.
        public float rippleFromOuter = 1f;        // the outermost point, opposite a jolt (motion)
        public float rippleFromTouch = 1f;        // where a collider first touches
        public float rippleFromNipples = 0f;      // auto-found nipples (breast bones, else shape)
        public float rippleFromPicked = 0f;       // the points picked below
        public List<int> ripplePoints = new List<int>();   // picked: render vert indices on this mesh

        // ----- fat jiggle -----
        public float jiggleEnable = 0f;           // 0/1 toggle for the fat-jiggle channel
        public float wobbleAmplitude = 0.06f;     // noise wobble height (m)
        public float wobbleScale = 2.2f;          // noise feature size
        public float wobbleSpeed = 2.5f;          // how fast the noise field evolves
        public float lateralMotion = 1.2f;        // sideways part of the wobble
        public float normalMotion = 0.5f;         // along-normal part of the wobble
        public float wobbleDecay = 1.0f;          // how quickly the wobble settles after a hit
        public float wobbleSmoothing = 3f;        // blur passes on the wobble

        // ----- penetration -----
        public float penEnable = 0f;              // 0/1 toggle for the penetration channel
        public float gripIntensity = 1.5f;        // how tightly the opening closes on the collider
        public float tightness = 0.9f;            // resting narrowness of the opening
        public float reach = 0.025f;              // how far up the collider the grip extends (m)
        public float clenchAmount = 0.5f;         // rhythmic squeeze depth
        public float clenchSpeed = 5f;            // rhythmic squeeze rate
        public float penJiggle = 0.3f;            // noise wobble on the gripping ring
        public float wetness = 0.4f;              // computed and published; nothing consumes it yet
        public float penOffset = 0.004f;          // this channel's own skin thickness (m)
        public float penSmoothing = 3f;           // blur passes on the grip
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
        public FieldRegion field = new FieldRegion();   // stage 4: the field effect family

        // Point every stage block at THIS region's paint. Call after loading, after
        // painting, and after adding or removing a collider — the blocks hold
        // references, not copies, so a later edit to the lists is seen by all three.
        public void Sync()
        {
            Wire(wobble); Wire(jello); Wire(squish); Wire(field);
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

    // Stage 4 owns its own cage, at its own density, with its own diagnostics.
    public class FieldStageSettings : StageSettings
    {
        // OFF by default, and Field Mix starts at 0 on every region. A config that has
        // never seen this build therefore renders exactly as v2.0.0 did and costs
        // exactly what v2.0.0 cost - no cage is built until the user asks for one.
        public FieldStageSettings() { enabled = false; }

        // NOTE: StageSettings.substeps is inherited here and this stage does not read it.
        // The field is not an integrator - see the note above FieldProxy.RunField - so the
        // Substeps row was removed from the Field - Cage section rather than left wired to
        // nothing. The FIELD is kept so an existing config still round-trips unchanged.

        public float remeshSize = 0.008f;      // cage target edge (m) - the 8 mm reference edge
        public float remeshPasses = 8f;        // isotropic remesh iterations
        public float projAvg = 0f;             // Laplacian passes on the cage displacement
        public float proxySmooth = 0f;         // peak/sharp-edge smoothing before projecting
        public bool warnOnEdgeDrift = true;    // log when the cage's real edge != remeshSize
        public bool diagIdentityWatch = false; // per-frame bit-equality check at sfxMix 0
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
        // Offset (m) of the capture zone, in the AVATAR's own axes: x = to her right,
        // y = up, z = forward. Each garment vert looks for body to follow at
        // (its position minus this offset), so +z lets cloth hanging in FRONT of the
        // body bind to the surface behind it. 0,0,0 is the old behaviour.
        public float offsetX = 0f, offsetY = 0f, offsetZ = 0f;
        // FLOATING ACCESSORIES. A connected piece of a garment whose cloth mostly sits
        // further than floatDist off the body (a bow, a ribbon, a dangling charm) is
        // moved as ONE rigid piece - a best-fit rotation + shift of its whole patch -
        // instead of point by point. Point by point, each bit latches onto a different
        // patch of skin and neighbours tear apart (measured: edges stretched 68x on a
        // breast bow). rigidFloat blends 0 = old per-point, 1 = fully rigid.
        public float rigidFloat = 1f;
        public float floatDist = 0.02f;
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
        // SKIN GUARD: the very last cloth pass. Every garment vert near the skin, bound or
        // not, is pushed out of the body that is actually on screen, with a soft falloff
        // round each push. Only where a later stage moved the body; elsewhere untouched.
        public bool skinGuard = true;
        public float guardGap = 0.004f;      // nearest the cloth may come to the skin (m), never more than its rest gap
        public float guardReach = 0.06f;     // cloth further than this from the skin at bind is ignored (m)
        public float guardSoft = 1f;         // falloff: a push of X lifts the cloth up to X * this around it
        public float guardStrength = 1f;     // 0..1 blend of the push
        public float guardSunk = 0f;         // cloth the MODEL itself has sunk into the skin, up to this deep (m),
                                             // is watched too and lifted just above the skin, moving or not
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

    // What the two overall-jiggle sliders resolve to this frame. Plain statics so the
    // stage sims (some on worker threads) read them without touching the config.
    public static class JiggleGlobal
    {
        public static float strength = 1f;   // multiplies every stage's displacement
        public static float decayMul = 1f;   // multiplies every oscillator's decay rate
    }

    public class SuiteSettings
    {
        public int version = 3;           // 3 = the suite plus the field stage;
                                          // 2 = the merged suite; 1 = the three old files
        public bool enabled = true;

        // ----- overall jiggle, every stage at once (1 = exactly the tuned look) -----
        // Strength scales what each stage ADDS to the body: the same motion, smaller.
        // Settle scales how long every oscillator keeps ringing (its decay rate / settle).
        public float jiggleStrength = 1f;
        public float jiggleSettle = 1f;

        public StageSettings wobble = new StageSettings();
        public JelloStageSettings jello = new JelloStageSettings();
        public StageSettings squish = new StageSettings();
        public FieldStageSettings field = new FieldStageSettings();
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
