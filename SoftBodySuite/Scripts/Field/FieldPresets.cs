using System;
using System.Collections.Generic;
using UnityEngine;

namespace SoftBodySuite.Field
{
    // The single wiring point for the field effect family, plus the twelve
    // built-in presets.
    //
    // Registration lives here rather than in the core on purpose: DeformField,
    // FieldSource, FieldPost and FieldProxy have NO compile dependency on which
    // effects exist. Adding a seventh effect is one new file and one new line in
    // RegisterAll - which is the bet this whole fork is making.
    //
    // The presets are transcribed literally. Nothing is
    // rounded and nothing is tidied: 0.291667 and 0.518519 are deliberately
    // dialled-in numbers and they stay as they are. There is no JSON and no disk
    // access here - these are compiled-in built-ins. User presets go through the
    // existing PresetStore.
    public static class FieldPresets
    {
        // ---------- registration ----------
        public static void RegisterAll(FieldRegistry r)
        {
            if (r == null) return;
            r.Register(new ContactSource());        // 100
            r.Register(new InflateSource());        // 200
            r.Register(new OrganicSource());        // 300
            r.Register(new PenetrationSource());    // 400
            r.Register(new RingSource());           // 150
            r.Register(new LiftSource());           // 160
            r.Register(new MotionExciter());        // 500
            r.Register(new RippleSource());         // 600
            r.Register(new NoiseJiggleSource());    // 700
            r.SortByOrder();
        }

        // Called once at UI build. Each source ships its own rows.
        public static void DescribeAll(UiSchema s)
        {
            if (s == null) return;
            // fx_mix belongs to no source: it is the crossfade between the shipped look
            // and the whole field, and it defaults to 0 so a config that has never seen
            // this build renders exactly as v2.0.0 did.
            // Its OWN header. Without one, Field Mix was built straight after the shipped
            // suite's last section and got filed under "Presets": collapsed, at the bottom,
            // where nobody could find the one control that switches the whole field on.
            s.AddSection("Field \u00b7 Master");
            s.AddSlider("Field \u00b7 Master", "fx_mix", "Field Mix", 0f, 1f, 0f,
                        delegate (SoftBodySuite.FieldRegion r) { return r.sfxMix; },
                        delegate (SoftBodySuite.FieldRegion r, float v) { r.sfxMix = v; });
            s.Tooltip("fx_mix", "0 = this stage adds nothing (the other stages' look only). 1 = the full "
                                + "field effect. Drag it on a live avatar to compare the two.");
            new ContactSource().Describe(s);
            new InflateSource().Describe(s);
            new OrganicSource().Describe(s);
            new PenetrationSource().Describe(s);
            new RingSource().Describe(s);
            new LiftSource().Describe(s);
            new MotionExciter().Describe(s);
            new RippleSource().Describe(s);
            new NoiseJiggleSource().Describe(s);
        }

        // ---------- factory defaults ----------
        // NOT the same numbers as the Soft Flesh
        // preset: these were tuned separately (dialled in on a test scene
        // and fixed as the
        // default). A new region is born with these; a preset button overwrites
        // them.
        public static void ApplyFactoryDefaults(SoftBodySuite.FieldRegion d)
        {
            if (d == null) return;
            d.effect = "Squish";
            d.intensity = 2.0f;
            d.shapeSmoothing = 15f;
            d.spread = 1f;
            d.volumePreservation = 0.21f;
            d.lateralFlow = 0.291667f;
            d.contactHardness = 0.518519f;
            d.correctiveSmooth = 4f;
            d.collisionOffset = 0.0f;
            d.splay = 0.0f;                 // SQUISH_SPLAY_DEFAULT
        }

        // SQUISH_INFLATE_DEFAULTS - a separate set; Squish and Inflate diverge.
        public static void ApplyInflateFactoryDefaults(SoftBodySuite.FieldRegion d)
        {
            if (d == null) return;
            d.effect = "Squish & Inflate";
            d.intensity = 1.38f;            // "Squish Strength"
            d.shapeSmoothing = 11f;
            d.spread = 9f;
            d.volumePreservation = 0.26f;
            d.lateralFlow = 0.0f;
            d.contactHardness = 0.0f;
            d.correctiveSmooth = 10f;
            d.collisionOffset = 0.0011f;
            d.splay = 0.0f;                 // SQUISH_INFLATE_SPLAY_DEFAULT
            d.inflateEnable = 1f;
            d.inflatePressure = 1.0f;       // SQUISH_INFLATE_PRESSURE_DEFAULT
        }

        // ---------- the twelve built-ins ----------
        class Entry
        {
            public string name;
            public string effect;
            public string desc;
            public Action<SoftBodySuite.FieldRegion> apply;
        }

        static List<Entry> list;
        static Dictionary<string, Entry> map;

