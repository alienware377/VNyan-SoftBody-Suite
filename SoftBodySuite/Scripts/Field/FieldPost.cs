using UnityEngine;

namespace SoftBodySuite.Field
{
    // ONE finisher, run once for all sources, each pass gated on postFlags.
    //
    // The order is a fixed chain:
    //     sources -> self-contact -> corrective smooth -> anti-clip
    // and it is not negotiable. The corrective smooth re-sinks vertices INTO the
    // collider, so the hard guarantee has to be computed after it, from the position
    // the smooth actually produced, and written somewhere the smooth can never reach.
    //
    // The postFlags gate is the whole performance story: a frame where only
    // RippleSource wrote leaves VolumePreserve, Barrier and AntiClip clear, and those
    // passes are skipped outright rather than sweeping 4500 nodes of zeroes.
    public static class FieldPost
    {
        // SELF_RANGE = 0.10 m, the self-hit cutoff
        public const float SelfRange = 0.10f;

        // Scratch. GROW-ONLY and shared across meshes on purpose: two proxies with
        // different node counts would otherwise re-allocate against each other every
        // frame. Nothing here is allocated once the largest cage has been seen.
        static Vector3[] defPos, corr, corrOut, smPos, smOut;
        static int scratchLen;

        static void EnsureScratch(int n)
        {
            if (scratchLen >= n && defPos != null) return;
            defPos = new Vector3[n];
            corr = new Vector3[n];
            corrOut = new Vector3[n];
            smPos = new Vector3[n];
            smOut = new Vector3[n];
            scratchLen = n;
        }

        // radius ~= edge * sqrt(iters * dens^2): the Blur Attribute is DIFFUSION, so a
        // pass's world-space radius grows with the SQUARE ROOT of the iteration count.
        // Holding the radius constant on a mesh k times finer therefore costs k^2
        // iterations, not k — scaling linearly measured 28.3 mm -> 13.7 mm at Subsurf 2
        // and that under-smoothing is what sharpened the contact seam and doubled the
        // peaks. Capped at 400. On our cage dens is 1.0 by
        // construction, so this hands the raw slider straight back — which is the whole
        // reason for keeping the cage.
        public static int ScaledIters(float iterations, float dens)
        {
            return Mathf.RoundToInt(Mathf.Min(iterations * dens * dens, 400f));
        }

