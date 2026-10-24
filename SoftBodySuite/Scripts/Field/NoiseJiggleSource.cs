using UnityEngine;

namespace SoftBodySuite.Field
{
    // Fat jiggle: 4D noise shaking the flesh, gated by the SAME excitation
    // envelope MotionExciter writes and attenuated over the SAME geodesic field
    // Ripple reads. That sharing is the whole point of the field architecture -
    // this file is short because the two expensive things it needs already exist.
    //
    // The one substitution is the noise
    // itself: 4D Perlin noise is replaced by ValueNoise, which is RMS-matched
    // rather than range-matched (see ValueNoise.cs for why).
    //
    // By design the sustained-contact case produces no tremor: a still hand
    // pressing gives a footprint and nothing else, because the envelope only
    // charges on RELATIVE speed.
    public class NoiseJiggleSource : FieldSource
    {
        public override string Id { get { return "fatjiggle"; } }
        public override int Order { get { return 700; } }

        bool warnedNoField;

        public override bool IsActive(FieldContext ctx)
        {
            if (ctx == null || ctx.p == null) return false;
            if (ctx.p.jiggleEnable < 0.5f) return false;
            if (ctx.p.wobbleAmplitude <= 0f) return false;
            return true;
        }

        public override void Prepare(FieldContext ctx)
        {
            // nothing to prepare: the envelope and the distance field are shared
        }

        public override void Write(FieldContext ctx, DeformField f)
        {
            SoftBodySuite.FieldRegion p = ctx.p;
            f.Require(FieldPostFlags.WaveSmooth);

            // SOLVE it, do not just read it. ContactField is allocated by SetGraph and is
            // non-null and all-ZERO until somebody calls SolveMulti, and exp(-0*decay) is 1 —
            // so reading it raw made Wobble Decay a dead slider and gave every node full
            // amplitude no matter how far from the impact it was.
            float[] dist = (ctx.geo != null && ctx.field != null)
                ? ctx.geo.SolveMulti(ctx.field.seed, CageGeodesic.ContactSeedThreshold) : null;
            // With nothing touching, the envelope was charged by MotionExciter's motion path —
            // an extra beyond contact - and whole-mass lag shakes the painted region
            // evenly rather than radiating from a point. So no anchor means no falloff, not no
            // jiggle. Only a real contact gives Wobble Decay something to decay from.
            bool anchored = dist != null && ctx.geo.ContactSeeded;
            if (dist == null)
            {
                if (!warnedNoField)
                {
                    warnedNoField = true;
                    Debug.LogWarning("[SfxField] Fat Jiggle: no cage geodesic graph; the spatial falloff has nothing to measure from.");
                }
                return;
            }

            int n = ctx.n;
            Vector3[] pos = ctx.pos;
            Vector3[] nrm = ctx.nrm;
            float[] mask = ctx.mask;
            float[] ex = f.excite;

            float w = ctx.time * p.wobbleSpeed;
            float scale = p.wobbleScale;
            float lat = p.lateralMotion;
            float nor = p.normalMotion;
            float dec = p.wobbleDecay / Mathf.Max(ctx.waveScale, 1e-3f);   // per region size
            // ctx.wobbleAmpM, not p.wobbleAmplitude: the slider is a fraction of the
            // region's own radius by default and FieldProxy is where it becomes metres.
            float baseAmp = ctx.wobbleAmpM;

            // 4D noise per node is the cost here, and ValueNoise is allocation-free and
            // thread-safe; every node writes only its own wave slot. So: all cores.
            Vector3[] dw = f.dispWave;
            ParallelLoop.For(n, delegate (int lo, int hi)
            {
                for (int i = lo; i < hi; i++)
                {
                    float e = ex[i];
                    if (e <= 1e-5f) continue;
                    float m = mask[i];
                    if (m <= 0f) continue;
                    float att = 1f;
                    if (anchored)
                    {
                        float d = dist[i];
                        if (d >= CageGeodesic.Unreached) continue;   // other side of a cage island
                        att = Mathf.Exp(-d * dec);
                    }

                    Vector3 wob = ValueNoise.Value4DVec(pos[i], w, scale);
                    Vector3 nv = nrm[i];
                    float wn = wob.x * nv.x + wob.y * nv.y + wob.z * nv.z;
                    Vector3 tang = wob - nv * wn;
                    Vector3 jig = tang * lat + nv * (wn * nor);

                    float amp = baseAmp * att * m * e;
                    if (amp == 0f) continue;
                    dw[i] += jig * amp;
                }
            });
        }

