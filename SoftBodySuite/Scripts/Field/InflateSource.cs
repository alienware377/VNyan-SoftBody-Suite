using UnityEngine;

namespace SoftBodySuite.Field
{
    // Internal pressure (the "Squish & Inflate" tail). Order 200,
    // so it runs on the halo ContactSource has already written. It inflates AWAY from the
    // contact - the flesh the collider is not touching bulges out because the squashed part
    // has to go somewhere - with a ring weight that keeps a little of the swell just outside
    // the contact edge.
    //
    // THE DOMAIN FIX
    // --------------
    // The pressure is defined as the mean of d_cap over the
    // WHOLE INPUT GEOMETRY - the entire mesh, painted or not. Our cage only covers
    // the paint, so the same mean is computed over a much smaller, entirely-contacted
    // population and comes out far larger. This is THE ONE UNRATIOED STATISTIC in the whole
    // field, and leaving it unratioed makes Soft Balloon inflate roughly by the
    // paint-coverage factor too hard.
    //
    // The conversion is the AREA ratio cageArea / renderArea. Be honest about what that is:
    // that mean is VERTEX-COUNT weighted, not area weighted, and a VRM's vertex
    // density is wildly non-uniform (a dense face, a sparse thigh), so a vertex-count ratio
    // and an area ratio give different numbers on a real avatar. The area ratio is the
    // physically meaningful one and is what the plan specifies; the discrepancy is logged at
    // first use rather than buried.
    public class InflateSource : FieldSource
    {
        public override string Id { get { return "inflate"; } }
        public override int Order { get { return 200; } }

        float pressure;
        bool loggedRatio;

        public override bool IsActive(FieldContext ctx)
        {
            if (ctx == null || ctx.p == null) return false;
            return ctx.p.inflateEnable > 0.5f && ctx.p.inflatePressure > 0.0001f;
        }

        public override void Reset() { loggedRatio = false; pressure = 0f; }

        // NOTHING HERE, AND THAT IS THE FIX.
        // The pressure statistic is the mean of f.seed, and f.seed does not exist yet at
        // Prepare time: DeformField.BeginFrame zeroes it every frame and ContactSource is
        // the only writer, filling it in its WRITE (ContactSource.cs:156), not its Prepare.
        // FieldRegistry runs EVERY Prepare before ANY Write, so a mean taken here was a mean
        // of a freshly-cleared array - exactly 0, every frame, forever, which zeroed pressure
        // and made Write return on its first line. That is what "structurally inert" meant:
        // no parameter value could ever have switched it on.
        // The mean now happens at the top of Write, where Order 200 > 100 guarantees
        // ContactSource.Write has already filled the seed.
        public override void Prepare(FieldContext ctx) { }

        public override void Write(FieldContext ctx, DeformField f)
        {
            int n = ctx.n;
            if (n <= 0) { pressure = 0f; return; }

            // ---- pressure ----
            // Mean of d_cap over every point, times Inflate Pressure, times a literal 8.
            // d_cap is LENGTH(seed) and f.seed is exactly that.
            float acc = 0f;
            for (int i = 0; i < n; i++) acc += f.seed[i];
            float meanD = acc / n;

            // A zero renderArea means FieldProxy has not measured the render mesh yet; fall
            // back to 1 rather than dividing by the epsilon, which would inflate by ~1e6.
            float ratio = (f.cageArea > 0f && f.renderArea > 1e-6f) ? f.cageArea / f.renderArea : 1f;
            pressure = meanD * ratio * ctx.p.inflatePressure * 8f;   // the 8 is literal

            if (!loggedRatio)
            {
                loggedRatio = true;
                Debug.Log(string.Concat("[SfxField] Field/Inflate: cage/render area ratio ",
                    ratio.ToString("0.0000"), "  (cage ", f.cageArea.ToString("0.00000"),
                    " m2, render ", f.renderArea.ToString("0.00000"),
                    " m2) - pressure is scaled by this so it matches a mean taken over the whole mesh."));
            }

            if (pressure <= 0f) return;
            float[] mask = ctx.mask;
            Vector3[] nrm = ctx.nrm;

            for (int i = 0; i < n; i++)
            {
                float mi = mask != null ? mask[i] : 1f;
                if (mi <= 0f) continue;
                float halo = f.halo[i];
                float contactZone = Mathf.Min(halo * 6f, 1f);
                float away = 1f - contactZone;
                if (away <= 0f) continue;                       // under the collider: no swell
                float near = Mathf.Min(halo * 2.5f, 1f);
                float ringW = 0.25f + 0.75f * near;             // a little extra just outside the dent
                float amt = pressure * away * ringW * mi;
                f.AddDisp(i, nrm[i] * amt, FieldAcc.Contact);
            }

            f.Require(FieldPostFlags.VolumePreserve | FieldPostFlags.CorrectiveSmooth
                      | FieldPostFlags.AntiClip);
        }

        public override void Describe(UiSchema schema)
        {
            const string S = "Field · Inflate";
            schema.AddSection(S);

            schema.AddToggle(S, "fx_inflate_on", "Inflate", false, GetOn, SetOn);
            schema.Tooltip("fx_inflate_on", "Internal pressure: flesh away from the contact swells "
                + "as the contact squashes it.");

            schema.AddSlider(S, "fx_inflate", "Pressure", 0f, 3f, 1.0f, GetPressure, SetPressure);
            schema.Tooltip("fx_inflate", "Strength of the swell. Scaled by the cage/render area "
                + "ratio so it means the same thing whatever fraction of the mesh is painted.");
        }

        static float GetOn(SoftBodySuite.FieldRegion r) { return r.inflateEnable; }
        static void SetOn(SoftBodySuite.FieldRegion r, float v) { r.inflateEnable = v; }
        static float GetPressure(SoftBodySuite.FieldRegion r) { return r.inflatePressure; }
        static void SetPressure(SoftBodySuite.FieldRegion r, float v) { r.inflatePressure = v; }
    }
}
