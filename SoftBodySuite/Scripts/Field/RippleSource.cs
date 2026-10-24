using UnityEngine;

namespace SoftBodySuite.Field
{
    // Waves travelling out from an impact, along the normal.
    //
    // DELIBERATE CHOICE: distance is GEODESIC, not Euclidean.
    // A proximity query against the collider
    // gives a straight-line distance, which is
    // simply wrong around a curved body - a wave from a slap on the front of a
    // thigh reappears on the back at the same phase because the two are close
    // through the air. We have CageGeodesic, so the distance runs over the
    // surface.
    //
    // Mode 0 - CONTINUOUS SINE TRAIN. The original, simpler mode.
    //
    // Mode 1 - SINGLE-WAVE INVERTED RICKER, and the DEFAULT.
    //          RECONSTRUCTED from a one-line description, with no existing wavelet,
    //          no Wave Width or Wave Range control,
    //          and no rising-edge latch to start from. What is
    //          implemented here is the documented retarded-time form, with the
    //          documented defaults (Amplitude 0.23, Frequency 8.0, Wave
    //          Speed 4.0, Decay 1.5, Smoothing 10, Wave Width 0.10, Range 0.5).
    //
    // In the documented form the static footprint is not playback-gated, only the wave
    // was - and we have no playback concept at all, so there is no gate here.
    public class RippleSource : FieldSource
    {
        public override string Id { get { return "ripple"; } }
        public override int Order { get { return 600; } }

        // per-event geodesic fields, owned here so the lifetime is ours: one
        // solve when the event is pushed, never again while it lives.
        float[][] evDist;
        int[] evNode;
        float[] evAge;
        int cachedN;
        bool warnedNoField;

        public override bool IsActive(FieldContext ctx)
        {
            if (ctx == null || ctx.p == null) return false;
            if (ctx.p.rippleEnable < 0.5f) return false;
            if (ctx.p.rippleAmplitude <= 0f) return false;
            return true;
        }

        public override void Prepare(FieldContext ctx)
        {
            EnsureCache(ctx.n);
            if (ctx.geo == null) return;

            DeformField f = ctx.field;
            if (f == null || f.events == null) return;

            // solve exactly once per event, the frame it appears
            for (int s = 0; s < f.events.Length && s < DeformField.MaxEvents; s++)
            {
                FieldEvent e = f.events[s];
                if (!e.live) { evNode[s] = -1; continue; }
                bool fresh = (evNode[s] != e.originNode) || (e.age < evAge[s]);
                evAge[s] = e.age;
                if (!fresh) continue;

                float[] src = ctx.geo.SolveSingle(e.originNode);
                if (src == null) { evNode[s] = -1; continue; }
                // copied into our own slot: SolveSingle may hand back a shared
                // scratch buffer, and a cache that aliases one is not a cache.
                float[] dst = evDist[s];
                int n = Mathf.Min(src.Length, dst.Length);
                for (int i = 0; i < n; i++) dst[i] = src[i];
                for (int i = n; i < dst.Length; i++) dst[i] = CageGeodesic.Unreached;
                evNode[s] = e.originNode;
            }
        }

        public override void Write(FieldContext ctx, DeformField f)
        {
            SoftBodySuite.FieldRegion p = ctx.p;
            f.Require(FieldPostFlags.WaveSmooth);
            // NOT CorrectiveSmooth: that is exactly the gate that would eat the
            // crests wherever a hand is touching.

            if (p.rippleMode < 0.5f) WriteTrain(ctx, f);
            else WriteSingle(ctx, f);
        }