        public override void Describe(UiSchema schema)
        {
            const string sec = "Field - Fat Jiggle";
            schema.AddSection(sec);

            schema.AddToggle(sec, "fx_jiggle_on", "Fat Jiggle", false, GetOn, SetOn);
            schema.Tooltip("fx_jiggle_on", "Soft-fat tremor after an impact. Returns exactly to rest.");

            schema.AddSlider(sec, "fx_wobamp", "Amplitude", 0f, 2f, 0.06f, GetAmp, SetAmp);
            schema.Tooltip("fx_wobamp", "How far the flesh shakes, as a fraction of the painted region's own radius. 0.06 is already generous.");

            schema.AddSlider(sec, "fx_wobscale", "Wobble Scale", 0.1f, 20f, 2.2f, GetScale, SetScale);
            schema.Tooltip("fx_wobscale", "Size of the wobbling lobes. Low = a few big masses, high = fine shivering.");

            schema.AddSlider(sec, "fx_wobspeed", "Wobble Speed", 0f, 20f, 2.5f, GetSpeed, SetSpeed);
            schema.Tooltip("fx_wobspeed", "How fast the pattern churns.");

            schema.AddSlider(sec, "fx_latmotion", "Lateral Motion", 0f, 2f, 1.2f, GetLat, SetLat);
            schema.Tooltip("fx_latmotion", "Sideways slosh across the surface.");

            schema.AddSlider(sec, "fx_nrmmotion", "Normal Motion", 0f, 2f, 0.5f, GetNrm, SetNrm);
            schema.Tooltip("fx_nrmmotion", "In-and-out shake along the surface normal.");

            schema.AddSlider(sec, "fx_wobdecay", "Decay", 0f, 20f, 1f, GetDecay, SetDecay);
            schema.Tooltip("fx_wobdecay", "How quickly the tremor dies off with distance from the contact.");

            schema.AddSlider(sec, "fx_wobsm", "Smoothing", 0f, 10f, 3f, GetSm, SetSm);
            schema.Tooltip("fx_wobsm", "Smoothing of the wave layer.");
        }

        static float GetOn(SoftBodySuite.FieldRegion r) { return r.jiggleEnable; }
        static void SetOn(SoftBodySuite.FieldRegion r, float v) { r.jiggleEnable = v; }
        static float GetAmp(SoftBodySuite.FieldRegion r) { return r.wobbleAmplitude; }
        static void SetAmp(SoftBodySuite.FieldRegion r, float v) { r.wobbleAmplitude = v; }
        static float GetScale(SoftBodySuite.FieldRegion r) { return r.wobbleScale; }
        static void SetScale(SoftBodySuite.FieldRegion r, float v) { r.wobbleScale = v; }
        static float GetSpeed(SoftBodySuite.FieldRegion r) { return r.wobbleSpeed; }
        static void SetSpeed(SoftBodySuite.FieldRegion r, float v) { r.wobbleSpeed = v; }
        static float GetLat(SoftBodySuite.FieldRegion r) { return r.lateralMotion; }
        static void SetLat(SoftBodySuite.FieldRegion r, float v) { r.lateralMotion = v; }
        static float GetNrm(SoftBodySuite.FieldRegion r) { return r.normalMotion; }
        static void SetNrm(SoftBodySuite.FieldRegion r, float v) { r.normalMotion = v; }
        static float GetDecay(SoftBodySuite.FieldRegion r) { return r.wobbleDecay; }
        static void SetDecay(SoftBodySuite.FieldRegion r, float v) { r.wobbleDecay = v; }
        static float GetSm(SoftBodySuite.FieldRegion r) { return r.wobbleSmoothing; }
        static void SetSm(SoftBodySuite.FieldRegion r, float v) { r.wobbleSmoothing = v; }
    }
}
