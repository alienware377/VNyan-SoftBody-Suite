using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace SoftBodySuite.Field
{
    public enum UiKind { Header = 0, Slider = 1, Toggle = 2, Button = 3 }

    // One declared control. A source declares its binding alongside its row, so
    // the getter/setter are built once at UI build and never per frame.
    public class UiRow
    {
        public string key;
        public string label;
        public string tooltip;
        public string section;
        public UiKind kind;
        public float min;
        public float max;
        public float defaultValue;
        public bool wholeNumbers;
        public Func<SoftBodySuite.FieldRegion, float> get;
        public Action<SoftBodySuite.FieldRegion, float> set;

        // the prefab object name this row expects
        public string ObjectName()
        {
            switch (kind)
            {
                case UiKind.Header: return "Hdr_" + key;
                case UiKind.Toggle: return "Toggle_" + key;
                case UiKind.Button: return "Button_" + key;
                default: return "Slider_" + key;
            }
        }
    }

    // The declared UI surface of the field family.
    //
    // Why this exists: the shipped build hooks 142 slider keys against 78 prefab
    // rows - 64 invisible dead sliders and three listener-less buttons, and not
    // one of them produces a warning. Adding ~70 more controls by hand would
    // reproduce that with certainty. Here every control is DECLARED once, the
    // missing ones are CLONED from a template at runtime, and SelfCheck reports
    // both halves of the drift unconditionally at startup.
    //
    // This works without opening Unity because SuitePlugin.BuildSections()
    // discovers sections by scanning child names at runtime: a cloned
    // "Hdr_Field - Contact" plus cloned "Slider_fx_*" rows are picked up by the
    // existing section machinery for free. Materialise MUST run BEFORE
    // BuildSections().
    public class UiSchema
    {
        readonly List<UiRow> rows = new List<UiRow>();
        readonly Dictionary<string, UiRow> byKey = new Dictionary<string, UiRow>();
        string curSection = "";

        // vertical layout of cloned rows, in the same negative-Y-downwards
        // convention BuildSections reads
        public float rowGap = 4f;
        public float headerGap = 14f;

        public int Count { get { return rows.Count; } }
        public UiRow Get(int i) { return (i >= 0 && i < rows.Count) ? rows[i] : null; }
        public List<UiRow> Rows { get { return rows; } }

        public UiRow Find(string key)
        {
            UiRow r;
            if (key != null && byKey.TryGetValue(key, out r)) return r;
            return null;
        }

        // ---------- declaration ----------
        public void AddSection(string section)
        {
            curSection = section;
            UiRow r = new UiRow();
            r.key = section;
            r.label = section;
            r.section = section;
            r.kind = UiKind.Header;
            Push(r);
        }

        public UiRow AddSlider(string section, string key, string label, float min, float max,
                               float dflt,
                               Func<SoftBodySuite.FieldRegion, float> get,
                               Action<SoftBodySuite.FieldRegion, float> set)
        {
            UiRow r = new UiRow();
            r.key = key;
            r.label = label;
            r.section = (section != null && section.Length > 0) ? section : curSection;
            r.kind = UiKind.Slider;
            r.min = min; r.max = max; r.defaultValue = dflt;
            r.get = get; r.set = set;
            Push(r);
            return r;
        }

        // toggles ride the same float field as everything else (0/1) so undo,
        // presets and the region copy all keep working without a second path
        public UiRow AddToggle(string section, string key, string label, bool dflt,
                               Func<SoftBodySuite.FieldRegion, float> get,
                               Action<SoftBodySuite.FieldRegion, float> set)
        {
            UiRow r = new UiRow();
            r.key = key;
            r.label = label;
            r.section = (section != null && section.Length > 0) ? section : curSection;
            r.kind = UiKind.Toggle;
            r.min = 0f; r.max = 1f; r.defaultValue = dflt ? 1f : 0f;
            r.wholeNumbers = true;
            r.get = get; r.set = set;
            Push(r);
            return r;
        }

        public UiRow AddButton(string section, string key, string label)
        {
            UiRow r = new UiRow();
            r.key = key;
            r.label = label;
            r.section = (section != null && section.Length > 0) ? section : curSection;
            r.kind = UiKind.Button;
            Push(r);
            return r;
        }

        public void Tooltip(string key, string text)
        {
            UiRow r = Find(key);
            if (r != null) r.tooltip = text;
        }

        void Push(UiRow r)
        {
            if (byKey.ContainsKey(r.key))
            {
                Debug.LogWarning("[SfxField] UiSchema: duplicate key '" + r.key + "' declared twice; the second is ignored.");
                return;
            }
            byKey[r.key] = r;
            rows.Add(r);
        }

        // ---------- materialisation ----------
        // Clones a template prefab row for every declared key that has no control.
        // Returns how many objects it created. Existing controls are left alone.
        public int Materialise(Transform window, RectTransform sectionContent)
        {
            if (window == null || sectionContent == null) return 0;

            // FULL-ROW TEMPLATES. Every row used to be cloned from the bare Slider_ object
            // alone, so field rows never got the number box beside them (Value_* is a
            // SIBLING in this prefab, not a child), and the clone carried the template's
            // own "?" tooltip button - whose "?" the label code then overwrote with the
            // row's name. That is the blue square with a word in it. Now each kind is
            // cloned from a real row of the same kind, parts and all, exactly as the
            // shipped panel builds them.
            RectTransform sTpl = null, lTpl = null, vTpl = null;
            for (int i = 0; i < sectionContent.childCount && sTpl == null; i++)
            {
                RectTransform rt = sectionContent.GetChild(i) as RectTransform;
                if (rt == null || !rt.name.StartsWith("Slider_", StringComparison.Ordinal)) continue;
                string k = rt.name.Substring(7);
                RectTransform l = FindDirect(sectionContent, "Lbl_" + k);
                RectTransform v = FindDirect(sectionContent, "Value_" + k);
                if (l != null && v != null) { sTpl = rt; lTpl = l; vTpl = v; }
            }
            RectTransform tTpl = null, bTpl = null;
            for (int i = 0; i < sectionContent.childCount; i++)
            {
                RectTransform rt = sectionContent.GetChild(i) as RectTransform;
                if (rt == null) continue;
                if (tTpl == null && rt.name.StartsWith("Toggle_", StringComparison.Ordinal)
                    && rt.GetComponent<Toggle>() != null && rt.Find("Label") != null) tTpl = rt;
                if (bTpl == null && rt.name.StartsWith("Button_", StringComparison.Ordinal)
                    && rt.GetComponent<Button>() != null && rt.GetComponentInChildren<Text>(true) != null) bTpl = rt;
            }
            RectTransform hdrTmpl = FindTemplate(sectionContent, "Hdr_");
            if (sTpl == null)
            {
                Debug.LogWarning("[SfxField] UiSchema.Materialise: no full slider row (Lbl_/Slider_/Value_) to clone; the field rows cannot be built.");
                return 0;
            }

            float rowStep = 26f;
            float cursorY = LowestEdge(sectionContent) - headerGap;
            float hdrStep = (hdrTmpl != null ? hdrTmpl.rect.height : 22f) + headerGap;
            float hdrX = hdrTmpl != null ? hdrTmpl.anchoredPosition.x : lTpl.anchoredPosition.x;
            // where the slider and box sit relative to their row's label, from the template
            float dySlider = sTpl.anchoredPosition.y - lTpl.anchoredPosition.y;
            float dyValue = vTpl.anchoredPosition.y - lTpl.anchoredPosition.y;

            int made = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                UiRow r = rows[i];
                if (FindDeep(window, r.ObjectName()) != null) continue;
                try
                {
                    if (r.kind == UiKind.Header)
                    {
                        RectTransform src = hdrTmpl != null ? hdrTmpl : lTpl;
                        RectTransform h = CloneAt(src, sectionContent, "Hdr_" + r.key, hdrX, cursorY - headerGap);
                        Text ht = h.GetComponent<Text>();
                        if (ht != null) ht.text = r.label;
                        cursorY -= headerGap + hdrStep;
                    }
                    else if (r.kind == UiKind.Slider)
                    {
                        RectTransform l = CloneAt(lTpl, sectionContent, "Lbl_" + r.key, lTpl.anchoredPosition.x, cursorY);
                        Text lt = l.GetComponent<Text>();
                        if (lt != null) FitLabel(lt, r.label);
                        RectTransform sl = CloneAt(sTpl, sectionContent, "Slider_" + r.key, sTpl.anchoredPosition.x, cursorY + dySlider);
                        Slider s0 = sl.GetComponent<Slider>();
                        if (s0 != null)
                        {
                            s0.onValueChanged.RemoveAllListeners();
                            s0.minValue = r.min; s0.maxValue = r.max; s0.wholeNumbers = r.wholeNumbers;
                            s0.value = Mathf.Clamp(r.defaultValue, r.min, r.max);
                        }
                        RectTransform vb = CloneAt(vTpl, sectionContent, "Value_" + r.key, vTpl.anchoredPosition.x, cursorY + dyValue);
                        InputField inp = vb.GetComponent<InputField>();
                        if (inp != null)
                        {
                            inp.onEndEdit.RemoveAllListeners();
                            inp.onValueChanged.RemoveAllListeners();
                            inp.text = r.defaultValue.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                        }
                        cursorY -= rowStep;
                    }
                    else if (r.kind == UiKind.Toggle && tTpl != null)
                    {
                        RectTransform t = CloneAt(tTpl, sectionContent, "Toggle_" + r.key, tTpl.anchoredPosition.x, cursorY);
                        Toggle tg = t.GetComponent<Toggle>();
                        if (tg != null)
                        {
                            tg.onValueChanged.RemoveAllListeners();
                            tg.group = null;
                            tg.isOn = r.defaultValue > 0.5f;
                            tg.interactable = true;
                        }
                        Transform lab = t.Find("Label");
                        Text tt = lab != null ? lab.GetComponent<Text>() : null;
                        if (tt != null) tt.text = r.label;
                        cursorY -= rowStep;
                    }
                    else if (r.kind == UiKind.Button && bTpl != null)
                    {
                        RectTransform b = CloneAt(bTpl, sectionContent, "Button_" + r.key, lTpl.anchoredPosition.x, cursorY);
                        // full row width, from the label's left edge to the box's right edge
                        float w = (vTpl.anchoredPosition.x + vTpl.rect.width) - lTpl.anchoredPosition.x;
                        b.sizeDelta = new Vector2(w, 24f);
                        Button bt = b.GetComponent<Button>();
                        if (bt != null) { bt.onClick.RemoveAllListeners(); bt.interactable = true; }
                        Text btx = b.GetComponentInChildren<Text>(true);
                        if (btx != null) btx.text = r.label;
                        cursorY -= rowStep + 4f;
                    }
                    else
                    {
                        // no template of this kind in the prefab: the old convert-a-slider path
                        RectTransform c = CloneAt(sTpl, sectionContent, r.ObjectName(), sTpl.anchoredPosition.x, cursorY + dySlider);
                        Dress(c.gameObject, r);
                        cursorY -= rowStep;
                    }
                    made++;
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning("[SfxField] UiSchema: row '" + r.key + "' (" + r.kind + ") could not be built: " + ex.Message + " - skipped.");
                }
            }

            if (made > 0)
            {
                float used = LowestEdge(sectionContent);
                Vector2 sd = sectionContent.sizeDelta;
                float need = -used + 40f;
                if (need > sd.y) sectionContent.sizeDelta = new Vector2(sd.x, need);
                Debug.Log("[SfxField] UiSchema: materialised " + made + " of " + rows.Count + " declared rows.");
            }
            return made;
        }

        // Every control the shipped panel has, keyed by object name, with its tooltip. The
        // plugin's own AddTooltips does the drawing, so the field rows get the same "?".
        public Dictionary<string, string> Tooltips()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            for (int i = 0; i < rows.Count; i++)
            {
                UiRow r = rows[i];
                if (r.kind == UiKind.Header || string.IsNullOrEmpty(r.tooltip)) continue;
                d[r.ObjectName()] = r.tooltip;
            }
            return d;
        }

        static RectTransform FindDirect(RectTransform parent, string name)
        {
            Transform t = parent.Find(name);
            return t as RectTransform;
        }

        // Clone a template object as a sibling, at (x, y), named `name`, minus any "?"
        // button it carried (that button is wired to the TEMPLATE's tooltip).
        static RectTransform CloneAt(RectTransform src, RectTransform parent, string name, float x, float y)
        {
            GameObject go = (GameObject)UnityEngine.Object.Instantiate(src.gameObject);
            go.name = name;
            Transform tip = go.transform.Find("TipBtn");
            if (tip != null) UnityEngine.Object.DestroyImmediate(tip.gameObject);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = src.anchorMin; rt.anchorMax = src.anchorMax;
            rt.pivot = src.pivot; rt.sizeDelta = src.sizeDelta;
            rt.anchoredPosition = new Vector2(x, y);
            go.SetActive(true);
            return rt;
        }

        // Same font and colour as the shipped labels; long names shrink to fit their
        // column instead of running under the slider.
        static void FitLabel(Text t, string label)
        {
            t.text = label;
            int sz = t.fontSize > 0 ? t.fontSize : 11;
            t.resizeTextForBestFit = true;
            t.resizeTextMaxSize = sz;
            t.resizeTextMinSize = Mathf.Max(7, sz - 4);
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
        }

        // strips the component set a cloned row must not keep, and applies the
        // declared label / range / kind
        void Dress(GameObject clone, UiRow r)
        {
            Slider s = clone.GetComponent<Slider>();
            Toggle t = clone.GetComponent<Toggle>();
            Button b = clone.GetComponent<Button>();

            if (r.kind == UiKind.Slider)
            {
                if (s != null)
                {
                    s.onValueChanged.RemoveAllListeners();
                    s.minValue = r.min; s.maxValue = r.max;
                    s.wholeNumbers = r.wholeNumbers;
                    s.value = Mathf.Clamp(r.defaultValue, r.min, r.max);
                }
            }
            else if (r.kind == UiKind.Toggle)
            {
                // cloned from a slider row: the slider must go or it will eat the
                // pointer, and a Toggle has to exist for HookToggle to find one
            // WHY DestroyImmediate AND NOT Destroy.
            // Every row is cloned from a SLIDER row, so a toggle/button row has to shed the
            // Slider and grow a Toggle or Button instead. Object.Destroy is deferred to the
            // END OF FRAME, so the Slider was still attached when AddComponent ran - and
            // Selectable is DisallowMultipleComponent, so AddComponent returned NULL and the
            // very next line threw. Sliders dressed fine; every Toggle and every Button in
            // the fork failed, which is all six effect switches, the three performance
            // toggles and all twelve preset buttons. DestroyImmediate removes it now, so the
            // slot is free when the replacement is added.
                if (s != null) { s.onValueChanged.RemoveAllListeners(); UnityEngine.Object.DestroyImmediate(s); }
                if (t == null) t = clone.AddComponent<Toggle>();
                if (t == null) throw new System.Exception("could not add a Toggle to the cloned row");
                t.onValueChanged.RemoveAllListeners();
                // REMOVING THE SLIDER COMPONENT DOES NOT REMOVE THE SLIDER.
                // DefaultControls.CreateSlider leaves Background + Fill Area + Handle Slide
                // Area behind as plain children, so a converted row kept the full look of a
                // slider - a track with a draggable-looking knob - while being a Toggle
                // underneath. It read as a broken slider: you drag it and nothing happens.
                // Strip the track furniture and give the control a face of its own.
                AsCheckbox(clone, t);
                t.isOn = r.defaultValue > 0.5f;
            }
            else if (r.kind == UiKind.Button)
            {
                // same deferred-Destroy trap as the Toggle branch above
                if (s != null) { s.onValueChanged.RemoveAllListeners(); UnityEngine.Object.DestroyImmediate(s); }
                if (b == null) b = clone.AddComponent<Button>();
                if (b == null) throw new System.Exception("could not add a Button to the cloned row");
                b.onClick.RemoveAllListeners();
                // Same leftover-furniture problem as the Toggle branch, and worse here:
                // twelve preset rows that LOOK like sliders but are one-shot buttons. The
                // clicks were landing all along - the status bar said so - but nothing about
                // the control said "press me".
                AsPressable(clone, b);
            }
            else if (r.kind == UiKind.Header)
            {
                if (s != null) { s.onValueChanged.RemoveAllListeners(); UnityEngine.Object.DestroyImmediate(s); }
                if (b != null) b.onClick.RemoveAllListeners();
            }

            SetLabel(clone, r.label, r.kind);

            InputField inp = null;
            Transform vt = FindDeep(clone.transform, "Value_" + r.key);
            if (vt != null) inp = vt.GetComponent<InputField>();
            if (inp != null)
            {
                inp.onEndEdit.RemoveAllListeners();
                if (r.kind == UiKind.Slider)
                    inp.text = r.defaultValue.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                else
                    inp.gameObject.SetActive(false);
            }
        }

        // the row's caption is the first Text that is not the manual-entry box's
        // own text, and not the slider's internal furniture
        static void SetLabel(GameObject clone, string label, UiKind kind)
        {
            if (label == null) return;
            Text[] texts = clone.GetComponentsInChildren<Text>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                Text tx = texts[i];
                if (tx == null) continue;
                if (tx.GetComponentInParent<InputField>() != null) continue;
                tx.text = label;

                // A HEADER'S LABEL IS THE HEADER. Do not move it.
                // A header row sits at the CONTENT's left edge, so the reposition below -
                // which parks a label 210 px to the LEFT of its row - pushed every section
                // title clean off the panel. With no visible title there was no visible
                // section either, and the whole Field UI looked deleted while the log still
                // read "82 of 82 materialised". Only control rows, which sit indented with
                // an empty column beside them, get the treatment.
                if (kind == UiKind.Header)
                {
                    tx.horizontalOverflow = HorizontalWrapMode.Overflow;
                    tx.verticalOverflow = VerticalWrapMode.Overflow;
                    return;
                }

                // SETTING .text IS NOT ENOUGH, AND THAT IS WHY THE PANEL WAS UNREADABLE.
                // The clone inherits the TEMPLATE's label geometry - a box barely wide
                // enough for the template's own caption - and uGUI clips to the rect. Every
                // declared label came out as its first two or three characters ("La", "Co",
                // "Sp"), which is unreadable and, worse, ambiguous: three different rows all
                // read "Co". The text is never truncated in the data, only in the rect.
                //
                // Park it in the empty column to the LEFT of the slider: anchored to the
                // row's left edge with the pivot on its RIGHT, so it grows leftwards into
                // space nothing else uses, right-aligned so the labels line up in a neat
                // edge against the controls. Overflow on both axes so a long label spills
                // rather than clipping or wrapping onto two lines.
                RectTransform lr = tx.rectTransform;
                lr.anchorMin = new Vector2(0f, 0.5f);
                lr.anchorMax = new Vector2(0f, 0.5f);
                lr.pivot = new Vector2(1f, 0.5f);
                lr.sizeDelta = new Vector2(LABEL_W, LABEL_H);
                lr.anchoredPosition = new Vector2(-LABEL_GAP, 0f);
                tx.alignment = TextAnchor.MiddleRight;
                tx.horizontalOverflow = HorizontalWrapMode.Overflow;
                tx.verticalOverflow = VerticalWrapMode.Overflow;
                tx.resizeTextForBestFit = false;
                tx.fontSize = 12;
                tx.color = Color.white;
                return;
            }
        }


        // ---- turning a cloned SLIDER row into something that looks like what it is ----
        // DefaultControls.CreateSlider builds: Background, Fill Area/Fill, Handle Slide
        // Area/Handle. Removing the Slider component leaves every one of those in place.

        static void StripTrack(GameObject row)
        {
            Transform fill = row.transform.Find("Fill Area");
            if (fill != null) fill.gameObject.SetActive(false);
            Transform handle = row.transform.Find("Handle Slide Area");
            if (handle != null) handle.gameObject.SetActive(false);
        }

        static Image BackgroundOf(GameObject row)
        {
            Transform bg = row.transform.Find("Background");
            return bg != null ? bg.GetComponent<Image>() : null;
        }

        static void AsCheckbox(GameObject row, Toggle t)
        {
            StripTrack(row);
            Image bg = BackgroundOf(row);
            if (bg == null) return;

            // a small square at the left of the row, instead of a full-width track
            RectTransform br = bg.rectTransform;
            br.anchorMin = new Vector2(0f, 0.5f);
            br.anchorMax = new Vector2(0f, 0.5f);
            br.pivot = new Vector2(0f, 0.5f);
            br.sizeDelta = new Vector2(BOX, BOX);
            br.anchoredPosition = new Vector2(0f, 0f);
            bg.color = new Color(0.16f, 0.15f, 0.20f, 1f);
            t.targetGraphic = bg;

            // the tick: its own Image, because Toggle.graphic is enabled and disabled by
            // isOn and must therefore not be anything the row still needs when off
            Transform existing = bg.transform.Find("Tick");
            GameObject tick;
            if (existing != null) tick = existing.gameObject;
            else
            {
                tick = new GameObject("Tick", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                tick.transform.SetParent(bg.transform, false);
            }
            RectTransform tr = tick.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(3f, 3f); tr.offsetMax = new Vector2(-3f, -3f);
            Image ti = tick.GetComponent<Image>();
            ti.color = new Color(0.45f, 0.85f, 0.50f, 1f);
            t.graphic = ti;
        }

        static void AsPressable(GameObject row, Button b)
        {
            StripTrack(row);
            Image bg = BackgroundOf(row);
            if (bg == null) { b.transition = Selectable.Transition.None; return; }
            // keep the full width - a wide bar with its name beside it is a fine button -
            // but colour it like a button and give it press feedback, which a slider track
            // deliberately does not have.
            bg.color = new Color(0.27f, 0.26f, 0.34f, 1f);
            b.targetGraphic = bg;
            b.transition = Selectable.Transition.ColorTint;
            ColorBlock cb = b.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            cb.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            cb.fadeDuration = 0.06f;
            b.colors = cb;
        }

        const float BOX = 18f;

        // The label column, to the left of every cloned control.
        const float LABEL_W = 210f;
        const float LABEL_H = 20f;
        const float LABEL_GAP = 8f;

        // renames the clone and every descendant that carried the template key
        static void Rename(Transform root, string srcKey, string newKey, string rootName)
        {
            root.name = rootName;
            RenameKids(root, srcKey, newKey);
        }

        static void RenameKids(Transform t, string srcKey, string newKey)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                Transform c = t.GetChild(i);
                if (srcKey != null && srcKey.Length > 0)
                {
                    int at = c.name.LastIndexOf("_" + srcKey, StringComparison.Ordinal);
                    if (at >= 0 && at + srcKey.Length + 1 == c.name.Length)
                        c.name = c.name.Substring(0, at + 1) + newKey;
                }
                RenameKids(c, srcKey, newKey);
            }
        }

        static string KeyOf(string objName)
        {
            int u = objName.IndexOf('_');
            return (u >= 0 && u + 1 < objName.Length) ? objName.Substring(u + 1) : "";
        }

        static RectTransform FindTemplate(RectTransform content, string prefix)
        {
            for (int i = 0; i < content.childCount; i++)
            {
                RectTransform rt = content.GetChild(i) as RectTransform;
                if (rt != null && rt.name.StartsWith(prefix, StringComparison.Ordinal)) return rt;
            }
            return null;
        }

        // bottom edge of the existing content, in the negative-Y-downwards
        // convention BuildSections reads
        static float LowestEdge(RectTransform content)
        {
            float low = 0f;
            for (int i = 0; i < content.childCount; i++)
            {
                RectTransform rt = content.GetChild(i) as RectTransform;
                if (rt == null) continue;
                float b = rt.anchoredPosition.y - rt.rect.height;
                if (b < low) low = b;
            }
            return low;
        }

        // ---------- self check ----------
        // (a) every declared key with no control found after Materialise
        // (b) every prefab Slider_/Toggle_/Button_ in the window no row declares
        // Run this at startup unconditionally: it is the reason this file exists.
        public void SelfCheck(Transform window, out int missing, out int orphaned)
        {
            missing = 0; orphaned = 0;
            if (window == null) return;

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < rows.Count; i++)
            {
                string want = rows[i].ObjectName();
                if (FindDeep(window, want) != null) continue;
                missing++;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(want);
            }
            if (missing > 0)
                Debug.LogWarning("[SfxField] UiSchema.SelfCheck: " + missing
                    + " declared control(s) have NO prefab row and could not be cloned: " + sb.ToString());

            StringBuilder mine = new StringBuilder();
            StringBuilder theirs = new StringBuilder();
            int mineN = 0, theirsN = 0;
            Walk(window, ref mineN, ref theirsN, mine, theirs);
            orphaned = mineN + theirsN;

            if (mineN > 0)
                Debug.LogWarning("[SfxField] UiSchema.SelfCheck: " + mineN
                    + " field control(s) exist in the window but NO schema row declares them (dead rows): " + mine.ToString());
            if (theirsN > 0)
                Debug.LogWarning("[SfxField] UiSchema.SelfCheck: " + theirsN
                    + " control(s) outside the field schema are undeclared here - expected for the shipped suite's own rows, listed once so drift stays visible: " + theirs.ToString());
        }

        void Walk(Transform t, ref int mineN, ref int theirsN, StringBuilder mine, StringBuilder theirs)
        {
            string n = t.name;
            string key = null;
            if (n.StartsWith("Slider_", StringComparison.Ordinal)) key = n.Substring(7);
            else if (n.StartsWith("Toggle_", StringComparison.Ordinal)) key = n.Substring(7);
            else if (n.StartsWith("Button_", StringComparison.Ordinal)) key = n.Substring(7);

            if (key != null && !byKey.ContainsKey(key))
            {
                if (key.StartsWith("fx_", StringComparison.Ordinal))
                { if (mineN > 0) mine.Append(", "); mine.Append(n); mineN++; }
                else
                { if (theirsN > 0) theirs.Append(", "); theirs.Append(n); theirsN++; }
            }
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), ref mineN, ref theirsN, mine, theirs);
        }

        // same recursive search SuitePlugin uses, so a cloned row is found the
        // same way a prefab one is
        public static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
