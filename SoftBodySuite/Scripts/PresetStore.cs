using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

namespace SoftBodySuite
{
    using SquishConfig = SoftBodySuite.SuiteConfig;

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
            s = s.Trim();
            // VNyan's own extension is too long for the generic short-extension rule below,
            // and leaving it on would stop "model v3.vsfavatar" matching "model v2.vsfavatar"
            string[] exts = { ".vsfavatar", ".warudo", ".vrca", ".vrm" };
            for (int e = 0; e < exts.Length; e++)
                if (s.EndsWith(exts[e], StringComparison.Ordinal)) { s = s.Substring(0, s.Length - exts[e].Length); break; }
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
            return normalised != null && normalised.Length >= 4
                && normalised.IndexOf("vsfavatartemporary", StringComparison.Ordinal) < 0;
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

        // Which file was this avatar loaded from? VNyan's AvatarCache keeps a list of
        // file path -> loaded avatar. Its class and getInstance() keep their real names in
        // VNyan's otherwise scrambled code; the list itself is found by its type. The
        // avatar on screen is a copy of a cache entry, so it is matched by its meshes.
        // If that ever stops working, VNyan's own log line for the last load is used.
        public static string FileIdentity(GameObject avatar)
        {
            if (avatar == null) return "";
            string fromCache = "";
            try { fromCache = FileFromAvatarCache(avatar); } catch { }
            if (fromCache.Length > 0) return fromCache;
            try { return FileFromLog(); } catch { return ""; }
        }

        static string FileFromAvatarCache(GameObject avatar)
        {
            Type t = Type.GetType("AvatarCache, Assembly-CSharp");
            if (t == null) return "";
            System.Reflection.MethodInfo gi = t.GetMethod("getInstance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            object inst = gi != null ? gi.Invoke(null, null) : null;
            if (inst == null) return "";

            Dictionary<string, GameObject> map = null;
            System.Reflection.FieldInfo[] fs = t.GetFields(System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            for (int i = 0; i < fs.Length && map == null; i++)
                map = fs[i].GetValue(inst) as Dictionary<string, GameObject>;
            if (map == null || map.Count == 0) return "";

            HashSet<string> mine = MeshSignature(avatar);
            string best = ""; int bestScore = 0, live = 0;
            foreach (KeyValuePair<string, GameObject> kv in map)
            {
                if (kv.Value == null || string.IsNullOrEmpty(kv.Key)) continue;
                live++;
                HashSet<string> theirs = MeshSignature(kv.Value);
                int score = 0;
                foreach (string m in mine) if (theirs.Contains(m)) score++;
                if (score > bestScore) { bestScore = score; best = kv.Key; }
                else if (live == 1 && best.Length == 0) best = kv.Key;   // only one candidate
            }
            if (bestScore == 0 && live != 1) return "";
            return Path.GetFileName(best);
        }

        // meshes are matched by name and vertex count, not by reference: other plugins
        // may swap a renderer's mesh for their own copy
        static HashSet<string> MeshSignature(GameObject go)
        {
            HashSet<string> set = new HashSet<string>();
            SkinnedMeshRenderer[] rs = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                Mesh m = rs[i] != null ? rs[i].sharedMesh : null;
                if (m != null) set.Add(m.name + "#" + m.vertexCount);
            }
            return set;
        }

        // VNyan writes "Loading colliders for avatar <file>.vsfavatar" on every load
        static string FileFromLog()
        {
            string path = Path.Combine(Application.persistentDataPath, "Player.log");
            if (!File.Exists(path)) return "";
            const string tag = "Loading colliders for avatar ";
            using (FileStream fsm = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fsm.Length - 512 * 1024);
                fsm.Seek(start, SeekOrigin.Begin);
                using (StreamReader sr = new StreamReader(fsm))
                {
                    string text = sr.ReadToEnd();
                    int at = text.LastIndexOf(tag, StringComparison.Ordinal);
                    if (at < 0) return "";
                    int end = text.IndexOf('\n', at);
                    string line = (end < 0 ? text.Substring(at) : text.Substring(at, end - at)).Trim();
                    return line.Substring(tag.Length).Trim();
                }
            }
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
            // VNyan names every loaded .vsfavatar "VSFAvatarTemporary(Clone)", so the object
            // name says nothing about which model this is. The file it came from does.
            string file = FileIdentity(avatar);
            if (file.Length > 0)
            {
                exactKey = file;
                string fromFile = Normalise(file);
                if (NameIsUsable(fromFile)) { fuzzyKey = fromFile; how = "file name"; return; }
            }
            else exactKey = avatar != null ? avatar.name : "";
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
            if (r.fuzzy && !NameIsUsable(r.key)) return false;
            if (!r.fuzzy) return string.Equals(r.key, exactKey, StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(fuzzyKey)) return false;
            return fuzzyKey.IndexOf(r.key, StringComparison.Ordinal) >= 0
                || r.key.IndexOf(fuzzyKey, StringComparison.Ordinal) >= 0;
        }

        // ---------- storage ----------

        public static PresetFile Load(string path)
        {
            if (!File.Exists(path)) return new PresetFile();
            try
            {
                PresetFile pf = JsonConvert.DeserializeObject<PresetFile>(File.ReadAllText(path));
                if (pf != null)
                {
                    if (pf.presets == null) pf.presets = new Dictionary<string, SquishConfig>();
                    if (pf.rules == null) pf.rules = new List<PresetRule>();
                    return pf;
                }
                Debug.LogWarning("[Presets] " + path + " was empty or unreadable");
            }
            catch (Exception e) { Debug.LogWarning("[Presets] load failed: " + e.Message); }
            // Keep the unreadable file. Carrying on with an empty store means the next save
            // would overwrite every preset and rule that was in it.
            try
            {
                string bak = path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(path, bak, true);
                Debug.LogWarning("[Presets] kept the unreadable file as " + bak);
            }
            catch { }
            return new PresetFile();
        }

        public static void Save(string path, PresetFile pf)
        {
            try
            {
                // write beside it, then swap it in: a crash mid-write can no longer leave a
                // half-written file that loads as empty
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(pf, Formatting.Indented));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
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
