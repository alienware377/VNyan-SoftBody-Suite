using UnityEngine;

namespace SoftBodySuite.Field
{
    // ONE shared excitation envelope for the whole field family. Ripple (600)
    // and Fat Jiggle (700) both read f.excite, so the impulse is tuned once and
    // every wave effect inherits it.
    //
    // Order 500 is deliberately AFTER every contact-writing source: CONTACT OWNS
    // THE ORIGIN. By the time this runs, ContactSource has already filled
    // ctx.contactActive / ctx.contactCentroid, and a hand resting on the chest
    // during a jump keeps its dent's ripple where the hand is instead of having
    // it yanked to a motion seed.
    //
    // Two paths feed one envelope, and they are NOT the same shape:
    //
    //   (a) CONTACT - rate-based.
    //       hit = overlap * min(relSpeed*0.6, 1) * dt * 6; E = min(E + hit*sens, 1.5).
    //       Correct because contact persists for tens of frames and the
    //       increments accumulate. relSpeed is RELATIVE (collider vs flesh, both
    //       captured on the main thread), so animating the body into a static
    //       collider fires identically to swinging a collider at a static body.
    //
    //   (b) MOTION - IMPULSIVE LATCH: E = max(E, gate * motionSensitivity).
    //       A jerk spike lasts 2-3 frames. Run through hit*dt*6 that deposits
    //       about 3*0.016*6 = 0.29 of the energy an equivalent contact deposits
    //       over tens of frames, i.e. roughly 20x too weak. Latch instead.
    //
    // The motion gate reads FILTERED jerk. An unfiltered jerk is a third
    // difference of a quantised rig signal and fires constantly on VMC or webcam
    // input - that is why jerkFilter is a parameter and not a constant.
    public class MotionExciter : FieldSource
    {
        public override string Id { get { return "motion"; } }
        public override int Order { get { return 500; } }

        // envelope
        float energy;
        public float Energy { get { return energy; } }

        // EMA chain: velocity -> acceleration -> jerk, all filtered
        Vector3 filtVel, prevFiltVel;
        Vector3 filtAcc, prevFiltAcc;
        Vector3 filtJerk;
        bool primed;
        bool gateLatched;
        bool latchRose;            // the motion gate went false -> true since the last Write
        bool prevContactActive;
        float lastGate;

        // event throttling
        float sinceEvent;
        float prevEnergy;

        // reductions
        Vector3 maskCentroid;
        float maskWeight;
        float cageSpan;

        // DERIVED, not measured: contact is the only defined trigger, so the jerk
        // threshold had to be picked. It is expressed against the two numbers the
        // user can already see - a jerk large enough to build accRef of
        // acceleration inside one filter time constant is a real event.
        //   jerkOn  = accRef / jerkFilter      (m/s^3)
        //   jerkOff = 0.45 * jerkOn            (hysteresis, stops chatter)
        const float JerkOffFrac = 0.45f;
        const float TeleportSpeed = 10f;     // m/s; faster is treated as a teleport
        // was 0.08 s: with arms rubbing the flesh nonstop that meant a fresh wave about
        // twelve times a second, which read as random triggering
        const float EventGap = 0.35f;        // min seconds between pushed events
        const float EventRise = 0.05f;       // min energy jump worth an event

        public override void Reset()
        {
            energy = 0f; prevEnergy = 0f;
            filtVel = Vector3.zero; prevFiltVel = Vector3.zero;
            filtAcc = Vector3.zero; prevFiltAcc = Vector3.zero;
            filtJerk = Vector3.zero;
            primed = false; gateLatched = false;
            sinceEvent = 999f;
            maskCentroid = Vector3.zero; maskWeight = 0f; cageSpan = 0.2f;
            nipN = 0; nipCageN = -1; nipTimer = 999f;
        }

