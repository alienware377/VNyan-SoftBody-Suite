using UnityEngine;

namespace SoftBodySuite.Field
{
    // INSTRUMENTATION FIRST — this file exists before any source does, on purpose.
    // There is no play-mode preview here: a round trip is 4-8 minutes (build, copy the
    // DLL, restart VNyan, re-pose the avatar) and a worker thread swallows exceptions,
    // so a bug arrives as a frozen dent with no log line at all. The only way anyone
    // can argue with the cost estimate, or prove Milestone 1, is numbers on screen.
    //
    // Nothing here allocates per frame: Mark() walks a fixed table of interned pass
    // names, VerifyIdentity reinterprets floats through a preallocated scratch pair,
    // and strings are built ONLY inside Report() and DumpCsv().
    public static class FieldDiag
    {
        // When on, FieldProxy runs VerifyIdentity every frame and logs the FIRST
        // frame the published buffer stops being bit-identical to the input.
        public static bool identityWatch;
        static bool identityBroken;

        // ---------------- identity ----------------

        // Milestone 1's acceptance criterion: maxDelta == 0.0f EXACTLY — not "< 1e-6",
        // EXACTLY — for 600 consecutive frames with the registry empty, at sfxMix 0
        // AND at sfxMix 1. That is only reachable if the publish path is a memcpy:
        // adding 0.0f to -0.0f is not the identity (it yields +0.0f, a different bit
        // pattern) and Vector3.Lerp(v, w, 0f) is not either (it is v + (w-v)*0, which
        // rounds). Anything clever in the publish path shows up here.
        public static void VerifyIdentity(Vector3[] input, Vector3[] published,
                                          out float maxDelta, out uint checksum)
        {
            maxDelta = 0f;
            checksum = 0u;
            if (input == null || published == null) { maxDelta = float.NaN; return; }
            if (input.Length != published.Length) { maxDelta = float.NaN; return; }
            float worst = 0f;
            for (int i = 0; i < input.Length; i++)
            {
                float dx = published[i].x - input[i].x; if (dx < 0f) dx = -dx;
                float dy = published[i].y - input[i].y; if (dy < 0f) dy = -dy;
                float dz = published[i].z - input[i].z; if (dz < 0f) dz = -dz;
                if (dx > worst) worst = dx;
                if (dy > worst) worst = dy;
                if (dz > worst) worst = dz;
            }
            maxDelta = worst;
            checksum = Checksum(published);
        }

        // Fletcher-32 over the RAW FLOAT BITS, so +0.0f and -0.0f give different
        // checksums — a value-space hash would hide exactly the drift being hunted.
        static readonly float[] fScratch = new float[3];
        static readonly uint[] uScratch = new uint[3];

        public static uint Checksum(Vector3[] v)
        {
            if (v == null) return 0u;
            uint s1 = 0xFFFF, s2 = 0xFFFF;
            for (int i = 0; i < v.Length; i++)
            {
                fScratch[0] = v[i].x; fScratch[1] = v[i].y; fScratch[2] = v[i].z;
                System.Buffer.BlockCopy(fScratch, 0, uScratch, 0, 12);
                for (int k = 0; k < 3; k++)
                {
                    uint w = uScratch[k];
                    s1 = (s1 + (w & 0xFFFF)) % 65535u;
                    s2 = (s2 + s1) % 65535u;
                    s1 = (s1 + (w >> 16)) % 65535u;
                    s2 = (s2 + s1) % 65535u;
                }
            }
            return (s2 << 16) | s1;
        }

        // Called by FieldProxy once per frame while identityWatch is on. Logs once and
        // then shuts up — a per-frame log at 90 fps is how you lose the log window.
        public static void WatchFrame(string who, Vector3[] input, Vector3[] published)
        {
            if (!identityWatch || identityBroken) return;
            float d; uint ck;
            VerifyIdentity(input, published, out d, out ck);
            if (d == 0f) return;
            identityBroken = true;
            Debug.LogWarning("[SfxField] IDENTITY BROKEN on '" + who + "': maxDelta="
                + d.ToString("0.#########") + " checksum=" + ck.ToString("X8")
                + " — the publish path is no longer a straight copy");
        }

