using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

namespace JelloStudio
{
    // ---------------------------------------------------------------------------
    // Named presets + per-avatar auto-load.
    //
    // A preset is a whole config (settings + meshes/regions) saved under a name. On
    // avatar load the store looks for a rule matching the avatar and applies its preset,
    // so switching models restores the right tuning without touching the UI.
    //
    // Matching an avatar is the awkward part. VNyan gives us a GameObject name that is
    // usually the file stem plus "(Clone)", and that stem drifts: version suffixes,
    // dates, descriptions appended anywhere. So a rule can match two ways:
    //
    //   FUZZY  — normalise both sides (lowercase, strip punctuation/whitespace, strip
    //            version and date fragments) and accept if either CONTAINS the other.
    //            "aliekatnya", "AlieKatNya v2.5f", "26-07-02 aliekatnya fixed" all match
    //            the key "aliekatnya".
    //   EXACT  — the avatar's identity string must match verbatim. Used automatically
    //            when a name carries no usable words (e.g. "20260224"): fuzzy matching a
    //            bare number string would collide with every other numeric model, so the
    //            rule pins to that model alone.
    //
    // When the name is uninformative we first try the avatar's own metadata (VRM title /
    // author, exposed through any component field or property called Title/Name/Author),
    // because that IS stable across re-exports. Only if that yields nothing do we fall
    // back to the exact-name pin.
    // ---------------------------------------------------------------------------
    public class PresetRule
    {
        public string key = "";          // what to match against (already normalised for fuzzy)
        public string preset = "";       // preset name to apply
        public bool fuzzy = true;        // false = the key must match the identity exactly
        public bool enabled = true;
        public string note = "";         // how the key was derived, for the UI
    }

    public class PresetFile
    {
        public Dictionary<string, SquishConfig> presets = new Dictionary<string, SquishConfig>();
        public List<PresetRule> rules = new List<PresetRule>();
        public bool autoLoad = true;
    }

    public static class PresetStore
    {
        // ---------- identity ----------

        // Strip the decoration VNyan and exporters add, so the same model recognises
        // itself across re-exports: clone suffix, extension, bracketed/parenthesised
        // chunks, dates, and version fragments.
        public static string Normalise(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.ToLowerInvariant();
            s = s.Replace("(clone)", " ");
            int dot = s.LastIndexOf('.');
            if (dot > 0 && s.Length - dot <= 6) s = s.Substring(0, dot);   // .vrm/.vsfavatar/...
            char[] buf = new char[s.Length];
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 'a' && c <= 'z') buf[n++] = c;
                else if (c >= '0' && c <= '9') buf[n++] = c;
                else buf[n++] = ' ';
            }
            s = new string(buf, 0, n);