        public override bool IsActive(FieldContext ctx)
        {
            if (ctx == null || ctx.p == null) return false;
            if (energy > 1e-5f) return true;
            if (ctx.overlapping && ctx.p.impactSensitivity > 0f) return true;
            if (ctx.p.motionSensitivity > 0f && ctx.fleshVel.sqrMagnitude > 1e-6f) return true;
            return false;
        }

        public override void Prepare(FieldContext ctx)
        {
            SoftBodySuite.FieldRegion p = ctx.p;
            float dt = Mathf.Max(ctx.dt, 1e-4f);
            sinceEvent += dt;
            prevEnergy = energy;

            // ---- filter chain -------------------------------------------------
            Vector3 raw = ctx.fleshVel;
            if (raw.magnitude > TeleportSpeed)
            {
                // teleport / scrub jump: not motion. Drop the whole chain so the
                // next real frame does not read a fabricated jerk.
                raw = Vector3.zero;
                filtVel = Vector3.zero; prevFiltVel = Vector3.zero;
                filtAcc = Vector3.zero; prevFiltAcc = Vector3.zero;
                filtJerk = Vector3.zero;
                gateLatched = false;
                primed = false;
            }

            float tau = Mathf.Max(p.jerkFilter, 0.02f);
            float a = 1f - Mathf.Exp(-dt / tau);

            if (!primed)
            {
                filtVel = raw;
                prevFiltVel = raw;
                prevFiltAcc = Vector3.zero;
                primed = true;
            }
            else
            {
                filtVel += (raw - filtVel) * a;
            }

            Vector3 acc = (filtVel - prevFiltVel) / dt;
            prevFiltVel = filtVel;
            filtAcc += (acc - filtAcc) * a;

            filtJerk = (filtAcc - prevFiltAcc) / dt;
            prevFiltAcc = filtAcc;

            // ---- motion gate --------------------------------------------------
            float accRef = Mathf.Max(p.accRef, 0.5f);
            // The onset threshold used to be accRef / tau, so turning Jerk Filter DOWN (to
            // react faster) and Motion Reference UP (for stronger hits) multiplied into
            // 60 / 0.02 = 3000 m/s3 - above anything a dancing avatar produced, and the
            // trigger went dead with no sign why (measured: peaks 2909, latched 0/122).
            // Below the default 0.15 s the threshold now stays put; the GATE still scales
            // with acc / accRef, so a small move still only gives a small kick.
            float jerkOn = accRef / Mathf.Max(tau, 0.15f);
            float jerkMag = filtJerk.magnitude;
            if (gateLatched) { if (jerkMag < jerkOn * JerkOffFrac) gateLatched = false; }
            else { if (jerkMag > jerkOn) { gateLatched = true; latchRose = true; } }

            float gate = 0f;
            if (gateLatched) gate = Mathf.Clamp01(filtAcc.magnitude / accRef);
            lastGate = gate;

            // diagnostics: the largest values seen since the last telemetry line. The
            // question these answer is "why does a dancing avatar never charge it" -
            // is the jerk short of the threshold, or is the sensitivity zero?
            if (jerkMag > DiagJerkMax) DiagJerkMax = jerkMag;
            float am = filtAcc.magnitude;
            if (am > DiagAccMax) DiagAccMax = am;
            float vm = raw.magnitude;
            if (vm > DiagVelMax) DiagVelMax = vm;
            DiagJerkOn = jerkOn;
            DiagSens = p.motionSensitivity;
            if (gateLatched) DiagLatched++;
            DiagFrames++;

            // ---- one shared decay, then the two paths -------------------------
            energy *= Mathf.Exp(-dt / Mathf.Max(p.relaxTime, 0.05f));

            // (a) contact: rate-based accumulation
            float overlap = ctx.overlapping ? 1f : 0f;
            float hit = overlap * Mathf.Min(ctx.relSpeed * 0.6f, 1f) * dt * 6f;
            energy = Mathf.Min(energy + hit * p.impactSensitivity, 1.5f);

            // (b) motion: impulsive latch
            if (p.motionSensitivity > 0f)
            {
                float lat = gate * p.motionSensitivity;
                if (lat > energy) energy = Mathf.Min(lat, 1.5f);
            }

            if (energy > DiagEMax) DiagEMax = energy;
            ReduceMask(ctx);
        }

