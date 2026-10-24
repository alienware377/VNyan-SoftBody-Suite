using UnityEngine;

namespace SoftBodySuite.Field
{
    // The flesh conform: the contact core, made of the flesh conform and the natural
    // squish core. Order 100, so it runs
    // first and every later source can read the contact reductions it publishes.
    //
    // Two passes:
    //   PASS 1  SOFT FOOTPRINT - the conform VECTOR is diffused (Shape Smoothing) BEFORE it is
    //           applied, so the footprint becomes the local average of the contact. Hard
    //           collider edges round off and the outer rim anticipates the dent instead of
    //           cutting dead. Volume PILES on top of it: displaced material heaps up along the
    //           smoothed escape direction, scaled by the halo. Without the pile the radial
    //           escape stamps a flat shelf on the collider's face (measured).
    //   PASS 2  HARD CLEANUP - a classic proximity conform at the ALREADY DISPLACED position,
    //           scaled by Contact Hardness. Below 1 it leaves a slight overlap, which is what
    //           flesh does.
    //
    // The outer ring bulge and the lateral flow are NOT here: they are FieldPost's
    // pass (a), reading f.halo and f.seed, because they must run once for all sources.
    public class ContactSource : FieldSource
    {
        public override string Id { get { return "contact"; } }
        public override int Order { get { return 100; } }

        // per-node scratch, grown only when the cage changes
        // how many nodes the limiter caught last frame, for the telemetry line
        public static int clampedThisFrame;
        Vector3[] seedVec;      // pre-blur contact seed (conform * yield)
        float[] seedMag;        // |seedVec|
        Vector3[] soft;         // pass-1 diffused footprint
        float[] insideF;        // 1 inside any collider, 0 outside
        float[] penF;           // flesh-conform penetration, metres
        float[] yieldF;         // clamp01(mask * intensity)
        int ns;

        public Vector3[] SeedVec { get { return seedVec; } }

        void Ensure(int n)
        {
            if (ns == n && seedVec != null) return;
            ns = n;
            seedVec = new Vector3[n]; seedMag = new float[n]; soft = new Vector3[n];
            insideF = new float[n]; penF = new float[n]; yieldF = new float[n];
        }

        // The knob that switches the conform on is NOT the same one in every family, and
        // testing p.intensity everywhere made two of them unswitchable:
        //   footprint families (Ripple, Fat Jiggle) have no Intensity control at all - their
        //     footprint is clamp01(Mask * Dent Depth), so Dent Depth is the gate;
        //   Organic drives the core from Collision Strength through ctx.ovIntensity, and
        //     OrganicSource publishes that in its own IsActive, which runs AFTER this one -
        //     so this has to read the region field directly rather than the override.
        public override bool IsActive(FieldContext ctx)
        {
            if (ctx == null || ctx.p == null || ctx.col == null) return false;
            if (!ctx.col.Any) return false;
            if (ctx.contactFootprint) return ctx.p.dentDepth > 0.0001f;
            if (ctx.family == FieldEffects.Organic)
                return ctx.p.organicEnable > 0.5f && ctx.p.collisionStrength > 0.0001f;
            return ctx.p.intensity > 0.0001f;
        }

