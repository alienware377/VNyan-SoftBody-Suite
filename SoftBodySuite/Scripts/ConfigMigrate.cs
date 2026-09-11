using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SoftBodySuite
{
    // ---------------------------------------------------------------------------
    // One-shot import of the three old plugins' settings into the merged file.
    //
    // Runs only when softbodysuite.json does not exist yet. The three old files are
    // never written to and never deleted — they are the way back if this goes wrong.
    //
    // Region SHAPE (which verts, which bones, which colliders) is taken from
    // squishstudio.json, because Squish Studio owned the paint tools and the other
    // two mirrored it. Region MOTION values are taken from each plugin's own file,
    // because those genuinely differ and are what the user spent the time tuning.
    // ---------------------------------------------------------------------------
    public static class ConfigMigrate
    {
        public class Report
        {
            public bool ran;
            public int meshes, regions, valuesCopied, mismatches;
            public readonly List<string> lines = new List<string>();
            public void Note(string s) { lines.Add(s); }
        }

        // squishJson / jelloJson / wobbleJson are raw file text; any may be null.
        public static SuiteConfig Migrate(string squishJson, string jelloJson, string wobbleJson,
                                          Report rep)
        {
            if (rep == null) rep = new Report();
            SuiteConfig cfg = new SuiteConfig();

            JObject sq = Parse(squishJson, "squishstudio.json", rep);
            JObject je = Parse(jelloJson, "jellostudio.json", rep);
            JObject wo = Parse(wobbleJson, "wobblestudio.json", rep);

            // The owner is Squish; if it is missing fall back to whichever we have, so a
            // user who only ever installed one plugin still gets their regions.
            JObject owner = sq != null ? sq : (je != null ? je : wo);
            if (owner == null) { rep.Note("no old config found — starting fresh"); return cfg; }
            if (sq == null) rep.Note("squishstudio.json missing — region shapes taken from " +
                                     (je != null ? "jellostudio.json" : "wobblestudio.json"));

            MigrateSettings(cfg.settings, sq, je, wo, rep);
            MigrateMeshes(cfg, owner, sq, je, wo, rep);
            cfg.Sync();   // point every stage block at the paint it belongs to

            rep.ran = true;
            return cfg;
        }

        static JObject Parse(string text, string label, Report rep)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try { return JObject.Parse(text); }
            catch (Exception e) { rep.Note("could not read " + label + ": " + e.Message); return null; }
        }

        // ------------------------------------------------------------------ settings

        static void MigrateSettings(SuiteSettings s, JObject sq, JObject je, JObject wo, Report rep)
        {
            JObject ss = Sub(sq, "settings"), js = Sub(je, "settings"), ws = Sub(wo, "settings");

            // Global knobs: the three files each had their own copy. Squish ran last and
            // is the one whose value actually shaped the final frame, so it wins.
            JObject g = ss != null ? ss : (js != null ? js : ws);
            if (g != null)
            {
                s.enabled = Bool(g, "enabled", s.enabled);
                s.nativeDisable = Bool(g, "nativeDisable", s.nativeDisable);
                s.nativeScoped = Bool(g, "nativeScoped", s.nativeScoped);
            }
            if (js != null) s.hiddenMeshes = Strings(js, "hiddenMeshes", s.hiddenMeshes);

            // Per-stage: a stage counts as "was installed and on" only if its own file existed.
            CopyStage(s.wobble, ws, wo != null);
            CopyStage(s.jello, js, je != null);
            CopyStage(s.squish, ss, sq != null);
            s.jello.asyncSim = false;   // Jello never had a worker thread

            if (js != null)
            {
                JelloStageSettings j = s.jello;
                j.useRemesh = Num(js, "useRemesh", j.useRemesh);
                j.remeshSize = Num(js, "remeshSize", j.remeshSize);
                j.remeshPasses = Num(js, "remeshPasses", j.remeshPasses);
                j.projAvg = Num(js, "projAvg", j.projAvg);
                j.proxySmooth = Num(js, "proxySmooth", j.proxySmooth);
                j.seamLevel = Num(js, "seamLevel", j.seamLevel);
                j.seamRange = Num(js, "seamRange", j.seamRange);
                j.seamMaxStretch = Num(js, "seamMaxStretch", j.seamMaxStretch);
                j.boostStrength = Num(js, "boostStrength", j.boostStrength);
                j.boostSpread = Num(js, "boostSpread", j.boostSpread);
                j.boostMax = Num(js, "boostMax", j.boostMax);
                j.slapSens = Num(js, "slapSens", j.slapSens);
                j.slapPower = Num(js, "slapPower", j.slapPower);
            }

            // ----- clothes: the cage half from Jello, the guard half from Squish -----
            ClothSettings c = s.cloth;
            bool follow = js != null && Bool(js, "cageDrive", false);
            bool guard = ss != null && Bool(ss, "clipGuard", false);
            c.enabled = follow || guard;
            c.follow.enabled = follow;   // was Jell-o's "cage drive"
            c.guard.enabled = guard;     // was Squish's "clip guard"
            c.defaultMode = follow && guard ? ClothMode.Both
                          : follow ? ClothMode.Follow
                          : guard ? ClothMode.Guard
                          : ClothMode.Follow;   // what it would do once switched on

            if (js != null)
            {
                ClothFollow f = c.follow;
                f.range = Num(js, "cageFollowRange", f.range);
                f.fitStrength = Num(js, "cageFitStrength", f.fitStrength);
                f.inflate = Num(js, "cageInflate", f.inflate);
                f.inflateDyn = Num(js, "cageInflateDyn", f.inflateDyn);
                f.bindWhole = Bool(js, "cageBindWhole", f.bindWhole);
                f.boneFilter = Bool(js, "cageBoneFilter", f.boneFilter);
                f.fillMax = Num(js, "cageFillMax", f.fillMax);
                f.anchorMin = Num(js, "cageAnchorMin", f.anchorMin);
                f.clothOutside = Bool(js, "cageClothOutside", f.clothOutside);
                f.minClear = Num(js, "cageMinClear", f.minClear);
                f.folSharp = Num(js, "cageFolSharp", f.folSharp);
                f.folSmooth = Num(js, "cageFolSmooth", f.folSmooth);
                // The follower reused the BODY's seam smoother verbatim, so seed it from
                // there — splitting them silently would change how the clothes look.
                f.seamLevel = Num(js, "seamLevel", f.seamLevel);
                f.seamRange = Num(js, "seamRange", f.seamRange);
                f.seamMaxStretch = Num(js, "seamMaxStretch", f.seamMaxStretch);
            }
            if (ss != null)
            {
                ClothGuard gd = c.guard;
                gd.clearance = Num(ss, "clipClearance", gd.clearance);
                gd.range = Num(ss, "clipRange", gd.range);
                gd.strength = Num(ss, "clipStrength", gd.strength);
                gd.rimFade = Num(ss, "clipRimFade", gd.rimFade);
            }
            rep.Note("clothes: follow=" + follow + " guard=" + guard + " -> " + c.defaultMode +
                     (c.enabled ? "" : " (off)"));
        }

        static void CopyStage(StageSettings st, JObject src, bool installed)
        {
            st.enabled = installed && (src == null || Bool(src, "enabled", true));
            if (src == null) return;
            st.substeps = (int)Num(src, "substeps", st.substeps);
            st.maxDeltaTime = Num(src, "maxDeltaTime", st.maxDeltaTime);
            st.halfRate = Bool(src, "halfRate", st.halfRate);
            st.halfRateLerp = Bool(src, "halfRateLerp", st.halfRateLerp);
            st.asyncSim = Bool(src, "asyncSim", st.asyncSim);
        }

        // ------------------------------------------------------------------ meshes

        static void MigrateMeshes(SuiteConfig cfg, JObject owner, JObject sq, JObject je, JObject wo,
                                  Report rep)
        {
            JArray om = Arr(owner, "meshes");
            if (om == null) return;

            for (int mi = 0; mi < om.Count; mi++)
            {
                JObject src = om[mi] as JObject;
                if (src == null) continue;
                string meshName = Str(src, "mesh", "");

                SuiteMesh dm = new SuiteMesh();
                dm.mesh = meshName;
                dm.enabled = Bool(src, "enabled", true);

                JObject wMesh = FindMesh(wo, meshName), jMesh = FindMesh(je, meshName),
                        sMesh = FindMesh(sq, meshName);
                JArray orr = Arr(src, "regions");
                if (orr == null) { cfg.meshes.Add(dm); continue; }

                for (int ri = 0; ri < orr.Count; ri++)
                {
                    JObject sr = orr[ri] as JObject;
                    if (sr == null) continue;
                    SuiteRegion r = new SuiteRegion();
                    r.name = Str(sr, "name", "region");
                    r.enabled = Bool(sr, "enabled", true);
                    r.vertIndex = Ints(sr, "vertIndex");
                    r.weight = Floats(sr, "weight");
                    r.srcBones = Strings(sr, "srcBones", new List<string>());
                    r.colliders = Colliders(sr);
                    r.gravityPoseOnly = Bool(sr, "gravityPoseOnly", r.gravityPoseOnly);
                    r.refBone = Str(sr, "refBone", r.refBone);

                    rep.valuesCopied += CopyParams(r.wobble, FindRegion(wMesh, ri, r.name, rep, meshName, "wobble"));
                    rep.valuesCopied += CopyParams(r.jello, FindRegion(jMesh, ri, r.name, rep, meshName, "jello"));
                    rep.valuesCopied += CopyParams(r.squish, FindRegion(sMesh, ri, r.name, rep, meshName, "squish"));

                    dm.regions.Add(r);
                    rep.regions++;
                }
                cfg.meshes.Add(dm);
                rep.meshes++;
            }
        }

        static JObject FindMesh(JObject root, string meshName)
        {
            JArray a = Arr(root, "meshes");
            if (a == null) return null;
            for (int i = 0; i < a.Count; i++)
            {
                JObject o = a[i] as JObject;
                if (o != null && Str(o, "mesh", "") == meshName) return o;
            }
            return null;
        }

        // Index first (the mirror kept the lists in step), name as the safety net.
        static JObject FindRegion(JObject mesh, int index, string name, Report rep,
                                  string meshName, string stage)
        {
            JArray a = Arr(mesh, "regions");
            if (a == null) return null;
            JObject byIndex = index < a.Count ? a[index] as JObject : null;
            if (byIndex != null && Str(byIndex, "name", "") == name) return byIndex;
            for (int i = 0; i < a.Count; i++)
            {
                JObject o = a[i] as JObject;
                if (o != null && Str(o, "name", "") == name)
                {
                    rep.Note("matched by name, not position: " + meshName + " / " + name +
                             " (" + stage + ")");
                    return o;
                }
            }
            rep.mismatches++;
            rep.Note("no " + stage + " values for " + meshName + " / " + name + " — using defaults");
            return null;
        }

        // Reflection so a renamed or forgotten field can never be silently dropped:
        // every public field on the target that also exists in the old json is copied.
        static int CopyParams(object target, JObject src)
        {
            if (target == null || src == null) return 0;
            int n = 0;
            FieldInfo[] fields = target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                JToken t = src[f.Name];
                if (t == null || t.Type == JTokenType.Null) continue;
                try
                {
                    if (f.FieldType == typeof(float)) f.SetValue(target, (float)t);
                    else if (f.FieldType == typeof(bool)) f.SetValue(target, (bool)t);
                    else if (f.FieldType == typeof(int)) f.SetValue(target, (int)t);
                    else if (f.FieldType == typeof(string)) f.SetValue(target, (string)t);
                    else continue;
                    n++;
                }
                catch { }
            }
            return n;
        }

        static List<SquishCollider> Colliders(JObject region)
        {
            List<SquishCollider> outp = new List<SquishCollider>();
            JArray a = Arr(region, "colliders");
            if (a == null) return outp;
            for (int i = 0; i < a.Count; i++)
            {
                JObject o = a[i] as JObject;
                if (o == null) continue;
                SquishCollider c = new SquishCollider();
                c.bone = Str(o, "bone", "");
                c.mesh = Str(o, "mesh", "");
                c.radius = Num(o, "radius", c.radius);
                c.length = Num(o, "length", c.length);
                c.enabled = Bool(o, "enabled", true);
                outp.Add(c);
            }
            return outp;
        }

        // ------------------------------------------------------------------ json helpers

        static JObject Sub(JObject o, string k) { return o == null ? null : o[k] as JObject; }
        static JArray Arr(JObject o, string k) { return o == null ? null : o[k] as JArray; }

        static float Num(JObject o, string k, float dflt)
        {
            if (o == null) return dflt;
            JToken t = o[k];
            if (t == null || t.Type == JTokenType.Null) return dflt;
            try { return (float)t; } catch { return dflt; }
        }

        static bool Bool(JObject o, string k, bool dflt)
        {
            if (o == null) return dflt;
            JToken t = o[k];
            if (t == null || t.Type == JTokenType.Null) return dflt;
            try { return (bool)t; } catch { return dflt; }
        }

        static string Str(JObject o, string k, string dflt)
        {
            if (o == null) return dflt;
            JToken t = o[k];
            if (t == null || t.Type == JTokenType.Null) return dflt;
            return t.ToString();
        }

        static List<int> Ints(JObject o, string k)
        {
            List<int> l = new List<int>();
            JArray a = Arr(o, k);
            if (a != null) for (int i = 0; i < a.Count; i++) l.Add((int)a[i]);
            return l;
        }

        static List<float> Floats(JObject o, string k)
        {
            List<float> l = new List<float>();
            JArray a = Arr(o, k);
            if (a != null) for (int i = 0; i < a.Count; i++) l.Add((float)a[i]);
            return l;
        }

        static List<string> Strings(JObject o, string k, List<string> dflt)
        {
            JArray a = Arr(o, k);
            if (a == null) return dflt;
            List<string> l = new List<string>();
            for (int i = 0; i < a.Count; i++) l.Add(a[i].ToString());
            return l;
        }

        // ------------------------------------------------------------------ entry point

        // Returns the merged config, writing it and a log next to the old files.
        // dir is VNyan's persistentDataPath.
        public static SuiteConfig RunIfNeeded(string dir, out bool migrated)
        {
            migrated = false;
            string target = Path.Combine(dir, "softbodysuite.json");
            if (File.Exists(target))
            {
                try
                {
                    SuiteConfig loaded = JsonConvert.DeserializeObject<SuiteConfig>(File.ReadAllText(target));
                    if (loaded == null) loaded = new SuiteConfig();
                    loaded.Sync();
                    return loaded;
                }
                catch { return new SuiteConfig(); }
            }

            Report rep = new Report();
            SuiteConfig cfg = Migrate(ReadOrNull(Path.Combine(dir, "squishstudio.json")),
                                      ReadOrNull(Path.Combine(dir, "jellostudio.json")),
                                      ReadOrNull(Path.Combine(dir, "wobblestudio.json")), rep);
            if (!rep.ran) return cfg;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Soft Body Suite — imported the three old configs.");
            sb.AppendLine("meshes=" + rep.meshes + " regions=" + rep.regions +
                          " values=" + rep.valuesCopied + " gaps=" + rep.mismatches);
            for (int i = 0; i < rep.lines.Count; i++) sb.AppendLine("  " + rep.lines[i]);
            try
            {
                File.WriteAllText(target, JsonConvert.SerializeObject(cfg, Formatting.Indented));
                File.WriteAllText(Path.Combine(dir, "softbodysuite.migration.log"), sb.ToString());
                migrated = true;
            }
            catch { }
            return cfg;
        }

        static string ReadOrNull(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }
    }
}