        public static float DiagJerkMax, DiagAccMax, DiagVelMax, DiagJerkOn, DiagSens, DiagEMax;
        public static int DiagLatched, DiagFrames, DiagEvents;
        public static string DiagLine()
        {
            string s = "  | motion: sens=" + DiagSens.ToString("0.00")
                + " vel=" + DiagVelMax.ToString("0.00") + "m/s acc=" + DiagAccMax.ToString("0.0")
                + "m/s2 jerk=" + DiagJerkMax.ToString("0") + "/" + DiagJerkOn.ToString("0") + "m/s3"
                + " latched=" + DiagLatched + "/" + DiagFrames + " Emax=" + DiagEMax.ToString("0.000")
                + " waves=" + DiagEvents;
            DiagEvents = 0;
            DiagJerkMax = 0f; DiagAccMax = 0f; DiagVelMax = 0f; DiagEMax = 0f; DiagLatched = 0; DiagFrames = 0;
            return s;
        }

        public override void Write(FieldContext ctx, DeformField f)
        {
            int n = ctx.n;
            float[] ex = f.excite;
            float[] mask = ctx.mask;
            if (ex == null || mask == null) return;

            float e = energy;
            for (int i = 0; i < n; i++) ex[i] = e * mask[i];

            // the nipple finder runs whenever that start is on, so the marker dots show
            // where it landed before anything has triggered
            ctx.markNipCount = 0;
            if (ctx.p.rippleFromNipples > 0.5f)
            {
                UpdateNipples(ctx);
                for (int k = 0; k < nipN; k++) ctx.markNip[k] = nipNodes[k];
                ctx.markNipCount = nipN;
            }

            // ---- event origin -------------------------------------------------
            // ONE branch, and it matters: contact owns the origin whenever there
            // is contact. Only a body moving with nothing touching it seeds from
            // its own motion.
            // ---- WHEN a wave starts --------------------------------------------
            // Only on an EDGE: the motion gate switching on (a jump-start, a fling, a sudden
            // stop) or a collider FIRST touching. Every energy rise used to start one, and
            // with arms rubbing the flesh all the time that was a wave every 80 ms - the
            // "random" triggering.
            bool motionEdge = latchRose; latchRose = false;
            bool contactEdge = ctx.contactActive && !prevContactActive;
            prevContactActive = ctx.contactActive;
            if (!(motionEdge || contactEdge) || sinceEvent < EventGap) return;
            if (ctx.geo == null || ctx.pos == null) return;

            // ---- WHERE it starts -----------------------------------------------
            // Four starts, each its own toggle, and every one that applies sends its own
            // wave: the outermost point (jolts only - it needs a direction), the touch point
            // (first contact only), the nipples and the picked points (either trigger).
            SoftBodySuite.FieldRegion p = ctx.p;
            float eMotion = Mathf.Clamp(Mathf.Max(lastGate * p.motionSensitivity, energy), 0f, 1.5f);
            float eContact = Mathf.Clamp(energy, 0f, 1.5f);
            float eAny = motionEdge ? eMotion : eContact;
            int pushed = 0;
            if (motionEdge && p.rippleFromOuter > 0.5f && maskWeight > 0f)
            {
                // The flesh keeps going when the body changes speed, so it slaps out at its
                // OUTERMOST point on the side OPPOSITE the acceleration (stop moving forward
                // and the front of the breast claps forward). That extreme painted node is
                // where the wave starts, and it runs across to the far side from there.
                Vector3 axis = filtAcc;
                float m = axis.magnitude;
                if (m < 1e-4f) { axis = -filtVel; m = axis.magnitude; }
                Vector3 outDir = m > 1e-4f ? -axis / m : Vector3.forward;
                int node = -1;
                float best = float.MinValue;
                for (int i = 0; i < n; i++)
                {
                    if (mask[i] < 0.5f) continue;
                    float dd = Vector3.Dot(ctx.pos[i] - maskCentroid, outDir);
                    if (dd > best) { best = dd; node = i; }
                }
                pushed += Push(ctx, f, node, eMotion);
            }
            if (contactEdge && p.rippleFromTouch > 0.5f)
                pushed += Push(ctx, f, ctx.geo.NearestNode(ctx.contactCentroid, ctx.pos), eContact);
            if (p.rippleFromNipples > 0.5f)
                for (int k = 0; k < nipN; k++) pushed += Push(ctx, f, nipNodes[k], eAny);
            if (p.rippleFromPicked > 0.5f)
                for (int k = 0; k < ctx.pickCount; k++)
                    pushed += Push(ctx, f, ctx.geo.NearestNode(ctx.pickPts[k], ctx.pos), eAny);
            if (pushed > 0) { sinceEvent = 0f; DiagEvents += pushed; }
        }

