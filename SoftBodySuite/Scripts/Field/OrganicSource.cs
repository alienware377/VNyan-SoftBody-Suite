using UnityEngine;

namespace SoftBodySuite.Field
{
    // Cartoon-flesh squash & stretch.
    //
    // It lives with the motion family and not with the contact family because
    // its squash is driven by the flesh centroid's VELOCITY, not by a collider -
    // a running character squashes even with nothing touching it.
    // Nothing has to touch you for this to fire.
    //
    // Three parts:
    //   1. a SOFTNESS field that replaces the paint mask inside the contact core,
    //      plus three core knobs derived from Collision Softness;
    //   2. a velocity squash decomposed around the soft-weighted centroid;
    //   3. a secondary post-impact wobble.
    //
    // ORDERING - two things the driver must honour, both flagged to the
    // integrator rather than worked around here:
    //
    //   (a) The core substitution is published in IsActive, not in Prepare,
    //       because ContactSource is Order 100 and this is Order 300. The driver
    //       MUST therefore call IsActive on EVERY source before it calls Prepare
    //       on ANY of them. That is the shape FieldContext was given
    //       (coreOverride / ovMask / ov*) and it is why those fields exist.
    //
    //   (b) The secondary wobble reads f.excite, which MotionExciter writes at
    //       Order 500 - after this. It is therefore one frame behind, which is
    //       invisible on a secondary tremor, PROVIDED DeformField.BeginFrame does
    //       not zero `excite`. MotionExciter overwrites the whole array every
    //       frame, so it can never go stale by more than one frame.
    public class OrganicSource : FieldSource
    {
        public override string Id { get { return "organic"; } }
        public override int Order { get { return 300; } }

        float[] soft;
        Vector3[] offRaw;
        Vector3[] offSm;
        int cachedN;

        Vector3 centroid;
        Vector3 vdir;
        float speed;
        bool moving;

        public override void Reset()
        {
            centroid = Vector3.zero; vdir = Vector3.zero;
            speed = 0f; moving = false;
        }

        // Also publishes the contact-core substitution. See note (a) above.
        public override bool IsActive(FieldContext ctx)
        {
            if (ctx == null || ctx.p == null) return false;
            SoftBodySuite.FieldRegion p = ctx.p;
            if (p.organicEnable < 0.5f) return false;

            Ensure(ctx.n);

            // Softness field: soft = clamp01(mask * (1 - stiffness)). This
            // REPLACES mask inside the contact core for this effect.
            float[] mask = ctx.mask;
            float inv = 1f - Mathf.Clamp01(p.stiffness01);
            int n = ctx.n;
            if (mask != null)
                for (int i = 0; i < n; i++) soft[i] = Mathf.Clamp01(mask[i] * inv);

            // Collision Softness derives three of the core's knobs; Lateral Flow
            // and Splay are fixed calibrated constants with no slider of their
            // own.
            float s = Mathf.Clamp01(p.collisionSoftness);
            ctx.coreOverride = true;
            // the core Intensity is driven from Collision Strength.
            ctx.ovIntensity = p.collisionStrength;
            ctx.ovShapeSmoothing = 3f + s * 9f;
            ctx.ovSpread = 4f + s * 14f;
            ctx.ovContactHardness = 1f - s * 0.5f;
            ctx.ovLateralFlow = 0.5f;
            ctx.ovSplay = 0.5f;
            ctx.ovMask = soft;
            return true;
        }

        public override void Prepare(FieldContext ctx)
        {
            Ensure(ctx.n);

            // Without real movement there is no block at all: rest is bit-perfect.
            Vector3 vel = ctx.fleshVel;
            speed = vel.magnitude;
            moving = speed > 1e-3f;
            if (!moving) { vdir = Vector3.zero; return; }
            vdir = vel / speed;

            // Soft-weighted centroid: the squash pivots on the centre of the
            // painted zone, not on the object origin.
            int n = ctx.n;
            Vector3[] pos = ctx.pos;
            Vector3 acc = Vector3.zero;
            float w = 0f;
            for (int i = 0; i < n; i++)
            {
                float m = soft[i];
                if (m <= 0f) continue;
                acc += pos[i] * m; w += m;
            }
            if (w > 1e-6f) centroid = acc / w;
            else moving = false;
        }

