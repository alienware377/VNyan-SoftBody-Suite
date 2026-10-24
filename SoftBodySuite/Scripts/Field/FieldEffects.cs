namespace SoftBodySuite.Field
{
    // WHICH SOURCES AN EFFECT FAMILY CONTAINS.
    //
    // FieldRegion.effect was written by every preset and by both factory-default
    // helpers and read by NOBODY, so the selector selected nothing: all twelve
    // presets ran the same source set (ContactSource's full two-pass core, plus
    // whatever per-channel enable flags the preset happened to set). A preset
    // called "Glute Jiggle" therefore shipped a full Soft-Flesh squish on top of
    // its wobble, at whatever Intensity the previous preset had left behind -
    // that preset does not set Intensity at all.
    //
    // Each effect family is its own separate effect, and they are not supersets
    // of one another:
    //
    //   Squish            natural squish core
    //   Squish & Inflate  natural squish core + pressure tail
    //   Ripple            flesh conform footprint + waves + impulse
    //   Fat Jiggle        flesh conform footprint + wobble + impulse
    //   Penetration       flesh conform footprint + grip   + impulse
    //   Squish Organic    natural squish core + squash    + impulse
    //
    // Two things follow, and both are implemented here:
    //   (1) a family only runs the sources it owns - Ripple has no pressure tail,
    //       Squish has no impulse envelope;
    //   (2) Ripple / Fat Jiggle / Penetration use the RAW flesh conform, not the
    //       core. Their footprint is conform * clamp01(Mask * Dent Depth) and
    //       nothing else: no Shape Smoothing diffusion, no volume pile, no
    //       Contact Hardness cleanup. ContactSource honours that through
    //       FieldContext.contactFootprint.
    public static class FieldEffects
    {
        public const int Squish        = 0;
        public const int SquishInflate = 1;
        public const int Ripple        = 2;
        public const int Jiggle        = 3;
        public const int Penetration   = 4;
        public const int Organic       = 5;

        // Tolerant on purpose: the presets say "Squish & Inflate" and "Organic",
        // the enum-style names SQUISH_INFLATE and SQUISH_ORGANIC also occur, and a
        // config hand-edited by a user may say either. An unknown string falls
        // back to Squish, which is the factory family and the least surprising.
        public static int Resolve(string effect)
        {
            if (effect == null) return Squish;
            string e = effect.Trim().ToLowerInvariant();
            if (e.Length == 0) return Squish;
            if (e.IndexOf("organic") >= 0) return Organic;
            if (e.IndexOf("inflate") >= 0) return SquishInflate;
            if (e.IndexOf("ripple") >= 0) return Ripple;
            if (e.IndexOf("jiggle") >= 0) return Jiggle;
            if (e.IndexOf("penetration") >= 0) return Penetration;
            return Squish;
        }

        public static string Name(int family)
        {
            switch (family)
            {
                case SquishInflate: return "Squish & Inflate";
                case Ripple: return "Ripple";
                case Jiggle: return "Fat Jiggle";
                case Penetration: return "Penetration";
                case Organic: return "Squish Organic";
                default: return "Squish";
            }
        }

        // Ripple and Fat Jiggle get the bare conform footprint from ContactSource
        // (RippleSource and NoiseJiggleSource write waves only and say so).
        // Penetration is NOT in this list because it is not in Allows either: it runs
        // its own flesh conform in PenetrationSource.Prepare and applies it as
        // `dent` in its own Write, so letting ContactSource run as well would stamp
        // the opening twice.
        public static bool FootprintOnly(int family)
        {
            return family == Ripple || family == Jiggle;
        }

        // Both scale that footprint by clamp01(Mask * Dent Depth).
        // Kept as its own query so the Penetration rule (clamp01(Mask)
        // alone) stays written down where the next family is added.
        public static bool FootprintUsesDent(int family)
        {
            return family == Ripple || family == Jiggle;
        }

        // The gate itself. A source that is not in the family never has IsActive
        // called, so it cannot publish anything either (OrganicSource publishes
        // ctx.coreOverride from IsActive, and outside its own family it must not).
        //
        // ContactSource is in five of the six families - see the case below.
        // THE ENABLE TOGGLES ARE A SECOND WAY IN. The family gate below treats
        // each effect family as a separate effect - but in this panel the
        // Ripple / Fat Jiggle / Inflate / Organic / Penetration sections each carry an
        // "on" toggle, and with the family left at Squish (the default, and what every
        // squish preset sets) those toggles, every slider under them and the motion
        // trigger were all gated off before they could run. Measured on a dancing
        // avatar: Field Mix 1, Ripple and Fat on, 0.0 mm every frame. A source whose own
        // toggle is on now runs whatever the family says; with every toggle off this is
        // exactly the old gate.
        public static bool Allows(int family, string sourceId, SoftBodySuite.FieldRegion p)
        {
            if (p != null && sourceId != null)
            {
                bool rip = p.rippleEnable > 0.5f, jig = p.jiggleEnable > 0.5f;
                switch (sourceId)
                {
                    case "ripple": if (rip) return true; break;
                    case "jiggle": if (jig) return true; break;
                    case "inflate": if (p.inflateEnable > 0.5f) return true; break;
                    case "organic": if (p.organicEnable > 0.5f) return true; break;
                    case "penetration": if (p.penEnable > 0.5f) return true; break;
                    // the wave sources need the impulse envelope to have anything to ride
                    case "motion": if (rip || jig) return true; break;
                }
            }
            return Allows(family, sourceId);
        }

        public static bool Allows(int family, string sourceId)
        {
            if (sourceId == null) return false;
            switch (sourceId)
            {
                case "contact":
                    // Every family applies a conform - but Penetration applies its OWN
                    // (PenetrationSource.cs:115 and :155), so ContactSource must stay out
                    // of that one or the channel opening is stamped twice.
                    return family != Penetration;
                case "inflate":
                    return family == SquishInflate;
                case "organic":
                    return family == Organic;
                case "penetration":
                    return family == Penetration;
                case "ripple":
                    return family == Ripple;
                case "jiggle":
                    return family == Jiggle;
                case "motion":
                    // the impulse envelope exists on ripple/jiggle/penetration/organic
                    // and on NEITHER squish family.
                    return family != Squish && family != SquishInflate;
            }
            // An effect family that has never heard of a source lets it through:
            // adding the eighth source must not need this file edited, which is
            // the whole bet of the fork. Add a case here only to RESTRICT one.
            return true;
        }
    }
}