        int Push(FieldContext ctx, DeformField f, int node, float e)
        {
            if (node < 0 || node >= ctx.n || e <= 1e-3f) return 0;
            f.PushEvent(node, ctx.pos[node], e, Mathf.Max(ctx.p.waveWidth, 0.02f));
            return 1;
        }

        // ---- NIPPLE FINDER ------------------------------------------------------
        // 1) Breast bones (FieldProxy hands over a ray per breast chain: from its last
        //    bone, along the chain): the painted node where that ray LEAVES the flesh.
        // 2) No such bones near the paint: shape. The most forward-and-slightly-down
        //    painted node, then the best one on the other side of the middle (a second
        //    breast), each re-aimed along its own lobe's average normal, because a
        //    sagging breast points down and out and "most forward" lands above the nipple.
        // Cage nodes ride the surface, so a result stays put as she moves; it is redone
        // every 5 s anyway (0.5 s while nothing is found) and logged when it changes.
        readonly int[] nipNodes = new int[4];
        int nipN, nipCageN = -1;
        float nipTimer = 999f;
        string nipKey = "";

        void UpdateNipples(FieldContext ctx)
        {
            nipTimer += Mathf.Max(ctx.dt, 0f);
            bool due = nipCageN != ctx.n || (nipN == 0 ? nipTimer > 0.5f : nipTimer > 5f);
            if (!due) return;
            nipTimer = 0f; nipCageN = ctx.n;
            int n = ctx.n;
            Vector3[] pos = ctx.pos;
            float[] mask = ctx.mask;
            if (pos == null || mask == null || n <= 0 || maskWeight <= 0f) { nipN = 0; return; }
            float R = Mathf.Max(ctx.regionRadius, 0.02f);
            int found = 0;
            string how = "breast bones";
            float tol = Mathf.Max(0.015f, 0.15f * R);
            for (int r = 0; r < ctx.nipCount && found < nipNodes.Length; r++)
            {
                Vector3 o = ctx.nipO[r], d = ctx.nipD[r];
                int best = -1; float bestT = float.MinValue;
                for (int i = 0; i < n; i++)
                {
                    if (mask[i] < 0.3f) continue;
                    Vector3 v = pos[i] - o;
                    float t = Vector3.Dot(v, d);
                    if (t < -R) continue;
                    if ((v - d * t).sqrMagnitude > tol * tol) continue;
                    if (t > bestT) { bestT = t; best = i; }
                }
                if (best >= 0 && !NearExisting(pos, best, found, 0.3f * R)) nipNodes[found++] = best;
            }
            if (found == 0)
            {
                how = "shape (no breast bones near the paint)";
                found = NipplesByShape(ctx, R);
            }
            nipN = found;
            string key = how + ": ";
            for (int k = 0; k < found; k++) key += nipNodes[k] + " ";
            if (key != nipKey)
            {
                nipKey = key;
                Debug.Log("[SfxField] ripple nipple finder: " + (found == 0 ? "none found" : found + " found by " + key));
            }
        }