        public static void ResetIdentityWatch()
        {
            identityBroken = false;
        }

        // ---------------- timing ----------------

        // Fixed table: capture / interp / geodesic, one slot per source Id, one per
        // FieldPost pass. 32 is well past what seven effects can need, and a full slot
        // table just stops recording rather than growing (growing would allocate).
        const int MaxTimers = 96;
        static readonly string[] tName = new string[MaxTimers];
        static readonly float[] tMs = new float[MaxTimers];       // EMA, alpha 0.08 (as SquishProxy's msBake)
        static readonly float[] tPeak = new float[MaxTimers];
        static readonly int[] tHits = new int[MaxTimers];
        static int tCount;

        static readonly object markLock = new object();

        // the solve runs on a worker thread in async mode while capture marks on the main
        // thread; a slot being added from both at once would corrupt the table
        // Diagnostics on (the perf flag file or the collider view): pass timers and the
        // periodic telemetry lines. Off in normal use, so the log stays quiet and the
        // timers cost nothing.
        public static volatile bool verbose;

        public static void Mark(string pass, float ms)
        {
            if (!verbose) return;
            lock (markLock) { MarkLocked(pass, ms); }
        }

        static void MarkLocked(string pass, float ms)
        {
            if (pass == null) return;
            int slot = -1;
            for (int i = 0; i < tCount; i++)
            {
                // reference test first: every caller passes a literal or a source's Id
                // property, so this hits on the first compare and never walks the string
                if (ReferenceEquals(tName[i], pass) || tName[i] == pass) { slot = i; break; }
            }
            if (slot < 0)
            {
                if (tCount >= MaxTimers) return;
                slot = tCount++;
                tName[slot] = pass;
                tMs[slot] = ms;
            }
            tMs[slot] = Mathf.Lerp(tMs[slot], ms, 0.08f);
            if (ms > tPeak[slot]) tPeak[slot] = ms;
            tHits[slot]++;
        }

        public static void ResetTimers()
        {
            for (int i = 0; i < MaxTimers; i++) { tName[i] = null; tMs[i] = 0f; tPeak[i] = 0f; tHits[i] = 0; }
            tCount = 0;
        }

        // The honest cost, on screen. Built here and nowhere else.
        public static string Report()
        {
            if (tCount == 0) return "[SfxField] no timings recorded";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(512);
            float total = 0f;
            sb.Append("[SfxField] pass timings (EMA ms / peak ms / calls)");
            for (int i = 0; i < tCount; i++)
            {
                total += tMs[i];
                sb.Append('\n').Append("  ").Append(tName[i]).Append(": ")
                  .Append(tMs[i].ToString("0.000")).Append(" / ")
                  .Append(tPeak[i].ToString("0.000")).Append(" / ")
                  .Append(tHits[i]);
            }
            sb.Append('\n').Append("  TOTAL: ").Append(total.ToString("0.000")).Append(" ms");
            return sb.ToString();
        }

        // ---------------- csv dump ----------------

