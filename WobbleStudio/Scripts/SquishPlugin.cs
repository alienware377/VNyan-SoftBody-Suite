using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WobbleStudio
{
    // Wobble Studio — mesh-level soft-body deformation with live weight painting.
    // Runs LAST in LateUpdate (order 20000) so the bake sees the final pose of the
    // frame: tracking, Pose Studio, bone physics — everything.
    [DefaultExecutionOrder(19000)]
    public class SquishPlugin : MonoBehaviour, VNyanInterface.IButtonClickedHandler
    {
        const string BUTTON_NAME = "SB3 Wobble Studio";
        const string CONFIG_FILE = "wobblestudio.json";

        public GameObject windowPrefab;

        GameObject window;
        Text statusLabel;
        readonly Dictionary<string, Slider> sliders = new Dictionary<string, Slider>();
        readonly Dictionary<string, InputField> valueInputs = new Dictionary<string, InputField>();
        Dropdown meshDropdown, regionDropdown, groupDropdown, colliderDropdown, refBoneDropdown, colMeshDropdown;
        InputField regionNameInput, refBoneInput, colBoneInput, colRadiusInput;
        List<string> refBoneOptions = new List<string>();
        Toggle enabledToggle, regionEnabledToggle, paintToggle, overlayToggle, gravityPoseToggle;
        bool suppress;

        SquishConfig config = new SquishConfig();
        string savePath, configPath;

        GameObject boundAvatar;
        Animator boundAnimator;
        readonly List<MeshProxy> proxies = new List<MeshProxy>();

        MeshProxy selProxy;
        SquishMesh selMesh;
        SquishRegion selRegion;

        bool paintMode;
        int paintBrushMode;          // 0 add, 1 subtract
        float brushRadius = 0.05f, brushStrength = 0.35f;
        bool groupChildren = true;   // vertex-group select includes descendant bones
        bool stroking;               // LMB stroke in progress (one undo step per stroke)

        // ----- undo/redo (weight edits: strokes, group select, blur, clear) -----
        class WeightSnapshot
        {
            public SquishRegion region;
            public int[] idx; public float[] w;
            public static WeightSnapshot Of(SquishRegion r)
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

        void PushUndo(SquishRegion r)
        {
            if (r == null) return;
            lastWeightEditT = Time.realtimeSinceStartup;
            undoStack.Add(WeightSnapshot.Of(r));
            if (undoStack.Count > UNDO_CAP) undoStack.RemoveAt(0);
            redoStack.Clear();
        }

        // a snapshot is only valid while its region object is still in the live config
        bool RegionLive(SquishRegion r)
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
            public SquishRegion region;
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

        void AfterWeightEdit(SquishRegion r)
        {
            if (selProxy != null)
            {
                selProxy.RebuildSims(boundAvatar, boundAnimator);
                if (selProxy.overlayOn) selProxy.RefreshOverlayColors(selRegion);
            }
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

            // live mirror of Squish Studio's regions (file watch, cheap mtime check)
            ownerCheckT += Time.deltaTime;
            if (ownerCheckT > 2f) { ownerCheckT = 0f; SyncRegionsFromOwner(false); }

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
            catch (Exception e) { Debug.LogWarning("[Wobble] registerPluginButton failed: " + e.Message); }
            savePath = Path.Combine(Application.persistentDataPath, CONFIG_FILE);
            LoadConfig();
            SyncRegionsFromOwner(true);
            SetupWindow();
            Debug.Log("[Wobble] initialized. Config: " + configPath);
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
            Rebind();
            Debug.Log("[Wobble] bound to avatar '" + av.name + "'");
        }

        void DetachAll()
        {
            for (int i = 0; i < proxies.Count; i++) proxies[i].Detach();
            proxies.Clear();
            selProxy = null;
        }

        void Rebind()
        {
            DetachAll();
            if (boundAvatar == null || config.meshes == null) return;
            SkinnedMeshRenderer[] rends = boundAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int m = 0; m < config.meshes.Count; m++)
            {
                SquishMesh sm = config.meshes[m];
                if (sm == null || !sm.enabled) continue;
                SkinnedMeshRenderer target = null;
                for (int r = 0; r < rends.Length; r++)
                    if (rends[r] != null && rends[r].name == sm.mesh) { target = rends[r]; break; }
                if (target == null) { Debug.LogWarning("[Wobble] mesh '" + sm.mesh + "' not found"); continue; }
                MeshProxy p = new MeshProxy();
                p.Attach(target, sm, boundAvatar, boundAnimator);
                proxies.Add(p);
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

            MeshProxy.halfRate = config.settings.halfRate;
            MeshProxy.asyncSim = config.settings.asyncSim;
            MeshProxy.halfRateLerp = config.settings.halfRateLerp;
            float dt = Mathf.Min(Time.deltaTime, config.settings.maxDeltaTime);
            if (dt <= 0f) return;
            bool anyDead = false;
            for (int i = 0; i < proxies.Count; i++)
            {
                proxies[i].Frame(dt, Mathf.Clamp(config.settings.substeps, 1, 8), Vector3.down, true);
                if (!proxies[i].Alive) anyDead = true;
            }
            if (anyDead) { Rebind(); return; }   // a proxy self-detached (mesh swap) — reattach cleanly

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
            catch (Exception e) { Debug.LogWarning("[Wobble] instantiateUIPrefab failed: " + e.Message); window = null; }
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
            FixDropdown(meshDropdown); FixDropdown(regionDropdown); FixDropdown(groupDropdown);
            FixDropdown(colliderDropdown); FixDropdown(colMeshDropdown); FixDropdown(refBoneDropdown);
            AddTooltips(new Dictionary<string, string>
            {
                { "Toggle_Enabled", "Master switch. Off fully detaches the plugin (original mesh shows again). State is saved." },
                { "Note_NeedsSquish", "Squish Studio owns region creation & painting. Install and use it to define regions; this plugin mirrors them automatically (checked every 2 s)." },
                { "Dropdown_Mesh", "Which skinned mesh to wobble. Regions come from Squish Studio." },
                { "Button_EnableMesh", "Start simulating this mesh (creates its proxy copy)." },
                { "Button_DisableMesh", "Stop simulating this mesh and restore the original." },
                { "Dropdown_Region", "Pick which mirrored region's wobble settings to edit. Create/paint regions in Squish Studio." },
                { "Toggle_RegionEnabled", "Turn this region's wobble on/off (mirrors Squish Studio's enabled state on sync)." },
                { "Slider_jiggle", "Overall wobble strength - how much the flesh lags behind bone movement." },
                { "Slider_stiffness", "Spring pull back to the rest shape. Higher = tighter, faster return." },
                { "Slider_damping", "How quickly oscillation dies out. Low = bouncy, high = heavy." },
                { "Slider_bounce", "Inertia drag - flesh resists sudden motion changes." },
                { "Slider_maxoff", "Hard cap on deformation distance in meters." },
                { "Slider_gravity", "Constant downward sag." },
                { "Toggle_GravityPose", "Apply gravity only when the reference bone leaves its rest pose (e.g. leaning forward)." },
                { "Dropdown_RefBone", "Bone whose rotation gates the pose-only gravity." },
                { "Slider_cloth", "Traveling surface ripples excited by movement, like fabric." },
                { "Slider_clothsize", "Wavelength of the cloth ripples." },
                { "Slider_jello", "Standing-wave wobble of the whole region, like jello." },
                { "Slider_jellosize", "Jello wave scale - bigger = slower, broader wobble." },
                { "Slider_liquid", "Fluid-like ripples that radiate from motion impacts." },
                { "Slider_liquidsize", "Wavelength of the liquid ripples." },
                { "Slider_wavespeed", "Propagation speed multiplier for all wave modes." },
                { "Slider_sway", "Slow pendulum sway of the whole region." },
                { "Slider_twistj", "Rotational wobble around the region's axis." },
                { "Slider_pulse", "Rhythmic in/out breathing motion." },
                { "Slider_pulserate", "Breathing speed." },
                { "Slider_stretch", "Velocity-based squash & stretch along the movement direction." },
                { "Slider_turb", "Random organic surface noise." },
                { "Slider_turbsize", "Scale of the turbulence noise." },
                { "Slider_cellulite", "Static dimpled surface relief." },
                { "Slider_cellsize", "Dimple size." },
                { "Toggle_NativeOff", "Disable VNyan/native spring & dynamic bones while wobbling - they fight the mesh simulation." },
                { "Toggle_HalfRate", "HALF-RATE physics: compute the simulation every 2nd frame (with doubled timestep) and hold the result between — near-halves the physics cost on slower PCs. Skinning/animation still updates every frame, so it is barely visible." },
                { "Toggle_HalfRateLerp", "HALF-RATE + SMOOTH: like half-rate physics, but held frames show a blend between the last two physics ticks instead of a repeat — smoother motion at the same cost, with half a tick of extra latency. Mutually exclusive with the other rate options." },
                { "Slider_jellorand", "Jell-o RANDOMIZER: slowly wanders the wobble's centre around the region (smooth noise, mostly horizontal) so the jell-o motion looks organic instead of a fixed standing wave. Higher = wider, faster wandering." },
                { "Toggle_NativeScoped", "Only disable native physics on bones driving mirrored regions." },
                { "Button_Reload", "Re-read the saved config from disk and re-bind." },
                { "Button_Save", "Write everything to wobblestudio.json." },
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
            WireButton("Button_BlurWeights", OnBlurWeights);
            WireButton("Button_ApplyMulti", OpenApplyPanel);
            {
                Toggle tHR = FindControl<Toggle>("Toggle_HalfRate");
                Toggle tHL = FindControl<Toggle>("Toggle_HalfRateLerp");
                if (tHR != null) tHR.onValueChanged.AddListener(v =>
                {
                    if (suppress) return;
                    config.settings.halfRate = v;
                    if (v) { config.settings.halfRateLerp = false; suppress = true; if (tHL != null) tHL.isOn = false; suppress = false; }
                });
                if (tHL != null) tHL.onValueChanged.AddListener(v =>
                {
                    if (suppress) return;
                    config.settings.halfRateLerp = v;
                    if (v) { config.settings.halfRate = false; suppress = true; if (tHR != null) tHR.isOn = false; suppress = false; }
                });
                suppress = true;
                if (tHR != null) tHR.isOn = config.settings.halfRate;
                if (tHL != null) tHL.isOn = config.settings.halfRateLerp;
                suppress = false;
            }
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
            HookRegionSlider("jiggle", 0f, 2f, (r, v) => r.jiggle = v, r => r.jiggle);
            HookRegionSlider("stiffness", 0.5f, 30f, (r, v) => r.stiffness = v, r => r.stiffness);
            HookRegionSlider("damping", 0f, 1f, (r, v) => r.damping = v, r => r.damping);
            HookRegionSlider("bounce", 0f, 2f, (r, v) => r.bounce = v, r => r.bounce);
            HookRegionSlider("maxoff", 0.005f, 0.25f, (r, v) => r.maxOffset = v, r => r.maxOffset);
            HookRegionSlider("gravity", 0f, 2f, (r, v) => r.gravity = v, r => r.gravity);
            HookRegionSlider("cloth", 0f, 1f, (r, v) => r.clothRipple = v, r => r.clothRipple);
            HookRegionSlider("clothsize", 0f, 1f, (r, v) => r.clothSize = v, r => r.clothSize);
            HookRegionSlider("jello", 0f, 1f, (r, v) => r.jello = v, r => r.jello);
            HookRegionSlider("jellosize", 0f, 1f, (r, v) => r.jelloSize = v, r => r.jelloSize);
            HookRegionSlider("jellorand", 0f, 1f, (r, v) => r.jelloRandom = v, r => r.jelloRandom);
            HookRegionSlider("liquid", 0f, 1f, (r, v) => r.liquid = v, r => r.liquid);
            HookRegionSlider("liquidsize", 0f, 1f, (r, v) => r.liquidSize = v, r => r.liquidSize);
            HookRegionSlider("wavespeed", 0.1f, 3f, (r, v) => r.waveSpeed = v, r => r.waveSpeed);
            HookRegionSlider("sway", 0f, 1f, (r, v) => r.sway = v, r => r.sway);
            HookRegionSlider("twistj", 0f, 1f, (r, v) => r.twistJiggle = v, r => r.twistJiggle);
            HookRegionSlider("pulse", 0f, 1f, (r, v) => r.pulse = v, r => r.pulse);
            HookRegionSlider("pulserate", 0f, 1f, (r, v) => r.pulseRate = v, r => r.pulseRate);
            HookRegionSlider("stretch", 0f, 1f, (r, v) => r.stretch = v, r => r.stretch);
            HookRegionSlider("turb", 0f, 1f, (r, v) => r.turbulence = v, r => r.turbulence);
            HookRegionSlider("turbsize", 0f, 1f, (r, v) => r.turbSize = v, r => r.turbSize);
            HookRegionSlider("cellulite", 0f, 1f, (r, v) => r.cellulite = v, r => r.cellulite);
            HookRegionSlider("cellsize", 0f, 1f, (r, v) => r.celluliteSize = v, r => r.celluliteSize);
            HookRegionSlider("squish", 0f, 2f, (r, v) => r.squish = v, r => r.squish);
            HookRegionSlider("squishdepth", 0f, 0.2f, (r, v) => r.squishDepth = v, r => r.squishDepth);
            HookRegionSlider("bulge", 0f, 2f, (r, v) => r.bulge = v, r => r.bulge);
            HookRegionSlider("selfsquish", 0f, 2f, (r, v) => r.selfSquish = v, r => r.selfSquish);

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
        void HookRegionSlider(string key, float min, float max, Action<SquishRegion, float> set, Func<SquishRegion, float> get)
        {
            regionGetters[key] = get;
            HookSlider(key, min, max, v => { if (selRegion != null) set(selRegion, v); });
        }
        readonly Dictionary<string, Func<SquishRegion, float>> regionGetters = new Dictionary<string, Func<SquishRegion, float>>();

        void SetValueLabel(string key, float v)
        {
            InputField inp;
            if (valueInputs.TryGetValue(key, out inp) && inp != null && !inp.isFocused)
                inp.text = v.ToString(key == "squishdepth" ? "0.000" : "0.00", CultureInfo.InvariantCulture);
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
            public bool open = true;
        }
        readonly List<UiSection> sections = new List<UiSection>();
        RectTransform sectionContent;
        float sectionContentH;

        // sections that mean "off" when their sliders sit at zero — these get a tick box
        static bool SectionCanMute(string label)
        {
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
                    sec.hdr.text = "▼ " + sec.hdr.text;
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

        void ToggleSection(UiSection sec)
        {
            sec.open = !sec.open;
            if (sec.hdr != null && sec.hdr.text.Length > 2)
                sec.hdr.text = (sec.open ? "▼ " : "▶ ") + sec.hdr.text.Substring(2);
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
                for (int i = 0; i < rends.Length; i++) if (rends[i] != null) cm.Add(rends[i].name);
                if (cm.Count == 0) cm.Add("(no meshes)");
                colMeshDropdown.ClearOptions(); colMeshDropdown.AddOptions(cm);
                colMeshDropdown.value = 0; ForceCaption(colMeshDropdown);
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
            if (sm.regions.Count == 0) { SquishRegion r = new SquishRegion(); r.name = "region 1"; sm.regions.Add(r); }
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
            SquishRegion r = new SquishRegion(); r.name = "region " + (selMesh.regions.Count + 1);
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
            foreach (KeyValuePair<string, Func<SquishRegion, float>> kv in regionGetters)
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
        readonly HashSet<string> groupSel = new HashSet<string>();
        List<string> lastGroupPick = new List<string>();   // remembered for apply-to-meshes

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
            float h = 66f + rowH + Mathf.Min(bones.Count, 24) * rowH + 46f;

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
            RtText("Sub", "Union of ticked groups" + (groupChildren ? " + their child bones" : ""), pad, y, w - 2 * pad, 16f, 10, FontStyle.Italic); y += 20f;

            float half = (w - 2 * pad - 6f) / 2f;
            RtButton("All", "All", pad, y, half, rowH, () => { groupSel.Clear(); for (int i = 0; i < bones.Count; i++) groupSel.Add(bones[i]); RebuildGroupPanel(); });
            RtButton("None", "None", pad + half + 6f, y, half, rowH, () => { groupSel.Clear(); RebuildGroupPanel(); }); y += rowH + 4f;

            for (int i = 0; i < bones.Count && i < 24; i++)
            {
                string nm = bones[i];
                bool on = groupSel.Contains(nm);
                RtButton("G_" + nm, (on ? "☒ " : "☐ ") + nm, pad, y, w - 2 * pad, rowH,
                    () => { if (!groupSel.Remove(nm)) groupSel.Add(nm); RebuildGroupPanel(); },
                    on ? new Color(0.65f, 1f, 0.65f, 1f) : Color.white); y += rowH;
            }
            if (bones.Count > 24) { RtText("More", "… " + (bones.Count - 24) + " more (raise the list cap if needed)", pad, y, w - 2 * pad, 16f, 10, FontStyle.Italic); y += 18f; }
            y += 6f;
            RtButton("Sel", "Select (" + groupSel.Count + " groups)", pad, y, half, 30f, OnGroupsApply, new Color(0.7f, 1f, 0.7f, 1f));
            RtButton("Cls", "Close", pad + half + 6f, y, half, 30f, () => { Destroy(groupPanel); groupPanel = null; });
        }

        void RebuildGroupPanel() { if (groupPanel != null) { Destroy(groupPanel); groupPanel = null; } BuildGroupPanel(); }

        void OnGroupsApply()
        {
            if (selProxy == null || selRegion == null || groupSel.Count == 0) { SetStatus("tick at least one group"); return; }
            PushUndo(selRegion);
            lastGroupPick = new List<string>(groupSel);
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
            if (selRegion == null || colBoneInput == null) return;
            string bone = colBoneInput.text;
            if (string.IsNullOrEmpty(bone)) { SetStatus("type a bone/transform name for the collider"); return; }
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
            if (string.IsNullOrEmpty(nm) || nm.StartsWith("(")) { SetStatus("pick a collider mesh in the dropdown"); return; }
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
            Debug.Log("[Wobble] native physics override: " + disabled + " disabled"
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
                    SquishRegion reg = px.cfg.regions[r];
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
                    Debug.Log("[Wobble] scoped native override bones (" + px.smr.name + "): " + names);
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
        int applyMethod = 1;   // 0 = by bone group, 1 = by surface transfer

        void OpenApplyPanel()
        {
            if (selMesh == null || selRegion == null) { SetStatus("select a mesh + region first"); return; }
            if (applyPanel != null) { Destroy(applyPanel); applyPanel = null; return; }
            BuildApplyPanel();
        }

        void BuildApplyPanel()
        {
            if (window == null) return;
            SkinnedMeshRenderer[] rends = boundAvatar != null
                ? boundAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true) : new SkinnedMeshRenderer[0];

            List<string> targets = new List<string>();
            for (int i = 0; i < rends.Length; i++)
                if (rends[i] != null && rends[i].name != selMesh.mesh) targets.Add(rends[i].name);

            float w = 340f, rowH = 24f, pad = 10f;
            float h = 96f + rowH * 2f + targets.Count * rowH + 46f;

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
            RtText("Title", "Apply region '" + selRegion.name + "' to other meshes", pad, y, w - 2 * pad, 20f, 13, FontStyle.Bold); y += 26f;

            RtButton("M0", (applyMethod == 0 ? "◉" : "○") + " By bone group (current Group pick)", pad, y, w - 2 * pad, rowH,
                () => { applyMethod = 0; RebuildApply(); }); y += rowH + 2f;
            RtButton("M1", (applyMethod == 1 ? "◉" : "○") + " By surface transfer (project painted area)", pad, y, w - 2 * pad, rowH,
                () => { applyMethod = 1; RebuildApply(); }); y += rowH + 8f;

            float half = (w - 2 * pad - 6f) / 2f;
            RtButton("All", "All meshes", pad, y, half, rowH, () => { applySel.Clear(); for (int i = 0; i < targets.Count; i++) applySel.Add(targets[i]); RebuildApply(); });
            RtButton("None", "None", pad + half + 6f, y, half, rowH, () => { applySel.Clear(); RebuildApply(); }); y += rowH + 6f;

            for (int i = 0; i < targets.Count; i++)
            {
                string nm = targets[i];
                bool on = applySel.Contains(nm);
                RtButton("T_" + nm, (on ? "☒ " : "☐ ") + nm, pad, y, w - 2 * pad, rowH,
                    () => { if (!applySel.Remove(nm)) applySel.Add(nm); RebuildApply(); },
                    on ? new Color(0.65f, 1f, 0.65f, 1f) : Color.white); y += rowH;
            }
            y += 8f;
            RtButton("Apply", "Apply (" + applySel.Count + ")", pad, y, half, 30f, OnApplyMulti, new Color(0.7f, 1f, 0.7f, 1f));
            RtButton("Cancel", "Close", pad + half + 6f, y, half, 30f, () => { Destroy(applyPanel); applyPanel = null; });
        }

        void RebuildApply() { if (applyPanel != null) { Destroy(applyPanel); applyPanel = null; } BuildApplyPanel(); }

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

        // ---------- region mirror: Squish Studio OWNS regions; this plugin consumes them.
        // Mirrored copies persist in wobblestudio.json, so they survive even when Squish
        // Studio is disabled or its window is closed.
        const string OWNER_FILE = "squishstudio.json";
        System.DateTime ownerStamp;
        float ownerCheckT;

        void SaveConfigQuiet()
        {
            try { File.WriteAllText(savePath, JsonConvert.SerializeObject(config, Formatting.Indented)); configPath = savePath; }
            catch (Exception e) { Debug.LogWarning("[Wobble] quiet save failed: " + e.Message); }
        }

        static bool SameSelection(SquishRegion a, SquishRegion b)
        {
            if (a.vertIndex == null || b.vertIndex == null || a.vertIndex.Count != b.vertIndex.Count) return false;
            int n = a.vertIndex.Count;
            if (n == 0) return true;
            if (a.vertIndex[n - 1] != b.vertIndex[n - 1]) return false;
            for (int i = 0; i < n; i += 97)
                if (a.vertIndex[i] != b.vertIndex[i] || a.weight[i] != b.weight[i]) return false;
            return true;
        }

        void SyncRegionsFromOwner(bool force)
        {
            try
            {
                string p = Path.Combine(Application.persistentDataPath, OWNER_FILE);
                if (!File.Exists(p)) return;
                System.DateTime st = File.GetLastWriteTimeUtc(p);
                if (!force && st == ownerStamp) return;
                ownerStamp = st;
                SquishConfig owner = JsonConvert.DeserializeObject<SquishConfig>(File.ReadAllText(p));
                if (owner == null || owner.meshes == null) return;
                if (config.meshes == null) config.meshes = new List<SquishMesh>();
                bool changed = false;
                foreach (SquishMesh om in owner.meshes)
                {
                    if (om == null || om.regions == null) continue;
                    SquishMesh mine = config.meshes.Find(x => x != null && x.mesh == om.mesh);
                    if (mine == null)
                    {
                        if (om.regions.Count == 0) continue;
                        mine = new SquishMesh { mesh = om.mesh, enabled = false };
                        config.meshes.Add(mine); changed = true;
                    }
                    if (mine.regions == null) mine.regions = new List<SquishRegion>();
                    foreach (SquishRegion src in om.regions)
                    {
                        if (src == null) continue;
                        SquishRegion r = mine.regions.Find(x => x != null && x.name == src.name);
                        if (r == null) { r = new SquishRegion { name = src.name }; mine.regions.Add(r); changed = true; }
                        // mirror ONLY the region definition (verts/weights/enabled) - the
                        // wobble parameters stay whatever the user set HERE
                        if (r.enabled != src.enabled) { r.enabled = src.enabled; changed = true; }
                        if (!SameSelection(r, src))
                        {
                            r.vertIndex = new List<int>(src.vertIndex);
                            r.weight = new List<float>(src.weight);
                            changed = true;
                        }
                        // colliders are OWNED by Squish Studio too - mirror the set
                        string sig = JsonConvert.SerializeObject(src.colliders);
                        if (JsonConvert.SerializeObject(r.colliders) != sig)
                        {
                            r.colliders = JsonConvert.DeserializeObject<List<SquishCollider>>(sig);
                            changed = true;
                        }
                        // colliders are owned by Squish Studio too — mirror them
                        int cc = src.colliders != null ? src.colliders.Count : 0;
                        bool colDiff = (r.colliders != null ? r.colliders.Count : 0) != cc;
                        if (!colDiff && r.colliders != null)
                            for (int q = 0; q < cc; q++)
                                if (r.colliders[q].bone != src.colliders[q].bone || r.colliders[q].mesh != src.colliders[q].mesh
                                    || r.colliders[q].radius != src.colliders[q].radius || r.colliders[q].length != src.colliders[q].length
                                    || r.colliders[q].enabled != src.colliders[q].enabled) { colDiff = true; break; }
                        if (colDiff)
                        {
                            r.colliders = new List<SquishCollider>();
                            for (int q = 0; q < cc; q++)
                            {
                                SquishCollider sc = src.colliders[q];
                                r.colliders.Add(new SquishCollider { bone = sc.bone, mesh = sc.mesh, radius = sc.radius, length = sc.length, enabled = sc.enabled });
                            }
                            changed = true;
                        }

                    }
                    for (int i = mine.regions.Count - 1; i >= 0; i--)
                        if (mine.regions[i] != null && om.regions.Find(x => x != null && x.name == mine.regions[i].name) == null)
                        { mine.regions.RemoveAt(i); changed = true; }
                }
                if (changed)
                {
                    SaveConfigQuiet();
                    Rebind();
                    RefreshMeshList();
                    Debug.Log("[Wobble] regions mirrored from Squish Studio");
                }
            }
            catch (Exception e) { Debug.LogWarning("[Wobble] region mirror failed: " + e.Message); }
        }

        static void CopyRegionParams(SquishRegion src, SquishRegion dst)
        {
            dst.enabled = src.enabled;
            dst.jiggle = src.jiggle; dst.stiffness = src.stiffness; dst.damping = src.damping;
            dst.bounce = src.bounce; dst.maxOffset = src.maxOffset;
            dst.gravity = src.gravity; dst.gravityPoseOnly = src.gravityPoseOnly; dst.refBone = src.refBone;
            dst.clothRipple = src.clothRipple; dst.clothSize = src.clothSize;
            dst.jello = src.jello; dst.jelloSize = src.jelloSize;
            dst.liquid = src.liquid; dst.liquidSize = src.liquidSize; dst.waveSpeed = src.waveSpeed;
            dst.sway = src.sway; dst.twistJiggle = src.twistJiggle;
            dst.pulse = src.pulse; dst.pulseRate = src.pulseRate;
            dst.stretch = src.stretch; dst.turbulence = src.turbulence; dst.turbSize = src.turbSize;
            dst.cellulite = src.cellulite; dst.celluliteSize = src.celluliteSize;
            dst.squish = src.squish; dst.squishDepth = src.squishDepth;
            dst.bulge = src.bulge; dst.selfSquish = src.selfSquish;
            dst.colliders = new List<SquishCollider>();
            for (int i = 0; i < src.colliders.Count; i++)
            {
                SquishCollider c = new SquishCollider();
                c.bone = src.colliders[i].bone; c.radius = src.colliders[i].radius;
                c.length = src.colliders[i].length; c.enabled = src.colliders[i].enabled;
                dst.colliders.Add(c);
            }
        }

        void OnApplyMulti()
        {
            if (applySel.Count == 0) { SetStatus("tick at least one target mesh"); return; }
            if (selProxy == null || selRegion == null) return;

            if (applyMethod == 0 && lastGroupPick.Count == 0)
            { SetStatus("bone-group method: pick vertex groups on the source region first"); return; }
            List<Vector4> samples = applyMethod == 1 ? selProxy.RegionWorldSamples(selRegion) : null;
            if (applyMethod == 1 && (samples == null || samples.Count == 0))
            { SetStatus("surface transfer needs painted weights on the source region"); return; }

            SkinnedMeshRenderer[] rends = boundAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            string report = "";
            foreach (string nm in applySel)
            {
                SkinnedMeshRenderer target = null;
                for (int r = 0; r < rends.Length; r++) if (rends[r] != null && rends[r].name == nm) { target = rends[r]; break; }
                if (target == null) continue;

                SquishMesh sm = null;
                for (int m = 0; m < config.meshes.Count; m++) if (config.meshes[m].mesh == nm) sm = config.meshes[m];
                if (sm == null) { sm = new SquishMesh(); sm.mesh = nm; config.meshes.Add(sm); }
                sm.enabled = true;

                SquishRegion reg = null;
                for (int r = 0; r < sm.regions.Count; r++) if (sm.regions[r].name == selRegion.name) reg = sm.regions[r];
                if (reg == null) { reg = new SquishRegion(); reg.name = selRegion.name; sm.regions.Add(reg); }
                CopyRegionParams(selRegion, reg);

                int count;
                if (applyMethod == 0)
                {
                    MeshProxy.SelectFromBonesOn(target, reg, lastGroupPick, groupThreshold, groupChildren);
                    count = reg.vertIndex.Count;
                }
                else count = MeshProxy.TransferWeights(target, reg, samples, 0.03f);

                report += (report.Length > 0 ? ", " : "") + nm + ":" + count;
            }

            Destroy(applyPanel); applyPanel = null;
            Rebind(); RefreshMeshList();
            SetStatus("applied '" + selRegion.name + "' → " + report + " verts — Save to keep");
        }

        // ==================== config IO ====================
        void LoadConfig()
        {
            configPath = null;
            string[] candidates = {
                savePath,
                Path.Combine(Directory.GetCurrentDirectory(), CONFIG_FILE),
                Path.Combine(Directory.GetCurrentDirectory(), "Items\\Assemblies\\WobbleStudio\\" + CONFIG_FILE),
                Path.Combine(Application.persistentDataPath, "squishstudio.json"),  // first run: inherit regions from Squish Studio
            };
            for (int i = 0; i < candidates.Length; i++)
                if (File.Exists(candidates[i])) { configPath = candidates[i]; break; }
            if (configPath == null) { configPath = savePath; config = new SquishConfig(); return; }
            try
            {
                SquishConfig parsed = JsonConvert.DeserializeObject<SquishConfig>(File.ReadAllText(configPath));
                if (parsed != null) config = parsed;
                if (config.settings == null) config.settings = new SquishSettings();
                if (config.meshes == null) config.meshes = new List<SquishMesh>();
            }
            catch (Exception e) { Debug.LogWarning("[Wobble] config load failed: " + e.Message); config = new SquishConfig(); }
            // reloading replaces every region object — snapshots referencing the old ones are dead
            undoStack.Clear(); redoStack.Clear();
            selMesh = null; selRegion = null;
        }

        void SaveConfig()
        {
            try
            {
                File.WriteAllText(savePath, JsonConvert.SerializeObject(config, Formatting.Indented));
                configPath = savePath;
                SetStatus("saved to " + savePath);
                Debug.Log("[Wobble] Saved to " + savePath);
            }
            catch (Exception e) { SetStatus("save failed: " + e.Message); }
        }
    }
}