        bool NearExisting(Vector3[] pos, int node, int count, float minD)
        {
            for (int k = 0; k < count; k++)
                if ((pos[nipNodes[k]] - pos[node]).sqrMagnitude < minD * minD) return true;
            return false;
        }

        int NipplesByShape(FieldContext ctx, float R)
        {
            int n = ctx.n;
            Vector3[] pos = ctx.pos;
            float[] mask = ctx.mask;
            Vector3 C = maskCentroid;
            Vector3 up = ctx.upLocal, fwd = ctx.fwdLocal;
            Vector3 right = Vector3.Cross(up, fwd);
            if (right.sqrMagnitude < 1e-8f) right = Vector3.right;
            right.Normalize();
            Vector3 D = (fwd - 0.35f * up).normalized;
            int a = -1; float sa = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (mask[i] < 0.5f) continue;
                float s = Vector3.Dot(pos[i] - C, D);
                if (s > sa) { sa = s; a = i; }
            }
            if (a < 0) return 0;
            float sideA = Vector3.Dot(pos[a] - C, right);
            int b = -1; float sb = float.MinValue;
            float sep2 = 0.25f * R * R;   // (0.5 R)^2
            for (int i = 0; i < n; i++)
            {
                if (mask[i] < 0.5f) continue;
                if (Vector3.Dot(pos[i] - C, right) * sideA > 0f) continue;   // same side as the first
                if ((pos[i] - pos[a]).sqrMagnitude < sep2) continue;
                float s = Vector3.Dot(pos[i] - C, D);
                if (s > sb) { sb = s; b = i; }
            }
            int cnt = 0;
            nipNodes[cnt++] = Refine(ctx, a, R, sideA, right, C);
            if (b >= 0 && sb >= sa - 0.35f * R)
                nipNodes[cnt++] = Refine(ctx, b, R, Vector3.Dot(pos[b] - C, right), right, C);
            return cnt;
        }

        int Refine(FieldContext ctx, int apex, float R, float side, Vector3 right, Vector3 C)
        {
            int n = ctx.n;
            Vector3[] pos = ctx.pos, nrm = ctx.nrm;
            float[] mask = ctx.mask;
            if (nrm == null) return apex;
            Vector3 pa = pos[apex];
            Vector3 N = Vector3.zero, L = Vector3.zero; int lc = 0;
            float r1 = 0.35f * R, r2 = 0.9f * R;
            for (int i = 0; i < n; i++)
            {
                if (mask[i] < 0.5f) continue;
                if (Vector3.Dot(pos[i] - C, right) * side < 0f) continue;
                float d2 = (pos[i] - pa).sqrMagnitude;
                if (d2 < r1 * r1) N += nrm[i];
                if (d2 < r2 * r2) { L += pos[i]; lc++; }
            }
            if (lc == 0 || N.sqrMagnitude < 1e-8f) return apex;
            N.Normalize(); L /= lc;
            int best = apex; float bs = float.MinValue; float r3 = 0.5f * R;
            for (int i = 0; i < n; i++)
            {
                if (mask[i] < 0.5f) continue;
                if (Vector3.Dot(pos[i] - C, right) * side < 0f) continue;
                if ((pos[i] - pa).sqrMagnitude > r3 * r3) continue;
                float s = Vector3.Dot(pos[i] - L, N);
                if (s > bs) { bs = s; best = i; }
            }
            return best;
        }

