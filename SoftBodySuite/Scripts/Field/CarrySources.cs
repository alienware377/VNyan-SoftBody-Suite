using UnityEngine;

namespace SoftBodySuite.Field
{
    // Two extra sources, added because the field had no way to
    // make pressed-in flesh go anywhere, and no way to move the whole mass from one push.
    // Both read the contact pass (seed = how far each node was pushed, dir = which way) and
    // the shared contact geodesic, so they cost one sweep each over the cage. Both write the
    // WAVE bucket on purpose: the contact bucket is hard-capped at Max Dent, and a lift or a
    // bulge capped at the dent limit would cut off exactly the motion these exist to add.

    // ------------------------------------------------------------------------------------
    // PUSH-OUT RING. The volume a collider presses in comes back up as a soft ring around
    // the dent, the way a finger pressed into a thigh raises a rim around it.
    // Volume is conserved on the uniform cage: every node has the same area, so the sum of
    // the ring heights equals (amount x) the sum of the dent depths.
    // ------------------------------------------------------------------------------------
    public class RingSource : FieldSource
    {
        public override string Id { get { return "ring"; } }
        public override int Order { get { return 150; } }   // after Contact (100) fills seed/dir

        float[] g;

        public override bool IsActive(FieldContext ctx)
        {
            return ctx != null && ctx.p != null && ctx.p.ringEnable > 0.5f && ctx.p.ringAmount > 0f;
        }

        public override void Prepare(FieldContext ctx) { }

        public override void Write(FieldContext ctx, DeformField f)
        {
            SoftBodySuite.FieldRegion p = ctx.p;
            int n = ctx.n;
            if (n <= 0 || f.seed == null || ctx.geo == null) return;
            float[] dist = ctx.geo.SolveMulti(f.seed, CageGeodesic.ContactSeedThreshold);
            if (dist == null || !ctx.geo.ContactSeeded) return;

            float vol = 0f;
            for (int i = 0; i < n; i++) vol += f.seed[i];
            if (vol <= 1e-6f) return;

            float R = Mathf.Max(ctx.regionRadius, 0.01f);
            float r0 = Mathf.Max(0f, p.ringDistance) * R;                 // ring centre, past the dent edge
            float w = Mathf.Max(p.ringWidth * R, ctx.usedEdge * 1.5f);    // ring half-width
            if (g == null || g.Length != n) g = new float[n];
            float sum = 0f;
            float[] mask = ctx.mask;
            for (int i = 0; i < n; i++)
            {
                g[i] = 0f;
                if (f.seed[i] > CageGeodesic.ContactSeedThreshold) continue;   // the dent itself
                float d = dist[i];
                if (d >= CageGeodesic.Unreached) continue;
                float u = (d - r0) / w;
                float gi = Mathf.Exp(-u * u) * (mask != null ? mask[i] : 1f);
                g[i] = gi; sum += gi;
            }
            if (sum <= 1e-6f) return;

            float h = p.ringAmount * vol / sum;
            // a narrow ring around a big dent would otherwise spike; never taller than the dent
            float cap = Mathf.Max(0.005f, p.maxDent > 0f ? p.maxDent : 0.05f);
            Vector3[] nrm = ctx.nrm;
            for (int i = 0; i < n; i++)
            {
                if (g[i] <= 0f) continue;
                float a = Mathf.Min(h * g[i], cap);
                f.AddDisp(i, nrm[i] * a, FieldAcc.Wave);
            }
            f.Require(FieldPostFlags.WaveSmooth);
        }

        public override void Describe(UiSchema schema)
        {
            const string sec = "Field - Push-out Ring";
            schema.AddSection(sec);
            schema.AddToggle(sec, "fx_ring_on", "Push-out Ring", false, GetOn, SetOn);
            schema.Tooltip("fx_ring_on", "Flesh pressed in by a collider comes back up as a soft bulge ring around the dent.");
            schema.AddSlider(sec, "fx_ringamt", "Amount", 0f, 2f, 0.6f, GetAmt, SetAmt);
            schema.Tooltip("fx_ringamt", "How much of the pressed-in volume reappears in the ring. 1 = all of it.");
            schema.AddSlider(sec, "fx_ringdist", "Distance", 0f, 1f, 0.12f, GetDist, SetDist);
            schema.Tooltip("fx_ringdist", "How far out from the dent's edge the ring peaks, as a fraction of the painted region's size.");
            schema.AddSlider(sec, "fx_ringwidth", "Width", 0.02f, 1f, 0.12f, GetWidth, SetWidth);
            schema.Tooltip("fx_ringwidth", "How wide the ring spreads. Narrow = a tight rim, wide = a broad swell.");
        }