        static void Build()
        {
            if (list != null) return;
            list = new List<Entry>();
            map = new Dictionary<string, Entry>();

            Add("Soft Flesh", "Squish",
                "Soft flesh (glutes, thighs): rounded footprint, natural volume and lateral flow",
                SoftFlesh);
            Add("Memory Foam", "Squish",
                "Foam: wide halo, recovers without bulging",
                MemoryFoam);
            Add("Elastic Rubber", "Squish",
                "Rubber: sharply defined footprint and pronounced bulge",
                ElasticRubber);
            Add("Glute Jiggle", "Fat Jiggle",
                "Glute fat: large lobes, pronounced lateral slosh",
                GluteJiggle);
            Add("Heavy Fat", "Fat Jiggle",
                "Heavy fat: large slow masses with a deep bounce",
                HeavyFat);
            Add("Squishy Jelly", "Ripple",
                "Jelly: dense fast waves while contact lasts",
                SquishyJelly);
            Add("Water Surface", "Ripple",
                "Water: wide slow waves that travel far",
                WaterSurface);
            Add("Soft Balloon", "Squish & Inflate",
                "Balloon: denting it visibly inflates the rest",
                SoftBalloon);
            Add("Bouncy Rubber", "Squish & Inflate",
                "Dense rubber: firm dent with subtle inflation",
                BouncyRubber);
            Add("Tight Grip", "Penetration",
                "Tight channel: firm clinging grip with a marked pulse",
                TightGrip);
            Add("Wet & Loose", "Penetration",
                "Soft wet channel: gentle grip, high sheen and jiggle",
                WetAndLoose);
            Add("Cartoon Flesh", "Organic",
                "Animated character flesh: very soft contact, pronounced squash & stretch with preserved volume and wobble after impact",
                CartoonFlesh);
        }

        static void Add(string name, string effect, string desc, Action<SoftBodySuite.FieldRegion> apply)
        {
            Entry e = new Entry();
            e.name = name; e.effect = effect; e.desc = desc; e.apply = apply;
            list.Add(e);
            map[name] = e;
        }

        public static string[] Names()
        {
            Build();
            string[] r = new string[list.Count];
            for (int i = 0; i < list.Count; i++) r[i] = list[i].name;
            return r;
        }

        public static string Description(string name)
        {
            Build();
            Entry e;
            if (name != null && map.TryGetValue(name, out e)) return e.desc;
            return "";
        }

        // A preset button overwrites the region's tuning and picks its own effect
        // family. sfxMix is deliberately NOT touched: that is the user's
        // crossfade against the shipped look, not part of the tuning.
        public static bool Apply(string name, SoftBodySuite.FieldRegion dst)
        {
            Build();
            if (dst == null || name == null) return false;
            Entry e;
            if (!map.TryGetValue(name, out e)) return false;

            ClearFamilies(dst);
            dst.effect = e.effect;
            e.apply(dst);
            return true;
        }

        // every family off, so switching preset is predictable instead of
        // accumulating whatever the last one turned on
        static void ClearFamilies(SoftBodySuite.FieldRegion d)
        {
            d.inflateEnable = 0f;
            d.organicEnable = 0f;
            d.rippleEnable = 0f;
            d.jiggleEnable = 0f;
            d.penEnable = 0f;
        }

        // ----- SQUISH -----
        static void SoftFlesh(SoftBodySuite.FieldRegion d)
        {
            d.intensity = 1.0f;
            d.shapeSmoothing = 8f;
            d.spread = 10f;
            d.volumePreservation = 1.0f;
            d.lateralFlow = 0.6f;
            d.contactHardness = 0.8f;
            d.correctiveSmooth = 3f;
            d.collisionOffset = 0.01f;
        }

        static void MemoryFoam(SoftBodySuite.FieldRegion d)
        {
            d.intensity = 1.0f;
            d.shapeSmoothing = 10f;
            d.spread = 12f;
            d.volumePreservation = 0.4f;
            d.lateralFlow = 0.25f;
            d.contactHardness = 0.75f;
            d.correctiveSmooth = 3f;
            // no Collision Offset in this preset: the built-in default stands
        }

        static void ElasticRubber(SoftBodySuite.FieldRegion d)
        {
            d.intensity = 1.0f;
            d.shapeSmoothing = 4f;
            d.spread = 6f;
            d.volumePreservation = 1.4f;
            d.lateralFlow = 0.6f;
            d.contactHardness = 0.95f;
            d.correctiveSmooth = 2f;
        }

        // ----- FAT JIGGLE -----
        static void GluteJiggle(SoftBodySuite.FieldRegion d)
        {
            d.jiggleEnable = 1f;
            d.wobbleAmplitude = 0.06f;
            d.wobbleScale = 2.2f;
            d.wobbleSpeed = 2.5f;
            d.lateralMotion = 1.2f;
            d.normalMotion = 0.5f;
            d.wobbleDecay = 1.0f;
            d.dentDepth = 1.0f;
            d.impactSensitivity = 2.5f;
            d.relaxTime = 0.5f;
            d.wobbleSmoothing = 3f;
        }