        // ---------------------------------------------------------------- reductions
        // One sweep over the whole cage producing the three global contact statistics the
        // later passes read. LIMITATION, known and accepted:
        // these are GLOBAL reductions. Two colliders pressing different parts of the SAME
        // paint mask blend their axes; axisQuality degrades that case toward the radial escape
        // rather than flattening along an invented direction. One contact per region is the
        // first-class supported case.
        public override void Prepare(FieldContext ctx)
        {
            DeformField f = ctx.field;
            int n = ctx.n;
            Ensure(n);

            float[] mask = (ctx.coreOverride && ctx.ovMask != null) ? ctx.ovMask : ctx.mask;
            Vector3[] pos = ctx.pos;
            Vector3[] nrm = ctx.nrm;
            FieldCollider col = ctx.col;

            // the inside test is the expensive half and each node is independent: all
            // cores fill insideF, then the cheap reduction below runs once, in order
            float[] ins = insideF;
            ParallelLoop.For(n, delegate (int lo, int hi)
            {
                for (int i = lo; i < hi; i++)
                {
                    float depth; Vector3 cp, cn;
                    ins[i] = col.InsideAnalytic(pos[i], out depth, out cp, out cn) ? 1f : 0f;
                }
            });
            float sumW = 0f;
            Vector3 accP = Vector3.zero, accN = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                bool inside = insideF[i] > 0f;
                if (!inside) continue;
                float w = mask != null ? mask[i] : 1f;
                if (w <= 0f) continue;
                accP += pos[i] * w;
                accN += nrm[i] * w;
                sumW += w;
            }

            float invW = 1f / Mathf.Max(sumW, 1e-6f);
            Vector3 cContact = accP * invW;
            Vector3 nMean = accN * invW;
            Vector3 push = -nMean.normalized;
            // Axis quality: |mean(n*w)| * (1/mean_w) * 2, clamped to 1. The means
            // share the same divisor n, so the ratio is |SUM(n*w)| / SUM(w) * 2 exactly - which
            // is nMean.magnitude * 2, not nMean.magnitude/sumW*2. Coherent contact -> 1; an
            // enveloping grip or a pinch, where the normals cancel, -> 0 and the flattening
            // loses its vote to the radial spill.
            float axisQ = Mathf.Min(nMean.magnitude * 2f, 1f);

            ctx.contactCentroid = cContact;
            ctx.pushAxis = push;
            ctx.axisQuality = axisQ;
            ctx.contactActive = sumW > 1e-6f;

            f.contactCentroid = cContact;
            f.pushAxis = push;
            f.axisQuality = axisQ;
            f.contactWeightSum = sumW;
        }