        static float GetOn(SoftBodySuite.FieldRegion r) { return r.ringEnable; }
        static void SetOn(SoftBodySuite.FieldRegion r, float v) { r.ringEnable = v; }
        static float GetAmt(SoftBodySuite.FieldRegion r) { return r.ringAmount; }
        static void SetAmt(SoftBodySuite.FieldRegion r, float v) { r.ringAmount = v; }
        static float GetDist(SoftBodySuite.FieldRegion r) { return r.ringDistance; }
        static void SetDist(SoftBodySuite.FieldRegion r, float v) { r.ringDistance = v; }
        static float GetWidth(SoftBodySuite.FieldRegion r) { return r.ringWidth; }
        static void SetWidth(SoftBodySuite.FieldRegion r, float v) { r.ringWidth = v; }
    }

    // ------------------------------------------------------------------------------------
    // LIFT. Whatever push the contact gives - a hand under a heavy breast - is carried to
    // the whole painted mass, not just the flesh it touches. Softness sets how far the
    // carry reaches: low lifts the whole ball as one piece, high lets the sides lag and
    // droop around the lifted centre.
    // ------------------------------------------------------------------------------------
    public class LiftSource : FieldSource
    {
        public override string Id { get { return "lift"; } }
        public override int Order { get { return 160; } }

        public override bool IsActive(FieldContext ctx)
        {
            return ctx != null && ctx.p != null && ctx.p.liftEnable > 0.5f && ctx.p.liftAmount > 0f;
        }

        public override void Prepare(FieldContext ctx) { }

