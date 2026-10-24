using System.Collections.Generic;
using UnityEngine;

namespace SoftBodySuite.Field
{
    // Everything one solve needs, with NO UnityEngine.Object anywhere in it. That is
    // the rule that keeps the whole field movable to a worker thread later: the main
    // thread captures Transforms into plain numbers (FieldProxy.CaptureFrameInputs)
    // and the sources only ever see this object. Allocated ONCE per sim and reused;
    // the array fields are ALIASES into the field, never copies.
    public class FieldContext
    {
        public SoftBodySuite.FieldRegion p;
        public DeformField field;
        public CageGeodesic geo;
        public FieldCollider col;
        public FieldBlur blur;

        public float dt;
        public float time;
        public int n;
        public Vector3[] pos;      // alias of field.pos
        public Vector3[] nrm;      // alias of field.nrm
        public float[] mask;       // alias of field.mask
        public int[] cageTris;
        public float usedEdge;
        public float dens;

        // The painted region's own size, in metres: the distance from the paint-weighted
        // centroid of the cage to its furthest painted node, measured ONCE per cage build
        // in the rest pose. This is the fork's feature-size measure, and it is what turns
        // FieldRegion.rippleAmplitude / wobbleAmplitude from a fraction into metres - see
        // the note on FieldRegion.amplitudeRelative. Rest pose, not the posed cage, so the
        // wave height does not breathe with the animation.
        public float regionRadius = 0.06f;

        // Resolved metre values for this frame, written by FieldProxy from regionRadius.
        // RippleSource and NoiseJiggleSource read THESE and never p.rippleAmplitude /
        // p.wobbleAmplitude, so the number the user typed is never written back over.
        public float rippleAmpM;
        public float wobbleAmpM;

        // The effect family this region is tuned as (FieldEffects.Resolve of
        // FieldRegion.effect), resolved ONCE per frame by FieldProxy and read by
        // FieldRegistry.PrepareAll to decide which sources exist at all.
        public int family;
        // FieldEffects.FootprintOnly(family): ContactSource runs the raw
        // flesh conform footprint instead of the two-pass core.
        public bool contactFootprint;
        // FieldEffects.FootprintUsesDent(family): that footprint is scaled by
        // clamp01(mask * dentDepth); false means clamp01(mask) alone (Penetration).
        public bool contactFootprintDent;

        // main-thread capture — no UnityEngine.Object may ever be stored here
        public Vector3 fleshVel;
        public Vector3 fleshAcc;
        public Vector3 fleshJerk;
        public Vector3 fleshCentroid;
        public Vector3 dragVec;
        public float relSpeed;     // max relative collider/flesh speed while overlapping
        public bool overlapping;   // any collider overlapping the paint this frame

        // set by ContactSource.Prepare, read by MotionExciter (Order 500 > 100)
        public bool contactActive;
        // World up and the avatar's forward, in cage space (Lift's axis weights).
        public Vector3 upLocal = Vector3.up, fwdLocal = Vector3.forward;
        // Metres per "region size" for the wave sliders (1 when amplitudeRelative is off).
        public float waveScale = 1f;
        public Vector3 contactCentroid;

        // ripple start points, captured on the MAIN thread (proxy-local)
        public Vector3[] pickPts = new Vector3[16];   // the user's picked points, this frame
        public int pickCount;
        public Vector3[] nipO = new Vector3[4], nipD = new Vector3[4];   // breast-bone rays
        public int nipCount;
        // what the nipple finder chose, for the marker dots (written by MotionExciter)
        public int[] markNip = new int[4];
        public int markNipCount;
        public Vector3 pushAxis;
        public float axisQuality;

        // set by OrganicSource so ContactSource can run its core with derived knobs
        public bool coreOverride;
        public float ovIntensity;  // drives the core Intensity (organic: Collision Strength)
        public float ovShapeSmoothing;
        public float ovSpread;
        public float ovContactHardness;
        public float ovLateralFlow;
        public float ovSplay;
        public float[] ovMask;     // softness field replacing mask inside the core
    }

    // One effect = one file that ships its own maths AND its own UI rows. The whole
    // bet of this fork is that adding the seventh effect costs one file and nothing
    // else, so nothing here may ever need editing to add a source.
    public abstract class FieldSource
    {
        public abstract string Id { get; }