        public override void Write(FieldContext ctx, DeformField f)
        {
            SoftBodySuite.FieldRegion p = ctx.p;
            int n = ctx.n;
            Vector3[] pos = ctx.pos;

            if (moving)
            {
                float di = p.directionInfluence;
                float volPres = Mathf.Clamp01(p.volumePreservation);
                float k = p.squishIntensity * p.velocityInfluence * speed * 0.15f;

                for (int i = 0; i < n; i++)
                {
                    float m = soft[i];
                    if (m <= 0f) { offRaw[i] = Vector3.zero; continue; }

                    // s in [0.4, 1]: at 2 m/s with Intensity 1 and soft 1 this is
                    // about 0.77 - cartoon range without stamping it flat.
                    float drive = k * m;
                    float s = 1f / (1f + drive);
                    if (s < 0.4f) s = 0.4f;
                    float sLat = 1f + (Mathf.Pow(s, -0.5f) - 1f) * volPres;

                    Vector3 pRel = pos[i] - centroid;
                    float axLen = pRel.x * vdir.x + pRel.y * vdir.y + pRel.z * vdir.z;
                    Vector3 axial = vdir * axLen;
                    Vector3 lateral = pRel - axial;
                    Vector3 pDir = centroid + axial * s + lateral * sLat;

                    // Direction Influence scales the directional squash against
                    // REST, not against a uniform compression, on purpose: mixing
                    // with a uniform compression measured 1.004 instead of 1.037
                    // on the lateral axis at DI 0.7 and lost 10% of the volume.
                    offRaw[i] = (pDir - pos[i]) * di;
                }

                int iters = FieldPost.ScaledIters(3f, ctx.dens);
                if (ctx.blur != null && iters > 0)
                {
                    ctx.blur.Blur(offRaw, ctx.mask, iters, 0.85f, offSm);
                    for (int i = 0; i < n; i++) f.AddDisp(i, offSm[i], FieldAcc.Contact);
                }
                else
                {
                    for (int i = 0; i < n; i++) f.AddDisp(i, offRaw[i], FieldAcc.Contact);
                }
            }

            WriteJiggle(ctx, f);
        }

        // Secondary post-impact wobble: only freshly struck flesh (halo gate) and
        // only while there is impulse energy.
        void WriteJiggle(FieldContext ctx, DeformField f)
        {
            SoftBodySuite.FieldRegion p = ctx.p;
            if (p.organicJiggle <= 0f) return;
            float[] ex = f.excite;
            float[] halo = f.halo;
            if (ex == null || halo == null) return;

            f.Require(FieldPostFlags.WaveSmooth);

            int n = ctx.n;
            Vector3[] pos = ctx.pos;
            Vector3[] nrm = ctx.nrm;
            float w = ctx.time * 2f;
            float jg = p.organicJiggle;

            // per-node 4D noise again; ValueNoise is thread-safe and each node writes only
            // its own wave slot, so it runs on every core like the Fat Jiggle loop
            float[] sf = soft, hl = halo;
            Vector3[] dw = f.dispWave;
            ParallelLoop.For(n, delegate (int lo, int hi)
            {
                for (int i = lo; i < hi; i++)
                {
                    float e = ex[i];
                    if (e <= 1e-5f) continue;
                    float m = sf[i];
                    if (m <= 0f) continue;
                    float contact = hl[i] * 6f;
                    if (contact > 1f) contact = 1f;
                    if (contact <= 0f) continue;

                    Vector3 jv = ValueNoise.Value4DVec(pos[i], w, 5f);
                    Vector3 nv = nrm[i];
                    float wn = jv.x * nv.x + jv.y * nv.y + jv.z * nv.z;
                    Vector3 tang = jv - nv * wn;
                    Vector3 jig = tang + nv * (wn * 0.6f);

                    float amp = jg * e * m * contact * 0.05f;
                    if (amp == 0f) continue;
                    dw[i] += jig * amp;
                }
            });
        }

