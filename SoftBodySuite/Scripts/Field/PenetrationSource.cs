using UnityEngine;

namespace SoftBodySuite.Field
{
    // Penetration channel. Order 400. Entirely stateless, by
    // design: axis inside -> walls out, axis gone -> rest, with no integrator to blow up.
    //
    //   OPENING   the flesh conform against the axis (walls ride ON the surface, never
    //             through it), scaled by the channel paint.
    //   GRIP      the flesh FOLLOWS the axis's per-frame displacement (ctx.dragVec) - friction,
    //             not attraction. Pulling flesh TOWARD the axis shrank the channel; following
    //             the direction of the push is what reads as tension.
    //   CLENCH    a sine pulse modulating the grip.
    //   JIGGLE    4D value noise on the nearby flesh, scaled by the shared excitation envelope.
    //   WETNESS   computed and published, NOT applied - see below.
    //
    // WETNESS IS COMPUTED AND DELIBERATELY NOT WRITTEN ANYWHERE
    // --------------------------------------------------------
    // The wetness is meant as a named attribute for a shader to read as sheen. In
    // this runtime the only channel we could push it down is VERTEX COLOUR, and vertex colour
    // is load-bearing for Poiyomi and MToon setups on real avatars - writing it would silently
    // change how somebody's model looks the first time they touch this slider. So the value is
    // computed faithfully and parked in the public Wet array for a future consumer (a material
    // property block, a float texture, an explicit opt-in), and nothing is written. This is in
    // the handover: it is a deliberate gap, not a dropped feature.
    public class PenetrationSource : FieldSource
    {
        public override string Id { get { return "penetration"; } }
        public override int Order { get { return 400; } }

        const float TAU = 6.283185307f;

        Vector3[] conformV;     // flesh conform against the axis
        Vector3[] offs;         // dent + wrap + jiggle, pre-smoothing
        Vector3[] offsSm;
        float[] stretch;
        float[] wet;
        float[] insideF;
        int ns;

        float engagement;
        bool warnedEdge;

        public float[] Wet { get { return wet; } }
        public float Engagement { get { return engagement; } }

        void Ensure(int n)
        {
            if (ns == n && conformV != null) return;
            ns = n;
            conformV = new Vector3[n]; offs = new Vector3[n]; offsSm = new Vector3[n];
            stretch = new float[n]; wet = new float[n]; insideF = new float[n];
        }

        public override bool IsActive(FieldContext ctx)
        {
            if (ctx == null || ctx.p == null || ctx.col == null) return false;
            if (!ctx.col.Any) return false;
            return ctx.p.penEnable > 0.5f;
        }

        public override void Reset() { warnedEdge = false; engagement = 0f; }

        public override void Prepare(FieldContext ctx)
        {
            SoftBodySuite.FieldRegion p = ctx.p;
            int n = ctx.n;
            Ensure(n);
            engagement = 0f;

            // The channel wants a finer cage than the default 8 mm: at 8 mm the 4 mm Collision
            // Offset is half an edge and the opening reads as faceted instead of smooth.
            if (!warnedEdge && ctx.usedEdge > 0.006f)
            {
                warnedEdge = true;
                Debug.LogWarning(string.Concat("[SfxField] Field/Penetration: cage edge is ",
                    (ctx.usedEdge * 1000f).ToString("0.0"), " mm but the channel is tuned for <= 6 mm ",
                    "(Collision Offset ", (p.penOffset * 1000f).ToString("0.0"),
                    " mm). Lower Remesh Size for this region or the opening will look faceted."));
            }

            float[] mask = ctx.mask;
            Vector3[] pos = ctx.pos;
            Vector3[] nrm = ctx.nrm;
            FieldCollider col = ctx.col;

            // own contact reductions: penetration must work with Contact intensity at 0, so it
            // cannot lean on ctx.pushAxis being filled in by ContactSource
            float sumW = 0f;
            Vector3 accP = Vector3.zero, accN = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                float depth; Vector3 cp, cn;
                bool inside = col.InsideAnalytic(pos[i], out depth, out cp, out cn);
                insideF[i] = inside ? 1f : 0f;
                if (!inside) continue;
                float w = mask != null ? mask[i] : 1f;
                if (w <= 0f) continue;
                accP += pos[i] * w; accN += nrm[i] * w; sumW += w;
            }
            float invW = 1f / Mathf.Max(sumW, 1e-6f);
            Vector3 cContact = accP * invW;
            Vector3 nMean = accN * invW;
            Vector3 push = -nMean.normalized;
            float axisQ = Mathf.Min(nMean.magnitude * 2f, 1f);