        public static void Run(FieldContext ctx, DeformField f)
        {
            if (ctx == null || f == null || f.n == 0) return;
            int n = f.n;
            EnsureScratch(n);
            SoftBodySuite.FieldRegion p = ctx.p;
            System.Diagnostics.Stopwatch sw = swPost;
            int flags = f.postFlags;

            // ---- a) volume preservation: ring bulge + lateral flow ----
            // basin is the OUTER ring: the halo
            // (blurred contact magnitude) minus the seed (the contact itself), so the
            // bulge lands around the dent and not in it.
            if ((flags & FieldPostFlags.VolumePreserve) != 0)
            {
                sw.Reset(); sw.Start();
                Vector3 centroid = f.contactCentroid;
                float volP = p.volumePreservation;
                // The ring bulge and the lateral flow ARE part of the contact core — the only
                // reason they live here and not in ContactSource is that they have to run once
                // for every source. So the core override has to reach them too: OrganicSource
                // publishes a calibrated Lateral Flow of 0.5
                // alongside the four knobs ContactSource already honours, and reading p.lateralFlow
                // unconditionally left that value written and never read.
                float latF = ctx.coreOverride ? ctx.ovLateralFlow : p.lateralFlow;
                for (int i = 0; i < n; i++)
                {
                    float basin = f.halo[i] - f.seed[i];
                    if (basin <= 0f) continue;
                    Vector3 nn = f.nrm[i];
                    Vector3 radial = f.pos[i] - centroid;
                    float rdotn = Vector3.Dot(radial, nn);
                    Vector3 tang = radial - nn * rdotn;
                    // the 1e-6 guards are not decoration: the contact centroid can land
                    // exactly on a vertex, and it does on a flat head-on press
                    float tlen = Mathf.Max(tang.magnitude, 1e-6f);
                    Vector3 tdir = tang / tlen;
                    float m = f.mask[i];
                    float ampN = basin * volP * m;
                    float ampT = basin * latF * m;
                    f.AddDisp(i, nn * ampN + tdir * ampT, FieldAcc.Contact);
                }
                sw.Stop();
                FieldDiag.Mark("post.volume", (float)sw.Elapsed.TotalMilliseconds);
            }

            // ---- b) self-contact: deformable-vs-deformable mutual separation ----
            // The main conform runs against the INPUT
            // geometry, so it cannot see overlaps the deformation itself created —
            // two masses pushed together interpenetrate and the intersection seam
            // shows. This pass runs on the CAGE, after the sources, and resolves it.
            if ((flags & FieldPostFlags.SelfContact) != 0 && ctx.col != null && ctx.blur != null)
            {
                sw.Reset(); sw.Start();
                // Twice: with only one side of a pair masked, a single iteration moves
                // half the overlap, so the second cuts the residual to a quarter.
                for (int iter = 0; iter < 2; iter++)
                {
                    BuildDeformed(f, n);
                    RecomputeDeformedNormals(ctx, f, n);
                    // force on iteration 0: defPos has just been rewritten from every source's
                    // output, and that is the surface the rays below are shot against. Iteration
                    // 1 reuses those buckets deliberately - it only has to resolve the residual
                    // half of an overlap iteration 0 already moved.
                    ctx.col.SetCageForSelf(defPos, ctx.cageTris, ctx.time, iter == 0);
                    System.Array.Clear(corr, 0, n);
                    for (int i = 0; i < n; i++)
                    {
                        if (f.mask[i] <= 0f) continue;
                        Vector3 nn = f.nrm[i];
                        Vector3 pI = defPos[i];
                        // DETECTOR: +N ray from 2 mm above the surface. Leaving through a
                        // near BACK face means this node sits inside other flesh; a fold
                        // of its own does not trigger (its opposite face is seen from the
                        // front, dot < 0). The 2*SELF_RANGE bound throws away far exits —
                        // without it a node under the chin "exited" through the crown.
                        float tA; Vector3 hpA, hnA;
                        if (!ctx.col.RayCage(pI + nn * 0.002f, nn, SelfRange * 2f, out tA, out hpA, out hnA)) continue;
                        if (Vector3.Dot(hnA, nn) <= 0f) continue;
                        if (tA >= SelfRange * 2f) continue;
                        // RESOLUTION: -N ray back to the surface it entered through. The
                        // 0.5*SELF_RANGE bound keeps this to real contact penetrations —
                        // the exit through the far side of the SAME mass is further away
                        // and must not count, or the thin periphery self-attracts.
                        Vector3 dB = -nn;
                        float tB; Vector3 hpB, hnB;
                        if (!ctx.col.RayCage(pI - nn * 0.002f, dB, SelfRange * 0.5f, out tB, out hpB, out hnB)) continue;
                        if (Vector3.Dot(hnB, dB) <= 0f) continue;
                        if (tB >= SelfRange * 0.5f) continue;
                        corr[i] = dB * (tB * 0.5f * f.mask[i]);
                    }
                    ctx.blur.Blur(corr, f.mask, ScaledIters(4f, ctx.dens), 0.85f, corrOut);
                    for (int i = 0; i < n; i++)
                        if (corrOut[i].sqrMagnitude > 0f) f.AddDisp(i, corrOut[i], FieldAcc.Contact);
                }
                sw.Stop();
                FieldDiag.Mark("post.self", (float)sw.Elapsed.TotalMilliseconds);
            }

            // ---- c) deformed normals, before the cleanup passes ----
            // The normal is re-evaluated after EVERY position change. The
            // corrective smooth and the anti-clip both read a normal that already
            // includes the dent; feeding them the rest normal moves flesh the wrong way
            // on any contact steep enough to matter.
            BuildDeformed(f, n);
            RecomputeDeformedNormals(ctx, f, n);

            // ---- d) corrective smooth ----
            // Blurs the POSITIONS, not the displacement, then mixes back only inside the
            // halo. THE HALO GATE APPLIES TO dispContact
            // ONLY. dispWave is not touched by this pass at all — that is the entire
            // reason the third accumulator exists. Gating dispWave by min(halo*12,1)
            // would eat the ripple crests exactly where a hand is touching, which is
            // the flagship Squish+Ripple case.
            if ((flags & FieldPostFlags.CorrectiveSmooth) != 0 && ctx.blur != null)
            {
                int iters = ScaledIters(p.correctiveSmooth, ctx.dens);
                if (iters > 0)
                {
                    sw.Reset(); sw.Start();
                    for (int i = 0; i < n; i++) smPos[i] = f.pos[i] + f.dispContact[i];
                    ctx.blur.Blur(smPos, f.mask, iters, 0.85f, smOut);
                    for (int i = 0; i < n; i++)
                    {
                        float fac = Mathf.Min(f.halo[i] * 12f, 1f);
                        if (fac <= 0f) continue;
                        Vector3 mixed = smPos[i] + (smOut[i] - smPos[i]) * fac;
                        f.dispContact[i] = mixed - f.pos[i];
                    }
                    sw.Stop();
                    FieldDiag.Mark("post.smooth", (float)sw.Elapsed.TotalMilliseconds);
                }
            }

            // ---- e) anti-clip: the hard guarantee, LAST ----
            // Everything above is soft flesh: the conform
            // is blurred, the cleanup is scaled by Contact Hardness (<1 permits overlap)
            // and the corrective smooth re-blurs positions back INSIDE the collider.
            // Here, as the last operation on the already-deformed mesh, the conform is
            // recomputed at FULL strength and anything still inside is expelled to the
            // surface plus skin. The 1.5 mm skin FLOOR is not negotiable: at Offset 0 a
            // thin collider's tip otherwise z-fights through and pokes out.
            // The result goes to dispGuard, NOT dispContact, so nothing downstream can
            // smooth the guarantee away.
            if ((flags & FieldPostFlags.AntiClip) != 0 && ctx.col != null)
            {
                sw.Reset(); sw.Start();
                BuildDeformed(f, n);
                float skin = Mathf.Max(p.collisionOffset, 0.0015f);
                FieldCollider colA = ctx.col;
                Vector3[] dp = defPos, nr = f.nrm, dg = f.dispGuard;
                float[] mk = f.mask;
                ParallelLoop.For(n, delegate (int lo, int hi)
                {
                    for (int i = lo; i < hi; i++)
                    {
                        float m = Mathf.Clamp01(mk[i] * 2f);
                        if (m <= 0f) continue;
                        Vector3 cf; float pen;
                        if (!colA.Conform(dp[i], nr[i], skin, out cf, out pen)) continue;
                        dg[i] += cf * m;
                    }
                });
                sw.Stop();
                FieldDiag.Mark("post.anticlip", (float)sw.Elapsed.TotalMilliseconds);
            }

            // ---- f) wave smoothing, on dispWave alone ----
            // Ideally footprint and wave would be smoothed TOGETHER
            // and we cannot: the footprint has already been smoothed through a halo gate
            // the waves must never see. So the waves get their own pass here.
            //
            // TENSION, stated rather than hidden: Ripple, Fat Jiggle and Penetration all
            // land in dispWave and each was tuned with a SEPARATE Smoothing slider.
            // One shared pass can only have one iteration count, so the largest enabled
            // one wins — under-smoothing spikes, over-smoothing only softens.
            if ((flags & FieldPostFlags.WaveSmooth) != 0 && ctx.blur != null)
            {
                // Only channels that actually WRITE dispWave get a vote. Penetration used to be
                // polled here and must not be: it blurs itself at penSmoothing and lands in
                // dispContact, so its slider was silently re-smoothing other channels' crests
                // while owning nothing in this accumulator.
                float sm = 0f;
                if (p.rippleEnable > 0.5f && p.rippleSmoothing > sm) sm = p.rippleSmoothing;
                if (p.jiggleEnable > 0.5f && p.wobbleSmoothing > sm) sm = p.wobbleSmoothing;
                // Squish Organic's secondary wobble is the third dispWave writer and has no
                // smoothing slider of its own, because it is meant to be finished by the effect's
                // shared corrective smooth at 3 iterations. Ours
                // cannot use that pass — it is halo-gated and the gate is exactly what must not
                // touch a wave — so the 3 is carried here. Without it, Organic on its own flagged
                // WaveSmooth, left sm at 0, and the pass silently did nothing.
                if (p.organicEnable > 0.5f && p.organicJiggle > 0f && 3f > sm) sm = 3f;
                int wi = ScaledIters(sm, ctx.dens);
                if (wi > 0)
                {
                    sw.Reset(); sw.Start();
                    ctx.blur.Blur(f.dispWave, f.mask, wi, 0.85f, smOut);
                    System.Array.Copy(smOut, f.dispWave, n);
                    sw.Stop();
                    FieldDiag.Mark("post.wavesmooth", (float)sw.Elapsed.TotalMilliseconds);
                }
            }

            // SkinRamp is NOT a pass. It lives inside every conform
            // and FieldCollider.Conform owns it.
            // The flag exists only so a source can record that it used one, and this is
            // the only place that reads it.
            if ((flags & FieldPostFlags.SkinRamp) != 0) FieldDiag.Mark("post.skinramp(flagged)", 0f);
        }

        static readonly System.Diagnostics.Stopwatch swPost = new System.Diagnostics.Stopwatch();

        // pos + everything written so far: the geometry a later pass would be looking at
        // after every displacement so far. dispGuard is included because a second anti-clip
        // read must see the first one's push.
        static void BuildDeformed(DeformField f, int n)
        {
            for (int i = 0; i < n; i++)
                defPos[i] = f.pos[i] + f.dispContact[i] + f.dispWave[i] + f.dispGuard[i];
        }

        // f.RecomputeNormals reads f.pos by contract, and f.pos is the UNDEFORMED cage.
        // Rather than duplicate the normal maths, swap the deformed buffer in for the
        // length of the call and put the original array reference straight back — every
        // alias (ctx.pos) is the same object again before anything else can look. No
        // allocation, and only one implementation of the normal accumulation exists.
        static void RecomputeDeformedNormals(FieldContext ctx, DeformField f, int n)
        {
            if (ctx.cageTris == null) return;
            Vector3[] keep = f.pos;
            f.pos = defPos;
            f.RecomputeNormals(ctx.cageTris);
            f.pos = keep;
        }
    }
}
