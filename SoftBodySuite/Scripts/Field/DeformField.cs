using UnityEngine;

namespace SoftBodySuite.Field
{
    // Which accumulator a source's displacement lands in. The split is by DESTINY,
    // not by source: FieldPost runs ONCE for everybody and has no way to ask "who
    // wrote this node" (postFlags is an OR across sources), so each source has to
    // declare up front how its contribution is allowed to be finished.
    public enum FieldAcc { Contact = 0, Wave = 1, Guard = 2 }

    // A source ORs these into field.postFlags in Write(); FieldPost reads them to
    // decide which passes run AT ALL. This is the short-circuit: a frame where only
    // RippleSource wrote leaves VolumePreserve/Barrier/AntiClip clear, and those
    // passes are then skipped entirely instead of sweeping a zero field.
    public static class FieldPostFlags
    {
        public const int VolumePreserve   = 1;
        public const int CorrectiveSmooth = 2;
        public const int SkinRamp         = 4;
        public const int Barrier          = 8;
        public const int AntiClip         = 16;
        public const int SelfContact      = 32;
        public const int WaveSmooth       = 64;
    }

    // One impulse. Ripple's expanding ring and the jiggle envelope both read these
    // rather than each keeping their own history — one impact, one event, however
    // many effects are listening.
    public struct FieldEvent
    {
        public int originNode;
        public Vector3 originPos;   // proxy-local
        public float age;           // seconds since push
        public float energy;
        public float width;
        public bool live;
    }

    // The shared per-cage-node state every FieldSource reads and writes. A plain data
    // holder plus three cheap helpers — it allocates in Ensure() and NOWHERE else, so
    // a frame costs no GC no matter how many sources are live.
    public class DeformField
    {
        public const int MaxEvents = 8;

        public int n;

        // THREE accumulators, not two:
        //   dispContact — smoothed by the halo gate, volume-preserved, barriered
        //   dispWave    — never touched by the halo gate (ripple/jiggle crests); the
        //                 min(halo*12,1) gate would eat exactly the crests under a
        //                 hand, which is the one case Squish+Ripple has to survive
        //   dispGuard   — written by FieldPost's anti-clip ONLY, applied last, never
        //                 smoothed: a smoothed guarantee is not a guarantee. It has to
        //                 be its own bucket because the corrective smooth re-sinks
        //                 vertices INTO the collider, which is why the hard anti-clip
        //                 runs dead last.
        public Vector3[] dispContact;
        public Vector3[] dispWave;
        public Vector3[] dispGuard;

        // typed channels
        public Vector3[] dir;      // normalised escape/push direction per node
        public float[] clear;      // penetration / clearance depth, metres
        public float[] halo;       // blur(|seed|, Spread)
        public float[] seed;       // |contact seed| pre-blur (basin = halo - seed)
        public bool[] seedLimb;    // that seed came from a hand / finger / arm capsule
        public float[] limbDepth;  // RAW depth of a limb in this node: not capped at Max Dent,
                                   // and kept even when SELF_RANGE rejected the conform
        public float[] excite;     // ONE shared excitation envelope, per node
        public float[] mask;       // cage paint weight (copy of cage.simWeight)

        // cage state this frame (proxy-local)
        public Vector3[] pos;
        public Vector3[] nrm;

        public float usedEdge;
        public float dens;         // clamp(0.008 / usedEdge, 1, 6)
        public float cageArea;
        public float renderArea;

        public int postFlags;
        public float dt;
        public float time;

        public FieldEvent[] events;
        public int eventCount;

        // contact reductions, filled by ContactSource.Prepare
        public Vector3 contactCentroid;
        public Vector3 pushAxis;
        public float axisQuality;
        public float contactWeightSum;

        // THE ONLY ALLOCATION SITE IN THIS FILE. Called once per cage build.
        public void Ensure(int nodes)
        {
            if (n == nodes && dispContact != null) return;
            dispContact = new Vector3[nodes];
            dispWave = new Vector3[nodes];
            dispGuard = new Vector3[nodes];
            dir = new Vector3[nodes];
            clear = new float[nodes];
            halo = new float[nodes];
            seed = new float[nodes];
            seedLimb = new bool[nodes];
            limbDepth = new float[nodes];
            excite = new float[nodes];
            mask = new float[nodes];
            pos = new Vector3[nodes];
            nrm = new Vector3[nodes];
            events = new FieldEvent[MaxEvents];
            n = nodes;
        }

        // Start of a frame. Clears what is re-derived from scratch and DELIBERATELY
        // leaves alone what is not:
        //   excite — an integrator with memory; MotionExciter and ContactSource decay
        //            it themselves, and zeroing it here would kill every envelope
        //   mask/pos/nrm — refilled wholesale by FieldProxy from the cage
        //   events — aged out separately by AgeEvents, which runs BEFORE the sources
        public void BeginFrame(float deltaTime, float now)
        {
            if (dispContact == null) return;
            System.Array.Clear(dispContact, 0, n);
            System.Array.Clear(dispWave, 0, n);
            System.Array.Clear(dispGuard, 0, n);
            System.Array.Clear(dir, 0, n);
            System.Array.Clear(clear, 0, n);
            System.Array.Clear(halo, 0, n);
            System.Array.Clear(seed, 0, n);
            System.Array.Clear(seedLimb, 0, n);
            System.Array.Clear(limbDepth, 0, n);
            dt = deltaTime;
            time = now;
            postFlags = 0;
            contactCentroid = Vector3.zero;
            pushAxis = Vector3.zero;
            axisQuality = 0f;
            contactWeightSum = 0f;
        }