        // ---- mode 0: continuous sine train ----------------------------------
        void WriteTrain(FieldContext ctx, DeformField f)
        {
            SoftBodySuite.FieldRegion p = ctx.p;
            // SOLVE it, do not just read it. ContactField is allocated by SetGraph and is
            // non-null and all-zero until somebody calls SolveMulti, and an all-zero distance
            // field puts every node at phase -t*spd: the whole cage pulsing in lockstep with no
            // travelling wave at all, and no warning, because the array was never null.
            float[] dist = (ctx.geo != null && ctx.field != null)
                ? ctx.geo.SolveMulti(ctx.field.seed, CageGeodesic.ContactSeedThreshold) : null;
            if (dist == null || !ctx.geo.ContactSeeded)
            {
                // Mode 0 measures from the contact.
                // With nothing touching, a sine train has no
                // origin to travel from — mode 1's per-event fields are what carries the
                // motion-only impulse.
                if (!warnedNoField)
                {
                    warnedNoField = true;
                    Debug.LogWarning("[SfxField] Ripple: no contact geodesic field this frame; the sine train has nothing to measure from. Mode 1 (single wave) does not need one.");
                }
                return;
            }

            int n = ctx.n;
            float[] ex = f.excite;
            float[] mask = ctx.mask;
            Vector3[] nrm = ctx.nrm;
            // ctx.rippleAmpM, not p.rippleAmplitude: the slider is a fraction of the
            // region's own radius by default and FieldProxy is where it becomes metres.
            float amp = ctx.rippleAmpM;
            float ws = Mathf.Max(ctx.waveScale, 1e-3f);
            float freq = p.rippleFrequency / ws;
            float spd = p.waveSpeedFx;
            float dec = p.rippleDecay / ws;
            float t = ctx.time;

            for (int i = 0; i < n; i++)
            {
                float e = ex[i];
                if (e <= 1e-5f) continue;
                float d = dist[i];
                if (d >= CageGeodesic.Unreached) continue;
                float phase = d * freq - t * spd;
                float att = Mathf.Exp(-d * dec);
                float wave = Mathf.Sin(phase) * amp * att * e * mask[i];
                if (wave == 0f) continue;
                f.AddDisp(i, nrm[i] * wave, FieldAcc.Wave);
            }
        }

        // ---- mode 1: single-wave inverted Ricker, in retarded time -----------
        // u = (waveSpeed*age - geoDist) / waveWidth
        // ricker = -(1 - 2u^2) * exp(-u^2)
        // Inverted because the leading edge of a real impact is a TROUGH, not a
        // crest. The wavelet peaks at exactly 1 at u = 0, so Amplitude is metres.
        void WriteSingle(FieldContext ctx, DeformField f)
        {
            SoftBodySuite.FieldRegion p = ctx.p;
            if (f.events == null) return;

            int n = ctx.n;
            float[] mask = ctx.mask;
            Vector3[] nrm = ctx.nrm;
            // ctx.rippleAmpM, not p.rippleAmplitude: the slider is a fraction of the
            // region's own radius by default and FieldProxy is where it becomes metres.
            float amp = ctx.rippleAmpM;
            float ws = Mathf.Max(ctx.waveScale, 1e-3f);
            float spd = p.waveSpeedFx * ws;                       // region sizes per second
            float dec = p.rippleDecay / ws;                       // falloff per region size
            float width = Mathf.Max(p.waveWidth * ws, 0.004f);    // ring thickness
            float range = Mathf.Max(p.waveRange * ws, 0.02f);     // how far it travels
            float invW = 1f / width;

            for (int s = 0; s < f.events.Length && s < DeformField.MaxEvents; s++)
            {
                FieldEvent e = f.events[s];
                if (!e.live || evNode[s] < 0) continue;
                if (e.energy <= 1e-4f) continue;
                float[] dist = evDist[s];
                float front = spd * e.age;
                // once the crest has run past the far edge of its range the
                // event contributes nothing anywhere
                if (front - range > 3f * width) continue;

                float scale = amp * e.energy;
                for (int i = 0; i < n; i++)
                {
                    float d = dist[i];
                    if (d > range) continue;
                    float u = (front - d) * invW;
                    if (u < -3f || u > 3f) continue;          // wavelet is dead past 3 sigma
                    float uu = u * u;
                    float ricker = -(1f - 2f * uu) * Mathf.Exp(-uu);
                    float w = ricker * scale * Mathf.Exp(-d * dec) * mask[i];
                    if (w == 0f) continue;
                    f.AddDisp(i, nrm[i] * w, FieldAcc.Wave);
                }
            }
        }