        // ---------------------------------------------------------------- write
        public override void Write(FieldContext ctx, DeformField f)
        {
            SoftBodySuite.FieldRegion p = ctx.p;
            int n = ctx.n;
            Ensure(n);

            float[] mask = (ctx.coreOverride && ctx.ovMask != null) ? ctx.ovMask : ctx.mask;
            float shapeSm = ctx.coreOverride ? ctx.ovShapeSmoothing : p.shapeSmoothing;
            float spread = ctx.coreOverride ? ctx.ovSpread : p.spread;
            float hardness = ctx.coreOverride ? ctx.ovContactHardness : p.contactHardness;
            float splay = ctx.coreOverride ? ctx.ovSplay : p.splay;
            float offset = p.collisionOffset;
            float intensity = ctx.coreOverride ? ctx.ovIntensity : p.intensity;
            // Ripple declares fx_dentdepth but must not re-implement the conform, and by
            // Order 600 dispContact also holds Inflate/Organic/Penetration. So the dent
            // multiplier is applied HERE, on the conform contribution it actually means.
            float dent = p.dentDepth;
            float maxDent = p.maxDent;
            float volPres = p.volumePreservation;

            Vector3[] pos = ctx.pos;
            Vector3[] nrm = ctx.nrm;
            FieldCollider col = ctx.col;
            Vector3 push = ctx.pushAxis;
            Vector3 cContact = ctx.contactCentroid;
            float axisQ = ctx.axisQuality;

            // Ripple / Fat Jiggle / Penetration are NOT built on the core. Their contact is
            // the bare flesh conform scaled by clamp01(Mask * Dent Depth) and nothing else
            // - no Shape Smoothing
            // diffusion, no volume pile, no Contact Hardness cleanup. coreOverride is the
            // Organic family, which DOES use the core, so it can never be footprint mode.
            bool footprint = ctx.contactFootprint && !ctx.coreOverride;
            // Strictly, all three footprint families would use a LITERAL 0.0 for splay
            // - they have no Splay control of their own.
            // Here it is kept, because the
            // panel shows a Splay slider whatever the effect, and a slider that silently
            // does nothing under Ripple / Fat Jiggle reads as broken. Default is 0, so the
            // look is unchanged until someone moves it.
            // if (footprint) splay = 0f;
            float fpScale = (footprint && ctx.contactFootprintDent) ? dent : 1f;

            // ---- conform sweep: outside nodes cost nothing beyond the inside flag
            for (int i = 0; i < n; i++)
            {
                float mi = mask != null ? mask[i] : 1f;
                // yield is clamp01(Mask * Strength) and exists for EVERY painted
                // node, inside or not - pass 2 below needs it on nodes the footprint has since
                // pushed into the collider. In footprint mode the scale is
                // clamp01(Mask * Dent Depth) instead - clamp01(Mask) for Penetration, which
                // has no Dent Depth - and Intensity is not one of those families' controls at all.
                yieldF[i] = footprint ? Mathf.Clamp01(mi * fpScale) : Mathf.Clamp01(mi * intensity);
                if (insideF[i] <= 0f)
                {
                    seedVec[i] = Vector3.zero; seedMag[i] = 0f; penF[i] = 0f;
                    f.seed[i] = 0f; f.clear[i] = 0f;
                    continue;
                }
                Vector3 conform; float pen; Vector3 escDir;
                col.FleshConform(pos[i], nrm[i], push, cContact, axisQ, splay, offset,
                                 out conform, out pen, out escDir);
                penF[i] = pen;
                if (f.seedLimb != null) f.seedLimb[i] = col.lastLimb;
                if (f.limbDepth != null) f.limbDepth[i] = col.lastLimb ? (pen > 0f ? pen : col.lastDeepPen) : 0f;
                seedVec[i] = footprint ? conform * yieldF[i] : conform * (yieldF[i] * dent);
                // ---- penetration limiter ----
                // Clamp the DENT, not the whole pipeline: past maxDent the flesh "gives
                // way" rather than following the collider in arbitrarily deep. The shipped
                // stages spend the excess on evacuation; the field stage has no evacuation
                // layer, so here it is simply dropped - which is still strictly better than
                // a 139 mm dent, and leaves the anti-clip with a few mm to fix instead of
                // seventy.
                if (maxDent > 0f)
                {
                    float sm0 = seedVec[i].magnitude;
                    if (sm0 > maxDent) { seedVec[i] *= maxDent / sm0; clampedThisFrame++; }
                }
                seedMag[i] = seedVec[i].magnitude;
                f.seed[i] = seedMag[i];
                f.clear[i] = pen;
            }

            if (footprint)
            {
                // The halo is still produced: FieldPost's corrective smooth gates on it, and a
                // footprint with no halo would be smoothed nowhere. It is the only part of the
                // core these three families keep.
                int itH = FieldPost.ScaledIters(spread, ctx.dens);
                ctx.blur.Blur(seedMag, mask, itH, 0.9f, f.halo);
                for (int i = 0; i < n; i++)
                {
                    Vector3 sv = seedVec[i];
                    if (sv.sqrMagnitude <= 1e-18f) continue;
                    f.AddDisp(i, sv, FieldAcc.Contact);
                    f.dir[i] = sv.normalized;
                }
                // NOT VolumePreserve: pass (a) is the core's ring bulge and lateral flow, and
                // none of these three families has it. AntiClip stays - it is the hard no-clipping
                // guarantee, not a look.
                f.Require(FieldPostFlags.CorrectiveSmooth | FieldPostFlags.SkinRamp
                          | FieldPostFlags.AntiClip);
                if (p.selfCollision > 0.5f) f.Require(FieldPostFlags.SelfContact);
                return;
            }

            // ---- pass 1: soft footprint + halo
            int itSoft = FieldPost.ScaledIters(shapeSm, ctx.dens);
            ctx.blur.Blur(seedVec, mask, itSoft, 0.85f, soft);
            int itHalo = FieldPost.ScaledIters(spread, ctx.dens);
            ctx.blur.Blur(seedMag, mask, itHalo, 0.9f, f.halo);

            for (int i = 0; i < n; i++)
            {
                Vector3 s = soft[i];
                if (s.sqrMagnitude <= 1e-18f && f.halo[i] <= 0f) continue;
                float mi = mask != null ? mask[i] : 1f;
                Vector3 dir = s.normalized;
                // 0.35 is a fixed constant. The pile rides the SMOOTHED escape direction, which
                // points away from the collider face the vertex landed on, so it can never
                // reintroduce penetration.
                Vector3 pile = dir * (f.halo[i] * 0.35f * volPres * mi);
                f.AddDisp(i, s + pile, FieldAcc.Contact);
                f.dir[i] = dir;
            }

            // ---- pass 2: hard cleanup at the DEFORMED position
            // FieldPost.RecomputeNormals has not run yet, so the deformed position is derived
            // here from the contact accumulator. ContactSource is Order 100 and the first
            // writer, so dispContact holds exactly pass 1 at this point.
            // It runs over every PAINTED node, not only the ones that started inside: the soft
            // footprint drags neighbours in, and those are exactly the ones that would be left
            // clipped if the cleanup only revisited the original contact set.
            if (hardness > 0.0001f)
            {
                Vector3[] dc = f.dispContact;
                float[] yf = yieldF;
                ParallelLoop.For(n, delegate (int lo, int hi)
                {
                    for (int i = lo; i < hi; i++)
                    {
                        if (yf[i] <= 0f) continue;
                        Vector3 dp = pos[i] + dc[i];
                        Vector3 c2; float pen2;
                        if (!col.Conform(dp, nrm[i], offset, out c2, out pen2)) continue;
                        dc[i] += c2 * (yf[i] * hardness);   // own slot only
                    }
                });
            }

            f.Require(FieldPostFlags.VolumePreserve | FieldPostFlags.CorrectiveSmooth
                      | FieldPostFlags.SkinRamp | FieldPostFlags.AntiClip);
            if (p.selfCollision > 0.5f) f.Require(FieldPostFlags.SelfContact);
        }