        // paint-weighted centroid and a rough painted span, for the motion seed
        void ReduceMask(FieldContext ctx)
        {
            int n = ctx.n;
            float[] mask = ctx.mask;
            Vector3[] pos = ctx.pos;
            if (mask == null || pos == null || n <= 0) { maskWeight = 0f; return; }

            Vector3 acc = Vector3.zero;
            float w = 0f;
            Vector3 lo = new Vector3(1e9f, 1e9f, 1e9f);
            Vector3 hi = new Vector3(-1e9f, -1e9f, -1e9f);
            for (int i = 0; i < n; i++)
            {
                float m = mask[i];
                if (m <= 0f) continue;
                Vector3 q = pos[i];
                acc += q * m; w += m;
                if (q.x < lo.x) lo.x = q.x; if (q.x > hi.x) hi.x = q.x;
                if (q.y < lo.y) lo.y = q.y; if (q.y > hi.y) hi.y = q.y;
                if (q.z < lo.z) lo.z = q.z; if (q.z > hi.z) hi.z = q.z;
            }
            maskWeight = w;
            if (w > 1e-6f)
            {
                maskCentroid = acc / w;
                cageSpan = Mathf.Max(0.02f, (hi - lo).magnitude);
            }
        }

        public override void Describe(UiSchema schema)
        {
            const string sec = "Field - Impulse";
            schema.AddSection(sec);

            schema.AddSlider(sec, "fx_impact", "Impact Sensitivity", 0f, 5f, 1.5f,
                GetImpact, SetImpact);
            schema.Tooltip("fx_impact",
                "How much energy a contact injects. Only RELATIVE speed inside the painted zone counts, so a slow press makes no wobble.");

            schema.AddSlider(sec, "fx_relax", "Relax Time", 0.05f, 5f, 0.6f,
                GetRelax, SetRelax);
            schema.Tooltip("fx_relax",
                "Seconds for the excitation to fade. The whole field returns exactly to rest.");

            // DEFAULT 0 on purpose: a config that has never seen this slider
            // behaves exactly as before, contact only, with no motion path at all.
            schema.AddSlider(sec, "fx_motionsens", "Motion Sensitivity", 0f, 5f, 0f,
                GetMotionSens, SetMotionSens);
            schema.Tooltip("fx_motionsens",
                "Jiggle from the body's OWN movement, with nothing touching it. 0 = contact only.");

            schema.AddSlider(sec, "fx_jerkfilter", "Jerk Filter", 0.02f, 0.5f, 0.15f,
                GetJerkFilter, SetJerkFilter);
            schema.Tooltip("fx_jerkfilter",
                "Smoothing on the motion detector, in seconds. Raise it if webcam or VMC tracking makes the flesh twitch constantly.");

            schema.AddSlider(sec, "fx_accref", "Move needed (lower = easier)", 1f, 60f, 20f,
                GetAccRef, SetAccRef);
            schema.Tooltip("fx_accref",
                "How hard a move (m/s^2) must be to give a FULL-strength kick. LOWER = gentler moves trigger it. "
                + "High values mean only violent jolts do anything; at 60 a normal dance barely registers.");
        }

        // delegates are built once at UI build, never per frame
        static float GetImpact(SoftBodySuite.FieldRegion r) { return r.impactSensitivity; }
        static void SetImpact(SoftBodySuite.FieldRegion r, float v) { r.impactSensitivity = v; }
        static float GetRelax(SoftBodySuite.FieldRegion r) { return r.relaxTime; }
        static void SetRelax(SoftBodySuite.FieldRegion r, float v) { r.relaxTime = v; }
        static float GetMotionSens(SoftBodySuite.FieldRegion r) { return r.motionSensitivity; }
        static void SetMotionSens(SoftBodySuite.FieldRegion r, float v) { r.motionSensitivity = v; }
        static float GetJerkFilter(SoftBodySuite.FieldRegion r) { return r.jerkFilter; }
        static void SetJerkFilter(SoftBodySuite.FieldRegion r, float v) { r.jerkFilter = v; }
        static float GetAccRef(SoftBodySuite.FieldRegion r) { return r.accRef; }
        static void SetAccRef(SoftBodySuite.FieldRegion r, float v) { r.accRef = v; }
    }
}