        void EnsureCache(int n)
        {
            if (cachedN == n && evDist != null) return;
            cachedN = n;
            evDist = new float[DeformField.MaxEvents][];
            evNode = new int[DeformField.MaxEvents];
            evAge = new float[DeformField.MaxEvents];
            for (int s = 0; s < DeformField.MaxEvents; s++)
            {
                evDist[s] = new float[n];
                evNode[s] = -1;
                evAge[s] = 0f;
            }
        }

        public override void Reset()
        {
            if (evNode != null)
                for (int s = 0; s < evNode.Length; s++) { evNode[s] = -1; evAge[s] = 0f; }
        }

        public override void Describe(UiSchema schema)
        {
            const string sec = "Field - Ripple";
            schema.AddSection(sec);

            schema.AddToggle(sec, "fx_ripple_on", "Ripple", false, GetOn, SetOn);
            schema.Tooltip("fx_ripple_on", "Waves spreading out from an impact.");

            schema.AddToggle(sec, "fx_rip_outer", "Start: outermost point (on a jolt)", true, GetOuter, SetOuter);
            schema.Tooltip("fx_rip_outer", "When she jolts or stops, the flesh claps out at its outermost point on the far side of the move, and the wave runs across from there.");
            schema.AddToggle(sec, "fx_rip_touch", "Start: where it's touched", true, GetTouch, SetTouch);
            schema.Tooltip("fx_rip_touch", "When a collider first touches the flesh, a wave starts at the touch point.");
            schema.AddToggle(sec, "fx_rip_nip", "Start: nipples (auto-detect)", false, GetNip, SetNip);
            schema.Tooltip("fx_rip_nip", "Waves start at the nipples, on a jolt or a touch. Found from the breast bones, or from the shape when there are none. Pink dots show where they landed while picking or while colliders are shown.");
            schema.AddToggle(sec, "fx_rip_pick", "Start: my picked points", false, GetPick, SetPick);
            schema.Tooltip("fx_rip_pick", "Waves start at the points you picked with the button below, on a jolt or a touch.");
            schema.AddButton(sec, "fx_ripplepick", "Pick ripple points (click the body)");
            schema.Tooltip("fx_ripplepick", "Press, then click on the body to add a start point (up to 6 per region). Click a dot again to remove it. Press again when done. Cyan dots = your points, pink = the found nipples.");
            schema.AddButton(sec, "fx_rippleclear", "Clear picked points (this region)");
            schema.Tooltip("fx_rippleclear", "Removes every picked ripple point from the selected region.");

            schema.AddSlider(sec, "fx_ripplemode", "Wave Mode", 0f, 1f, 1f, GetMode, SetMode)
                  .wholeNumbers = true;
            schema.Tooltip("fx_ripplemode",
                "0 = a continuous train of waves while the contact lasts. 1 = one expanding ring per impact, which is what a slap actually looks like.");

            schema.AddSlider(sec, "fx_rippleamp", "Amplitude", 0f, 5f, 0.23f, GetAmp, SetAmp);
            schema.Tooltip("fx_rippleamp", "Wave height along the normal, as a fraction of the painted region's own radius.");

            schema.AddSlider(sec, "fx_ripplefreq", "Frequency", 0f, 100f, 8f, GetFreq, SetFreq);
            schema.Tooltip("fx_ripplefreq", "Waves per metre of distance from the contact. Train mode only.");

            schema.AddSlider(sec, "fx_ripplespeed", "Wave Speed", -20f, 20f, 3f, GetSpeed, SetSpeed);
            schema.Tooltip("fx_ripplespeed", "How fast the wave crosses the flesh, in region sizes per second. 2 to 4 reads as a wave; much faster looks like the whole ball pulsing.");

            schema.AddSlider(sec, "fx_rippledecay", "Decay", 0f, 10f, 0.8f, GetDecay, SetDecay);
            schema.Tooltip("fx_rippledecay", "How much the wave fades as it travels, per region size. Low = it reaches the opposite side.");

            schema.AddSlider(sec, "fx_wavewidth", "Wave Width", 0.02f, 0.5f, 0.10f, GetWidth, SetWidth);
            schema.Tooltip("fx_wavewidth", "Thickness of the travelling ring, as a fraction of the painted region's size. Small = a thin wave of flesh.");

            schema.AddSlider(sec, "fx_waverange", "Wave Range", 0.05f, 4f, 2.2f, GetRange, SetRange);
            schema.Tooltip("fx_waverange", "How far the ring travels, as a fraction of the painted region's size. About 2 reaches the opposite side.");

            schema.AddSlider(sec, "fx_dentdepth", "Dent Depth", 0f, 2f, 1f, GetDent, SetDent);
            schema.Tooltip("fx_dentdepth", "Depth of the static footprint the collider presses in.");

            schema.AddSlider(sec, "fx_ripplesm", "Smoothing", 0f, 10f, 10f, GetSm, SetSm);
            schema.Tooltip("fx_ripplesm", "Smoothing of the wave layer only. The contact dent has its own.");
        }