        // The sort key. Registered order is irrelevant; this is what runs:
        //   ContactSource     100
        //   InflateSource     200
        //   OrganicSource     300
        //   PenetrationSource 400
        //   MotionExciter     500
        //   RippleSource      600
        //   NoiseJiggleSource 700
        // MotionExciter is at 500 ON PURPOSE — after every contact-writing source,
        // because CONTACT OWNS THE ORIGIN: the exciter has to see whether a contact
        // fired this frame before it decides whether to push a motion event of its own.
        public abstract int Order { get; }

        public abstract bool IsActive(FieldContext ctx);

        // Global reductions only (contact centroid, push axis, pressure mean) — the
        // things a LATER source might need to read. Never touches disp.
        public abstract void Prepare(FieldContext ctx);

        // Where the source actually writes displacement and ORs its postFlags.
        public abstract void Write(FieldContext ctx, DeformField f);

        // Each effect contributes its own UI rows; the window is built from the union.
        public abstract void Describe(UiSchema schema);

        public virtual void Reset() { }
    }

    // The ordered list of live sources. Deliberately knows NOTHING about the concrete
    // effect classes — all wiring lives in FieldPresets.RegisterAll(FieldRegistry),
    // which FieldProxy calls. With an empty registry every method below is a no-op
    // that costs nothing, and that is exactly what makes Milestone 1 (the empty field
    // rendering bit-identical to shipped v2.0.0) testable before any source exists.
    public class FieldRegistry
    {
        readonly List<FieldSource> sources = new List<FieldSource>();

        public int Count { get { return sources.Count; } }

        public FieldSource Get(int i)
        {
            return i >= 0 && i < sources.Count ? sources[i] : null;
        }

        public void Register(FieldSource s)
        {
            if (s == null) return;
            sources.Add(s);
        }

        // Insertion sort, called ONCE after registration. Seven items; List.Sort would
        // want a Comparison delegate and LINQ is banned outright.
        public void SortByOrder()
        {
            for (int i = 1; i < sources.Count; i++)
            {
                FieldSource key = sources[i];
                int j = i - 1;
                while (j >= 0 && sources[j].Order > key.Order)
                {
                    sources[j + 1] = sources[j];
                    j--;
                }
                sources[j + 1] = key;
            }
        }

        public void ResetAll()
        {
            for (int i = 0; i < sources.Count; i++) sources[i].Reset();
        }

        // TWO passes over the whole list, never merged. Prepare is where a source
        // computes the global reductions a later source may read (ContactSource's
        // centroid and push axis, which MotionExciter at Order 500 depends on);
        // Write is where disp is touched. Merging them would make the reduction a
        // source produced visible to only half the sources that need it, depending on
        // where it happened to sort.
        // IsActive is ALSO a publish step for some sources: OrganicSource is Order 300
        // but it sets ctx.coreOverride there, and ContactSource at Order 100 reads it.
        // So every source is asked IsActive before ANY source is prepared, and the
        // answers are cached for the whole frame - never re-asked inside Write, where a
        // source that has already published would answer a different question.
        bool[] active = new bool[0];

        // The effect-family gate runs BEFORE IsActive and short-circuits it. That
        // ordering is load-bearing: IsActive is a publish step for OrganicSource
        // (it writes ctx.coreOverride), and a source outside the current family
        // must not get the chance to publish anything. The registry still knows
        // nothing about the concrete effect CLASSES - it asks FieldEffects about a
        // string id, which is the same thing it already uses for diagnostics.
        public void PrepareAll(FieldContext ctx)
        {
            if (active.Length < sources.Count) active = new bool[sources.Count];
            for (int i = 0; i < sources.Count; i++)
                active[i] = FieldEffects.Allows(ctx.family, sources[i].Id, ctx.p) && sources[i].IsActive(ctx);
            for (int i = 0; i < sources.Count; i++)
                if (active[i]) sources[i].Prepare(ctx);
        }

        public bool WasActive(int i)
        {
            return i >= 0 && i < active.Length && active[i];
        }

        public void WriteAll(FieldContext ctx, DeformField f)
        {
            for (int i = 0; i < sources.Count; i++)
                if (i < active.Length && active[i]) sources[i].Write(ctx, f);
        }

        public void DescribeAll(UiSchema schema)
        {
            for (int i = 0; i < sources.Count; i++) sources[i].Describe(schema);
        }
    }
}