            // drop tokens that are pure decoration: dates, versions, bare numbers
            string[] parts = s.Split(' ');
            List<string> keep = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string t = parts[i];
                if (t.Length == 0) continue;
                if (IsAllDigits(t)) continue;                       // 20260224, 26, 07...
                if (t.Length <= 4 && t[0] == 'v' && IsAllDigits(t.Substring(1))) continue;   // v2, v25
                keep.Add(t);
            }
            return string.Join("", keep.ToArray());
        }

        static bool IsAllDigits(string t)
        {
            if (t.Length == 0) return false;
            for (int i = 0; i < t.Length; i++) if (t[i] < '0' || t[i] > '9') return false;
            return true;
        }

        // Does the normalised name carry anything worth fuzzy-matching? A model called
        // "20260224" normalises to "" — fuzzy matching that would hit every other
        // number-named model, so such rules must pin exactly instead.
        public static bool NameIsUsable(string normalised)
        {
            return normalised != null && normalised.Length >= 4;
        }

        // Look inside the avatar for stable metadata (VRM title/author). Zero-dependency:
        // reflect over components for a Title/Name/Author-ish string rather than
        // referencing UniVRM types, which may not exist in this runtime.
        public static string MetaIdentity(GameObject avatar)
        {
            if (avatar == null) return "";
            MonoBehaviour[] comps = avatar.GetComponentsInChildren<MonoBehaviour>(true);
            string title = "", author = "";
            for (int i = 0; i < comps.Length && title.Length == 0; i++)
            {
                MonoBehaviour mb = comps[i];
                if (mb == null) continue;
                string tn = mb.GetType().Name.ToLowerInvariant();
                if (tn.IndexOf("vrm") < 0 && tn.IndexOf("meta") < 0) continue;
                object metaObj = FieldOrProp(mb, "Meta") ?? FieldOrProp(mb, "meta") ?? mb;
                if (metaObj == null) continue;
                title = AsString(FieldOrProp(metaObj, "Title")) ;
                if (title.Length == 0) title = AsString(FieldOrProp(metaObj, "title"));
                if (title.Length == 0) title = AsString(FieldOrProp(metaObj, "Name"));
                if (author.Length == 0) author = AsString(FieldOrProp(metaObj, "Author"));
                if (author.Length == 0) author = AsString(FieldOrProp(metaObj, "author"));
            }
            if (title.Length == 0) return "";
            return author.Length > 0 ? title + " " + author : title;
        }

        static object FieldOrProp(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            System.Reflection.FieldInfo f = t.GetField(name);
            if (f != null) { try { return f.GetValue(o); } catch { } }
            System.Reflection.PropertyInfo p = t.GetProperty(name);
            if (p != null && p.CanRead) { try { return p.GetValue(o, null); } catch { } }
            return null;
        }

        static string AsString(object o)
        {
            string s = o as string;
            return s == null ? "" : s.Trim();
        }

        // The identity a rule is matched against, plus whether it can be fuzzy-matched.
        public static void Identify(GameObject avatar, out string fuzzyKey, out string exactKey, out string how)
        {
            exactKey = avatar != null ? avatar.name : "";
            string fromName = Normalise(exactKey);
            if (NameIsUsable(fromName)) { fuzzyKey = fromName; how = "name"; return; }

            string meta = Normalise(MetaIdentity(avatar));
            if (NameIsUsable(meta)) { fuzzyKey = meta; how = "model info"; return; }

            fuzzyKey = "";            // nothing deterministic — caller must pin exactly
            how = "exact name only";
        }

        public static bool Matches(PresetRule r, string fuzzyKey, string exactKey)
        {
            if (r == null || !r.enabled || string.IsNullOrEmpty(r.key)) return false;
            if (!r.fuzzy) return string.Equals(r.key, exactKey, StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(fuzzyKey)) return false;
            return fuzzyKey.IndexOf(r.key, StringComparison.Ordinal) >= 0
                || r.key.IndexOf(fuzzyKey, StringComparison.Ordinal) >= 0;
        }

        // ---------- storage ----------

        public static PresetFile Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    PresetFile pf = JsonConvert.DeserializeObject<PresetFile>(File.ReadAllText(path));
                    if (pf != null)
                    {
                        if (pf.presets == null) pf.presets = new Dictionary<string, SquishConfig>();
                        if (pf.rules == null) pf.rules = new List<PresetRule>();
                        return pf;
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[Presets] load failed: " + e.Message); }
            return new PresetFile();
        }

        public static void Save(string path, PresetFile pf)
        {
            try { File.WriteAllText(path, JsonConvert.SerializeObject(pf, Formatting.Indented)); }
            catch (Exception e) { Debug.LogWarning("[Presets] save failed: " + e.Message); }
        }

        // deep copy through JSON so a preset never aliases the live config
        public static SquishConfig Clone(SquishConfig c)
        {
            if (c == null) return new SquishConfig();
            return JsonConvert.DeserializeObject<SquishConfig>(JsonConvert.SerializeObject(c));
        }
    }
}