        static float GetOuter(SoftBodySuite.FieldRegion r) { return r.rippleFromOuter; }
        static void SetOuter(SoftBodySuite.FieldRegion r, float v) { r.rippleFromOuter = v; }
        static float GetTouch(SoftBodySuite.FieldRegion r) { return r.rippleFromTouch; }
        static void SetTouch(SoftBodySuite.FieldRegion r, float v) { r.rippleFromTouch = v; }
        static float GetNip(SoftBodySuite.FieldRegion r) { return r.rippleFromNipples; }
        static void SetNip(SoftBodySuite.FieldRegion r, float v) { r.rippleFromNipples = v; }
        static float GetPick(SoftBodySuite.FieldRegion r) { return r.rippleFromPicked; }
        static void SetPick(SoftBodySuite.FieldRegion r, float v) { r.rippleFromPicked = v; }
        static float GetOn(SoftBodySuite.FieldRegion r) { return r.rippleEnable; }
        static void SetOn(SoftBodySuite.FieldRegion r, float v) { r.rippleEnable = v; }
        static float GetMode(SoftBodySuite.FieldRegion r) { return r.rippleMode; }
        static void SetMode(SoftBodySuite.FieldRegion r, float v) { r.rippleMode = v; }
        static float GetAmp(SoftBodySuite.FieldRegion r) { return r.rippleAmplitude; }
        static void SetAmp(SoftBodySuite.FieldRegion r, float v) { r.rippleAmplitude = v; }
        static float GetFreq(SoftBodySuite.FieldRegion r) { return r.rippleFrequency; }
        static void SetFreq(SoftBodySuite.FieldRegion r, float v) { r.rippleFrequency = v; }
        static float GetSpeed(SoftBodySuite.FieldRegion r) { return r.waveSpeedFx; }
        static void SetSpeed(SoftBodySuite.FieldRegion r, float v) { r.waveSpeedFx = v; }
        static float GetDecay(SoftBodySuite.FieldRegion r) { return r.rippleDecay; }
        static void SetDecay(SoftBodySuite.FieldRegion r, float v) { r.rippleDecay = v; }
        static float GetWidth(SoftBodySuite.FieldRegion r) { return r.waveWidth; }
        static void SetWidth(SoftBodySuite.FieldRegion r, float v) { r.waveWidth = v; }
        static float GetRange(SoftBodySuite.FieldRegion r) { return r.waveRange; }
        static void SetRange(SoftBodySuite.FieldRegion r, float v) { r.waveRange = v; }
        // NOTE FOR THE INTEGRATOR: dentDepth must scale ContactSource's conform
        // contribution. Ripple owns the slider (it is a ripple-family control)
        // but must not re-implement the conform, and it cannot scale
        // f.dispContact here because by Order 600 that accumulator also holds
        // Inflate, Organic and Penetration. One line in ContactSource.Write.
        static float GetDent(SoftBodySuite.FieldRegion r) { return r.dentDepth; }
        static void SetDent(SoftBodySuite.FieldRegion r, float v) { r.dentDepth = v; }
        static float GetSm(SoftBodySuite.FieldRegion r) { return r.rippleSmoothing; }
        static void SetSm(SoftBodySuite.FieldRegion r, float v) { r.rippleSmoothing = v; }
    }
}