        public override void Write(FieldContext ctx, DeformField f)
        {
            SoftBodySuite.FieldRegion p = ctx.p;
            int n = ctx.n;
            if (n <= 0 || f.seed == null || f.dir == null || ctx.geo == null) return;
            // WHICH WAY TO LIFT. The first version averaged the flesh's ESCAPE directions,
            // and with a hand buried in the middle of a breast the flesh above it escapes up
            // and the flesh below escapes down: measured, the net push flipped between
            // (0.28, 0.40, 0.87) and (0.03, -0.99, -0.15) frame to frame, so Lift often
            // pushed the breast DOWN. Now the direction is the PRESS AXIS - minus the mean
            // surface normal of the flesh the limb is touching - which is the way the hand
            // pushes into the flesh, and does not flip. Only hand / finger / arm contacts
            // count; the torso capsules press all the time from every side.
            Vector3 nSum = Vector3.zero, cSum = Vector3.zero; float ws = 0f, s2 = 0f; int limbN = 0, otherN = 0;
            Vector3[] pos = ctx.pos, nrm = ctx.nrm;
            // HOW FAR TO LIFT: the hand's RAW depth (limbDepth), not the contact seed. The seed
            // is clamped at Max Dent (50 mm) before anything downstream sees it, and hits deeper
            // than 10 cm are rejected outright by SELF_RANGE - so a hand pushed well into a big
            // breast reported at most 50 mm of press, and Lift could never carry more.
            for (int i = 0; i < n; i++)
            {
                float s = f.limbDepth != null ? f.limbDepth[i] : 0f;
                if (s <= CageGeodesic.ContactSeedThreshold)
                {
                    if (f.seed[i] > CageGeodesic.ContactSeedThreshold) otherN++;
                    continue;
                }
                limbN++;
                nSum += nrm[i] * s; cSum += pos[i] * s;
                ws += s; s2 += s * s;
            }
            DiagLimb = Mathf.Max(DiagLimb, limbN); DiagOther = Mathf.Max(DiagOther, otherN);
            if (ws <= 1e-6f || nSum.sqrMagnitude < 1e-12f) { DiagNoContact++; return; }
            Vector3 axis = -nSum.normalized;
            Vector3 center = cSum / ws;
            Vector3 P = axis * (s2 / ws);   // depth-weighted mean press depth along the axis
            // AXIS WEIGHTS. Arms pressing the base of a big breast press FORWARD, and carrying
            // the whole mass along that stretched it out in front like a sausage. Up is world
            // up; forward is the avatar's forward with the up part removed.
            {
                Vector3 up = ctx.upLocal;
                Vector3 fw = ctx.fwdLocal - up * Vector3.Dot(ctx.fwdLocal, up);
                if (fw.sqrMagnitude < 1e-6f) fw = Vector3.forward;
                fw.Normalize();
                Vector3 pu = up * Vector3.Dot(P, up);
                Vector3 pf = fw * Vector3.Dot(P, fw);
                Vector3 ps = P - pu - pf;
                P = pu * p.liftUp + pf * p.liftForward + ps * p.liftSide;
                // report in BODY terms: the cage's own axes are the mesh's import axes,
                // which on this avatar are not up/forward at all
                float cu = Vector3.Dot(P, up), cf = Vector3.Dot(P, fw), cs = (P - up * cu - fw * cf).magnitude;
                if (P.magnitude > DiagPeak)   // keep the components of the STRONGEST frame
                { DiagPeak = P.magnitude; DiagUp = cu; DiagFwd = cf; DiagSide = cs; }
            }

            // diagnostics: how COHERENT the contact's push is. coherence = |net push| over
            // the mean size of the individual pushes. 1 = everything pushed the same way;
            // near 0 = pushes in different directions cancelled (a hand pressing the middle
            // of a breast pushes flesh OUT in every direction round it).
            float meanMag = s2 / ws; int cn = limbN;
            // coherence now = how flat the touched patch is (1 = all normals agree)
            float coh = nSum.magnitude / ws;
            DiagFrames++;
            DiagContact = Mathf.Max(DiagContact, cn);
            DiagPushMean = Mathf.Max(DiagPushMean, meanMag);
            DiagNet = Mathf.Max(DiagNet, P.magnitude);
            DiagCohSum += coh;
            DiagDir = P.sqrMagnitude > 1e-12f ? P.normalized : DiagDir;
            if (P.sqrMagnitude < 1e-10f) return;

            float R = Mathf.Max(ctx.regionRadius, 0.01f);
            float soft = Mathf.Clamp01(p.liftSoftness);
            float L = Mathf.Lerp(2.5f, 0.15f, soft) * R;   // reach of the carry, along the surface
            float dent = Mathf.Max(0.005f, p.maxDent > 0f ? p.maxDent : 0.05f);
            float[] mask = ctx.mask;
            for (int i = 0; i < n; i++)
            {
                float d = (pos[i] - center).magnitude;
                // nodes a LIMB is touching are already placed by the conform; blend them out
                // so the carry does not push them off the hand
                float held = (f.seedLimb != null && f.seedLimb[i]) ? Mathf.Clamp01(f.seed[i] / dent) : 0f;
                float u = d / L;
                float w = Mathf.Exp(-u * u) * (1f - held) * (mask != null ? mask[i] : 1f);
                if (w <= 1e-4f) continue;
                Vector3 lv = P * (p.liftAmount * w);
                // never carry flesh further than the painted region is big
                float lvm = lv.magnitude;
                if (lvm > R) lv *= R / lvm;
                f.AddDisp(i, lv, FieldAcc.Wave);
                float lm = P.magnitude * p.liftAmount * w;
                if (lm > DiagMaxLift) DiagMaxLift = lm;
                if (lm > 0.002f) DiagMovedAcc++;
            }
            f.Require(FieldPostFlags.WaveSmooth);
        }