            float maxStretch = 0f;
            for (int i = 0; i < n; i++)
            {
                float mi = mask != null ? mask[i] : 1f;
                if (insideF[i] <= 0f) { conformV[i] = Vector3.zero; stretch[i] = 0f; wet[i] = 0f; continue; }
                Vector3 conform; float pen; Vector3 escDir;
                // splay 0 here: a channel wall has nowhere to
                // spill sideways to.
                col.FleshConform(pos[i], nrm[i], push, cContact, axisQ, 0f, p.penOffset,
                                 out conform, out pen, out escDir);
                conformV[i] = conform;
                float st = conform.magnitude;
                stretch[i] = st;
                float sw = st * mi;
                if (sw > maxStretch) maxStretch = sw;
            }

            // ENGAGEMENT is the MAX of stretch*mask over the cage, not the mean.
            // The peak stretch anywhere in the channel scales the drag of ALL the masked flesh,
            // so the whole region moves with the push instead of only the ring that is touched.
            engagement = Mathf.Min(maxStretch / 0.005f, 1.2f);

            // wetness: wet where the axis penetrates or stretches locally, by paint weight
            for (int i = 0; i < n; i++)
            {
                float mi = mask != null ? mask[i] : 1f;
                wet[i] = Mathf.Clamp01(insideF[i] + stretch[i] / 0.003f) * p.wetness * mi;
            }
        }

        public override void Write(FieldContext ctx, DeformField f)
        {
            SoftBodySuite.FieldRegion p = ctx.p;
            int n = ctx.n;
            float[] mask = ctx.mask;
            Vector3[] pos = ctx.pos;

            float pulse = 1f + Mathf.Sin(ctx.time * p.clenchSpeed * TAU) * p.clenchAmount;
            float tight = 0.3f + p.tightness * 0.7f;
            float cap = Mathf.Max(p.reach * 2f, 0.006f);          // skin stretches, then SLIPS
            Vector3 drag = ctx.dragVec;
            float jigBase = p.penJiggle * 0.02f;                  // noise is +-0.5, scale to mm

            for (int i = 0; i < n; i++)
            {
                float mi = mask != null ? mask[i] : 1f;
                if (mi <= 0f) { offs[i] = Vector3.zero; continue; }

                Vector3 dent = conformV[i] * Mathf.Clamp01(mi);

                float follow = p.gripIntensity * engagement * tight * pulse * mi;
                Vector3 wrap = drag * follow;
                float wm = wrap.magnitude;
                if (wm > cap && wm > 1e-6f) wrap *= cap / wm;

                Vector3 jig = Vector3.zero;
                float ex = f.excite != null ? f.excite[i] : 0f;
                float jigAmt = jigBase * ex * mi * engagement;
                if (jigAmt > 1e-9f)
                    jig = ValueNoise.Value4DVec(pos[i], ctx.time * 1.5f, 6f) * jigAmt;

                offs[i] = dent + wrap + jig;
            }

            int it = FieldPost.ScaledIters(p.penSmoothing, ctx.dens);
            ctx.blur.Blur(offs, mask, it, 0.85f, offsSm);
            for (int i = 0; i < n; i++)
            {
                if (offsSm[i].sqrMagnitude <= 1e-18f) continue;
                f.AddDisp(i, offsSm[i], FieldAcc.Contact);
            }

            // The penetration effect has no corrective smooth and no volume-preserve
            // stage - it does its own blur and ends. Only the hard anti-clip guarantee is
            // asked for, because that is the promise the channel is making.
            f.Require(FieldPostFlags.AntiClip);
        }