        void Ensure(int n)
        {
            if (cachedN == n && soft != null) return;
            cachedN = n;
            soft = new float[n];
            offRaw = new Vector3[n];
            offSm = new Vector3[n];
        }

        public override void Describe(UiSchema schema)
        {
            const string sec = "Field - Organic";
            schema.AddSection(sec);

            schema.AddToggle(sec, "fx_organic_on", "Organic Squash", false, GetOn, SetOn);
            schema.Tooltip("fx_organic_on",
                "Cartoon squash & stretch driven by the body's own movement. Nothing has to touch you.");

            schema.AddSlider(sec, "fx_sqintensity", "Squish Intensity", 0f, 2f, 1.2f, GetSq, SetSq);
            schema.Tooltip("fx_sqintensity", "How hard the flesh compresses along the direction it is travelling.");

            schema.AddSlider(sec, "fx_dirinf", "Direction Influence", 0f, 1f, 0.7f, GetDir, SetDir);
            schema.Tooltip("fx_dirinf", "Weight of the directional squash. Volume is preserved across the whole slider.");

            schema.AddSlider(sec, "fx_colstrength", "Collision Strength", 0f, 2f, 1.2f, GetColStr, SetColStr);
            schema.Tooltip("fx_colstrength", "How much the flesh yields to a collider while this effect is on.");

            schema.AddSlider(sec, "fx_colsoftness", "Collision Softness", 0f, 1f, 0.55f, GetColSoft, SetColSoft);
            schema.Tooltip("fx_colsoftness",
                "Organic softness of the contact: rounds the footprint, widens the halo and allows a little overlap.");

            schema.AddSlider(sec, "fx_stiffness01", "Stiffness", 0f, 1f, 0.6f, GetStiff, SetStiff);
            schema.Tooltip("fx_stiffness01", "0 = soft fat, 1 = rigid. The deformation is strongest where stiffness is low.");

            schema.AddSlider(sec, "fx_velinf", "Velocity Influence", 0f, 1f, 1f, GetVel, SetVel);
            schema.Tooltip("fx_velinf", "Overall attenuator of the velocity squash.");

            schema.AddSlider(sec, "fx_orgjig", "Jiggle", 0f, 2f, 0.5f, GetJig, SetJig);
            schema.Tooltip("fx_orgjig", "Secondary wobble of freshly struck flesh.");
        }

        static float GetOn(SoftBodySuite.FieldRegion r) { return r.organicEnable; }
        static void SetOn(SoftBodySuite.FieldRegion r, float v) { r.organicEnable = v; }
        static float GetSq(SoftBodySuite.FieldRegion r) { return r.squishIntensity; }
        static void SetSq(SoftBodySuite.FieldRegion r, float v) { r.squishIntensity = v; }
        static float GetDir(SoftBodySuite.FieldRegion r) { return r.directionInfluence; }
        static void SetDir(SoftBodySuite.FieldRegion r, float v) { r.directionInfluence = v; }
        static float GetColStr(SoftBodySuite.FieldRegion r) { return r.collisionStrength; }
        static void SetColStr(SoftBodySuite.FieldRegion r, float v) { r.collisionStrength = v; }
        static float GetColSoft(SoftBodySuite.FieldRegion r) { return r.collisionSoftness; }
        static void SetColSoft(SoftBodySuite.FieldRegion r, float v) { r.collisionSoftness = v; }
        static float GetStiff(SoftBodySuite.FieldRegion r) { return r.stiffness01; }
        static void SetStiff(SoftBodySuite.FieldRegion r, float v) { r.stiffness01 = v; }
        static float GetVel(SoftBodySuite.FieldRegion r) { return r.velocityInfluence; }
        static void SetVel(SoftBodySuite.FieldRegion r, float v) { r.velocityInfluence = v; }
        static float GetJig(SoftBodySuite.FieldRegion r) { return r.organicJiggle; }
        static void SetJig(SoftBodySuite.FieldRegion r, float v) { r.organicJiggle = v; }
    }
}
