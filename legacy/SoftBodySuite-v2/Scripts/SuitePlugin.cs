using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SoftBodySuite
{
    // The three stages live in their own sub-namespaces so their internals could stay
    // untouched; these names are how the rest of this file talks to them.
    using WobbleProxy = SoftBodySuite.Wobble.MeshProxy;
    using JelloProxy = SoftBodySuite.Jello.MeshProxy;
    using MeshProxy = SoftBodySuite.Squish.MeshProxy;
    using SquishSim = SoftBodySuite.Squish.SquishSim;
    using SquishMesh = SoftBodySuite.SuiteMesh;
    using SquishConfig = SoftBodySuite.SuiteConfig;
    using SquishSettings = SoftBodySuite.SuiteSettings;

    // Soft Body Studio - wobble, jell-o and squish in one plugin, with the clothes
    // fitted once at the very end instead of twice in the middle.
    // Runs LAST in LateUpdate (order 20000) so the bake sees the final pose of the
    // frame: tracking, Pose Studio, bone physics — everything.
    [DefaultExecutionOrder(20700)]   // FINAL stage: runs after SoftBody (20500) and Jello (20600)
    public class SquishPlugin : MonoBehaviour, VNyanInterface.IButtonClickedHandler
    {
        const string BUTTON_NAME = "Soft Body Suite";
        const string CONFIG_FILE = "softbodysuite.json";

        public GameObject windowPrefab;

        GameObject window;
        Text statusLabel;
        readonly Dictionary<string, Slider> sliders = new Dictionary<string, Slider>();
        readonly Dictionary<string, InputField> valueInputs = new Dictionary<string, InputField>();
        Dropdown meshDropdown, regionDropdown, groupDropdown, colliderDropdown, refBoneDropdown, colMeshDropdown, colBoneDropdown;
        InputField regionNameInput, refBoneInput, colBoneInput, colRadiusInput;
        List<string> refBoneOptions = new List<string>();
        Toggle enabledToggle, regionEnabledToggle, paintToggle, overlayToggle, gravityPoseToggle;
        Toggle clipGuardToggle;
        bool suppress;

        SquishConfig config = new SquishConfig();
        string savePath, configPath;

        GameObject boundAvatar;
        Animator boundAnimator;
        readonly List<MeshProxy> proxies = new List<MeshProxy>();

        MeshProxy selProxy;
        SquishMesh selMesh;
        SuiteRegion selRegion;

        bool paintMode;
        int paintBrushMode;          // 0 add, 1 subtract
        float brushRadius = 0.05f, brushStrength = 0.35f;
        bool groupChildren = true;   // vertex-group select includes descendant bones
        bool stroking;               // LMB stroke in progress (one undo step per stroke)

        // ----- undo/redo (weight edits: strokes, group select, blur, clear) -----
        class WeightSnapshot
        {
            public SuiteRegion region;
            public int[] idx; public float[] w;
            public static WeightSnapshot Of(SuiteRegion r)
            {
                WeightSnapshot s = new WeightSnapshot();
                s.region = r; s.idx = r.vertIndex.ToArray(); s.w = r.weight.ToArray();
                return s;
            }
            public void Restore()
            {
                region.vertIndex.Clear(); region.vertIndex.AddRange(idx);
                region.weight.Clear(); region.weight.AddRange(w);
            }
        }
        readonly List<WeightSnapshot> undoStack = new List<WeightSnapshot>();
        readonly List<WeightSnapshot> redoStack = new List<WeightSnapshot>();
        const int UNDO_CAP = 40;

        float lastWeightEditT;

        void PushUndo(SuiteRegion r)
        {
            if (r == null) return;
            lastWeightEditT = Time.realtimeSinceStartup;
            undoStack.Add(WeightSnapshot.Of(r));
            if (undoStack.Count > UNDO_CAP) undoStack.RemoveAt(0);
            redoStack.Clear();
        }

        // a snapshot is only valid while its region object is still in the live config
        bool RegionLive(SuiteRegion r)
        {
            if (r == null || config == null || config.meshes == null) return false;
            for (int m = 0; m < config.meshes.Count; m++)
                if (config.meshes[m].regions != null && config.meshes[m].regions.Contains(r)) return true;
            return false;
        }


        // ----- parameter undo (slider / toggle edits) -----
        // Weight history alone left the buttons useless in the studios that don't paint,
        // so every parameter edit is recorded too. Repeated changes to the SAME control
        // within a moment coalesce into one step, so dragging a slider is one undo.
        class ParamSnap
        {
            public SuiteRegion region;
            public Dictionary<string, float> regF = new Dictionary<string, float>();
            public Dictionary<string, bool> regB = new Dictionary<string, bool>();
            public Dictionary<string, float> setF = new Dictionary<string, float>();
            public Dictionary<string, bool> setB = new Dictionary<string, bool>();
            public string key; public float time;
        }
        readonly List<ParamSnap> pUndo = new List<ParamSnap>();
        readonly List<ParamSnap> pRedo = new List<ParamSnap>();

        static void GrabFields(object o, Dictionary<string, float> fs, Dictionary<string, bool> bs)
        {
            if (o == null) return;
            System.Reflection.FieldInfo[] fi = o.GetType().GetFields();
            for (int i = 0; i < fi.Length; i++)
            {
                if (fi[i].FieldType == typeof(float)) fs[fi[i].Name] = (float)fi[i].GetValue(o);
                else if (fi[i].FieldType == typeof(bool)) bs[fi[i].Name] = (bool)fi[i].GetValue(o);
            }
        }
        static void PutFields(object o, Dictionary<string, float> fs, Dictionary<string, bool> bs)
        {
            if (o == null) return;
            System.Reflection.FieldInfo[] fi = o.GetType().GetFields();
            for (int i = 0; i < fi.Length; i++)
            {
                float fv; bool bv;
                if (fi[i].FieldType == typeof(float) && fs.TryGetValue(fi[i].Name, out fv)) fi[i].SetValue(o, fv);
                else if (fi[i].FieldType == typeof(bool) && bs.TryGetValue(fi[i].Name, out bv)) fi[i].SetValue(o, bv);
            }
        }

        ParamSnap CaptureParams(string key)
        {
            ParamSnap s = new ParamSnap();
            s.key = key; s.time = Time.realtimeSinceStartup; s.region = selRegion;
            GrabFields(selRegion, s.regF, s.regB);
            GrabFields(config != null ? config.settings : null, s.setF, s.setB);
            return s;
        }

        public void PushParamUndo(string key)
        {
            if (suppress || config == null) return;
            // same control, still mid-interaction -> keep the FIRST value of the drag
            if (pUndo.Count > 0)
            {
                ParamSnap top = pUndo[pUndo.Count - 1];
                if (top.key == key && top.region == selRegion &&
                    Time.realtimeSinceStartup - top.time < 0.7f)
                { top.time = Time.realtimeSinceStartup; return; }
            }
            pUndo.Add(CaptureParams(key));
            if (pUndo.Count > UNDO_CAP) pUndo.RemoveAt(0);
            pRedo.Clear();
        }

        void ApplyParams(ParamSnap s)
        {
            PutFields(config != null ? config.settings : null, s.setF, s.setB);
            if (s.region != null && RegionLive(s.region)) PutFields(s.region, s.regF, s.regB);
            suppress = true;
            PushRegionToUI();
            suppress = false;
            Rebind();
            SaveConfig();
        }

        void DoUndo()
        {
            // drop snapshots orphaned by a config reload (their regions no longer exist)
            while (undoStack.Count > 0 && !RegionLive(undoStack[undoStack.Count - 1].region))
                undoStack.RemoveAt(undoStack.Count - 1);
            if (pUndo.Count > 0 && (undoStack.Count == 0 ||
                pUndo[pUndo.Count - 1].time >= lastWeightEditT))
            {
                ParamSnap ps = pUndo[pUndo.Count - 1]; pUndo.RemoveAt(pUndo.Count - 1);
                ParamSnap cur = CaptureParams(ps.key); cur.region = ps.region;
                pRedo.Add(cur);
                ApplyParams(ps);
                SetStatus("undo: " + ps.key + " (" + (undoStack.Count + pUndo.Count) + " left)");
                return;
            }
            if (undoStack.Count == 0) { SetStatus("nothing to undo"); return; }
            WeightSnapshot s = undoStack[undoStack.Count - 1]; undoStack.RemoveAt(undoStack.Count - 1);
            redoStack.Add(WeightSnapshot.Of(s.region));
            s.Restore();
            AfterWeightEdit(s.region);
            SetStatus("undo (" + undoStack.Count + " left)");
        }

        void DoRedo()
        {
            while (redoStack.Count > 0 && !RegionLive(redoStack[redoStack.Count - 1].region))
                redoStack.RemoveAt(redoStack.Count - 1);
            if (pRedo.Count > 0)
            {
                ParamSnap ps = pRedo[pRedo.Count - 1]; pRedo.RemoveAt(pRedo.Count - 1);
                ParamSnap cur = CaptureParams(ps.key); cur.region = ps.region;
                pUndo.Add(cur);
                ApplyParams(ps);
                SetStatus("redo: " + ps.key);
                return;
            }
            if (redoStack.Count == 0) { SetStatus("nothing to redo"); return; }
            WeightSnapshot s = redoStack[redoStack.Count - 1]; redoStack.RemoveAt(redoStack.Count - 1);
            undoStack.Add(WeightSnapshot.Of(s.region));
            s.Restore();
            AfterWeightEdit(s.region);
            SetStatus("redo");
        }

        void AfterWeightEdit(SuiteRegion r)
        {
            if (selProxy != null)
            {
                selProxy.RebuildSims(boundAvatar, boundAnimator);
                if (selProxy.overlayOn) selProxy.RefreshOverlayColors(selRegion);
            }
            SaveConfig();   // regions are the source of truth for Wobble Studio's mirror — keep the file fresh
        }

        void Update()
        {
            // tooltip auto-close: 5 s after the cursor leaves the bubble
            if (tipBubble != null)
            {
                RectTransform brt = (RectTransform)tipBubble.transform;
                Canvas cv2 = tipBubble.GetComponentInParent<Canvas>();
                Camera cam2 = cv2 != null && cv2.renderMode != RenderMode.ScreenSpaceOverlay ? cv2.worldCamera : null;
                bool over = RectTransformUtility.RectangleContainsScreenPoint(brt, Input.mousePosition, cam2);
                tipIdleT = over ? 0f : tipIdleT + Time.deltaTime;
                if (tipIdleT > 5f) { Destroy(tipBubble); tipBubble = null; tipOwner = null; }
            }

            // F10: collider visualisation + perf/build info (troubleshooting)
            if (Input.GetKeyDown(KeyCode.F10))
            {
                MeshProxy.debugDraw = !MeshProxy.debugDraw;
                Debug.Log("[Squish] collider debug " + (MeshProxy.debugDraw ? "ON" : "OFF"));
                if (MeshProxy.debugDraw)
                    for (int i = 0; i < proxies.Count; i++)
                        if (proxies[i] != null && proxies[i].Alive) proxies[i].LogColliderInfo();
            }

            // F11: dump displaced/rest mesh + solver fields for offline analysis
            if (Input.GetKeyDown(KeyCode.F11))
                for (int i = 0; i < proxies.Count; i++)
                    if (proxies[i] != null && proxies[i].Alive) proxies[i].DumpDebug();

            // Ctrl+Z / Ctrl+Shift+Z while the window is open
            if (window == null || !window.activeSelf) return;
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KeyCode.Z))
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (shift) DoRedo(); else DoUndo();
            }
        }

        // ==================== lifecycle ====================
        void Awake()
        {
            try { VNyanInterface.VNyanInterface.VNyanUI.registerPluginButton(BUTTON_NAME, this); }
            catch (Exception e) { Debug.LogWarning("[Squish] registerPluginButton failed: " + e.Message); }
            savePath = Path.Combine(Application.persistentDataPath, CONFIG_FILE);
            LoadConfig();
            SetupWindow();
            Debug.Log("[Squish] initialized. Config: " + configPath);
        }

        public void pluginButtonClicked()
        {
            if (window == null) return;
            bool show = !window.activeSelf;
            window.SetActive(show);
            if (show) { window.transform.SetAsLastSibling(); RefreshMeshList(); }
        }

        void EnsureAvatar()
        {
            GameObject av = null;
            try { av = (GameObject)VNyanInterface.VNyanInterface.VNyanAvatar.getAvatarObject(); }
            catch { }
            if (av == null)
            {
                if (boundAvatar != null) { DetachAll(); boundAvatar = null; }
                return;
            }
            if (ReferenceEquals(av, boundAvatar)) return;
            boundAvatar = av;
            boundAnimator = av.GetComponentInChildren<Animator>();
            AutoLoadPresetFor(av);   // per-model tuning, before anything binds
            { string fz, ex, hw; PresetStore.Identify(av, out fz, out ex, out hw);
              Debug.Log("[SoftBody] avatar identified as '" + ex + "' (" + hw + ", key '" + fz + "')"); }
            Rebind();
            Debug.Log("[Squish] bound to avatar '" + av.name + "'");
        }

        // one entry per configured mesh, index-aligned with `proxies` (the squish stage)
        readonly List<WobbleProxy> wobProxies = new List<WobbleProxy>();
        readonly List<JelloProxy> jelProxies = new List<JelloProxy>();
        int lastStageFlags = -1;

        // ---------- presets ----------
        // One preset now carries the whole look: all three stages' numbers, the paint and
        // the clothes settings, because they all live in one config.
        PresetFile presets;
        string presetPath;
        Dropdown presetDropdown;
        InputField presetNameInput;
        Toggle presetAutoToggle;
        string lastAutoKey = "~none~";

        void EnsurePresets()
        {
            if (presets != null) return;
            presetPath = Path.Combine(Application.persistentDataPath, "softbodysuite.presets.json");
            presets = PresetStore.Load(presetPath);
        }

        List<string> PresetNames()
        {
            EnsurePresets();
            List<string> names = new List<string>(presets.presets.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        void RefreshPresetList() { RefreshPresetList(SelectedPresetName()); }

        // keep: the preset to leave selected (callers that rename or delete pass the name
        // they captured BEFORE changing the list, since the index shifts underneath it)
        void RefreshPresetList(string keep)
        {
            if (presetDropdown == null) return;
            List<string> names = PresetNames();
            if (names.Count == 0) names.Add("(no presets)");
            suppress = true;
            presetDropdown.ClearOptions();
            presetDropdown.AddOptions(names);
            int keepAt = string.IsNullOrEmpty(keep) ? -1 : names.IndexOf(keep);
            presetDropdown.value = keepAt >= 0 ? keepAt : 0;
            presetDropdown.RefreshShownValue();
            if (presetAutoToggle != null) presetAutoToggle.isOn = presets.autoLoad;
            suppress = false;
            RebuildPresetMgr();
            RebuildRuleMgr();
        }

        string SelectedPresetName()
        {
            if (presetDropdown == null) return null;
            List<string> names = PresetNames();
            int v = presetDropdown.value;
            return (v >= 0 && v < names.Count) ? names[v] : null;
        }

        void OnPresetSave()
        {
            EnsurePresets();
            string name = presetNameInput != null ? presetNameInput.text.Trim() : "";
            if (name.Length == 0) name = SelectedPresetName();
            if (string.IsNullOrEmpty(name)) { SetStatus("type a preset name first"); return; }
            presets.presets[name] = PresetStore.Clone(config);
            PresetStore.Save(presetPath, presets);
            RefreshPresetList(name);
            SetStatus("preset '" + name + "' saved");
        }

        void ApplyPreset(SquishConfig src, string label)
        {
            config = PresetStore.Clone(src);
            Rebind();
            RefreshMeshList();
            PushRegionToUI();
            PushSettingsToUI();      // global sliders/toggles showed the OLD preset's values
            RefreshSectionTicks();   // and so did the stage on/off ticks
            SaveConfig();
            SetStatus("preset '" + label + "' applied");
        }

        void OnPresetLoad()
        {
            EnsurePresets();
            string name = SelectedPresetName();
            SquishConfig src;
            if (string.IsNullOrEmpty(name) || !presets.presets.TryGetValue(name, out src))
            { SetStatus("no preset selected"); return; }
            ApplyPreset(src, name);
        }

        void OnPresetDelete()
        {
            EnsurePresets();
            string name = SelectedPresetName();
            if (string.IsNullOrEmpty(name) || !presets.presets.ContainsKey(name)) { SetStatus("no preset selected"); return; }
            presets.presets.Remove(name);
            for (int i = presets.rules.Count - 1; i >= 0; i--)
                if (presets.rules[i] != null && presets.rules[i].preset == name) presets.rules.RemoveAt(i);
            PresetStore.Save(presetPath, presets);
            RefreshPresetList();
            SetStatus("preset '" + name + "' deleted");
        }

        // Bind the selected preset to whatever avatar is loaded right now.
        void OnPresetBindAvatar()
        {
            EnsurePresets();
            string name = SelectedPresetName();
            if (string.IsNullOrEmpty(name) || !presets.presets.ContainsKey(name)) { SetStatus("save/select a preset first"); return; }
            if (boundAvatar == null) { SetStatus("load an avatar first"); return; }

            string fuzzy, exact, how;
            PresetStore.Identify(boundAvatar, out fuzzy, out exact, out how);
            PresetRule r = new PresetRule();
            r.preset = name;
            if (PresetStore.NameIsUsable(fuzzy)) { r.key = fuzzy; r.fuzzy = true; r.note = "matched by " + how; }
            else
            {
                // nothing deterministic in the name OR the model info: pin to this exact
                // avatar rather than fuzzy-matching a bare number against every model
                r.key = exact; r.fuzzy = false; r.note = "this model only (no usable name/model info)";
            }
            for (int i = presets.rules.Count - 1; i >= 0; i--)
                if (presets.rules[i] != null && presets.rules[i].key == r.key && presets.rules[i].fuzzy == r.fuzzy)
                    presets.rules.RemoveAt(i);
            presets.rules.Add(r);
            PresetStore.Save(presetPath, presets);
            pendingRuleDelete = null;
            RebuildRuleMgr();   // an open rules window would otherwise edit rules that are gone
            lastAutoKey = exact;   // don't immediately re-apply over what the user has now
            SetStatus(r.fuzzy
                ? "'" + name + "' will auto-load for models named like '" + r.key + "'"
                : "'" + name + "' will auto-load for THIS model only (" + r.note + ")");
        }

        // Called when the bound avatar changes.
        void AutoLoadPresetFor(GameObject avatar)
        {
            EnsurePresets();
            if (!presets.autoLoad || avatar == null || presets.rules.Count == 0) return;
            string fuzzy, exact, how;
            PresetStore.Identify(avatar, out fuzzy, out exact, out how);
            if (exact == lastAutoKey) return;             // already handled this avatar
            lastAutoKey = exact;
            for (int i = 0; i < presets.rules.Count; i++)
            {
                PresetRule r = presets.rules[i];
                if (!PresetStore.Matches(r, fuzzy, exact)) continue;
                SquishConfig src;
                if (!presets.presets.TryGetValue(r.preset, out src)) continue;
                ApplyPreset(src, r.preset);
                Debug.Log("[SoftBody] auto-loaded preset '" + r.preset + "' for '" + exact
                    + "' (" + (r.fuzzy ? "fuzzy '" + r.key + "' via " + how : "exact pin") + ")");
                return;
            }
        }


        void DetachAll()
        {
            for (int i = 0; i < wobProxies.Count; i++) wobProxies[i].Detach();
            for (int i = 0; i < jelProxies.Count; i++) jelProxies[i].Detach();
            for (int i = 0; i < proxies.Count; i++) proxies[i].Detach();
            wobProxies.Clear();
            jelProxies.Clear();
            proxies.Clear();
            selProxy = null;
        }

        void Rebind()
        {
            DetachAll();
            JelloProxy.directChain = true;
            MeshProxy.directChain = true;
            MeshProxy.settingsRef = config.settings;
            MeshProxy.configRef = config;
            JelloProxy.settingsRef = config.settings;
            JelloProxy.configRef = config;
            if (boundAvatar == null || config.meshes == null) return;
            SkinnedMeshRenderer[] rends = boundAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int m = 0; m < config.meshes.Count; m++)
            {
                SquishMesh sm = config.meshes[m];
                if (sm == null || !sm.enabled) continue;
                SkinnedMeshRenderer target = null;
                for (int r = 0; r < rends.Length; r++)
                    if (rends[r] != null && rends[r].name == sm.mesh) { target = rends[r]; break; }
                if (target == null) { Debug.LogWarning("[Squish] mesh '" + sm.mesh + "' not found"); continue; }
                // All three stages attach to the same renderer. Which of them actually
                // runs is decided per frame from the stage toggles, so a stage can be
                // switched off and back on without a rebind.
                WobbleProxy wp = new WobbleProxy();
                wp.Attach(target, sm, boundAvatar, boundAnimator);
                wobProxies.Add(wp);
                JelloProxy jp = new JelloProxy();
                jp.Attach(target, sm, boundAvatar, boundAnimator);
                jelProxies.Add(jp);
                MeshProxy p = new MeshProxy();
                p.Attach(target, sm, boundAvatar, boundAnimator);
                proxies.Add(p);
                // Clothes follow the FINISHED body — but only ask the squish stage for it
                // while that stage is actually running. Switched off, its verts are last
                // frame's leftovers, and the clothes were being dragged onto them.
                jp.finalBodySource = () => (config.settings != null && config.settings.squish.enabled)
                                           ? p.OutVerts : null;
                // NOTE: do NOT feed wobble's displacement in here. The cage re-samples the
                // body every frame, so wobble is already in the surface the clothes wrap
                // to; adding it again drove them twice as far and smeared them.
                jp.upstreamExtra = null;
            }
            // restore selection
            selProxy = null;
            for (int i = 0; i < proxies.Count; i++)
                if (proxies[i].cfg == selMesh) selProxy = proxies[i];
            if (selProxy == null && proxies.Count > 0) { selProxy = proxies[0]; selMesh = selProxy.cfg; }
            if (selMesh != null && (selRegion == null || !selMesh.regions.Contains(selRegion)))
                selRegion = selMesh.regions.Count > 0 ? selMesh.regions[0] : null;
            if (selProxy != null && selProxy.overlayOn) selProxy.RefreshOverlayColors(selRegion);
            ApplyNativeOverride();
        }

        void OnDestroy() { RestoreNative(); DetachAll(); }

        void LateUpdate()
        {
            EnsureAvatar();
            if (boundAvatar == null) return;
            if (config.settings == null || !config.settings.enabled)
            {
                if (proxies.Count > 0) DetachAll();   // never leave a frozen copy behind
                return;
            }

            SuiteSettings st = config.settings;

            // Turning a stage off leaves its arrays and bindings where they were; turning it
            // back on has to start clean, or it carries on from a stale frame. This is what
            // the master switch was doing by hand.
            int flags = (st.wobble.enabled ? 1 : 0) | (st.jello.enabled ? 2 : 0)
                      | (st.squish.enabled ? 4 : 0) | (st.cloth.enabled ? 8 : 0)
                      | (st.cloth.follow.enabled ? 16 : 0) | (st.cloth.guard.enabled ? 32 : 0);
            if (flags != lastStageFlags) { lastStageFlags = flags; Rebind(); return; }

            if (!st.wobble.enabled && !st.jello.enabled && !st.squish.enabled)
            {
                if (proxies.Count > 0) DetachAll();   // nothing left to draw — give the body back
                return;
            }

            WobbleProxy.halfRate = st.wobble.halfRate;
            WobbleProxy.halfRateLerp = st.wobble.halfRateLerp;
            WobbleProxy.asyncSim = st.wobble.asyncSim;
            // the jell-o stage's frame-rate savers were never being handed over, so its
            // half-rate settings did nothing at all
            JelloProxy.halfRate = st.jello.halfRate;
            JelloProxy.halfRateLerp = st.jello.halfRateLerp;
            MeshProxy.halfRate = st.squish.halfRate;
            MeshProxy.halfRateLerp = st.squish.halfRateLerp;
            MeshProxy.asyncSim = st.squish.asyncSim;

            float dtW = Mathf.Min(Time.deltaTime, st.wobble.maxDeltaTime);
            float dtJ = Mathf.Min(Time.deltaTime, st.jello.maxDeltaTime);
            float dtS = Mathf.Min(Time.deltaTime, st.squish.maxDeltaTime);
            if (dtW <= 0f || dtJ <= 0f || dtS <= 0f) return;

            bool anyDead = false;
            for (int i = 0; i < proxies.Count; i++)
            {
                WobbleProxy wp = wobProxies[i];
                JelloProxy jp = jelProxies[i];
                MeshProxy sp = proxies[i];

                // Each stage hands its verts to the next one that is switched on; the last
                // one running is the only one on screen.
                Vector3[] v = null, nrm = null;
                if (st.wobble.enabled)
                {
                    wp.Frame(dtW, Mathf.Clamp(st.wobble.substeps, 1, 8), Vector3.down, true);
                    v = wp.OutVerts; nrm = wp.OutNormals;
                }
                // The jell-o stage ALWAYS runs, even switched off — with its sim disabled it
                // simply passes the body through. It has to, because the clothes are fitted
                // from here, and they need a body that is up to date this frame.
                jp.upVerts = v; jp.upNormals = nrm;
                jp.upDisp = st.wobble.enabled ? wp.Disp : null;   // lets it attach clothes un-wobbled
                jp.Frame(dtJ, Mathf.Clamp(st.jello.substeps, 1, 8), Vector3.down, st.jello.enabled);
                v = jp.OutVerts; nrm = jp.OutNormals;
                if (st.squish.enabled)
                {
                    sp.upVerts = v; sp.upNormals = nrm;
                    sp.Frame(dtS, Mathf.Clamp(st.squish.substeps, 1, 8), Vector3.down, true);
                }

                wp.SetVisible(false);              // jell-o always runs downstream of it
                jp.SetVisible(!st.squish.enabled);
                sp.SetVisible(st.squish.enabled);

                // ----- clothes, once, at the very end -----
                // The garments are fitted to the finished body first; only then does the
                // guard tuck any remaining poke-through back inside them.
                // when the pass cannot run the garments must be handed back, or they
                // freeze at whatever shape we last drove them to
                if (st.cloth.enabled && st.cloth.follow.enabled) jp.LateFollowUpdate();
                else jp.ReleaseFollowers();
                if (st.cloth.enabled && st.cloth.guard.enabled && st.squish.enabled)
                    sp.RunClipGuard();

                if (!sp.Alive || !jp.Alive || !wp.Alive) anyDead = true;
            }
            if (anyDead) { Rebind(); return; }   // a stage self-detached (mesh swap) — reattach cleanly

            HandlePainting();
        }

        // ==================== painting ====================
        void HandlePainting()
        {
            if (!paintMode || selProxy == null || selRegion == null) { EndStroke(); return; }
            if (!Input.GetMouseButton(0)) { EndStroke(); return; }
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
            Camera cam = Camera.main;
            if (cam == null && Camera.allCamerasCount > 0) cam = Camera.allCameras[0];
            if (cam == null) return;
            if (!stroking) { PushUndo(selRegion); stroking = true; }   // one undo step per stroke
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            if (selProxy.PaintStroke(selRegion, ray, brushRadius, brushStrength * Time.deltaTime * 8f, paintBrushMode))
                selProxy.RefreshOverlayColors(selRegion);
        }

        void EndStroke()
        {
            if (!stroking) return;
            stroking = false;
            // stroke finished — apply the new weights to the live sim
            if (selProxy != null) selProxy.RebuildSims(boundAvatar, boundAnimator);
        }

        // ==================== window ====================
        void SetupWindow()
        {
            if (windowPrefab == null) return;
            try { window = (GameObject)VNyanInterface.VNyanInterface.VNyanUI.instantiateUIPrefab(windowPrefab); }
            catch (Exception e) { Debug.LogWarning("[Squish] instantiateUIPrefab failed: " + e.Message); window = null; }
            if (window == null) return;

            statusLabel = FindControl<Text>("Label_Status");
            meshDropdown = FindControl<Dropdown>("Dropdown_Mesh");
            regionDropdown = FindControl<Dropdown>("Dropdown_Region");
            groupDropdown = FindControl<Dropdown>("Dropdown_Group");
            colliderDropdown = FindControl<Dropdown>("Dropdown_Collider");
            colMeshDropdown = FindControl<Dropdown>("Dropdown_ColMesh");
            regionNameInput = FindControl<InputField>("Input_RegionName");
            refBoneInput = FindControl<InputField>("Input_RefBone");
            refBoneDropdown = FindControl<Dropdown>("Dropdown_RefBone");
            if (refBoneDropdown != null) refBoneDropdown.onValueChanged.AddListener(i =>
            {
                if (suppress || selRegion == null) return;
                selRegion.refBone = (i <= 0 || i >= refBoneOptions.Count) ? "" : refBoneOptions[i];
                RebindSel();
            });
            colBoneInput = FindControl<InputField>("Input_ColBone");
            colBoneDropdown = FindControl<Dropdown>("Dropdown_ColBonePick");
            colRadiusInput = FindControl<InputField>("Input_ColRadius");
            enabledToggle = FindControl<Toggle>("Toggle_Enabled");
            regionEnabledToggle = FindControl<Toggle>("Toggle_RegionEnabled");
            paintToggle = FindControl<Toggle>("Toggle_Paint");
            overlayToggle = FindControl<Toggle>("Toggle_Overlay");
            gravityPoseToggle = FindControl<Toggle>("Toggle_GravityPose");

            HookToggle(enabledToggle, v => { if (config.settings != null) config.settings.enabled = v;
                if (!v) DetachAll(); else Rebind();
                SaveConfig(); });   // OFF must fully detach (frozen-copy bug); state persists across restarts
            if (enabledToggle != null && config.settings != null)
            { suppress = true; enabledToggle.isOn = config.settings.enabled; suppress = false; }
            // paint mode + overlay start OFF (prefab toggles default to on)
            suppress = true;
            if (paintToggle != null) paintToggle.isOn = false;
            if (overlayToggle != null) overlayToggle.isOn = false;
            suppress = false;
            paintMode = false;
            FixDropdown(meshDropdown); FixDropdown(regionDropdown); FixDropdown(groupDropdown);
            FixDropdown(colliderDropdown); FixDropdown(colMeshDropdown); FixDropdown(refBoneDropdown);
            AddTooltips(new Dictionary<string, string>
            {
                { "Toggle_Enabled", "Master switch. Off fully detaches the plugin from the avatar (original mesh shows again). State is saved." },
                { "Dropdown_Mesh", "Which skinned mesh to work on. Enable squish on it below, then paint a region." },
                { "Button_EnableMesh", "Start simulating this mesh (creates its proxy copy)." },
                { "Button_DisableMesh", "Stop simulating this mesh and restore the original." },
                { "Dropdown_Region", "A region = a named set of painted vertices with its own squish settings. Pick which one to edit." },
                { "Button_AddRegion", "Create a new empty region on this mesh." },
                { "Button_RemoveRegion", "Delete the selected region and its paint." },
                { "Toggle_RegionEnabled", "Temporarily turn this region's simulation on/off without deleting it." },
                { "Input_RegionName", "Rename the region." },
                { "Toggle_Paint", "Paint weights with the left mouse button directly on the avatar." },
                { "Toggle_Overlay", "Show the paint as colors on the model: blue = 0, red = 1." },
                { "Button_PaintAdd", "Brush adds weight." },
                { "Button_PaintSub", "Brush erases weight." },
                { "Button_Undo", "Undo the last paint stroke (Ctrl+Z)." },
                { "Button_Redo", "Redo (Ctrl+Shift+Z)." },
                { "Button_BlurWeights", "Smooth the painted weights — softens hard paint edges (fewer artifacts at region borders)." },
                { "Slider_radius", "Brush size in meters." },
                { "Slider_strength", "How much weight each stroke adds/removes." },
                { "Slider_overlayop", "Overlay transparency." },
                { "Toggle_GroupChildren", "When picking vertex groups, also include every child bone down that branch of the rig." },
                { "Slider_groupthr", "Minimum skin weight for a vertex to be selected by vertex-group picking." },
                { "Button_PickGroups", "Select region vertices from the mesh's bone weight groups (multi-select checkbox list)." },
                { "Button_ClearWeights", "Erase ALL paint in this region." },
                { "Button_ApplyMulti", "Copy region(s) onto other meshes: Auto (same bones as the painted area), bone group, or surface projection — with radius / bone-cutoff options and a copy-ALL-regions switch." },
                { "Slider_squish", "How strongly the skin dents under a collider. The soft, aesthetic layer." },
                { "Slider_squishdepth", "Maximum dent depth in meters. 0 = automatic from region size." },
                { "Slider_bulge", "Volume pushed sideways around the contact — the flesh 'flows' out around fingers/arms." },
                { "Slider_selfsquish", "Squish when this region presses into ANOTHER region (breasts pressing together)." },
                { "Slider_maxdent", "Penetration limiter: dent depth (m) where the flesh 'gives way'. Deeper presses stop denting and instead move the whole breast (see the two Evacuate sliders). This is the anti-swallow control." },
                { "Slider_evacbone", "Past the limit, TRANSLATE the region's own driver bones (auto-detected, e.g. breast bones) away from the press. Only the top bone of each chain moves — nested bones ride along, so messy rigs can't fight." },
                { "Slider_evacall", "Evacuate ALL bones: nested/child driver bones ALSO translate individually (on top of the parent's shift), each pushed by the contact demand measured around ITS OWN spot — finer, deeper local evacuation. Multiplier on the bone-evac strength. 0 = classic topmost-only." },
                { "Slider_evacblob", "Past the limit, shift + squash the whole painted blob away from the press (water-balloon style). Works even when the region has no dedicated bones." },
                { "Input_ColBone", "Bone name for a simple sphere/capsule collider (e.g. LeftHand)." },
                { "Input_ColRadius", "Collider radius / skin gap in meters. For mesh colliders this is how far the skin stays above the surface." },
                { "Dropdown_ColMesh", "Mesh to auto-generate colliders from. '(all meshes)' covers every skinned mesh at once. Only arm/hand bones get capsules." },
                { "Button_AddCollider", "Add the bone collider picked on the left." },
                { "Button_AddMeshCol", "Add auto-generated arm/hand capsules from the mesh picked above. Press F10 to see them." },
                { "Dropdown_Collider", "All colliders on this region." },
                { "Button_RemoveCollider", "Remove the collider selected in the list." },
                { "Dropdown_ColBonePick", "Pick a bone from the avatar for a manual sphere collider (no typing needed)." },
                { "Button_ShowCol", "Toggle translucent capsules showing every ACTIVE collider (same as F10). The colliders made here are used by all three stages." },
                { "Toggle_clipguard", "OPTIONAL, and it reshapes the BODY: flesh is tucked inside the clothes covering it. Prefer 'Keep clothes off the skin' above, which fixes clipping by moving the CLOTHES instead and leaves your body silhouette untouched. Only use this for garments that aren't moved onto the body.\n\nOriginal note: anti-clip guarantee. Runs at the very end of the pipeline, on the body that actually renders: every painted vertex is bound to the clothes covering it and is held INSIDE that cloth. Works no matter how well the clothes track the body — nothing can poke through. Prefers a garment's deformed (soft-body driven) shape when one exists." },
                { "Slider_clipclear", "EXTRA margin (m) beyond the fit measured when the guard bound. 0 preserves the exact rest relationship (nothing moves while idle) and only holds the flesh back when a sim pushes it toward the cloth. Raise a millimetre or two if you still see the surface graze through." },
                { "Slider_cliprange", "Bind range: painted body verts within this distance of a garment are guarded by it. Raise it if deep flesh still escapes, lower it to limit the guard to skin-tight areas." },
                { "Slider_clipstr", "Blend of the correction (1 = never allowed through, lower = soft/partial). Use below 1 only if the flattening reads too hard." },
                { "Toggle_NativeOff", "Disable VNyan/native spring & dynamic bones while squishing — they fight the mesh simulation." },
                { "Toggle_HalfRate", "HALF-RATE physics: compute the simulation every 2nd frame (with doubled timestep) and hold the result between — near-halves the physics cost on slower PCs. Skinning/animation still updates every frame, so it is barely visible." },
                { "Toggle_HalfRateLerp", "HALF-RATE + SMOOTH: like half-rate physics, but held frames show a blend between the last two physics ticks instead of a repeat — smoother motion at the same cost, with half a tick of extra latency. Mutually exclusive with the other rate options." },
                { "Toggle_AsyncSim", "ASYNC physics: run the whole simulation on a background worker thread — the main thread only captures inputs and applies last frame's result, so almost the entire physics cost disappears from the frame. Costs 1 frame of physics latency. Mutually exclusive with the half-rate options; disabled while F10 debug is on." },
                { "Toggle_NativeScoped", "Only disable native physics on bones that drive painted regions (instead of everywhere)." },
                { "Button_Reload", "Re-read the saved config from disk and re-bind." },
                { "Button_Save", "Write everything to softbodysuite.json." },
                { "Button_Close", "Hide this window (plugin keeps running)." },
            });
            HookToggle(regionEnabledToggle, v => { if (selRegion != null) selRegion.enabled = v; });
            HookToggle(paintToggle, v => { paintMode = v; if (v && overlayToggle != null && !overlayToggle.isOn) overlayToggle.isOn = true; SetStatus(v ? "painting: LMB adds weight (Subtract button for erase)" : "paint off"); });
            HookToggle(overlayToggle, v => { if (selProxy != null) { selProxy.SetOverlay(v); if (v) selProxy.RefreshOverlayColors(selRegion); } });
            HookToggle(gravityPoseToggle, v => { if (selRegion != null) selRegion.gravityPoseOnly = v; });

            if (meshDropdown != null) meshDropdown.onValueChanged.AddListener(OnMeshSelected);
            if (regionDropdown != null) regionDropdown.onValueChanged.AddListener(OnRegionSelected);
            if (regionNameInput != null) regionNameInput.onEndEdit.AddListener(t => { if (!suppress && selRegion != null && t.Length > 0) { selRegion.name = t; RefreshRegionList(); } });
            if (refBoneInput != null) refBoneInput.onEndEdit.AddListener(t => { if (!suppress && selRegion != null) { selRegion.refBone = t; RebindSel(); } });

            WireButton("Button_EnableMesh", OnEnableMesh);
            WireButton("Button_DisableMesh", OnDisableMesh);
            WireButton("Button_AddRegion", OnAddRegion);
            WireButton("Button_RemoveRegion", OnRemoveRegion);
            WireButton("Button_PaintAdd", () => { paintBrushMode = 0; SetStatus("brush: ADD"); });
            WireButton("Button_PaintSub", () => { paintBrushMode = 1; SetStatus("brush: SUBTRACT"); });
            WireButton("Button_PickGroups", OpenGroupPanel);
            WireButton("Button_ClearWeights", OnClearWeights);
            WireButton("Button_AddMeshCol", OnAddMeshCollider);
            WireButton("Button_ShowCol", () =>
            {
                MeshProxy.debugDraw = !MeshProxy.debugDraw;
                SetStatus("collider visualisation " + (MeshProxy.debugDraw ? "ON" : "OFF"));
                if (MeshProxy.debugDraw)
                    for (int i = 0; i < proxies.Count; i++)
                        if (proxies[i] != null && proxies[i].Alive) proxies[i].LogColliderInfo();
            });
            WireButton("Button_BlurWeights", OnBlurWeights);
            WireButton("Button_ApplyMulti", OpenApplyPanel);
            // Each stage has its own frame-rate cheats now, so each gets its own row.
            // Half-rate and half-rate-lerp are two ways of doing the same thing, so
            // ticking one unticks the other for that stage.
            WirePerf("wob", config.settings.wobble, true);
            WirePerf("jel", config.settings.jello, false);   // jell-o has no worker thread
            WirePerf("sq", config.settings.squish, true);
            HookToggle(FindControl<Toggle>("Toggle_NativeOff"), v => { config.settings.nativeDisable = v; ApplyNativeOverride(); });
            HookToggle(FindControl<Toggle>("Toggle_NativeScoped"), v => { config.settings.nativeScoped = v; ApplyNativeOverride(); });
            WireButton("Button_Undo", DoUndo);
            WireButton("Button_Redo", DoRedo);
            HookToggle(FindControl<Toggle>("Toggle_GroupChildren"), v => groupChildren = v);
            WireButton("Button_AddCollider", OnAddCollider);
            WireButton("Button_RemoveCollider", OnRemoveCollider);
            WireButton("Button_Reload", () => { LoadConfig(); Rebind(); RefreshMeshList(); SetStatus("reloaded from disk"); });
            WireButton("Button_Save", SaveConfig);
            WireButton("Button_Close", () => window.SetActive(false));

            // sliders: key -> range
            HookSlider("radius", 0.005f, 0.3f, v => brushRadius = v);
            HookSlider("strength", 0.02f, 1f, v => brushStrength = v);
            HookSlider("overlayop", 0.05f, 1f, v => { if (selProxy != null) selProxy.SetOverlayOpacity(v); });
            HookSlider("groupthr", 0.01f, 1f, v => groupThreshold = v);
            clipGuardToggle = FindControl<Toggle>("Toggle_clipguard");
            if (clipGuardToggle != null) clipGuardToggle.onValueChanged.AddListener(v =>
            {
                if (suppress || config.settings == null) return;
                config.settings.cloth.guard.enabled = v;
                for (int i = 0; i < proxies.Count; i++) proxies[i].ClipGuardInvalidate();
                SetStatus(v ? "clip guard ON — flesh will be held inside the clothes covering it"
                            : "clip guard off");
            });
            HookSlider("clipclear", 0f, 0.03f, v =>
            { if (config.settings != null) config.settings.cloth.guard.clearance = v; });
            HookSlider("cliprange", 0.005f, 0.15f, v =>
            { if (config.settings != null) config.settings.cloth.guard.range = v; });
            HookSlider("clipstr", 0f, 1f, v =>
            { if (config.settings != null) config.settings.cloth.guard.strength = v; });
            if (config.settings != null)
            {
                suppress = true;
                if (clipGuardToggle != null) clipGuardToggle.isOn = config.settings.cloth.guard.enabled;
                Slider cs;
                if (sliders.TryGetValue("clipclear", out cs) && cs != null)
                { cs.value = config.settings.cloth.guard.clearance; SetValueLabel("clipclear", cs.value); }
                if (sliders.TryGetValue("cliprange", out cs) && cs != null)
                { cs.value = config.settings.cloth.guard.range; SetValueLabel("cliprange", cs.value); }
                if (sliders.TryGetValue("clipstr", out cs) && cs != null)
                { cs.value = config.settings.cloth.guard.strength; SetValueLabel("clipstr", cs.value); }
                suppress = false;
            }
            // ---- global settings that came from the other two studios ----
            HookSlider("remeshsize", 0.002f, 0.15f, v => { if (config.settings != null) config.settings.jello.remeshSize = v; });
            HookSlider("remeshpasses", 5f, 20f, v => { if (config.settings != null) config.settings.jello.remeshPasses = v; });
            HookSlider("projavg", 0f, 30f, v => { if (config.settings != null) config.settings.jello.projAvg = v; });
            HookSlider("proxysmooth", 0f, 60f, v => { if (config.settings != null) config.settings.jello.proxySmooth = v; });
            HookSlider("seamlevel", 0f, 40f, v => { if (config.settings != null) config.settings.jello.seamLevel = v; });
            HookSlider("seamrange", 0f, 0.08f, v => { if (config.settings != null) config.settings.jello.seamRange = v; });
            HookSlider("seammax", 0f, 0.05f, v => { if (config.settings != null) config.settings.jello.seamMaxStretch = v; });
            HookSlider("boost", 0f, 2f, v => { if (config.settings != null) config.settings.jello.boostStrength = v; });
            HookSlider("boostspread", 0f, 60f, v => { if (config.settings != null) config.settings.jello.boostSpread = v; });
            HookSlider("boostmax", 0.001f, 0.05f, v => { if (config.settings != null) config.settings.jello.boostMax = v; });
            HookSlider("slapsens", 0f, 2f, v => { if (config.settings != null) config.settings.jello.slapSens = v; });
            HookSlider("slappower", 0f, 2f, v => { if (config.settings != null) config.settings.jello.slapPower = v; });
            HookSlider("cagefollow", 0.005f, 0.5f, v => { if (config.settings != null) config.settings.cloth.follow.range = v; });
            HookSlider("cagefit", 0.25f, 1.5f, v => { if (config.settings != null) config.settings.cloth.follow.fitStrength = v; });
            HookSlider("cageinflate", 0f, 0.02f, v => { if (config.settings != null) config.settings.cloth.follow.inflate = v; });
            HookSlider("cageinflatedyn", 0f, 1f, v => { if (config.settings != null) config.settings.cloth.follow.inflateDyn = v; });
            HookSlider("fillmax", 0f, 2000f, v => { if (config.settings != null) config.settings.cloth.follow.fillMax = v; });
            HookSlider("anchormin", 10f, 2000f, v => { if (config.settings != null) config.settings.cloth.follow.anchorMin = v; });
            HookSlider("cageminclear", 0f, 0.02f, v => { if (config.settings != null) config.settings.cloth.follow.minClear = v; });
            HookSlider("cagefolsharp", 0f, 1f, v => { if (config.settings != null) config.settings.cloth.follow.folSharp = v; });
            HookSlider("cagefolsm", 0f, 40f, v => { if (config.settings != null) config.settings.cloth.follow.folSmooth = v; });
            HookSlider("cliprimfade", 0f, 0.1f, v => { if (config.settings != null) config.settings.cloth.guard.rimFade = v; });
            HookSlider("wob_substeps", 1f, 8f, v => { if (config.settings != null) config.settings.wobble.substeps = Mathf.RoundToInt(v); });
            HookSlider("jel_substeps", 1f, 8f, v => { if (config.settings != null) config.settings.jello.substeps = Mathf.RoundToInt(v); });
            HookSlider("sq_substeps", 1f, 8f, v => { if (config.settings != null) config.settings.squish.substeps = Mathf.RoundToInt(v); });
            HookToggle(FindControl<Toggle>("Toggle_useremesh"), v => { if (config.settings != null) { config.settings.jello.useRemesh = v ? 1f : 0f; Rebind(); } });
            HookToggle(FindControl<Toggle>("Toggle_cagedrive"), v => { if (config.settings != null) { config.settings.cloth.follow.enabled = v; config.settings.cloth.enabled = config.settings.cloth.follow.enabled || config.settings.cloth.guard.enabled; } });
            HookToggle(FindControl<Toggle>("Toggle_cagewhole"), v => { if (config.settings != null) config.settings.cloth.follow.bindWhole = v; });
            HookToggle(FindControl<Toggle>("Toggle_bonefilter"), v => { if (config.settings != null) config.settings.cloth.follow.boneFilter = v; });
            HookToggle(FindControl<Toggle>("Toggle_clothout"), v => { if (config.settings != null) config.settings.cloth.follow.clothOutside = v; });

            EnsurePresets();
            presetDropdown = FindControl<Dropdown>("Dropdown_Preset");
            if (presetDropdown != null) FixDropdown(presetDropdown);
            presetNameInput = FindControl<InputField>("Input_presetname");
            presetAutoToggle = FindControl<Toggle>("Toggle_presetauto");
            if (presetAutoToggle != null) presetAutoToggle.onValueChanged.AddListener(v =>
            {
                if (suppress || presets == null) return;
                presets.autoLoad = v; PresetStore.Save(presetPath, presets);
                SetStatus(v ? "presets will auto-load when a model loads" : "preset auto-load off");
            });
            WireButton("Button_PresetSave", OnPresetSave);
            WireButton("Button_PresetLoad", OnPresetLoad);
            WireButton("Button_PresetBind", OnPresetBindAvatar);
            WireButton("Button_PresetDelete", OnPresetDelete);
            autoNameInput = FindControl<InputField>("Input_autoname");
            WireButton("Button_PresetBindName", OnPresetBindName);
            WireButton("Button_ManagePresets", OpenPresetMgr);
            WireButton("Button_ManageRules", OpenRuleMgr);
            RefreshPresetList();

            // Every stage keeps its own copy of the shared numbers, so the same region can
            // wobble hard, jiggle gently and barely squish at once — exactly as the three
            // separate plugins allowed.
            // ----- Wobble -----
            HookRegionSlider("wob_jiggle", 0f, 2f, (r, v) => r.wobble.jiggle = v, r => r.wobble.jiggle);
            HookRegionSlider("wob_stiffness", 0.5f, 30f, (r, v) => r.wobble.stiffness = v, r => r.wobble.stiffness);
            HookRegionSlider("wob_damping", 0f, 1f, (r, v) => r.wobble.damping = v, r => r.wobble.damping);
            HookRegionSlider("wob_bounce", 0f, 2f, (r, v) => r.wobble.bounce = v, r => r.wobble.bounce);
            HookRegionSlider("wob_maxoff", 0.005f, 0.25f, (r, v) => r.wobble.maxOffset = v, r => r.wobble.maxOffset);
            HookRegionSlider("wob_gravity", 0f, 2f, (r, v) => r.wobble.gravity = v, r => r.wobble.gravity);
            HookRegionSlider("wob_cloth", 0f, 1f, (r, v) => r.wobble.clothRipple = v, r => r.wobble.clothRipple);
            HookRegionSlider("wob_clothsize", 0f, 1f, (r, v) => r.wobble.clothSize = v, r => r.wobble.clothSize);
            HookRegionSlider("wob_jello", 0f, 1f, (r, v) => r.wobble.jello = v, r => r.wobble.jello);
            HookRegionSlider("wob_jellosize", 0f, 1f, (r, v) => r.wobble.jelloSize = v, r => r.wobble.jelloSize);
            HookRegionSlider("wob_liquid", 0f, 1f, (r, v) => r.wobble.liquid = v, r => r.wobble.liquid);
            HookRegionSlider("wob_liquidsize", 0f, 1f, (r, v) => r.wobble.liquidSize = v, r => r.wobble.liquidSize);
            HookRegionSlider("wob_wavespeed", 0.1f, 3f, (r, v) => r.wobble.waveSpeed = v, r => r.wobble.waveSpeed);
            HookRegionSlider("wob_sway", 0f, 1f, (r, v) => r.wobble.sway = v, r => r.wobble.sway);
            HookRegionSlider("wob_twistj", 0f, 1f, (r, v) => r.wobble.twistJiggle = v, r => r.wobble.twistJiggle);
            HookRegionSlider("wob_pulse", 0f, 1f, (r, v) => r.wobble.pulse = v, r => r.wobble.pulse);
            HookRegionSlider("wob_pulserate", 0f, 1f, (r, v) => r.wobble.pulseRate = v, r => r.wobble.pulseRate);
            HookRegionSlider("wob_stretch", 0f, 1f, (r, v) => r.wobble.stretch = v, r => r.wobble.stretch);
            HookRegionSlider("wob_turb", 0f, 1f, (r, v) => r.wobble.turbulence = v, r => r.wobble.turbulence);
            HookRegionSlider("wob_turbsize", 0f, 1f, (r, v) => r.wobble.turbSize = v, r => r.wobble.turbSize);
            HookRegionSlider("wob_cellulite", 0f, 1f, (r, v) => r.wobble.cellulite = v, r => r.wobble.cellulite);
            HookRegionSlider("wob_cellsize", 0f, 1f, (r, v) => r.wobble.celluliteSize = v, r => r.wobble.celluliteSize);
            HookRegionSlider("wob_squish", 0f, 2f, (r, v) => r.wobble.squish = v, r => r.wobble.squish);
            HookRegionSlider("wob_squishdepth", 0f, 0.2f, (r, v) => r.wobble.squishDepth = v, r => r.wobble.squishDepth);
            HookRegionSlider("wob_bulge", 0f, 2f, (r, v) => r.wobble.bulge = v, r => r.wobble.bulge);
            HookRegionSlider("wob_selfsquish", 0f, 2f, (r, v) => r.wobble.selfSquish = v, r => r.wobble.selfSquish);
            HookRegionSlider("wob_ropepull", 0f, 2f, (r, v) => r.wobble.ropePull = v, r => r.wobble.ropePull);
            HookRegionSlider("wob_ropeease", 0.05f, 3f, (r, v) => r.wobble.ropePullEase = v, r => r.wobble.ropePullEase);
            HookRegionSlider("wob_jellospeed", 0.05f, 3f, (r, v) => r.wobble.jelloSpeed = v, r => r.wobble.jelloSpeed);
            HookRegionSlider("wob_jellorand", 0f, 1f, (r, v) => r.wobble.jelloRandomSize = v, r => r.wobble.jelloRandomSize);
            HookRegionSlider("wob_jellorandsp", 0f, 3f, (r, v) => r.wobble.jelloRandomSpeed = v, r => r.wobble.jelloRandomSpeed);
            HookRegionSlider("wob_swayspeed", 0.05f, 3f, (r, v) => r.wobble.swaySpeed = v, r => r.wobble.swaySpeed);
            HookRegionSlider("wob_swaydamp", 0f, 1f, (r, v) => r.wobble.swayDamp = v, r => r.wobble.swayDamp);
            HookRegionSlider("wob_twistspeed", 0.05f, 3f, (r, v) => r.wobble.twistSpeed = v, r => r.wobble.twistSpeed);
            HookRegionSlider("wob_twistdamp", 0f, 1f, (r, v) => r.wobble.twistDamp = v, r => r.wobble.twistDamp);

            // ----- Jell-o -----
            HookRegionSlider("jel_jiggle", 0f, 2f, (r, v) => r.jello.jiggle = v, r => r.jello.jiggle);
            HookRegionSlider("jel_stiffness", 0.5f, 30f, (r, v) => r.jello.stiffness = v, r => r.jello.stiffness);
            HookRegionSlider("jel_damping", 0f, 1f, (r, v) => r.jello.damping = v, r => r.jello.damping);
            HookRegionSlider("jel_bounce", 0f, 2f, (r, v) => r.jello.bounce = v, r => r.jello.bounce);
            HookRegionSlider("jel_maxoff", 0.005f, 0.25f, (r, v) => r.jello.maxOffset = v, r => r.jello.maxOffset);
            HookRegionSlider("jel_gravity", 0f, 2f, (r, v) => r.jello.gravity = v, r => r.jello.gravity);
            HookRegionSlider("jel_cloth", 0f, 1f, (r, v) => r.jello.clothRipple = v, r => r.jello.clothRipple);
            HookRegionSlider("jel_clothsize", 0f, 1f, (r, v) => r.jello.clothSize = v, r => r.jello.clothSize);
            HookRegionSlider("jel_jello", 0f, 1f, (r, v) => r.jello.jello = v, r => r.jello.jello);
            HookRegionSlider("jel_jellosize", 0f, 1f, (r, v) => r.jello.jelloSize = v, r => r.jello.jelloSize);
            HookRegionSlider("jel_liquid", 0f, 1f, (r, v) => r.jello.liquid = v, r => r.jello.liquid);
            HookRegionSlider("jel_liquidsize", 0f, 1f, (r, v) => r.jello.liquidSize = v, r => r.jello.liquidSize);
            HookRegionSlider("jel_wavespeed", 0.1f, 3f, (r, v) => r.jello.waveSpeed = v, r => r.jello.waveSpeed);
            HookRegionSlider("jel_sway", 0f, 1f, (r, v) => r.jello.sway = v, r => r.jello.sway);
            HookRegionSlider("jel_twistj", 0f, 1f, (r, v) => r.jello.twistJiggle = v, r => r.jello.twistJiggle);
            HookRegionSlider("jel_pulse", 0f, 1f, (r, v) => r.jello.pulse = v, r => r.jello.pulse);
            HookRegionSlider("jel_pulserate", 0f, 1f, (r, v) => r.jello.pulseRate = v, r => r.jello.pulseRate);
            HookRegionSlider("jel_stretch", 0f, 1f, (r, v) => r.jello.stretch = v, r => r.jello.stretch);
            HookRegionSlider("jel_turb", 0f, 1f, (r, v) => r.jello.turbulence = v, r => r.jello.turbulence);
            HookRegionSlider("jel_turbsize", 0f, 1f, (r, v) => r.jello.turbSize = v, r => r.jello.turbSize);
            HookRegionSlider("jel_cellulite", 0f, 1f, (r, v) => r.jello.cellulite = v, r => r.jello.cellulite);
            HookRegionSlider("jel_cellsize", 0f, 1f, (r, v) => r.jello.celluliteSize = v, r => r.jello.celluliteSize);
            HookRegionSlider("jel_squish", 0f, 2f, (r, v) => r.jello.squish = v, r => r.jello.squish);
            HookRegionSlider("jel_squishdepth", 0f, 0.2f, (r, v) => r.jello.squishDepth = v, r => r.jello.squishDepth);
            HookRegionSlider("jel_bulge", 0f, 2f, (r, v) => r.jello.bulge = v, r => r.jello.bulge);
            HookRegionSlider("jel_selfsquish", 0f, 2f, (r, v) => r.jello.selfSquish = v, r => r.jello.selfSquish);
            HookRegionSlider("jel_maxdent", 0f, 0.15f, (r, v) => r.jello.maxDent = v, r => r.jello.maxDent);
            HookRegionSlider("jel_evacbone", 0f, 2f, (r, v) => r.jello.evacBone = v, r => r.jello.evacBone);
            HookRegionSlider("jel_evacblob", 0f, 2f, (r, v) => r.jello.evacBlob = v, r => r.jello.evacBlob);
            HookRegionSlider("jel_xiter", 1f, 20f, (r, v) => r.jello.xIter = v, r => r.jello.xIter);
            HookRegionSlider("jel_xstretch", 0f, 1f, (r, v) => r.jello.xStretch = v, r => r.jello.xStretch);
            HookRegionSlider("jel_xattach", 0f, 1f, (r, v) => r.jello.xAttach = v, r => r.jello.xAttach);
            HookRegionSlider("jel_xmaxstretch", 0f, 0.4f, (r, v) => r.jello.xMaxStretch = v, r => r.jello.xMaxStretch);
            HookRegionSlider("jel_xpressure", 0f, 4f, (r, v) => r.jello.xPressure = v, r => r.jello.xPressure);
            HookRegionSlider("jel_xgrid", 0.002f, 1f, (r, v) => r.jello.xGrid = v, r => r.jello.xGrid);
            HookRegionSlider("jel_xgridmin", 0.001f, 0.05f, (r, v) => r.jello.xGridMin = v, r => r.jello.xGridMin);
            HookRegionSlider("jel_xgridmax", 0.002f, 0.1f, (r, v) => r.jello.xGridMax = v, r => r.jello.xGridMax);
            HookRegionSlider("jel_xgridauto", 0f, 1f, (r, v) => r.jello.xGridAuto = v, r => r.jello.xGridAuto);
            HookRegionSlider("jel_xsigma", 0.001f, 0.05f, (r, v) => r.jello.xSigma = v, r => r.jello.xSigma);
            HookRegionSlider("jel_xcorr", 0.0005f, 0.02f, (r, v) => r.jello.xCorr = v, r => r.jello.xCorr);
            HookRegionSlider("jel_xcolrelax", 0f, 1f, (r, v) => r.jello.xColRelax = v, r => r.jello.xColRelax);
            HookRegionSlider("jel_xcompress", 0f, 1f, (r, v) => r.jello.xCompress = v, r => r.jello.xCompress);
            HookRegionSlider("jel_xbend", 0f, 2f, (r, v) => r.jello.xBend = v, r => r.jello.xBend);
            HookRegionSlider("jel_xtension", 0f, 1f, (r, v) => r.jello.xTension = v, r => r.jello.xTension);
            HookRegionSlider("jel_xsmooth", 0f, 20f, (r, v) => r.jello.xSmoothPasses = v, r => r.jello.xSmoothPasses);

            // ----- Squish -----
            HookRegionSlider("sq_jiggle", 0f, 2f, (r, v) => r.squish.jiggle = v, r => r.squish.jiggle);
            HookRegionSlider("sq_stiffness", 0.5f, 30f, (r, v) => r.squish.stiffness = v, r => r.squish.stiffness);
            HookRegionSlider("sq_damping", 0f, 1f, (r, v) => r.squish.damping = v, r => r.squish.damping);
            HookRegionSlider("sq_bounce", 0f, 2f, (r, v) => r.squish.bounce = v, r => r.squish.bounce);
            HookRegionSlider("sq_maxoff", 0.005f, 0.25f, (r, v) => r.squish.maxOffset = v, r => r.squish.maxOffset);
            HookRegionSlider("sq_gravity", 0f, 2f, (r, v) => r.squish.gravity = v, r => r.squish.gravity);
            HookRegionSlider("sq_cloth", 0f, 1f, (r, v) => r.squish.clothRipple = v, r => r.squish.clothRipple);
            HookRegionSlider("sq_clothsize", 0f, 1f, (r, v) => r.squish.clothSize = v, r => r.squish.clothSize);
            HookRegionSlider("sq_jello", 0f, 1f, (r, v) => r.squish.jello = v, r => r.squish.jello);
            HookRegionSlider("sq_jellosize", 0f, 1f, (r, v) => r.squish.jelloSize = v, r => r.squish.jelloSize);
            HookRegionSlider("sq_liquid", 0f, 1f, (r, v) => r.squish.liquid = v, r => r.squish.liquid);
            HookRegionSlider("sq_liquidsize", 0f, 1f, (r, v) => r.squish.liquidSize = v, r => r.squish.liquidSize);
            HookRegionSlider("sq_wavespeed", 0.1f, 3f, (r, v) => r.squish.waveSpeed = v, r => r.squish.waveSpeed);
            HookRegionSlider("sq_sway", 0f, 1f, (r, v) => r.squish.sway = v, r => r.squish.sway);
            HookRegionSlider("sq_twistj", 0f, 1f, (r, v) => r.squish.twistJiggle = v, r => r.squish.twistJiggle);
            HookRegionSlider("sq_pulse", 0f, 1f, (r, v) => r.squish.pulse = v, r => r.squish.pulse);
            HookRegionSlider("sq_pulserate", 0f, 1f, (r, v) => r.squish.pulseRate = v, r => r.squish.pulseRate);
            HookRegionSlider("sq_stretch", 0f, 1f, (r, v) => r.squish.stretch = v, r => r.squish.stretch);
            HookRegionSlider("sq_turb", 0f, 1f, (r, v) => r.squish.turbulence = v, r => r.squish.turbulence);
            HookRegionSlider("sq_turbsize", 0f, 1f, (r, v) => r.squish.turbSize = v, r => r.squish.turbSize);
            HookRegionSlider("sq_cellulite", 0f, 1f, (r, v) => r.squish.cellulite = v, r => r.squish.cellulite);
            HookRegionSlider("sq_cellsize", 0f, 1f, (r, v) => r.squish.celluliteSize = v, r => r.squish.celluliteSize);
            HookRegionSlider("sq_squish", 0f, 2f, (r, v) => r.squish.squish = v, r => r.squish.squish);
            HookRegionSlider("sq_squishdepth", 0f, 0.2f, (r, v) => r.squish.squishDepth = v, r => r.squish.squishDepth);
            HookRegionSlider("sq_bulge", 0f, 2f, (r, v) => r.squish.bulge = v, r => r.squish.bulge);
            HookRegionSlider("sq_selfsquish", 0f, 2f, (r, v) => r.squish.selfSquish = v, r => r.squish.selfSquish);
            HookRegionSlider("sq_maxdent", 0f, 0.15f, (r, v) => r.squish.maxDent = v, r => r.squish.maxDent);
            HookRegionSlider("sq_evacbone", 0f, 2f, (r, v) => r.squish.evacBone = v, r => r.squish.evacBone);
            HookRegionSlider("sq_evacall", 0f, 2f, (r, v) => r.squish.evacAllBones = v, r => r.squish.evacAllBones);
            HookRegionSlider("sq_evacblob", 0f, 2f, (r, v) => r.squish.evacBlob = v, r => r.squish.evacBlob);

            PushSettingsToUI();
            BuildSections();
            window.SetActive(false);
        }

        float groupThreshold = 0.25f;

        void ForceRestDisplay()
        {
            for (int i = 0; i < proxies.Count; i++)
                for (int r = 0; r < proxies[i].sims.Count; r++) proxies[i].sims[r].ResetState();
        }

        void RebindSel()
        {
            if (selProxy == null) return;
            for (int r = 0; r < selProxy.sims.Count; r++)
                selProxy.ResolveRegionRefs(selProxy.sims[r], boundAvatar, boundAnimator);
            selProxy.ResolveColliderMeshes(boundAvatar);
        }

        // ---------- UI helpers ----------
        void HookToggle(Toggle t, Action<bool> set)
        {
            if (t != null) t.onValueChanged.AddListener(v => { if (!suppress) set(v); });
        }
        void HookSlider(string key, float min, float max, Action<float> set)
        {
            Slider s = FindControl<Slider>("Slider_" + key);
            if (s == null) return;
            s.minValue = min; s.maxValue = max;
            sliders[key] = s;
            s.onValueChanged.AddListener(v => { if (!suppress) { PushParamUndo(key); set(v); SetValueLabel(key, v); } });

            // manual-entry box next to the slider: type a value, hit enter
            InputField inp = FindControl<InputField>("Value_" + key);
            if (inp != null)
            {
                valueInputs[key] = inp;
                inp.onEndEdit.AddListener(txt =>
                {
                    if (suppress) return;
                    float v;
                    if (!float.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { SetValueLabel(key, s.value); return; }
                    v = Mathf.Clamp(v, min, max);
                    s.value = v;              // fires the listener above (set + label)
                    SetValueLabel(key, v);
                });
            }
        }
        void HookRegionSlider(string key, float min, float max, Action<SuiteRegion, float> set, Func<SuiteRegion, float> get)
        {
            regionGetters[key] = get;
            HookSlider(key, min, max, v => { if (selRegion != null) set(selRegion, v); });
        }
        readonly Dictionary<string, Func<SuiteRegion, float>> regionGetters = new Dictionary<string, Func<SuiteRegion, float>>();


        // Wires one stage's half-rate / half-rate-lerp / async row.
        void WirePerf(string prefix, StageSettings st, bool canAsync)
        {
            Toggle tHR = FindControl<Toggle>("Toggle_" + prefix + "_HalfRate");
            Toggle tHL = FindControl<Toggle>("Toggle_" + prefix + "_HalfRateLerp");
            Toggle tAS = FindControl<Toggle>("Toggle_" + prefix + "_AsyncSim");
            if (tHR != null) tHR.onValueChanged.AddListener(v =>
            {
                if (suppress) return;
                st.halfRate = v;
                if (v && st.halfRateLerp)
                { st.halfRateLerp = false; suppress = true; if (tHL != null) tHL.isOn = false; suppress = false; }
            });
            if (tHL != null) tHL.onValueChanged.AddListener(v =>
            {
                if (suppress) return;
                st.halfRateLerp = v;
                if (v && st.halfRate)
                { st.halfRate = false; suppress = true; if (tHR != null) tHR.isOn = false; suppress = false; }
            });
            if (tAS != null)
            {
                if (!canAsync)
                {
                    // The jell-o solver reads collider transforms while it steps, which a
                    // worker thread is not allowed to do — so this one stays off.
                    st.asyncSim = false;
                    tAS.isOn = false;
                    tAS.interactable = false;
                }
                else tAS.onValueChanged.AddListener(v => { if (!suppress) st.asyncSim = v; });
            }
            suppress = true;
            if (tHR != null) tHR.isOn = st.halfRate;
            if (tHL != null) tHL.isOn = st.halfRateLerp;
            if (tAS != null && canAsync) tAS.isOn = st.asyncSim;
            suppress = false;
        }


        // Put the saved settings INTO the controls. Everything here runs under `suppress`
        // so setting a value cannot fire the listener that would write it straight back.
        void PushSettingsToUI()
        {
            if (config == null || config.settings == null) return;
            SuiteSettings st = config.settings;
            suppress = true;

            SetSlider("remeshsize", st.jello.remeshSize);
            SetSlider("remeshpasses", st.jello.remeshPasses);
            SetSlider("projavg", st.jello.projAvg);
            SetSlider("proxysmooth", st.jello.proxySmooth);
            SetSlider("seamlevel", st.jello.seamLevel);
            SetSlider("seamrange", st.jello.seamRange);
            SetSlider("seammax", st.jello.seamMaxStretch);
            SetSlider("boost", st.jello.boostStrength);
            SetSlider("boostspread", st.jello.boostSpread);
            SetSlider("boostmax", st.jello.boostMax);
            SetSlider("slapsens", st.jello.slapSens);
            SetSlider("slappower", st.jello.slapPower);

            SetSlider("cagefollow", st.cloth.follow.range);
            SetSlider("cagefit", st.cloth.follow.fitStrength);
            SetSlider("cageinflate", st.cloth.follow.inflate);
            SetSlider("cageinflatedyn", st.cloth.follow.inflateDyn);
            SetSlider("fillmax", st.cloth.follow.fillMax);
            SetSlider("anchormin", st.cloth.follow.anchorMin);
            SetSlider("cageminclear", st.cloth.follow.minClear);
            SetSlider("cagefolsharp", st.cloth.follow.folSharp);
            SetSlider("cagefolsm", st.cloth.follow.folSmooth);
            SetSlider("cliprimfade", st.cloth.guard.rimFade);

            SetSlider("wob_substeps", st.wobble.substeps);
            SetSlider("jel_substeps", st.jello.substeps);
            SetSlider("sq_substeps", st.squish.substeps);

            SetTog("Toggle_useremesh", st.jello.useRemesh > 0.5f);
            SetTog("Toggle_cagedrive", st.cloth.follow.enabled);
            SetTog("Toggle_cagewhole", st.cloth.follow.bindWhole);
            SetTog("Toggle_bonefilter", st.cloth.follow.boneFilter);
            SetTog("Toggle_clothout", st.cloth.follow.clothOutside);
            SetTog("Toggle_clipguard", st.cloth.guard.enabled);

            suppress = false;
        }

        void SetSlider(string key, float v)
        {
            Slider sl;
            if (!sliders.TryGetValue(key, out sl) || sl == null) return;
            sl.value = Mathf.Clamp(v, sl.minValue, sl.maxValue);
            SetValueLabel(key, sl.value);
        }

        void SetTog(string name, bool v)
        {
            Toggle t = FindControl<Toggle>(name);
            if (t != null) t.isOn = v;
        }

        void SetValueLabel(string key, float v)
        {
            InputField inp;
            if (valueInputs.TryGetValue(key, out inp) && inp != null && !inp.isFocused)
                inp.text = v.ToString(key == "squishdepth" || key == "maxdent" ? "0.000" : "0.00", CultureInfo.InvariantCulture);
        }

        // Unity dropdowns sometimes leave the closed caption blank after options rebuild
        // wire the caption Text permanently so Unity itself refreshes it on USER selection —
        // the prefab dropdowns ship with captionText unassigned, which is why the closed box
        // showed blank even though the value was set (ForceCaption only covered rebuilds)
        static void FixDropdown(Dropdown dd)
        {
            if (dd == null) return;
            Text cap = dd.captionText;
            if (cap == null)
            {
                Transform lbl = dd.transform.Find("Label");
                if (lbl != null) cap = lbl.GetComponent<Text>();
                if (cap == null)
                    for (int i = 0; i < dd.transform.childCount && cap == null; i++)
                        cap = dd.transform.GetChild(i).GetComponent<Text>();
                dd.captionText = cap;
            }
            if (cap != null)
            {
                if (cap.font == null) cap.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                cap.horizontalOverflow = HorizontalWrapMode.Overflow;
                cap.verticalOverflow = VerticalWrapMode.Overflow;
                if (cap.fontSize > 13 || cap.fontSize < 8) cap.fontSize = 12;
                cap.color = new Color(0.05f, 0.05f, 0.07f, 1f);
            }
            Dropdown captured = dd;
            dd.onValueChanged.AddListener(_ => ForceCaption(captured));
            ForceCaption(dd);
        }

        // ---------- tooltips: a "?" beside every control, click for a speech bubble ----------
        GameObject tipBubble;

        void AddTooltips(Dictionary<string, string> tips)
        {
            if (window == null) return;
            foreach (KeyValuePair<string, string> kv in tips)
            {
                Transform ctl = FindDeep(window.transform, kv.Key);
                if (ctl == null || ctl.Find("TipBtn") != null) continue;
                GameObject go = new GameObject("TipBtn", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                go.GetComponent<Image>().color = new Color(0.22f, 0.30f, 0.55f, 0.95f);
                RectTransform rt = go.GetComponent<RectTransform>();
                rt.SetParent(ctl, false);
                // sliders: sit just LEFT of the track (the corner spot is under the handle);
                // everything else: top-right corner chip
                bool leftSide = kv.Key.StartsWith("Slider_");
                Vector2 a = leftSide ? new Vector2(0f, 1f) : new Vector2(1f, 1f);
                rt.anchorMin = a; rt.anchorMax = a; rt.pivot = new Vector2(1f, 1f);
                rt.sizeDelta = new Vector2(13f, 13f);
                rt.anchoredPosition = leftSide ? new Vector2(-5f, -1f) : new Vector2(0f, 0f);
                go.transform.SetAsLastSibling();
                GameObject tg = new GameObject("T", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                Text t = tg.GetComponent<Text>();
                t.text = "?"; t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                t.fontSize = 10; t.fontStyle = FontStyle.Bold; t.color = Color.white;
                t.alignment = TextAnchor.MiddleCenter;
                RectTransform trt = tg.GetComponent<RectTransform>();
                trt.SetParent(go.transform, false);
                trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
                trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
                Transform ctlCap = ctl; string txtCap = kv.Value;
                go.GetComponent<Button>().onClick.AddListener(() => ShowTip(ctlCap, txtCap));
            }
        }

        Transform tipOwner;
        float tipIdleT;

        void ShowTip(Transform near, string txt)
        {
            if (tipBubble != null)
            {
                bool same = tipOwner == near;
                Destroy(tipBubble); tipBubble = null; tipOwner = null;
                if (same) return;   // clicking the same ? again just closes it
            }
            tipOwner = near; tipIdleT = 0f;
            const float w = 280f;
            float h = 40f + 13f * Mathf.Ceil(txt.Length / 44f);
            tipBubble = new GameObject("TipBubble", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform rt = tipBubble.GetComponent<RectTransform>();
            rt.SetParent(window.transform, false);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(w, h);
            tipBubble.GetComponent<Image>().color = new Color(0.16f, 0.15f, 0.24f, 0.99f);
            rt.position = near.position;              // same canvas — world snap, then nudge up
            rt.anchoredPosition += new Vector2(0f, 14f);
            // keep the bubble inside the window horizontally
            float half = ((RectTransform)window.transform).sizeDelta.x * 0.5f;
            Vector2 ap = rt.anchoredPosition;
            ap.x = Mathf.Clamp(ap.x, -half + w * 0.5f + 4f, half - w * 0.5f - 4f);
            rt.anchoredPosition = ap;
            tipBubble.transform.SetAsLastSibling();

            GameObject tg = new GameObject("Txt", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            Text t = tg.GetComponent<Text>();
            t.text = txt; t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = 11; t.color = Color.white; t.alignment = TextAnchor.UpperLeft;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
            RectTransform trt = tg.GetComponent<RectTransform>();
            trt.SetParent(tipBubble.transform, false);
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(8f, 6f); trt.offsetMax = new Vector2(-8f, -6f);

            GameObject cb = new GameObject("X", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            cb.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.10f);
            RectTransform crt2 = cb.GetComponent<RectTransform>();
            crt2.SetParent(tipBubble.transform, false);
            crt2.anchorMin = new Vector2(1f, 1f); crt2.anchorMax = new Vector2(1f, 1f); crt2.pivot = new Vector2(1f, 1f);
            crt2.sizeDelta = new Vector2(16f, 16f);
            GameObject xg = new GameObject("T", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            Text xt = xg.GetComponent<Text>();
            xt.text = "×"; xt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            xt.fontSize = 12; xt.color = Color.white; xt.alignment = TextAnchor.MiddleCenter;
            RectTransform xrt = xg.GetComponent<RectTransform>();
            xrt.SetParent(cb.transform, false);
            xrt.anchorMin = Vector2.zero; xrt.anchorMax = Vector2.one; xrt.offsetMin = Vector2.zero; xrt.offsetMax = Vector2.zero;
            cb.GetComponent<Button>().onClick.AddListener(() => { Destroy(tipBubble); tipBubble = null; });
        }

        static void ForceCaption(Dropdown dd)
        {
            if (dd == null || dd.options.Count == 0) return;
            dd.RefreshShownValue();
            Text cap = dd.captionText;
            if (cap == null)
            {
                Transform lbl = dd.transform.Find("Label");
                if (lbl != null) cap = lbl.GetComponent<Text>();
            }
            if (cap != null)
            {
                int v = Mathf.Clamp(dd.value, 0, dd.options.Count - 1);
                cap.text = dd.options[v].text;
                cap.horizontalOverflow = HorizontalWrapMode.Overflow;
            }
        }
        void WireButton(string name, UnityEngine.Events.UnityAction act)
        {
            Button b = FindControl<Button>(name);
            if (b != null) b.onClick.AddListener(act);
        }

        // ---------------- collapsible sections ----------------
        // The window is one long list of rows, so sections are found at runtime: a "Hdr_*"
        // label starts one and everything until the next belongs to it. The header becomes a
        // button with a little triangle, and sections that simply switch off at zero also get
        // a tick box, which remembers the slider values it zeroed so they come back untouched.
        class UiSection
        {
            public string label;
            public Text hdr;
            public Toggle onBox;
            public RectTransform boxRt;
            public readonly List<RectTransform> rows = new List<RectTransform>();
            public readonly List<float> rowY = new List<float>();
            public readonly List<string> keys = new List<string>();
            public readonly Dictionary<string, float> muted = new Dictionary<string, float>();
            public float hdrY, height;
            public bool open = false;
            public string baseText = "";   // label without the open/closed marker
        }
        readonly List<UiSection> sections = new List<UiSection>();
        RectTransform sectionContent;
        float sectionContentH;

        // sections that mean "off" when their sliders sit at zero — these get a tick box
        // "" for a normal section; otherwise which stage this section switches on and off.
        // The three plugin sections and Clothes get a real on/off tick rather than the
        // slider-zeroing kind, so turning one off actually stops that stage running.
        static string SectionStage(string label)
        {
            string l = label.ToLowerInvariant();
            if (l.Contains("wobble")) return "wobble";
            if (l.Contains("jell")) return "jello";
            if (l.Contains("clothes")) return "cloth";
            if (l.Contains("squish")) return "squish";
            return "";
        }

        bool StageOn(string stage)
        {
            if (config == null || config.settings == null) return true;
            if (stage == "wobble") return config.settings.wobble.enabled;
            if (stage == "jello") return config.settings.jello.enabled;
            if (stage == "squish") return config.settings.squish.enabled;
            if (stage == "cloth") return config.settings.cloth.enabled;
            return true;
        }

        void SetStageOn(string stage, bool on)
        {
            if (config == null || config.settings == null) return;
            if (stage == "wobble") config.settings.wobble.enabled = on;
            else if (stage == "jello") config.settings.jello.enabled = on;
            else if (stage == "squish") config.settings.squish.enabled = on;
            else if (stage == "cloth") config.settings.cloth.enabled = on;
        }

        static bool SectionCanMute(string label)
        {
            if (SectionStage(label).Length > 0) return true;
            string l = label.ToLowerInvariant();
            return l.Contains("boost") || l.Contains("slap") || l.Contains("clothes")
                || l.Contains("seam") || l.Contains("wave") || l.Contains("jiggle mode")
                || l.Contains("surface") || l.Contains("advanced") || l.Contains("clip");
        }

        void BuildSections()
        {
            sections.Clear();
            if (window == null) return;
            Transform content = FindDeep(window.transform, "Content");
            if (content == null) return;
            sectionContent = content as RectTransform;
            if (sectionContent != null) sectionContentH = sectionContent.sizeDelta.y;

            List<RectTransform> kids = new List<RectTransform>();
            for (int i = 0; i < content.childCount; i++)
            {
                RectTransform rt = content.GetChild(i) as RectTransform;
                if (rt != null) kids.Add(rt);
            }
            // visual order: anchoredPosition y runs negative downwards
            kids.Sort(delegate (RectTransform a, RectTransform b)
            { return b.anchoredPosition.y.CompareTo(a.anchoredPosition.y); });

            UiSection cur = null;
            for (int i = 0; i < kids.Count; i++)
            {
                RectTransform rt = kids[i];
                if (rt.name.StartsWith("Hdr_"))
                {
                    cur = new UiSection();
                    cur.label = rt.name.Substring(4);
                    cur.hdr = rt.GetComponent<Text>();
                    cur.hdrY = rt.anchoredPosition.y;
                    sections.Add(cur);
                    continue;
                }
                if (cur == null) continue;   // rows above the first header stay put
                cur.rows.Add(rt);
                cur.rowY.Add(rt.anchoredPosition.y);
                if (rt.name.StartsWith("Slider_")) cur.keys.Add(rt.name.Substring(7));
            }

            for (int s = 0; s < sections.Count; s++)
            {
                UiSection sec = sections[s];
                float bottom = sec.hdrY;
                for (int r = 0; r < sec.rows.Count; r++)
                {
                    float b = sec.rowY[r] - sec.rows[r].rect.height;
                    if (b < bottom) bottom = b;
                }
                sec.height = sec.hdrY - bottom;

                if (sec.hdr != null)
                {
                    UiSection captured = sec;
                    Button btn = sec.hdr.gameObject.GetComponent<Button>();
                    if (btn == null) btn = sec.hdr.gameObject.AddComponent<Button>();
                    btn.transition = Selectable.Transition.None;
                    btn.onClick.AddListener(delegate { ToggleSection(captured); });
                    sec.baseText = sec.hdr.text;
                    sec.hdr.text = Marker(sec.open) + sec.baseText;
                }
                if (SectionCanMute(sec.label) && sec.keys.Count > 0) MakeSectionBox(sec);
            }
            RelayoutSections();
        }

        void MakeSectionBox(UiSection sec)
        {
            if (sec.hdr == null || sectionContent == null) return;
            GameObject go = new GameObject("SecOn_" + sec.label,
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Toggle));
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(sectionContent, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(16f, 16f);
            RectTransform hrt = sec.hdr.rectTransform;
            rt.anchoredPosition = new Vector2(hrt.anchoredPosition.x + hrt.rect.width - 22f,
                                              hrt.anchoredPosition.y - 2f);
            Image bg = go.GetComponent<Image>();
            bg.color = new Color(1f, 1f, 1f, 0.22f);

            GameObject ck = new GameObject("Check", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform crt = ck.GetComponent<RectTransform>();
            crt.SetParent(rt, false);
            crt.anchorMin = new Vector2(0.18f, 0.18f);
            crt.anchorMax = new Vector2(0.82f, 0.82f);
            crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
            ck.GetComponent<Image>().color = new Color(0.55f, 1f, 0.65f, 1f);

            Toggle t = go.GetComponent<Toggle>();
            t.targetGraphic = bg;
            t.graphic = ck.GetComponent<Image>();
            t.isOn = !SectionIsZero(sec);   // unticked when it is already doing nothing
            sec.onBox = t;
            sec.boxRt = rt;
            UiSection captured = sec;
            t.onValueChanged.AddListener(delegate (bool on) { SetSectionOn(captured, on); });

            sec.rows.Add(rt);
            sec.rowY.Add(rt.anchoredPosition.y);
        }

        bool SectionIsZero(UiSection sec)
        {
            string stage = SectionStage(sec.label);
            if (stage.Length > 0) return !StageOn(stage);
            for (int i = 0; i < sec.keys.Count; i++)
            {
                Slider s;
                if (sliders.TryGetValue(sec.keys[i], out s) && s != null && Mathf.Abs(s.value) > 0.0001f)
                    return false;
            }
            return true;
        }

        void SetSectionOn(UiSection sec, bool on)
        {
            if (suppress) return;
            string stage = SectionStage(sec.label);
            if (stage.Length > 0)
            {
                SetStageOn(stage, on);
                SetStatus(sec.label.Trim() + (on ? " on" : " off"));
                return;   // the sliders keep their values; the stage just stops running
            }
            if (!on)
            {
                sec.muted.Clear();
                for (int i = 0; i < sec.keys.Count; i++)
                {
                    Slider s;
                    if (!sliders.TryGetValue(sec.keys[i], out s) || s == null) continue;
                    sec.muted[sec.keys[i]] = s.value;
                    s.value = Mathf.Clamp(0f, s.minValue, s.maxValue);
                }
                SetStatus(sec.label.Trim() + " turned off");
            }
            else
            {
                foreach (KeyValuePair<string, float> kv in sec.muted)
                {
                    Slider s;
                    if (sliders.TryGetValue(kv.Key, out s) && s != null) s.value = kv.Value;
                }
                sec.muted.Clear();
                SetStatus(sec.label.Trim() + " back on");
            }
        }

        static string Marker(bool open) { return open ? "[-] " : "[+] "; }

        void ToggleSection(UiSection sec)
        {
            sec.open = !sec.open;
            if (sec.hdr != null) sec.hdr.text = Marker(sec.open) + sec.baseText;

            RelayoutSections();
        }

        // rows keep their built positions; a shut section just pulls everything below it up
        void RelayoutSections()
        {
            float shift = 0f;
            for (int s = 0; s < sections.Count; s++)
            {
                UiSection sec = sections[s];
                if (sec.hdr != null)
                {
                    RectTransform h = sec.hdr.rectTransform;
                    h.anchoredPosition = new Vector2(h.anchoredPosition.x, sec.hdrY + shift);
                }
                for (int r = 0; r < sec.rows.Count; r++)
                {
                    RectTransform rt = sec.rows[r];
                    if (rt == null) continue;
                    bool vis = sec.open || rt == sec.boxRt;   // the tick box stays visible
                    if (rt.gameObject.activeSelf != vis) rt.gameObject.SetActive(vis);
                    if (vis) rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, sec.rowY[r] + shift);
                }
                if (!sec.open) shift += Mathf.Max(0f, sec.height - 24f);
            }
            if (sectionContent != null)
                sectionContent.sizeDelta = new Vector2(sectionContent.sizeDelta.x,
                    Mathf.Max(60f, sectionContentH - shift));
        }

        T FindControl<T>(string name) where T : Component
        {
            if (window == null) return null;
            Transform t = FindDeep(window.transform, name);
            return t != null ? t.GetComponent<T>() : null;
        }
        static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
        void SetStatus(string msg) { if (statusLabel != null) statusLabel.text = msg; }

        // ---------- mesh / region management ----------
        void RefreshMeshList()
        {
            if (meshDropdown == null || boundAvatar == null) return;
            List<string> opts = new List<string>();
            SkinnedMeshRenderer[] rends = boundAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < rends.Length; i++)
            {
                bool on = false;
                for (int m = 0; m < config.meshes.Count; m++) if (config.meshes[m].mesh == rends[i].name && config.meshes[m].enabled) { on = true; break; }
                opts.Add((on ? "● " : "") + rends[i].name);
            }
            suppress = true;
            meshDropdown.ClearOptions(); meshDropdown.AddOptions(opts);
            int sel = 0;
            if (selMesh != null)
                for (int i = 0; i < rends.Length; i++) if (rends[i].name == selMesh.mesh) { sel = i; break; }
            meshDropdown.value = sel; ForceCaption(meshDropdown);
            if (colMeshDropdown != null)
            {
                List<string> cm = new List<string>();
                cm.Add("(all meshes)");
                for (int i = 0; i < rends.Length; i++) if (rends[i] != null) cm.Add(rends[i].name);
                colMeshDropdown.ClearOptions(); colMeshDropdown.AddOptions(cm);
                colMeshDropdown.value = 0; ForceCaption(colMeshDropdown);
            }
            if (colBoneDropdown != null)
            {
                // every bone that skins any mesh, deduped + sorted — an actual picker
                // instead of typing bone names by hand
                HashSet<string> bset = new HashSet<string>();
                for (int i = 0; i < rends.Length; i++)
                {
                    if (rends[i] == null || rends[i].bones == null) continue;
                    foreach (Transform b in rends[i].bones) if (b != null) bset.Add(b.name);
                }
                List<string> bl = new List<string>(bset);
                bl.Sort(System.StringComparer.OrdinalIgnoreCase);
                bl.Insert(0, "(pick bone)");
                colBoneDropdown.ClearOptions(); colBoneDropdown.AddOptions(bl);
                colBoneDropdown.value = 0; ForceCaption(colBoneDropdown);
            }
            suppress = false;
            RefreshRegionList();
            RefreshGroupList();
        }

        string MeshNameAt(int index)
        {
            if (boundAvatar == null) return null;
            SkinnedMeshRenderer[] rends = boundAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (index < 0 || index >= rends.Length) return null;
            return rends[index].name;
        }

        void OnMeshSelected(int index)
        {
            if (suppress) return;
            string nm = MeshNameAt(index);
            if (nm == null) return;
            selMesh = null; selProxy = null;
            for (int m = 0; m < config.meshes.Count; m++) if (config.meshes[m].mesh == nm) selMesh = config.meshes[m];
            for (int p = 0; p < proxies.Count; p++) if (proxies[p].cfg == selMesh) selProxy = proxies[p];
            selRegion = (selMesh != null && selMesh.regions.Count > 0) ? selMesh.regions[0] : null;
            CloseApplyPanel();   // its source/target lists are stale now
            RefreshRegionList(); RefreshGroupList(); PushRegionToUI();
        }

        void OnEnableMesh()
        {
            if (meshDropdown == null) return;
            string nm = MeshNameAt(meshDropdown.value);
            if (nm == null) { SetStatus("no mesh selected"); return; }
            SquishMesh sm = null;
            for (int m = 0; m < config.meshes.Count; m++) if (config.meshes[m].mesh == nm) sm = config.meshes[m];
            if (sm == null) { sm = new SquishMesh(); sm.mesh = nm; config.meshes.Add(sm); }
            sm.enabled = true;
            if (sm.regions.Count == 0) { SuiteRegion r = new SuiteRegion(); r.name = "region 1"; sm.regions.Add(r); }
            selMesh = sm; selRegion = sm.regions[0];
            Rebind(); RefreshMeshList(); PushRegionToUI();
            SetStatus("squish enabled on '" + nm + "' — paint a region, then Save");
        }

        void OnDisableMesh()
        {
            if (selMesh == null) return;
            selMesh.enabled = false;
            Rebind(); RefreshMeshList();
            SetStatus("squish disabled on '" + selMesh.mesh + "'");
        }

        void RefreshRegionList()
        {
            if (regionDropdown == null) return;
            List<string> opts = new List<string>();
            if (selMesh != null)
                for (int r = 0; r < selMesh.regions.Count; r++) opts.Add(selMesh.regions[r].name);
            if (opts.Count == 0) opts.Add("(none)");
            suppress = true;
            regionDropdown.ClearOptions(); regionDropdown.AddOptions(opts);
            int sel = selMesh != null ? selMesh.regions.IndexOf(selRegion) : -1;
            regionDropdown.value = Mathf.Max(0, sel); regionDropdown.RefreshShownValue();
            suppress = false;
            PushRegionToUI();
        }

        void OnRegionSelected(int index)
        {
            if (suppress || selMesh == null) return;
            if (index >= 0 && index < selMesh.regions.Count) selRegion = selMesh.regions[index];
            PushRegionToUI();
            if (selProxy != null && selProxy.overlayOn) selProxy.RefreshOverlayColors(selRegion);
        }

        void OnAddRegion()
        {
            if (selMesh == null) { SetStatus("enable squish on a mesh first"); return; }
            SuiteRegion r = new SuiteRegion(); r.name = "region " + (selMesh.regions.Count + 1);
            selMesh.regions.Add(r); selRegion = r;
            Rebind(); RefreshRegionList();
            SetStatus("added '" + r.name + "' — paint weights or select from a bone group");
        }

        void OnRemoveRegion()
        {
            if (selMesh == null || selRegion == null) return;
            string nm = selRegion.name;
            selMesh.regions.Remove(selRegion);
            selRegion = selMesh.regions.Count > 0 ? selMesh.regions[0] : null;
            CloseApplyPanel();
            Rebind(); RefreshRegionList();
            SetStatus("removed region '" + nm + "'");
        }

        void PushRegionToUI()
        {
            if (selRegion == null) return;
            suppress = true;
            if (regionNameInput != null) regionNameInput.text = selRegion.name;
            if (refBoneInput != null) refBoneInput.text = selRegion.refBone ?? "";
            if (refBoneDropdown != null)
            {
                int sel = 0;
                for (int i = 1; i < refBoneOptions.Count; i++)
                    if (refBoneOptions[i] == selRegion.refBone) { sel = i; break; }
                refBoneDropdown.value = sel; refBoneDropdown.RefreshShownValue();
            }
            if (regionEnabledToggle != null) regionEnabledToggle.isOn = selRegion.enabled;
            if (gravityPoseToggle != null) gravityPoseToggle.isOn = selRegion.gravityPoseOnly;
            foreach (KeyValuePair<string, Func<SuiteRegion, float>> kv in regionGetters)
            {
                Slider s;
                if (sliders.TryGetValue(kv.Key, out s) && s != null)
                { s.value = kv.Value(selRegion); SetValueLabel(kv.Key, s.value); }
            }
            suppress = false;
            RefreshColliderList();
        }

        // ---------- vertex groups (skin-weight quick select) ----------
        void RefreshGroupList()
        {
            List<string> bones = selProxy != null ? selProxy.BoneNamesWithWeights() : new List<string>();
            // legacy dropdown (removed from the window; kept null-safe)
            if (groupDropdown != null)
            {
                suppress = true;
                groupDropdown.ClearOptions();
                groupDropdown.AddOptions(bones.Count > 0 ? bones : new List<string> { "(no mesh)" });
                groupDropdown.value = 0; groupDropdown.RefreshShownValue();
                suppress = false;
            }
            // ref-bone dropdown: "(auto)" + every skinned bone of this mesh
            if (refBoneDropdown != null)
            {
                refBoneOptions = new List<string>(); refBoneOptions.Add("(auto: highest weight)");
                refBoneOptions.AddRange(bones);
                suppress = true;
                refBoneDropdown.ClearOptions(); refBoneDropdown.AddOptions(refBoneOptions);
                refBoneDropdown.value = 0; refBoneDropdown.RefreshShownValue();
                suppress = false;
            }
            groupSel.Clear();
        }

        // ----- multi vertex-group picker (checkbox panel) -----
        GameObject groupPanel;
        ScrollRect groupScroll;
        float groupScrollPos = 1f;   // 1 = top; remembered across the per-click rebuilds
        readonly HashSet<string> groupSel = new HashSet<string>();
        List<string> lastGroupPick = new List<string>();   // remembered for apply-to-meshes

        void SaveGroupScroll() { if (groupScroll != null) groupScrollPos = groupScroll.verticalNormalizedPosition; }

        void OpenGroupPanel()
        {
            if (selProxy == null || selRegion == null) { SetStatus("select a mesh + region first"); return; }
            if (groupPanel != null) { Destroy(groupPanel); groupPanel = null; return; }
            BuildGroupPanel();
        }

        void BuildGroupPanel()
        {
            List<string> bones = selProxy.BoneNamesWithWeights();
            if (bones.Count == 0) { SetStatus("mesh has no skinned bones?"); return; }
            float w = 320f, rowH = 22f, pad = 10f;
            int visRows = Mathf.Min(bones.Count, 24);
            float vpH = visRows * rowH;
            float h = 66f + rowH + vpH + 46f;

            groupPanel = new GameObject("SquishGroupPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform prt = groupPanel.GetComponent<RectTransform>();
            prt.SetParent(window.transform.parent, false);
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(w, h);
            prt.anchoredPosition = new Vector2(-260f, 0f);
            groupPanel.GetComponent<Image>().color = new Color(0.10f, 0.09f, 0.14f, 0.98f);
            groupPanel.AddComponent<SquishWindowDrag>();
            groupPanel.transform.SetAsLastSibling();
            rtParent = groupPanel;

            float y = pad;
            RtText("Title", "Pick vertex groups for '" + selRegion.name + "'", pad, y, w - 2 * pad, 20f, 13, FontStyle.Bold); y += 24f;
            RtText("Sub", "Union of ticked groups" + (groupChildren ? " + their child bones" : "")
                + (bones.Count > visRows ? " — scroll for all " + bones.Count : ""), pad, y, w - 2 * pad, 16f, 10, FontStyle.Italic); y += 20f;

            float half = (w - 2 * pad - 6f) / 2f;
            RtButton("All", "All", pad, y, half, rowH, () => { groupSel.Clear(); for (int i = 0; i < bones.Count; i++) groupSel.Add(bones[i]); SaveGroupScroll(); RebuildGroupPanel(); });
            RtButton("None", "None", pad + half + 6f, y, half, rowH, () => { groupSel.Clear(); SaveGroupScroll(); RebuildGroupPanel(); }); y += rowH + 4f;

            // scrollable bone list (mouse wheel / drag) — EVERY skinned bone, no cap
            GameObject vp = new GameObject("VP", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
            RectTransform vprt = vp.GetComponent<RectTransform>();
            vprt.SetParent(groupPanel.transform, false);
            vprt.anchorMin = vprt.anchorMax = new Vector2(0f, 1f); vprt.pivot = new Vector2(0f, 1f);
            vprt.sizeDelta = new Vector2(w - 2 * pad, vpH);
            vprt.anchoredPosition = new Vector2(pad, -y);
            vp.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.02f);

            GameObject ct = new GameObject("CT", typeof(RectTransform));
            RectTransform ctrt = ct.GetComponent<RectTransform>();
            ctrt.SetParent(vp.transform, false);
            ctrt.anchorMin = new Vector2(0f, 1f); ctrt.anchorMax = new Vector2(0f, 1f); ctrt.pivot = new Vector2(0f, 1f);
            ctrt.sizeDelta = new Vector2(w - 2 * pad, bones.Count * rowH);
            ctrt.anchoredPosition = Vector2.zero;

            ScrollRect sr = vp.AddComponent<ScrollRect>();
            sr.content = ctrt; sr.viewport = vprt;
            sr.horizontal = false; sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 24f;
            groupScroll = sr;

            rtParent = ct;
            for (int i = 0; i < bones.Count; i++)
            {
                string nm = bones[i];
                bool on = groupSel.Contains(nm);
                RtButton("G_" + nm, (on ? "[x] " : "[ ] ") + nm, 0f, i * rowH, w - 2 * pad, rowH,
                    () => { if (!groupSel.Remove(nm)) groupSel.Add(nm); SaveGroupScroll(); RebuildGroupPanel(); },
                    on ? new Color(0.65f, 1f, 0.65f, 1f) : Color.white);
            }
            rtParent = groupPanel;
            y += vpH + 6f;
            RtButton("Sel", "Select (" + groupSel.Count + " groups)", pad, y, half, 30f, OnGroupsApply, new Color(0.7f, 1f, 0.7f, 1f));
            RtButton("Cls", "Close", pad + half + 6f, y, half, 30f, () => { Destroy(groupPanel); groupPanel = null; });

            // restore where the user was scrolled before this click's rebuild
            if (bones.Count > visRows) sr.verticalNormalizedPosition = Mathf.Clamp01(groupScrollPos);
        }

        void RebuildGroupPanel() { if (groupPanel != null) { Destroy(groupPanel); groupPanel = null; } BuildGroupPanel(); }

        void OnGroupsApply()
        {
            if (selProxy == null || selRegion == null || groupSel.Count == 0) { SetStatus("tick at least one group"); return; }
            PushUndo(selRegion);
            lastGroupPick = new List<string>(groupSel);
            selRegion.srcBones = new List<string>(lastGroupPick);   // consumers filter cloth by these
            MeshProxy.SelectFromBonesOn(selProxy.smr, selRegion, lastGroupPick, groupThreshold, groupChildren);
            AfterWeightEdit(selRegion);
            Destroy(groupPanel); groupPanel = null;
            SetStatus("region '" + selRegion.name + "' = " + lastGroupPick.Count + " group(s)"
                + (groupChildren ? " + children" : "") + " (" + selRegion.vertIndex.Count + " verts)");
        }

        void OnClearWeights()
        {
            if (selRegion == null) return;
            PushUndo(selRegion);
            selRegion.vertIndex.Clear(); selRegion.weight.Clear();
            AfterWeightEdit(selRegion);
            SetStatus("weights cleared");
        }

        void OnBlurWeights()
        {
            if (selProxy == null || selRegion == null) { SetStatus("select a mesh + region first"); return; }
            if (selRegion.vertIndex.Count == 0) { SetStatus("nothing painted to blur"); return; }
            PushUndo(selRegion);
            selProxy.BlurRegion(selRegion, 0.6f);
            AfterWeightEdit(selRegion);
            SetStatus("blurred (" + selRegion.vertIndex.Count + " verts) — click again for more");
        }

        // ---------- colliders ----------
        void RefreshColliderList()
        {
            if (colliderDropdown == null) return;
            List<string> opts = new List<string>();
            if (selRegion != null)
                for (int c = 0; c < selRegion.colliders.Count; c++)
                {
                    SquishCollider sc = selRegion.colliders[c];
                    string label = string.IsNullOrEmpty(sc.mesh) ? sc.bone : "[mesh] " + sc.mesh;
                    opts.Add(label + " (r=" + sc.radius.ToString("0.00") + ")");
                }
            if (opts.Count == 0) opts.Add("(none)");
            suppress = true;
            colliderDropdown.ClearOptions(); colliderDropdown.AddOptions(opts);
            colliderDropdown.value = 0; ForceCaption(colliderDropdown);
            suppress = false;
        }

        void OnAddCollider()
        {
            if (selRegion == null) return;
            string bone = "";
            if (colBoneDropdown != null && colBoneDropdown.value > 0)
                bone = colBoneDropdown.options[colBoneDropdown.value].text;
            if (string.IsNullOrEmpty(bone) && colBoneInput != null) bone = colBoneInput.text.Trim();
            if (string.IsNullOrEmpty(bone)) { SetStatus("pick a bone in the dropdown first"); return; }
            float rad = 0.05f;
            if (colRadiusInput != null) float.TryParse(colRadiusInput.text, NumberStyles.Float, CultureInfo.InvariantCulture, out rad);
            SquishCollider c = new SquishCollider(); c.bone = bone; c.radius = Mathf.Max(0.005f, rad);
            selRegion.colliders.Add(c);
            RebindSel(); RefreshColliderList();
            SetStatus("collider '" + bone + "' added — Save to keep");
        }

        // Add the mesh currently shown in the Mesh dropdown as a WHOLE-MESH collider for
        // the selected region (its animated surface squishes the region; if it's the
        // region's own mesh, the painted verts are excluded so hands still poke chest).
        void OnAddMeshCollider()
        {
            if (selRegion == null) { SetStatus("select a region first"); return; }
            string nm = null;
            if (colMeshDropdown != null && colMeshDropdown.options.Count > 0)
                nm = colMeshDropdown.options[colMeshDropdown.value].text;
            if (nm == "(all meshes)") nm = "*";   // one collider entry covering EVERY skinned mesh
            else if (string.IsNullOrEmpty(nm) || nm.StartsWith("(")) { SetStatus("pick a collider mesh in the dropdown"); return; }
            float rad = 0.015f;
            if (colRadiusInput != null && colRadiusInput.text.Length > 0)
                float.TryParse(colRadiusInput.text, NumberStyles.Float, CultureInfo.InvariantCulture, out rad);
            SquishCollider c = new SquishCollider();
            c.mesh = nm; c.radius = Mathf.Clamp(rad, 0.004f, 0.1f);
            selRegion.colliders.Add(c);
            Rebind(); RefreshColliderList();
            SetStatus("mesh collider '" + nm + "' added (r=" + c.radius.ToString("0.000") + ") — Save to keep");
        }

        void OnRemoveCollider()
        {
            if (selRegion == null || colliderDropdown == null || selRegion.colliders.Count == 0) return;
            int i = Mathf.Clamp(colliderDropdown.value, 0, selRegion.colliders.Count - 1);
            selRegion.colliders.RemoveAt(i);
            RebindSel(); RefreshColliderList();
        }

        // ==================== native bone physics override ====================
        // Disables VRM SpringBone / DynamicBone / MagicaCloth / SPCR solvers that fight
        // the mesh squish — either globally or only where their bones skin painted
        // regions. Everything is restored when toggled off, rebound, or destroyed.
        readonly List<Behaviour> nativeDisabled = new List<Behaviour>();

        void RestoreNative()
        {
            for (int i = 0; i < nativeDisabled.Count; i++)
                if (nativeDisabled[i] != null) nativeDisabled[i].enabled = true;
            nativeDisabled.Clear();
        }

        void ApplyNativeOverride()
        {
            RestoreNative();
            if (config.settings == null || !config.settings.nativeDisable || boundAvatar == null) return;

            HashSet<Transform> regionBones = config.settings.nativeScoped ? CollectRegionBones() : null;
            Behaviour[] all = boundAvatar.GetComponentsInChildren<Behaviour>(true);
            int disabled = 0, left = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Behaviour b = all[i];
                if (b == null) continue;
                string fn = b.GetType().FullName.ToLowerInvariant();
                if (!(fn.Contains("springbone") || fn.Contains("dynamicbone")
                   || fn.Contains("magicacloth") || fn.Contains("spcrjointdynamics"))) continue;
                if (fn.Contains("collider")) continue;               // groups/colliders are harmless
                if (regionBones != null && !SolverOverlaps(b, regionBones)) { left++; continue; }
                if (b.enabled) { b.enabled = false; nativeDisabled.Add(b); disabled++; }
            }
            Debug.Log("[Squish] native physics override: " + disabled + " disabled"
                + (regionBones != null ? ", " + left + " left (out of scope)" : " (global)"));
            SetStatus("native bone physics: " + disabled + " solver(s) disabled");
        }

        // Bones that DOMINANTLY move painted flesh. "Any painted vert touches it > 0.1"
        // pulled in neighbouring chains too (breast paint carries residual weights to
        // belly/spine bones, so the belly's spring bones died when only breast bones
        // were selected). A bone now qualifies only when the PAINTED share of its total
        // skin influence is significant — its own flesh is mostly inside the region.
        HashSet<Transform> CollectRegionBones()
        {
            HashSet<Transform> set = new HashSet<Transform>();
            for (int p = 0; p < proxies.Count; p++)
            {
                MeshProxy px = proxies[p];
                if (!px.Alive || px.smr.sharedMesh == null) continue;
                BoneWeight[] bw = px.smr.sharedMesh.boneWeights;
                Transform[] bones = px.smr.bones;

                float[] paintOf = new float[bw.Length];
                for (int r = 0; r < px.cfg.regions.Count; r++)
                {
                    SuiteRegion reg = px.cfg.regions[r];
                    if (!reg.enabled) continue;
                    for (int v = 0; v < reg.vertIndex.Count; v++)
                    {
                        int vi = reg.vertIndex[v];
                        if (vi >= 0 && vi < paintOf.Length && reg.weight[v] > paintOf[vi]) paintOf[vi] = reg.weight[v];
                    }
                }

                float[] painted = new float[bones.Length];
                float[] total = new float[bones.Length];
                for (int v = 0; v < bw.Length; v++)
                {
                    BoneWeight w4 = bw[v];
                    float pw = paintOf[v];
                    if (w4.boneIndex0 < total.Length) { total[w4.boneIndex0] += w4.weight0; painted[w4.boneIndex0] += w4.weight0 * pw; }
                    if (w4.boneIndex1 < total.Length) { total[w4.boneIndex1] += w4.weight1; painted[w4.boneIndex1] += w4.weight1 * pw; }
                    if (w4.boneIndex2 < total.Length) { total[w4.boneIndex2] += w4.weight2; painted[w4.boneIndex2] += w4.weight2 * pw; }
                    if (w4.boneIndex3 < total.Length) { total[w4.boneIndex3] += w4.weight3; painted[w4.boneIndex3] += w4.weight3 * pw; }
                }
                string names = "";
                for (int b = 0; b < bones.Length; b++)
                {
                    if (bones[b] == null || painted[b] < 0.5f) continue;
                    if (painted[b] < 0.35f * Mathf.Max(0.0001f, total[b])) continue;   // mostly-unpainted bone: leave its physics alone
                    if (set.Add(bones[b]) && names.Length < 220)
                        names += (names.Length > 0 ? ", " : "") + bones[b].name;
                }
                if (names.Length > 0)
                    Debug.Log("[Squish] scoped native override bones (" + px.smr.name + "): " + names);
            }
            return set;
        }

        // does this solver drive any of the region bones? (reflects its root-bone list;
        // when we can't tell, we leave it running rather than break unrelated physics)
        static bool SolverOverlaps(Behaviour b, HashSet<Transform> regionBones)
        {
            List<Transform> roots = new List<Transform>();
            System.Type t = b.GetType();
            string[] fieldNames = { "RootBones", "m_Root", "m_Roots", "rootBones", "root" };
            for (int f = 0; f < fieldNames.Length; f++)
            {
                object val = null;
                System.Reflection.FieldInfo fi = t.GetField(fieldNames[f],
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (fi != null) val = fi.GetValue(b);
                else
                {
                    System.Reflection.PropertyInfo pi = t.GetProperty(fieldNames[f],
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (pi != null && pi.CanRead) { try { val = pi.GetValue(b, null); } catch { } }
                }
                if (val == null) continue;
                Transform single = val as Transform;
                if (single != null) roots.Add(single);
                System.Collections.IEnumerable many = val as System.Collections.IEnumerable;
                if (many != null)
                    foreach (object o in many) { Transform tr = o as Transform; if (tr != null) roots.Add(tr); }
            }
            if (roots.Count == 0) return false;   // undeterminable — leave it running
            foreach (Transform rb in regionBones)
                for (int r = 0; r < roots.Count; r++)
                    if (roots[r] != null && (rb == roots[r] || rb.IsChildOf(roots[r]))) return true;
            return false;
        }

        // ==================== apply-to-meshes panel ====================
        GameObject applyPanel;
        readonly HashSet<string> applySel = new HashSet<string>();
        int applyMethod = 2;           // 0 = by bone group, 1 = by surface transfer, 2 = auto (region's own bones)
        float applyRadius = 0.03f;     // surface-transfer projection radius (metres)
        float applyBoneShare = 0.10f;  // auto mode: min share of the region's total skin weight
        bool applyAllRegions = false;  // copy every region on this mesh, not just the selected one

        void OpenApplyPanel()
        {
            // destroy-toggle FIRST so the button can always close a stale panel
            if (applyPanel != null) { Destroy(applyPanel); applyPanel = null; return; }
            if (selMesh == null || selRegion == null) { SetStatus("select a mesh + region first"); return; }
            BuildApplyPanel();
        }

        void CloseApplyPanel() { if (applyPanel != null) { Destroy(applyPanel); applyPanel = null; } }

        void BuildApplyPanel()
        {
            if (window == null) return;
            // selection can die while the panel is open (mesh switch, region delete, reload)
            // and every option click rebuilds — bail safely instead of NRE-ing mid-build
            if (selMesh == null || selRegion == null)
            { CloseApplyPanel(); SetStatus("apply panel closed — select a mesh + region first"); return; }
            SkinnedMeshRenderer[] rends = boundAvatar != null
                ? boundAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true) : new SkinnedMeshRenderer[0];

            List<string> targets = new List<string>();
            for (int i = 0; i < rends.Length; i++)
                if (rends[i] != null && rends[i].name != selMesh.mesh) targets.Add(rends[i].name);
            // ticks remembered from another source mesh may no longer be valid targets
            // (the SOURCE itself must never be in here — src==dst would wipe its paint)
            applySel.RemoveWhere(delegate(string n) { return !targets.Contains(n); });

            float w = 340f, rowH = 24f, pad = 10f;
            float h = 284f + targets.Count * rowH;

            applyPanel = new GameObject("SquishApplyPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform prt = applyPanel.GetComponent<RectTransform>();
            prt.SetParent(window.transform.parent, false);
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(w, h);
            prt.anchoredPosition = new Vector2(240f, 0f);
            Image bg = applyPanel.GetComponent<Image>();
            bg.color = new Color(0.10f, 0.09f, 0.14f, 0.98f);
            applyPanel.AddComponent<SquishWindowDrag>();
            applyPanel.transform.SetAsLastSibling();
            rtParent = applyPanel;

            float y = pad;
            RtText("Title", "Copy " + (applyAllRegions ? "ALL regions" : "region '" + selRegion.name + "'") + " to other meshes",
                pad, y, w - 2 * pad, 20f, 13, FontStyle.Bold); y += 26f;

            RtButton("M2", (applyMethod == 2 ? "(o)" : "( )") + " Auto: same bones as the painted area", pad, y, w - 2 * pad, rowH,
                () => { applyMethod = 2; RebuildApply(); }); y += rowH + 2f;
            RtButton("M0", (applyMethod == 0 ? "(o)" : "( )") + " By bone group (current Group pick)", pad, y, w - 2 * pad, rowH,
                () => { applyMethod = 0; applyAllRegions = false; RebuildApply(); }); y += rowH + 2f;
            RtButton("M1", (applyMethod == 1 ? "(o)" : "( )") + " By surface transfer (project painted area)", pad, y, w - 2 * pad, rowH,
                () => { applyMethod = 1; RebuildApply(); }); y += rowH + 8f;

            RtButton("AllReg", applyMethod == 0
                ? "[ ] Copy ALL regions — n/a for the group-pick method"
                : (applyAllRegions ? "[x] " : "[ ] ") + "Copy ALL regions of this mesh (" + selMesh.regions.Count + ")",
                pad, y, w - 2 * pad, rowH,
                () =>
                {
                    if (applyMethod == 0) { SetStatus("one group pick can't fill several regions — use Auto or surface transfer"); return; }
                    applyAllRegions = !applyAllRegions; RebuildApply();
                }); y += rowH + 2f;

            RtText("LblRad", "Projection radius (surface):", pad, y + 4f, 170f, 18f, 11, FontStyle.Normal);
            RtButton("RadDn", "-", pad + 178f, y, 26f, rowH, () => { applyRadius = Mathf.Max(0.005f, applyRadius * 0.75f); RebuildApply(); });
            RtText("RadV", applyRadius.ToString("0.000") + " m", pad + 210f, y + 4f, 60f, 18f, 11, FontStyle.Bold);
            RtButton("RadUp", "+", pad + 274f, y, 26f, rowH, () => { applyRadius = Mathf.Min(0.3f, applyRadius * 1.3333f); RebuildApply(); });
            y += rowH + 2f;

            RtText("LblShare", "Auto-bone weight share ≥", pad, y + 4f, 170f, 18f, 11, FontStyle.Normal);
            RtButton("ShDn", "-", pad + 178f, y, 26f, rowH, () => { applyBoneShare = Mathf.Max(0.02f, applyBoneShare - 0.02f); RebuildApply(); });
            RtText("ShV", Mathf.RoundToInt(applyBoneShare * 100f) + " %", pad + 210f, y + 4f, 60f, 18f, 11, FontStyle.Bold);
            RtButton("ShUp", "+", pad + 274f, y, 26f, rowH, () => { applyBoneShare = Mathf.Min(0.6f, applyBoneShare + 0.02f); RebuildApply(); });
            y += rowH + 8f;

            float half = (w - 2 * pad - 6f) / 2f;
            RtButton("All", "All meshes", pad, y, half, rowH, () => { applySel.Clear(); for (int i = 0; i < targets.Count; i++) applySel.Add(targets[i]); RebuildApply(); });
            RtButton("None", "None", pad + half + 6f, y, half, rowH, () => { applySel.Clear(); RebuildApply(); }); y += rowH + 6f;

            for (int i = 0; i < targets.Count; i++)
            {
                string nm = targets[i];
                bool on = applySel.Contains(nm);
                RtButton("T_" + nm, (on ? "[x] " : "[ ] ") + nm, pad, y, w - 2 * pad, rowH,
                    () => { if (!applySel.Remove(nm)) applySel.Add(nm); RebuildApply(); },
                    on ? new Color(0.65f, 1f, 0.65f, 1f) : Color.white); y += rowH;
            }
            y += 8f;
            RtButton("Apply", "Apply (" + applySel.Count + ")", pad, y, half, 30f, OnApplyMulti, new Color(0.7f, 1f, 0.7f, 1f));
            RtButton("Cancel", "Close", pad + half + 6f, y, half, 30f, () => { Destroy(applyPanel); applyPanel = null; });
        }

        void RebuildApply() { if (applyPanel != null) { Destroy(applyPanel); applyPanel = null; } BuildApplyPanel(); }


        // ==================== preset + auto-load manager windows ====================
        GameObject presetMgrPanel, ruleMgrPanel;
        ScrollRect presetMgrScroll, ruleMgrScroll;
        float presetMgrScrollPos = 1f, ruleMgrScrollPos = 1f;
        Vector2 presetMgrPos = new Vector2(-300f, 0f), ruleMgrPos = new Vector2(330f, 0f);
        string pendingPresetDelete = "";     // waiting for its "Sure?" second click
        PresetRule pendingRuleDelete;
        InputField autoNameInput;

        // Auto-load for any model whose name INCLUDES what was typed — for when the file
        // name alone is not what should be matched on.
        void OnPresetBindName()
        {
            EnsurePresets();
            string name = SelectedPresetName();
            if (string.IsNullOrEmpty(name) || !presets.presets.ContainsKey(name)) { SetStatus("save/select a preset first"); return; }
            string typed = autoNameInput != null ? autoNameInput.text.Trim() : "";
            string key = PresetStore.Normalise(typed);
            if (!PresetStore.NameIsUsable(key)) { SetStatus("type at least 4 letters of the model's name"); return; }
            for (int i = presets.rules.Count - 1; i >= 0; i--)
                if (presets.rules[i] != null && presets.rules[i].fuzzy && presets.rules[i].key == key)
                    presets.rules.RemoveAt(i);
            PresetRule r = new PresetRule();
            r.key = key; r.preset = name; r.fuzzy = true; r.note = "typed: " + typed;
            presets.rules.Add(r);
            PresetStore.Save(presetPath, presets);
            RebuildRuleMgr();
            SetStatus("'" + name + "' will auto-load for models whose name includes '" + typed + "'");
        }

        string CurrentModelLabel()
        {
            if (boundAvatar == null) return "(no model loaded)";
            string fz, ex, how;
            PresetStore.Identify(boundAvatar, out fz, out ex, out how);
            return ex + (fz.Length > 0 ? "   (matches text: " + fz + ")" : "");
        }

        GameObject MakeMgrPanel(string goName, float w, float h, Vector2 pos)
        {
            GameObject p = new GameObject(goName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform prt = p.GetComponent<RectTransform>();
            prt.SetParent(window.transform.parent, false);
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(w, h);
            prt.anchoredPosition = pos;
            p.GetComponent<Image>().color = new Color(0.10f, 0.09f, 0.14f, 0.98f);
            p.AddComponent<SquishWindowDrag>();
            p.transform.SetAsLastSibling();
            return p;
        }

        // a scrolling list inside a manager window; rows are added to the returned object
        GameObject MakeMgrList(GameObject panel, float x, float y, float w, float vpH, float contentH, out ScrollRect sr)
        {
            GameObject vp = new GameObject("VP", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
            RectTransform vprt = vp.GetComponent<RectTransform>();
            vprt.SetParent(panel.transform, false);
            vprt.anchorMin = vprt.anchorMax = new Vector2(0f, 1f); vprt.pivot = new Vector2(0f, 1f);
            vprt.sizeDelta = new Vector2(w, vpH);
            vprt.anchoredPosition = new Vector2(x, -y);
            vp.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.02f);
            GameObject ct = new GameObject("CT", typeof(RectTransform));
            RectTransform ctrt = ct.GetComponent<RectTransform>();
            ctrt.SetParent(vp.transform, false);
            ctrt.anchorMin = ctrt.anchorMax = new Vector2(0f, 1f); ctrt.pivot = new Vector2(0f, 1f);
            ctrt.sizeDelta = new Vector2(w, contentH);
            ctrt.anchoredPosition = Vector2.zero;
            sr = vp.AddComponent<ScrollRect>();
            sr.content = ctrt; sr.viewport = vprt;
            sr.horizontal = false; sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 24f;
            return ct;
        }

        // a single-line text box; onCommit fires on Enter or when it loses focus
        InputField RtInput(string name, string text, float x, float y, float w, float h,
                           UnityEngine.Events.UnityAction<string> onCommit)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(rtParent.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(x, -y);
            GameObject tg = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            Text t = tg.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = 12; t.color = Color.white; t.alignment = TextAnchor.MiddleLeft; t.supportRichText = false;
            RectTransform trt = tg.GetComponent<RectTransform>();
            trt.SetParent(go.transform, false);
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(6f, 1f); trt.offsetMax = new Vector2(-6f, -1f);
            // added last, so the text it drives already exists when it wakes up
            InputField f = go.AddComponent<InputField>();
            f.textComponent = t;
            f.targetGraphic = go.GetComponent<Image>();
            f.lineType = InputField.LineType.SingleLine;
            f.text = text ?? "";
            // Only Enter commits. onEndEdit also fires when the box merely loses focus, and
            // committing then rebuilt the window in the middle of the very click that moved
            // the focus — so that click was lost. Clicking away now just puts the text back.
            string start = f.text;
            InputField self = f;
            if (onCommit != null) f.onEndEdit.AddListener(delegate (string v)
            {
                bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
                if (!enter || (v ?? "") == start) { if (self != null) self.text = start; return; }
                onCommit(v);
            });
            return f;
        }

        // ---------------- presets window ----------------
        void OpenPresetMgr()
        {
            if (presetMgrPanel != null) { ClosePresetMgr(); return; }
            BuildPresetMgr();
        }

        void ClosePresetMgr()
        {
            if (presetMgrPanel == null) return;
            presetMgrPos = presetMgrPanel.GetComponent<RectTransform>().anchoredPosition;
            Destroy(presetMgrPanel); presetMgrPanel = null;
            pendingPresetDelete = "";
        }

        void RebuildPresetMgr()
        {
            if (presetMgrPanel == null) return;
            if (presetMgrScroll != null) presetMgrScrollPos = presetMgrScroll.verticalNormalizedPosition;
            presetMgrPos = presetMgrPanel.GetComponent<RectTransform>().anchoredPosition;
            Destroy(presetMgrPanel); presetMgrPanel = null;
            BuildPresetMgr();
        }

        void BuildPresetMgr()
        {
            if (window == null) return;
            EnsurePresets();
            List<string> names = PresetNames();
            float w = 380f, rowH = 26f, step = 30f, pad = 10f;
            int visRows = Mathf.Clamp(names.Count, 1, 14);
            float vpH = visRows * step;
            float h = 66f + vpH + 48f;
            presetMgrPanel = MakeMgrPanel("SoftBodyPresetMgr", w, h, presetMgrPos);
            rtParent = presetMgrPanel;

            float y = pad;
            RtText("Title", "Presets", pad, y, w - 2 * pad, 20f, 13, FontStyle.Bold); y += 22f;
            RtText("Sub", "Edit a name and press Enter to rename it. Delete asks twice.",
                pad, y, w - 2 * pad, 16f, 10, FontStyle.Italic); y += 24f;

            ScrollRect sr;
            GameObject ct = MakeMgrList(presetMgrPanel, pad, y, w - 2 * pad, vpH, Mathf.Max(1, names.Count) * step, out sr);
            presetMgrScroll = sr;
            rtParent = ct;
            if (names.Count == 0) RtText("None", "no presets saved yet", 4f, 0f, w - 2 * pad, rowH, 11, FontStyle.Italic);
            float bw = 58f, gap = 4f, nameW = (w - 2 * pad) - 2 * (bw + gap) - 4f;
            for (int i = 0; i < names.Count; i++)
            {
                string nm = names[i];          // one fresh variable per row, for the lambdas
                float ry = i * step;
                bool sure = pendingPresetDelete == nm;
                RtInput("N_" + i, nm, 0f, ry, nameW, rowH, delegate (string v) { RenamePreset(nm, v); });
                RtButton("L_" + i, "Load", nameW + gap, ry, bw, rowH, delegate { ApplyPresetNamed(nm); });
                RtButton("D_" + i, sure ? "Sure?" : "Delete", nameW + gap + bw + gap, ry, bw, rowH,
                    delegate
                    {
                        if (pendingPresetDelete == nm) DeletePresetNamed(nm);
                        else { pendingPresetDelete = nm; RebuildPresetMgr(); }
                    },
                    sure ? new Color(1f, 0.55f, 0.55f, 1f) : Color.white);
            }
            rtParent = presetMgrPanel;
            y += vpH + 10f;
            RtButton("Cls", "Close", pad, y, w - 2 * pad, 28f, ClosePresetMgr);
            if (names.Count > visRows) sr.verticalNormalizedPosition = Mathf.Clamp01(presetMgrScrollPos);
        }

        void ApplyPresetNamed(string nm)
        {
            SquishConfig src;
            if (presets != null && presets.presets.TryGetValue(nm, out src))
            {
                ApplyPreset(src, nm);
                RefreshPresetList(nm);   // so the main Save/Delete now act on this one
            }
        }

        void RenamePreset(string oldName, string newName)
        {
            string sel = SelectedPresetName();   // read before the list changes under it
            newName = (newName ?? "").Trim();
            if (newName.Length == 0 || newName == oldName) return;
            if (presets.presets.ContainsKey(newName))
            { SetStatus("a preset called '" + newName + "' already exists"); RebuildPresetMgr(); return; }
            SquishConfig p;
            if (!presets.presets.TryGetValue(oldName, out p)) return;
            presets.presets.Remove(oldName);
            presets.presets[newName] = p;
            for (int i = 0; i < presets.rules.Count; i++)   // auto-load rules follow the rename
                if (presets.rules[i] != null && presets.rules[i].preset == oldName) presets.rules[i].preset = newName;
            PresetStore.Save(presetPath, presets);
            RefreshPresetList(sel == oldName ? newName : sel);
            SetStatus("renamed '" + oldName + "' to '" + newName + "'");
        }

        void DeletePresetNamed(string nm)
        {
            pendingPresetDelete = "";
            string sel = SelectedPresetName();   // read before the list changes under it
            if (!presets.presets.Remove(nm)) { RebuildPresetMgr(); return; }
            int gone = 0;
            for (int i = presets.rules.Count - 1; i >= 0; i--)
                if (presets.rules[i] != null && presets.rules[i].preset == nm) { presets.rules.RemoveAt(i); gone++; }
            PresetStore.Save(presetPath, presets);
            RefreshPresetList(sel);
            SetStatus("preset '" + nm + "' deleted" + (gone > 0 ? " (and " + gone + " auto-load rule" + (gone > 1 ? "s" : "") + ")" : ""));
        }

        // ---------------- auto-load window ----------------
        void OpenRuleMgr()
        {
            if (ruleMgrPanel != null) { CloseRuleMgr(); return; }
            BuildRuleMgr();
        }

        void CloseRuleMgr()
        {
            if (ruleMgrPanel == null) return;
            ruleMgrPos = ruleMgrPanel.GetComponent<RectTransform>().anchoredPosition;
            Destroy(ruleMgrPanel); ruleMgrPanel = null;
            pendingRuleDelete = null;
        }

        void RebuildRuleMgr()
        {
            if (ruleMgrPanel == null) return;
            if (ruleMgrScroll != null) ruleMgrScrollPos = ruleMgrScroll.verticalNormalizedPosition;
            ruleMgrPos = ruleMgrPanel.GetComponent<RectTransform>().anchoredPosition;
            Destroy(ruleMgrPanel); ruleMgrPanel = null;
            BuildRuleMgr();
        }

        void BuildRuleMgr()
        {
            if (window == null) return;
            EnsurePresets();
            List<string> pnames = PresetNames();
            List<PresetRule> rules = presets.rules;
            float w = 480f, rowH = 26f, step = 46f, pad = 10f;
            int visRows = Mathf.Clamp(rules.Count, 1, 10);
            float vpH = visRows * step;
            float h = 90f + vpH + 48f;
            ruleMgrPanel = MakeMgrPanel("SoftBodyRuleMgr", w, h, ruleMgrPos);
            rtParent = ruleMgrPanel;

            float y = pad;
            RtText("Title", "Auto-load rules", pad, y, w - 2 * pad, 20f, 13, FontStyle.Bold); y += 22f;
            RtText("Sub", "A preset loads when the model's name includes the text. Edit the text and press Enter.",
                pad, y, w - 2 * pad, 16f, 10, FontStyle.Italic); y += 18f;
            RtText("Cur", "This model: " + CurrentModelLabel(), pad, y, w - 2 * pad, 16f, 10, FontStyle.Italic); y += 26f;

            ScrollRect sr;
            GameObject ct = MakeMgrList(ruleMgrPanel, pad, y, w - 2 * pad, vpH, Mathf.Max(1, rules.Count) * step, out sr);
            ruleMgrScroll = sr;
            rtParent = ct;
            if (rules.Count == 0) RtText("None", "no auto-load rules yet", 4f, 0f, w - 2 * pad, rowH, 11, FontStyle.Italic);
            float cw = w - 2 * pad, gap = 4f;
            float onW = 34f, delW = 58f, keyW = (cw - onW - delW - 3 * gap) * 0.55f;
            float preW = cw - onW - delW - keyW - 3 * gap;
            for (int i = 0; i < rules.Count; i++)
            {
                PresetRule r = rules[i];       // one fresh variable per row, for the lambdas
                if (r == null) continue;
                float ry = i * step;
                bool inert = r.fuzzy && !PresetStore.NameIsUsable(r.key);
                bool missing = !presets.presets.ContainsKey(r.preset);
                bool sure = ReferenceEquals(pendingRuleDelete, r);
                float x = 0f;
                RtButton("E_" + i, r.enabled ? "[x]" : "[ ]", x, ry, onW, rowH,
                    delegate { if (!RuleLive(r)) return; r.enabled = !r.enabled; SaveRules(); },
                    r.enabled ? new Color(0.65f, 1f, 0.65f, 1f) : Color.white);
                x += onW + gap;
                RtInput("K_" + i, r.key, x, ry, keyW, rowH, delegate (string v) { EditRuleKey(r, v); });
                x += keyW + gap;
                RtButton("P_" + i, (missing ? "(missing) " : "") + r.preset, x, ry, preW, rowH,
                    delegate { CycleRulePreset(r, pnames); },
                    missing ? new Color(1f, 0.6f, 0.6f, 1f) : Color.white);
                x += preW + gap;
                RtButton("D_" + i, sure ? "Sure?" : "Delete", x, ry, delW, rowH,
                    delegate
                    {
                        if (!RuleLive(r)) return;
                        if (ReferenceEquals(pendingRuleDelete, r)) { presets.rules.Remove(r); pendingRuleDelete = null; SaveRules(); }
                        else { pendingRuleDelete = r; RebuildRuleMgr(); }
                    },
                    sure ? new Color(1f, 0.55f, 0.55f, 1f) : Color.white);
                string note = inert ? "never matches - VNyan's placeholder name. Edit it or delete it."
                            : (r.fuzzy ? "name includes the text" : "this exact model only");
                if (!inert && !string.IsNullOrEmpty(r.note)) note += "   -   " + r.note;
                RtText("Note_" + i, note, onW + gap, ry + rowH + 1f, cw - onW - gap, 14f, 9, FontStyle.Italic);
            }
            rtParent = ruleMgrPanel;
            y += vpH + 10f;
            RtButton("Cls", "Close", pad, y, w - 2 * pad, 28f, CloseRuleMgr);
            if (rules.Count > visRows) sr.verticalNormalizedPosition = Mathf.Clamp01(ruleMgrScrollPos);
        }

        void SaveRules()
        {
            PresetStore.Save(presetPath, presets);
            RebuildRuleMgr();
        }

        // editing the text always makes it a "name includes" rule — that is what it means
        bool RuleLive(PresetRule r)
        {
            if (presets != null && presets.rules.Contains(r)) return true;
            RebuildRuleMgr();
            return false;
        }

        void EditRuleKey(PresetRule r, string typed)
        {
            if (!RuleLive(r)) return;
            string key = PresetStore.Normalise(typed);
            if (key == r.key && r.fuzzy) return;
            if (!PresetStore.NameIsUsable(key))
            { SetStatus("needs at least 4 letters of the model's name"); RebuildRuleMgr(); return; }
            r.key = key; r.fuzzy = true; r.note = "typed: " + (typed ?? "").Trim();
            SaveRules();
            SetStatus("rule now loads '" + r.preset + "' for names including '" + key + "'");
        }

        void CycleRulePreset(PresetRule r, List<string> names)
        {
            if (!RuleLive(r)) return;
            if (names == null || names.Count == 0) return;
            int at = names.IndexOf(r.preset);
            r.preset = names[(at + 1) % names.Count];
            SaveRules();
        }

        // stage on/off ticks are read once when the window is built; a preset load or a
        // reload changes the stages underneath them
        void RefreshSectionTicks()
        {
            suppress = true;
            for (int i = 0; i < sections.Count; i++)
            {
                UiSection sec = sections[i];
                if (sec.onBox != null) sec.onBox.isOn = !SectionIsZero(sec);
            }
            suppress = false;
        }

        GameObject rtParent;   // which runtime panel RtText/RtButton attach to

        void RtText(string name, string text, float x, float y, float w, float h, int size, FontStyle style)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            Text t = go.GetComponent<Text>();
            t.text = text; t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = size; t.fontStyle = style; t.color = Color.white;
            t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(rtParent.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(x, -y);
        }

        void RtButton(string name, string label, float x, float y, float w, float h,
                      UnityEngine.Events.UnityAction act, Color? txtCol = null)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.06f);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(rtParent.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(x, -y);
            go.GetComponent<Button>().onClick.AddListener(act);
            GameObject tg = new GameObject("Txt", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            Text t = tg.GetComponent<Text>();
            t.text = label; t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = 12; t.color = txtCol ?? Color.white; t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            RectTransform trt = tg.GetComponent<RectTransform>();
            trt.SetParent(go.transform, false);
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(8f, 0f); trt.offsetMax = Vector2.zero;
        }

        // Copies the tuned numbers of ALL THREE stages, plus the region's own settings.
        // Reflection rather than a field list, so a slider added to one stage later cannot
        // quietly stop being copied.
        static void CopyStageParams(StageRegion src, StageRegion dst)
        {
            if (src == null || dst == null) return;
            System.Reflection.FieldInfo[] fs = src.GetType().GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            for (int i = 0; i < fs.Length; i++)
            {
                if (fs[i].IsDefined(typeof(Newtonsoft.Json.JsonIgnoreAttribute), true)) continue;
                fs[i].SetValue(dst, fs[i].GetValue(src));
            }
        }

        static void CopyRegionParams(SuiteRegion src, SuiteRegion dst)
        {
            if (src == null || dst == null || src == dst) return;   // src==dst would wipe colliders mid-copy
            dst.enabled = src.enabled;
            dst.gravityPoseOnly = src.gravityPoseOnly;
            dst.refBone = src.refBone;
            CopyStageParams(src.wobble, dst.wobble);
            CopyStageParams(src.jello, dst.jello);
            CopyStageParams(src.squish, dst.squish);
            dst.colliders = new List<SquishCollider>();
            for (int i = 0; i < src.colliders.Count; i++)
            {
                SquishCollider c = new SquishCollider();
                c.bone = src.colliders[i].bone; c.mesh = src.colliders[i].mesh;
                c.radius = src.colliders[i].radius;
                c.length = src.colliders[i].length; c.enabled = src.colliders[i].enabled;
                dst.colliders.Add(c);
            }
        }

        void OnApplyMulti()
        {
            if (applySel.Count == 0) { SetStatus("tick at least one target mesh"); return; }
            if (selProxy == null || selMesh == null || selRegion == null) return;

            List<SuiteRegion> srcs = new List<SuiteRegion>();
            if (applyAllRegions) { for (int r = 0; r < selMesh.regions.Count; r++) if (selMesh.regions[r] != null) srcs.Add(selMesh.regions[r]); }
            else srcs.Add(selRegion);
            if (srcs.Count == 0) { SetStatus("no regions on this mesh"); return; }

            if (applyMethod == 0 && lastGroupPick.Count == 0)
            { SetStatus("bone-group method: pick vertex groups on the source region first"); return; }

            // per-source-region precompute: world samples (projection) / derived bones (auto)
            List<List<Vector4>> samplesPer = new List<List<Vector4>>();
            List<List<string>> bonesPer = new List<List<string>>();
            string skipped = "";
            for (int i = 0; i < srcs.Count; i++)
            {
                samplesPer.Add(applyMethod == 1 ? selProxy.RegionWorldSamples(srcs[i]) : null);
                bonesPer.Add(applyMethod == 2 ? selProxy.DeriveRegionBones(srcs[i], applyBoneShare) : null);
                bool empty = (applyMethod == 1 && samplesPer[i].Count == 0) ||
                             (applyMethod == 2 && bonesPer[i].Count == 0);
                if (empty) skipped += (skipped.Length > 0 ? ", " : "") + srcs[i].name;
            }
            if (skipped.Length > 0)
            {
                int live = 0;
                for (int i = 0; i < srcs.Count; i++)
                    if (!((applyMethod == 1 && samplesPer[i].Count == 0) || (applyMethod == 2 && bonesPer[i].Count == 0))) live++;
                if (live == 0) { SetStatus("no painted weights to copy from (" + skipped + ")"); return; }
            }

            SkinnedMeshRenderer[] rends = boundAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            string report = "";
            bool anyApplied = false;
            foreach (string nm in applySel)
            {
                if (nm == selMesh.mesh) continue;   // never let the source be its own target
                SkinnedMeshRenderer target = null;
                for (int r = 0; r < rends.Length; r++) if (rends[r] != null && rends[r].name == nm) { target = rends[r]; break; }
                if (target == null) continue;

                // existing config entry only — created lazily on the first real commit so a
                // fully-failed apply doesn't enable a do-nothing proxy on this mesh
                SquishMesh sm = null;
                for (int m = 0; m < config.meshes.Count; m++) if (config.meshes[m].mesh == nm) sm = config.meshes[m];

                int meshTotal = 0;
                HashSet<SuiteRegion> claimed = new HashSet<SuiteRegion>();   // duplicate src names stay 1:1
                for (int s = 0; s < srcs.Count; s++)
                {
                    SuiteRegion src = srcs[s];
                    if (applyMethod == 1 && samplesPer[s].Count == 0) continue;
                    if (applyMethod == 2 && bonesPer[s].Count == 0) continue;

                    // trial-transfer into a scratch region: existing paint on the target is
                    // only overwritten when the method actually found vertices there
                    SuiteRegion trial = new SuiteRegion();
                    if (applyMethod == 0)
                        MeshProxy.SelectFromBonesOn(target, trial, lastGroupPick, groupThreshold, groupChildren);
                    else if (applyMethod == 2)
                        MeshProxy.SelectFromBonesOn(target, trial, bonesPer[s], groupThreshold, false);
                    else
                        MeshProxy.TransferWeights(target, trial, samplesPer[s], applyRadius);
                    if (trial.vertIndex.Count == 0) continue;

                    if (sm == null) { sm = new SquishMesh(); sm.mesh = nm; config.meshes.Add(sm); }
                    SuiteRegion reg = null;
                    for (int r = 0; r < sm.regions.Count; r++)
                        if (sm.regions[r].name == src.name && !claimed.Contains(sm.regions[r])) { reg = sm.regions[r]; break; }
                    if (reg == null) { reg = new SuiteRegion(); reg.name = src.name; sm.regions.Add(reg); }
                    claimed.Add(reg);

                    PushUndo(reg);
                    CopyRegionParams(src, reg);
                    reg.vertIndex = trial.vertIndex;
                    reg.weight = trial.weight;
                    reg.srcBones = applyMethod == 0 ? new List<string>(lastGroupPick)
                                 : applyMethod == 2 ? new List<string>(bonesPer[s])
                                 : new List<string>(src.srcBones);
                    meshTotal += reg.vertIndex.Count;
                }
                if (meshTotal > 0 && sm != null) { sm.enabled = true; anyApplied = true; }
                report += (report.Length > 0 ? ", " : "") + nm + ":" + meshTotal;
            }

            Destroy(applyPanel); applyPanel = null;
            Rebind(); RefreshMeshList();
            // auto-save like every other weight edit — this is what triggers the
            // Wobble/Jello region mirror (2 s mtime watch) and their cage rebuilds
            if (anyApplied) SaveConfig();
            string msg = "applied " + (applyAllRegions ? srcs.Count + " region(s)" : "'" + selRegion.name + "'")
                + " → " + report + " verts";
            if (skipped.Length > 0) msg += " (skipped empty: " + skipped + ")";
            if (applyMethod == 2 && bonesPer.Count > 0 && bonesPer[0] != null && bonesPer[0].Count > 0)
            {
                string bl = "";
                for (int i = 0; i < bonesPer[0].Count && i < 4; i++) bl += (i > 0 ? ", " : "") + bonesPer[0][i];
                if (bonesPer[0].Count > 4) bl += " +" + (bonesPer[0].Count - 4);
                msg += " [bones: " + bl + "]";
            }
            SetStatus(msg + (anyApplied ? " — saved (mirrors to Wobble/Jello in ~2 s)" : ""));
        }

        // ==================== config IO ====================
        void LoadConfig()
        {
            configPath = savePath;
            try
            {
                bool migrated;
                // First run reads the three old files and writes the merged one. They are
                // left exactly as they are, so going back is always possible.
                config = ConfigMigrate.RunIfNeeded(Application.persistentDataPath, out migrated);
                if (config == null) config = new SquishConfig();
                if (config.settings == null) config.settings = new SquishSettings();
                if (config.meshes == null) config.meshes = new List<SquishMesh>();
                config.Sync();
                if (migrated)
                    Debug.Log("[SoftBody] imported settings from the three old studios — see "
                              + "softbodysuite.migration.log");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SoftBody] config load failed: " + e.Message);
                config = new SquishConfig();
            }
            // reloading replaces every region object — snapshots referencing the old ones are dead
            undoStack.Clear(); redoStack.Clear();
            selMesh = null; selRegion = null;
        }

        void SaveConfig()
        {
            try
            {
                config.Sync();
                File.WriteAllText(savePath, JsonConvert.SerializeObject(config, Formatting.Indented));
                configPath = savePath;
                SetStatus("saved to " + savePath);
                Debug.Log("[SoftBody] Saved to " + savePath);
            }
            catch (Exception e) { SetStatus("save failed: " + e.Message); }
        }
    }
}