        // ---------------------------------------------------------------- ui
        public override void Describe(UiSchema schema)
        {
            const string S = "Field · Contact";
            schema.AddSection(S);

            schema.AddSlider(S, "fx_intensity", "Intensity", 0f, 2f, 2.0f, GetIntensity, SetIntensity);
            schema.Tooltip("fx_intensity", "How much of the contact each node yields to. "
                + "Multiplied by the paint weight and clamped, so 1 is already full yield on fully painted flesh.");

            schema.AddSlider(S, "fx_shapesm", "Shape Smoothing", 0f, 15f, 15f, GetShapeSm, SetShapeSm);
            schema.Tooltip("fx_shapesm", "Diffuses the dent BEFORE it is applied: high = rounded, "
                + "soft footprint; low = the collider's own silhouette. One of the three sliders "
                + "that separate Soft Flesh (8) from Memory Foam (10) and Elastic Rubber (4).");

            schema.AddSlider(S, "fx_spread", "Spread", 0f, 25f, 1f, GetSpread, SetSpread);
            schema.Tooltip("fx_spread", "Width of the halo around the contact - how far the "
                + "displaced material is felt. Foam 12, Soft Flesh 10, Rubber 6.");

            schema.AddSlider(S, "fx_volpres", "Volume Preservation", 0f, 2f, 0.21f, GetVolPres, SetVolPres);
            schema.Tooltip("fx_volpres", "How much of the squashed material piles up around the "
                + "dent instead of vanishing. Rubber 1.4 bulges hard, Foam 0.4 barely at all.");

            schema.AddSlider(S, "fx_latflow", "Lateral Flow", 0f, 1f, 0.291667f, GetLatFlow, SetLatFlow);
            schema.Tooltip("fx_latflow", "Sideways slide of the ring away from the contact centre. "
                + "Applied by the shared post pass, not here.");

            schema.AddSlider(S, "fx_hardness", "Contact Hardness", 0f, 1f, 0.518519f, GetHardness, SetHardness);
            schema.Tooltip("fx_hardness", "Second cleanup pass: 1 pushes everything fully out of "
                + "the collider, below 1 allows the slight overlap real flesh has.");

            schema.AddSlider(S, "fx_corrsm", "Corrective Smooth", 0f, 10f, 4f, GetCorrSm, SetCorrSm);
            schema.Tooltip("fx_corrsm", "Final smoothing of the contact deformation, gated to the "
                + "halo so it never touches ripple or jiggle crests.");

            schema.AddSlider(S, "fx_coloffset", "Collision Offset", 0f, 0.05f, 0f, GetColOffset, SetColOffset);
            schema.Tooltip("fx_coloffset", "Skin thickness held above the collider. Ramped by real "
                + "penetration, so a graze does not stick.");

            schema.AddSlider(S, "fx_splay", "Splay", 0f, 1f, 0f, GetSplay, SetSplay);
            schema.Tooltip("fx_splay", "0 = flesh flattens straight back under the pressing collider. "
                + "Higher = flesh near the collider's sides slides round it and bulges up beside it "
                + "instead. With round colliders (arms, fingers) the sideways way out is round the "
                + "curve, so that is the escape this uses.");

            schema.AddToggle(S, "fx_selfcol", "Self Collision", false, GetSelfCol, SetSelfCol);
            schema.Tooltip("fx_selfcol", "Also resolve flesh against the deformed cage itself "
                + "(breast against chest). Costs a ray per node against the self hull.");
        }

