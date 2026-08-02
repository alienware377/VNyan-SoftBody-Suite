using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SquishStudio
{
    // Squish Studio — mesh-level soft-body deformation with live weight painting.
    // Runs LAST in LateUpdate (order 20000) so the bake sees the final pose of the
    // frame: tracking, Pose Studio, bone physics — everything.
    [DefaultExecutionOrder(20700)]   // FINAL stage: runs after SoftBody (20500) and Jello (20600)
    public class SquishPlugin : MonoBehaviour, VNyanInterface.IButtonClickedHandler
    {
        const string BUTTON_NAME = "Squish Studio";
        const string CONFIG_FILE = "squishstudio.json";

        public GameObject windowPrefab;

        GameObject window;
        Text statusLabel;
        readonly Dictionary<string, Slider> sliders = new Dictionary<string, Slider>();
        readonly Dictionary<string, InputField> valueInputs = new Dictionary<string, InputField>();
        Dropdown meshDropdown, regionDropdown, groupDropdown, colliderDropdown, refBoneDropdown, colMeshDropdown, colBoneDropdown;
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

        void PushUndo(SquishRegion r)
        {
            if (r == null) return;
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

        void DoUndo()
        {
            // drop snapshots orphaned by a config reload (their regions no longer exist)
            while (undoStack.Count > 0 && !RegionLive(undoStack[undoStack.Count - 1].region))
                undoStack.RemoveAt(undoStack.Count - 1);
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
            Rebind();
            Debug.Log("[Squish] bound to avatar '" + av.name + "'");
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
                if (target == null) { Debug.LogWarning("[Squish] mesh '" + sm.mesh + "' not found"); continue; }
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

            float dt = Mathf.Min(Time.deltaTime, config.settings.maxDeltaTime);
            if (dt <= 0f) return;
            MeshProxy.halfRate = config.settings.halfRate;
            MeshProxy.halfRateLerp = config.settings.halfRateLerp;
            MeshProxy.asyncSim = config.settings.asyncSim;
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
                { "Input_RegionName", "Rename the region. Regions sync to Wobble Studio by name." },
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
                { "Button_ShowCol", "Toggle translucent capsules showing every ACTIVE collider (same as F10). Collider sets are made HERE and mirror to Wobble + SoftBody Studio." },
                { "Toggle_NativeOff", "Disable VNyan/native spring & dynamic bones while squishing — they fight the mesh simulation." },
                { "Toggle_HalfRate", "HALF-RATE physics: compute the simulation every 2nd frame (with doubled timestep) and hold the result between — near-halves the physics cost on slower PCs. Skinning/animation still updates every frame, so it is barely visible." },
                { "Toggle_HalfRateLerp", "HALF-RATE + SMOOTH: like half-rate physics, but held frames show a blend between the last two physics ticks instead of a repeat — smoother motion at the same cost, with half a tick of extra latency. Mutually exclusive with the other rate options." },
                { "Toggle_AsyncSim", "ASYNC physics: run the whole simulation on a background worker thread — the main thread only captures inputs and applies last frame's result, so almost the entire physics cost disappears from the frame. Costs 1 frame of physics latency. Mutually exclusive with the half-rate options; disabled while F10 debug is on." },
                { "Toggle_NativeScoped", "Only disable native physics on bones that drive painted regions (instead of everywhere)." },
                { "Button_Reload", "Re-read the saved config from disk and re-bind." },
                { "Button_Save", "Write everything to squishstudio.json (also syncs regions to Wobble Studio)." },
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
            {
                Toggle tHR = FindControl<Toggle>("Toggle_HalfRate");
                Toggle tHL = FindControl<Toggle>("Toggle_HalfRateLerp");
                Toggle tAS = FindControl<Toggle>("Toggle_AsyncSim");
                if (tHR != null) tHR.onValueChanged.AddListener(v =>
                {
                    if (suppress) return;
                    config.settings.halfRate = v;
                    if (v) { config.settings.halfRateLerp = false; config.settings.asyncSim = false; suppress = true; if (tHL != null) tHL.isOn = false; if (tAS != null) tAS.isOn = false; suppress = false; }
                });
                if (tHL != null) tHL.onValueChanged.AddListener(v =>
                {
                    if (suppress) return;
                    config.settings.halfRateLerp = v;
                    if (v) { config.settings.halfRate = false; config.settings.asyncSim = false; suppress = true; if (tHR != null) tHR.isOn = false; if (tAS != null) tAS.isOn = false; suppress = false; }
                });
                if (tAS != null) tAS.onValueChanged.AddListener(v =>
                {
                    if (suppress) return;
                    config.settings.asyncSim = v;
                    if (v) { config.settings.halfRate = false; config.settings.halfRateLerp = false; suppress = true; if (tHR != null) tHR.isOn = false; if (tHL != null) tHL.isOn = false; suppress = false; }
                });
                suppress = true;
                if (tHR != null) tHR.isOn = config.settings.halfRate;
                if (tHL != null) tHL.isOn = config.settings.halfRateLerp;
                if (tAS != null) tAS.isOn = config.settings.asyncSim;
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
            HookRegionSlider("maxdent", 0f, 0.15f, (r, v) => r.maxDent = v, r => r.maxDent);
            HookRegionSlider("evacbone", 0f, 2f, (r, v) => r.evacBone = v, r => r.evacBone);
            HookRegionSlider("evacall", 0f, 2f, (r, v) => r.evacAllBones = v, r => r.evacAllBones);
            HookRegionSlider("evacblob", 0f, 2f, (r, v) => r.evacBlob = v, r => r.evacBlob);

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
            s.onValueChanged.AddListener(v => { if (!suppress) { set(v); SetValueLabel(key, v); } });

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

        // every bone that skins any painted vertex of any active region
        HashSet<Transform> CollectRegionBones()
        {
            HashSet<Transform> set = new HashSet<Transform>();
            for (int p = 0; p < proxies.Count; p++)
            {
                MeshProxy px = proxies[p];
                if (!px.Alive || px.smr.sharedMesh == null) continue;
                BoneWeight[] bw = px.smr.sharedMesh.boneWeights;
                Transform[] bones = px.smr.bones;
                for (int r = 0; r < px.cfg.regions.Count; r++)
                {
                    SquishRegion reg = px.cfg.regions[r];
                    for (int v = 0; v < reg.vertIndex.Count; v++)
                    {
                        int vi = reg.vertIndex[v];
                        if (vi >= bw.Length) continue;
                        BoneWeight w4 = bw[vi];
                        if (w4.weight0 > 0.1f && w4.boneIndex0 < bones.Length && bones[w4.boneIndex0] != null) set.Add(bones[w4.boneIndex0]);
                        if (w4.weight1 > 0.1f && w4.boneIndex1 < bones.Length && bones[w4.boneIndex1] != null) set.Add(bones[w4.boneIndex1]);
                        if (w4.weight2 > 0.1f && w4.boneIndex2 < bones.Length && bones[w4.boneIndex2] != null) set.Add(bones[w4.boneIndex2]);
                        if (w4.weight3 > 0.1f && w4.boneIndex3 < bones.Length && bones[w4.boneIndex3] != null) set.Add(bones[w4.boneIndex3]);
                    }
                }
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

            RtButton("M2", (applyMethod == 2 ? "◉" : "○") + " Auto: same bones as the painted area", pad, y, w - 2 * pad, rowH,
                () => { applyMethod = 2; RebuildApply(); }); y += rowH + 2f;
            RtButton("M0", (applyMethod == 0 ? "◉" : "○") + " By bone group (current Group pick)", pad, y, w - 2 * pad, rowH,
                () => { applyMethod = 0; applyAllRegions = false; RebuildApply(); }); y += rowH + 2f;
            RtButton("M1", (applyMethod == 1 ? "◉" : "○") + " By surface transfer (project painted area)", pad, y, w - 2 * pad, rowH,
                () => { applyMethod = 1; RebuildApply(); }); y += rowH + 8f;

            RtButton("AllReg", applyMethod == 0
                ? "☐ Copy ALL regions — n/a for the group-pick method"
                : (applyAllRegions ? "☒ " : "☐ ") + "Copy ALL regions of this mesh (" + selMesh.regions.Count + ")",
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

        static void CopyRegionParams(SquishRegion src, SquishRegion dst)
        {
            if (src == null || dst == null || src == dst) return;   // src==dst would wipe colliders mid-copy
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
            dst.maxDent = src.maxDent; dst.evacBone = src.evacBone;
            dst.evacAllBones = src.evacAllBones; dst.evacBlob = src.evacBlob;
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

            List<SquishRegion> srcs = new List<SquishRegion>();
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
                HashSet<SquishRegion> claimed = new HashSet<SquishRegion>();   // duplicate src names stay 1:1
                for (int s = 0; s < srcs.Count; s++)
                {
                    SquishRegion src = srcs[s];
                    if (applyMethod == 1 && samplesPer[s].Count == 0) continue;
                    if (applyMethod == 2 && bonesPer[s].Count == 0) continue;

                    // trial-transfer into a scratch region: existing paint on the target is
                    // only overwritten when the method actually found vertices there
                    SquishRegion trial = new SquishRegion();
                    if (applyMethod == 0)
                        MeshProxy.SelectFromBonesOn(target, trial, lastGroupPick, groupThreshold, groupChildren);
                    else if (applyMethod == 2)
                        MeshProxy.SelectFromBonesOn(target, trial, bonesPer[s], groupThreshold, false);
                    else
                        MeshProxy.TransferWeights(target, trial, samplesPer[s], applyRadius);
                    if (trial.vertIndex.Count == 0) continue;

                    if (sm == null) { sm = new SquishMesh(); sm.mesh = nm; config.meshes.Add(sm); }
                    SquishRegion reg = null;
                    for (int r = 0; r < sm.regions.Count; r++)
                        if (sm.regions[r].name == src.name && !claimed.Contains(sm.regions[r])) { reg = sm.regions[r]; break; }
                    if (reg == null) { reg = new SquishRegion(); reg.name = src.name; sm.regions.Add(reg); }
                    claimed.Add(reg);

                    PushUndo(reg);
                    CopyRegionParams(src, reg);
                    reg.vertIndex = trial.vertIndex;
                    reg.weight = trial.weight;
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
            configPath = null;
            string[] candidates = {
                savePath,
                Path.Combine(Directory.GetCurrentDirectory(), CONFIG_FILE),
                Path.Combine(Directory.GetCurrentDirectory(), "Items\\Assemblies\\SquishStudio\\" + CONFIG_FILE),
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
            catch (Exception e) { Debug.LogWarning("[Squish] config load failed: " + e.Message); config = new SquishConfig(); }
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
                Debug.Log("[Squish] Saved to " + savePath);
            }
            catch (Exception e) { SetStatus("save failed: " + e.Message); }
        }
    }
}