        public static int DiagFrames, DiagContact, DiagNoContact, DiagMovedAcc, DiagLimb, DiagOther;
        public static float DiagPushMean, DiagNet, DiagCohSum, DiagMaxLift;
        public static Vector3 DiagDir;
        public static float DiagUp, DiagFwd, DiagSide, DiagPeak;
        public static string DiagLine()
        {
            if (DiagFrames == 0 && DiagNoContact == 0) return "";
            string r = "  | lift: limbNodes=" + DiagLimb + " torsoNodes=" + DiagOther
                + " push=" + (DiagPushMean * 1000f).ToString("0.0") + "mm"
                + " net=" + (DiagNet * 1000f).ToString("0.0") + "mm"
                + " coherence=" + (DiagFrames > 0 ? (DiagCohSum / DiagFrames).ToString("0.00") : "-")
                + " up=" + (DiagUp * 1000f).ToString("0") + "mm fwd=" + (DiagFwd * 1000f).ToString("0")
                + "mm side=" + (DiagSide * 1000f).ToString("0") + "mm"
                + " maxLift=" + (DiagMaxLift * 1000f).ToString("0.0") + "mm"
                + " lifted>2mm/frame=" + (DiagFrames > 0 ? DiagMovedAcc / DiagFrames : 0)
                + (DiagNoContact > 0 ? " noContactFrames=" + DiagNoContact : "");
            DiagFrames = 0; DiagContact = 0; DiagNoContact = 0; DiagMovedAcc = 0; DiagLimb = 0; DiagOther = 0;
            DiagPushMean = 0f; DiagNet = 0f; DiagCohSum = 0f; DiagMaxLift = 0f; DiagPeak = 0f; DiagUp = 0f; DiagFwd = 0f; DiagSide = 0f;
            return r;
        }

        public override void Describe(UiSchema schema)
        {
            const string sec = "Field - Lift";
            schema.AddSection(sec);
            schema.AddToggle(sec, "fx_lift_on", "Lift", false, GetOn, SetOn);
            schema.Tooltip("fx_lift_on", "A push on part of the flesh (a hand under a breast) moves the whole mass, not just the touched spot.");
            schema.AddSlider(sec, "fx_liftamt", "Amount", 0f, 4f, 1f, GetAmt, SetAmt);
            schema.Tooltip("fx_liftamt", "How much of the push the rest of the flesh follows. 1 = the same distance as the touched spot.");
            schema.AddSlider(sec, "fx_liftsoft", "Softness", 0f, 1f, 0.4f, GetSoft, SetSoft);
            schema.AddSlider(sec, "fx_liftup", "Follow up/down", 0f, 1f, 1f, GetUp, SetUp);
            schema.Tooltip("fx_liftup", "How much of the push the flesh follows up and down.");
            schema.AddSlider(sec, "fx_liftfwd", "Follow forward/back", 0f, 1f, 0.2f, GetFwd, SetFwd);
            schema.Tooltip("fx_liftfwd", "How much of the push the flesh follows forward and back. Low stops arms shoving the whole breast out in front.");
            schema.AddSlider(sec, "fx_liftside", "Follow sideways", 0f, 1f, 0.5f, GetSide, SetSide);
            schema.Tooltip("fx_liftside", "How much of the push the flesh follows left and right.");
            schema.Tooltip("fx_liftsoft", "0 = the whole ball lifts as one firm piece. Higher = the sides lag and droop around the lifted centre.");
        }

        static float GetOn(SoftBodySuite.FieldRegion r) { return r.liftEnable; }
        static void SetOn(SoftBodySuite.FieldRegion r, float v) { r.liftEnable = v; }
        static float GetAmt(SoftBodySuite.FieldRegion r) { return r.liftAmount; }
        static void SetAmt(SoftBodySuite.FieldRegion r, float v) { r.liftAmount = v; }
        static float GetUp(SoftBodySuite.FieldRegion r) { return r.liftUp; }
        static void SetUp(SoftBodySuite.FieldRegion r, float v) { r.liftUp = v; }
        static float GetFwd(SoftBodySuite.FieldRegion r) { return r.liftForward; }
        static void SetFwd(SoftBodySuite.FieldRegion r, float v) { r.liftForward = v; }
        static float GetSide(SoftBodySuite.FieldRegion r) { return r.liftSide; }
        static void SetSide(SoftBodySuite.FieldRegion r, float v) { r.liftSide = v; }
        static float GetSoft(SoftBodySuite.FieldRegion r) { return r.liftSoftness; }
        static void SetSoft(SoftBodySuite.FieldRegion r, float v) { r.liftSoftness = v; }
    }
}