        // One row per cage node, plus a header block of everything that would change
        // the reading of those rows. Modelled on SquishSim.DumpCsv (SquishSim.cs:1382)
        // — same idea, more channels, because six effects share these columns.
        public static void DumpCsv(FieldContext ctx, DeformField f, System.Text.StringBuilder sb)
        {
            if (f == null || sb == null) return;
            sb.Append("# n=").Append(f.n)
              .Append(" usedEdge=").Append(f.usedEdge.ToString("0.00000"))
              .Append(" dens=").Append(f.dens.ToString("0.000"))
              .Append(" cageArea=").Append(f.cageArea.ToString("0.00000"))
              .Append(" renderArea=").Append(f.renderArea.ToString("0.00000"))
              .Append(" areaRatio=").Append((f.cageArea / Mathf.Max(f.renderArea, 1e-9f)).ToString("0.00000"))
              .Append('\n');
            sb.Append("# postFlags=").Append(f.postFlags)
              .Append(" dt=").Append(f.dt.ToString("0.00000"))
              .Append(" time=").Append(f.time.ToString("0.000"))
              .Append(" eventCount=").Append(f.eventCount)
              .Append(" contactWeightSum=").Append(f.contactWeightSum.ToString("0.0000"))
              .Append(" axisQuality=").Append(f.axisQuality.ToString("0.0000"))
              .Append('\n');
            sb.Append("# contactCentroid=").Append(V(f.contactCentroid))
              .Append(" pushAxis=").Append(V(f.pushAxis)).Append('\n');
            if (ctx != null)
                sb.Append("# fleshVel=").Append(V(ctx.fleshVel))
                  .Append(" fleshAcc=").Append(V(ctx.fleshAcc))
                  .Append(" fleshJerk=").Append(V(ctx.fleshJerk))
                  .Append(" relSpeed=").Append(ctx.relSpeed.ToString("0.000"))
                  .Append(" overlapping=").Append(ctx.overlapping ? 1 : 0)
                  .Append(" contactActive=").Append(ctx.contactActive ? 1 : 0)
                  .Append('\n');
            if (f.events != null)
                for (int e = 0; e < f.events.Length; e++)
                {
                    if (!f.events[e].live) continue;
                    sb.Append("# event ").Append(e)
                      .Append(" node=").Append(f.events[e].originNode)
                      .Append(" origin=").Append(V(f.events[e].originPos))
                      .Append(" age=").Append(f.events[e].age.ToString("0.0000"))
                      .Append(" energy=").Append(f.events[e].energy.ToString("0.0000"))
                      .Append(" width=").Append(f.events[e].width.ToString("0.0000"))
                      .Append('\n');
                }
            sb.Append("i,px,py,pz,nx,ny,nz,mask,seed,halo,clear,excite,dirx,diry,dirz,")
              .Append("cx,cy,cz,wx,wy,wz,gx,gy,gz\n");
            for (int i = 0; i < f.n; i++)
            {
                sb.Append(i).Append(',');
                A(sb, f.pos[i]); A(sb, f.nrm[i]);
                sb.Append(f.mask[i].ToString("0.0000")).Append(',')
                  .Append(f.seed[i].ToString("0.00000")).Append(',')
                  .Append(f.halo[i].ToString("0.00000")).Append(',')
                  .Append(f.clear[i].ToString("0.00000")).Append(',')
                  .Append(f.excite[i].ToString("0.00000")).Append(',');
                A(sb, f.dir[i]); A(sb, f.dispContact[i]); A(sb, f.dispWave[i]);
                sb.Append(f.dispGuard[i].x.ToString("0.00000")).Append(',')
                  .Append(f.dispGuard[i].y.ToString("0.00000")).Append(',')
                  .Append(f.dispGuard[i].z.ToString("0.00000")).Append('\n');
            }
        }

        static void A(System.Text.StringBuilder sb, Vector3 v)
        {
            sb.Append(v.x.ToString("0.00000")).Append(',')
              .Append(v.y.ToString("0.00000")).Append(',')
              .Append(v.z.ToString("0.00000")).Append(',');
        }

        static string V(Vector3 v)
        {
            return v.x.ToString("0.00000") + " " + v.y.ToString("0.00000") + " " + v.z.ToString("0.00000");
        }

        // Convenience for the F11 path: build the CSV and drop it next to the other
        // suite dumps. Returns the path written, or "" on failure.
        public static string WriteCsv(string who, FieldContext ctx, DeformField f)
        {
            try
            {
                string dir = System.IO.Path.Combine(Application.persistentDataPath, "fielddebug");
                System.IO.Directory.CreateDirectory(dir);
                System.Text.StringBuilder sb = new System.Text.StringBuilder(1 << 20);
                DumpCsv(ctx, f, sb);
                string path = System.IO.Path.Combine(dir, who + "_field.csv");
                System.IO.File.WriteAllText(path, sb.ToString());
                return path;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[SfxField] csv dump failed: " + e.Message);
                return "";
            }
        }
    }
}