        public override void Describe(UiSchema schema)
        {
            const string S = "Field · Penetration";
            schema.AddSection(S);

            schema.AddToggle(S, "fx_pen_on", "Penetration", false, GetOn, SetOn);
            schema.Tooltip("fx_pen_on", "Channel behaviour: the walls open around the axis, grip "
                + "it, and follow it. Wants a cage edge of 6 mm or finer.");

            schema.AddSlider(S, "fx_grip", "Grip Intensity", 0f, 2f, 1.5f, GetGrip, SetGrip);
            schema.Tooltip("fx_grip", "How strongly the surrounding flesh follows the axis.");

            schema.AddSlider(S, "fx_tight", "Tightness", 0f, 1f, 0.9f, GetTight, SetTight);
            schema.Tooltip("fx_tight", "How closely the flesh sticks to the axis's motion.");

            schema.AddSlider(S, "fx_reach", "Reach", 0.002f, 0.3f, 0.025f, GetReach, SetReach);
            schema.Tooltip("fx_reach", "Distance the grip extends - and, doubled, the cap on how "
                + "far skin is dragged before it slips.");

            schema.AddSlider(S, "fx_clench", "Clench Amount", 0f, 1f, 0.5f, GetClench, SetClench);
            schema.Tooltip("fx_clench", "Amplitude of the pulsing squeeze.");

            schema.AddSlider(S, "fx_clenchspd", "Clench Speed", 0f, 20f, 5f, GetClenchSpd, SetClenchSpd);
            schema.Tooltip("fx_clenchspd", "Pulses per second of the squeeze.");

            schema.AddSlider(S, "fx_penjig", "Jiggle", 0f, 2f, 0.3f, GetJiggle, SetJiggle);
            schema.Tooltip("fx_penjig", "Secondary noise on the nearby flesh, driven by the shared "
                + "excitation envelope, so it answers the push instead of running constantly.");

            schema.AddSlider(S, "fx_wet", "Wetness", 0f, 1f, 0.4f, GetWet, SetWet);
            schema.Tooltip("fx_wet", "Computed and published for a future shader consumer. It does "
                + "NOT write vertex colour, because that would change how avatars using vertex "
                + "colour in their shaders look. Nothing reads it yet.");

            schema.AddSlider(S, "fx_penoffset", "Collision Offset", 0f, 0.2f, 0.004f, GetOffset, SetOffset);
            schema.Tooltip("fx_penoffset", "Minimum clearance kept above the axis - the anti-clip cap.");

            schema.AddSlider(S, "fx_pensm", "Smoothing", 0f, 10f, 3f, GetSmoothing, SetSmoothing);
            schema.Tooltip("fx_pensm", "Final smoothing of the whole channel deformation.");
        }

        static float GetOn(SoftBodySuite.FieldRegion r) { return r.penEnable; }
        static void SetOn(SoftBodySuite.FieldRegion r, float v) { r.penEnable = v; }
        static float GetGrip(SoftBodySuite.FieldRegion r) { return r.gripIntensity; }
        static void SetGrip(SoftBodySuite.FieldRegion r, float v) { r.gripIntensity = v; }
        static float GetTight(SoftBodySuite.FieldRegion r) { return r.tightness; }
        static void SetTight(SoftBodySuite.FieldRegion r, float v) { r.tightness = v; }
        static float GetReach(SoftBodySuite.FieldRegion r) { return r.reach; }
        static void SetReach(SoftBodySuite.FieldRegion r, float v) { r.reach = v; }
        static float GetClench(SoftBodySuite.FieldRegion r) { return r.clenchAmount; }
        static void SetClench(SoftBodySuite.FieldRegion r, float v) { r.clenchAmount = v; }
        static float GetClenchSpd(SoftBodySuite.FieldRegion r) { return r.clenchSpeed; }
        static void SetClenchSpd(SoftBodySuite.FieldRegion r, float v) { r.clenchSpeed = v; }
        static float GetJiggle(SoftBodySuite.FieldRegion r) { return r.penJiggle; }
        static void SetJiggle(SoftBodySuite.FieldRegion r, float v) { r.penJiggle = v; }
        static float GetWet(SoftBodySuite.FieldRegion r) { return r.wetness; }
        static void SetWet(SoftBodySuite.FieldRegion r, float v) { r.wetness = v; }
        static float GetOffset(SoftBodySuite.FieldRegion r) { return r.penOffset; }
        static void SetOffset(SoftBodySuite.FieldRegion r, float v) { r.penOffset = v; }
        static float GetSmoothing(SoftBodySuite.FieldRegion r) { return r.penSmoothing; }
        static void SetSmoothing(SoftBodySuite.FieldRegion r, float v) { r.penSmoothing = v; }
    }
}