        static void HeavyFat(SoftBodySuite.FieldRegion d)
        {
            d.jiggleEnable = 1f;
            d.wobbleAmplitude = 0.09f;
            d.wobbleScale = 1.6f;
            d.wobbleSpeed = 1.5f;
            d.lateralMotion = 1.0f;
            d.normalMotion = 0.8f;
            d.wobbleDecay = 0.7f;
            d.dentDepth = 1.0f;
            d.impactSensitivity = 2.0f;
            d.relaxTime = 0.9f;
            d.wobbleSmoothing = 3f;
        }

        // ----- RIPPLE -----
        // Both ripple presets were tuned for the continuous
        // sine train - their descriptions ("dense fast waves while contact
        // lasts", "waves that travel far") are train behaviour and their
        // Frequency values only mean anything in train mode. So they select mode
        // 0 explicitly, even though the region default is the single-wave Ricker.
        static void SquishyJelly(SoftBodySuite.FieldRegion d)
        {
            d.rippleEnable = 1f;
            d.rippleMode = 0f;
            d.rippleAmplitude = 0.25f;
            d.rippleFrequency = 10.0f;
            d.waveSpeedFx = 6.0f;
            d.rippleDecay = 1.2f;
            d.dentDepth = 1.0f;
            d.impactSensitivity = 2.0f;
            d.relaxTime = 0.7f;
            d.rippleSmoothing = 2f;
        }

        static void WaterSurface(SoftBodySuite.FieldRegion d)
        {
            d.rippleEnable = 1f;
            d.rippleMode = 0f;
            d.rippleAmplitude = 0.35f;
            d.rippleFrequency = 4.0f;
            d.waveSpeedFx = 2.5f;
            d.rippleDecay = 0.35f;
            d.dentDepth = 1.0f;
            d.impactSensitivity = 1.5f;
            d.relaxTime = 1.2f;
            d.rippleSmoothing = 3f;
        }

        // ----- SQUISH & INFLATE -----
        static void SoftBalloon(SoftBodySuite.FieldRegion d)
        {
            d.inflateEnable = 1f;
            d.intensity = 1.0f;             // "Squish Strength"
            d.inflatePressure = 1.6f;
            d.shapeSmoothing = 8f;
            d.spread = 10f;
            d.volumePreservation = 0.4f;
            d.lateralFlow = 0.4f;
            d.contactHardness = 0.8f;
            d.correctiveSmooth = 2f;
        }

        static void BouncyRubber(SoftBodySuite.FieldRegion d)
        {
            d.inflateEnable = 1f;
            d.intensity = 1.0f;
            d.inflatePressure = 0.7f;
            d.shapeSmoothing = 5f;
            d.spread = 6f;
            d.volumePreservation = 0.6f;
            d.lateralFlow = 0.5f;
            d.contactHardness = 0.9f;
            d.correctiveSmooth = 2f;
        }

        // ----- PENETRATION -----
        static void TightGrip(SoftBodySuite.FieldRegion d)
        {
            d.penEnable = 1f;
            d.gripIntensity = 1.5f;
            d.tightness = 0.9f;
            d.reach = 0.025f;
            d.clenchAmount = 0.5f;
            d.clenchSpeed = 5.0f;
            d.penJiggle = 0.3f;
            d.wetness = 0.4f;
            d.penOffset = 0.004f;
            d.penSmoothing = 3f;
        }

        static void WetAndLoose(SoftBodySuite.FieldRegion d)
        {
            d.penEnable = 1f;
            d.gripIntensity = 0.7f;
            d.tightness = 0.4f;
            d.reach = 0.05f;
            d.clenchAmount = 0.2f;
            d.clenchSpeed = 2.0f;
            d.penJiggle = 0.6f;
            d.wetness = 0.9f;
            d.penOffset = 0.01f;
            d.penSmoothing = 4f;
        }

        // ----- SQUISH ORGANIC -----
        static void CartoonFlesh(SoftBodySuite.FieldRegion d)
        {
            d.organicEnable = 1f;
            d.squishIntensity = 1.2f;
            d.directionInfluence = 0.7f;
            d.volumePreservation = 1.0f;
            d.collisionStrength = 1.2f;
            d.collisionSoftness = 0.55f;
            d.velocityInfluence = 1.0f;
            d.organicJiggle = 0.5f;
            d.collisionOffset = 0.01f;
            d.impactSensitivity = 2.0f;
            d.relaxTime = 0.6f;
            // Stiffness is not in the preset: the built-in default (0.6) stands.
        }
    }
}