        // C# 5: no expression-bodied members, so the schema accessors are named methods.
        static float GetIntensity(SoftBodySuite.FieldRegion r) { return r.intensity; }
        static void SetIntensity(SoftBodySuite.FieldRegion r, float v) { r.intensity = v; }
        static float GetShapeSm(SoftBodySuite.FieldRegion r) { return r.shapeSmoothing; }
        static void SetShapeSm(SoftBodySuite.FieldRegion r, float v) { r.shapeSmoothing = v; }
        static float GetSpread(SoftBodySuite.FieldRegion r) { return r.spread; }
        static void SetSpread(SoftBodySuite.FieldRegion r, float v) { r.spread = v; }
        static float GetVolPres(SoftBodySuite.FieldRegion r) { return r.volumePreservation; }
        static void SetVolPres(SoftBodySuite.FieldRegion r, float v) { r.volumePreservation = v; }
        static float GetLatFlow(SoftBodySuite.FieldRegion r) { return r.lateralFlow; }
        static void SetLatFlow(SoftBodySuite.FieldRegion r, float v) { r.lateralFlow = v; }
        static float GetHardness(SoftBodySuite.FieldRegion r) { return r.contactHardness; }
        static void SetHardness(SoftBodySuite.FieldRegion r, float v) { r.contactHardness = v; }
        static float GetCorrSm(SoftBodySuite.FieldRegion r) { return r.correctiveSmooth; }
        static void SetCorrSm(SoftBodySuite.FieldRegion r, float v) { r.correctiveSmooth = v; }
        static float GetColOffset(SoftBodySuite.FieldRegion r) { return r.collisionOffset; }
        static void SetColOffset(SoftBodySuite.FieldRegion r, float v) { r.collisionOffset = v; }
        static float GetSplay(SoftBodySuite.FieldRegion r) { return r.splay; }
        static void SetSplay(SoftBodySuite.FieldRegion r, float v) { r.splay = v; }
        static float GetSelfCol(SoftBodySuite.FieldRegion r) { return r.selfCollision; }
        static void SetSelfCol(SoftBodySuite.FieldRegion r, float v) { r.selfCollision = v; }
    }
}