        // ~4500 nodes x ~6 sources a frame: a plain switch, no delegates, no bounds
        // games, nothing that a JIT without inlining would turn into a call.
        public void AddDisp(int i, Vector3 d, FieldAcc acc)
        {
            switch (acc)
            {
                case FieldAcc.Contact: dispContact[i] += d; break;
                case FieldAcc.Wave: dispWave[i] += d; break;
                case FieldAcc.Guard: dispGuard[i] += d; break;
            }
        }

        public void Require(int flags)
        {
            postFlags |= flags;
        }

        // Free slot first; when the ring is full the WEAKEST event is the one that
        // loses its slot, not the oldest — a big new impact must never be dropped
        // because eight taps are still ringing.
        public int PushEvent(int originNode, Vector3 originPos, float energy, float width)
        {
            if (events == null || events.Length == 0) return -1;
            int slot = -1;
            for (int i = 0; i < events.Length; i++)
                if (!events[i].live) { slot = i; break; }
            if (slot < 0)
            {
                float worst = float.MaxValue;
                for (int i = 0; i < events.Length; i++)
                    if (events[i].energy < worst) { worst = events[i].energy; slot = i; }
            }
            events[slot].originNode = originNode;
            events[slot].originPos = originPos;
            events[slot].age = 0f;
            events[slot].energy = energy;
            events[slot].width = width;
            events[slot].live = true;
            int live = 0;
            for (int i = 0; i < events.Length; i++) if (events[i].live) live++;
            eventCount = live;
            return slot;
        }

        // FieldProxy calls this ONCE per frame, BEFORE the sources run, so every
        // source sees the same event ages for the whole frame.
        public void AgeEvents(float deltaTime, float maxAge)
        {
            if (events == null) return;
            int live = 0;
            for (int i = 0; i < events.Length; i++)
            {
                if (!events[i].live) continue;
                events[i].age += deltaTime;
                if (events[i].age > maxAge) { events[i].live = false; continue; }
                live++;
            }
            eventCount = live;
        }

        // Area-weighted vertex normals of the DEFORMED cage. Not decoration: the normal
        // must be re-evaluated after every position change, so the pass-2
        // hard cleanup and the anti-clip both read a normal that already includes the
        // dent. FieldPost calls this between passes. Accumulates straight into nrm
        // (the un-normalised cross product is 2*area*n, which IS the area weighting)
        // so there is no scratch buffer and no allocation.
        public void RecomputeNormals(int[] tris)
        {
            if (tris == null || nrm == null || pos == null) return;
            System.Array.Clear(nrm, 0, n);
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if (a >= n || b >= n || c >= n) continue;
                Vector3 pa = pos[a], pb = pos[b], pc = pos[c];
                Vector3 cr = Vector3.Cross(pb - pa, pc - pa);
                nrm[a] += cr; nrm[b] += cr; nrm[c] += cr;
            }
            for (int i = 0; i < n; i++)
            {
                float m = nrm[i].magnitude;
                nrm[i] = m > 1e-9f ? nrm[i] / m : Vector3.up;
            }
        }

        // The sleep path's "is there anything left to do" test. Residual excitation
        // counts: a jiggle that is still ringing must keep the proxy awake even with
        // every collider long gone.
        public bool AnyActive
        {
            get
            {
                if (postFlags != 0) return true;
                if (events != null && eventCount > 0)
                    for (int i = 0; i < events.Length; i++)
                        if (events[i].live) return true;
                if (excite != null)
                    for (int i = 0; i < n; i++)
                        if (excite[i] > 1e-5f) return true;
                return false;
            }
        }

        // Reset / rebind: zero EVERYTHING, excite and the event ring included.
        public void ClearAll()
        {
            if (dispContact == null) return;
            System.Array.Clear(dispContact, 0, n);
            System.Array.Clear(dispWave, 0, n);
            System.Array.Clear(dispGuard, 0, n);
            System.Array.Clear(dir, 0, n);
            System.Array.Clear(clear, 0, n);
            System.Array.Clear(halo, 0, n);
            System.Array.Clear(seed, 0, n);
            System.Array.Clear(seedLimb, 0, n);
            System.Array.Clear(limbDepth, 0, n);
            System.Array.Clear(excite, 0, n);
            for (int i = 0; i < events.Length; i++) events[i].live = false;
            eventCount = 0;
            postFlags = 0;
            contactCentroid = Vector3.zero;
            pushAxis = Vector3.zero;
            axisQuality = 0f;
            contactWeightSum = 0f;
        }
    }
}
