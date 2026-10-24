using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Builds the Soft Body Suite window prefab + starter prefab and packs them into the
// VNyan .vnobj asset bundle. Invoked via:
//   Unity.exe -batchmode -quit -executeMethod SuiteBuild.Build
public static class SuiteBuild
{
    static DefaultControls.Resources _res;

    public static void Build()
    {
        const string windowPrefabPath = "Assets/SoftBodySuiteWindow.prefab";
        const string starterPrefabPath = "Assets/SoftBodySuiteStarter.prefab";
        const string bundleName = "softbodysuite_bundle";
        const string outDir = "AssetBundles";

        GameObject windowAsset = BuildWindowPrefab(windowPrefabPath);

        GameObject go = new GameObject("VNyanTemp");
        SoftBodySuite.SquishPlugin plugin = go.AddComponent<SoftBodySuite.SquishPlugin>();
        plugin.windowPrefab = windowAsset;
        PrefabUtility.SaveAsPrefabAsset(go, starterPrefabPath);
        Object.DestroyImmediate(go);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        AssetBundleBuild abb = new AssetBundleBuild();
        abb.assetBundleName = bundleName;
        abb.assetNames = new string[] { starterPrefabPath };
        abb.addressableNames = new string[] { "vnyanitem" };

        Directory.CreateDirectory(outDir);
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            outDir, new AssetBundleBuild[] { abb },
            BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);

        if (manifest == null) { Debug.LogError("[SuiteBuild] bundle build failed"); EditorApplication.Exit(2); return; }
        string built = Path.Combine(outDir, bundleName);
        string final = Path.Combine(outDir, "SoftBodySuite.vnobj");
        if (File.Exists(final)) File.Delete(final);
        File.Copy(built, final);
        Debug.Log("[SuiteBuild] wrote " + final);
        EditorApplication.Exit(0);
    }

    static GameObject BuildWindowPrefab(string prefabPath)
    {
        _res = new DefaultControls.Resources
        {
            standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
            background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
            inputField = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/InputFieldBackground.psd"),
            knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
            checkmark = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd"),
            dropdown = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd"),
            mask = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UIMask.psd")
        };

        const float W = 440f;
        const float P = 12f;
        const float gap = 8f;
        const float headerH = 56f;
        const float viewportH = 470f;
        const float footerH = 50f;
        const float totalH = headerH + viewportH + footerH;
        const float sbW = 14f;
        const float outerX = P;
        const float outerW = W - 2f * P;

        GameObject root = new GameObject("SoftBodySuiteWindow",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform rrt = root.GetComponent<RectTransform>();
        rrt.anchorMin = new Vector2(0.5f, 0.5f); rrt.anchorMax = new Vector2(0.5f, 0.5f);
        rrt.pivot = new Vector2(0.5f, 0.5f);
        rrt.sizeDelta = new Vector2(W, totalH);
        Image bg = root.GetComponent<Image>();
        bg.sprite = _res.background; bg.type = Image.Type.Sliced;
        bg.color = new Color(0.13f, 0.11f, 0.16f, 0.97f);

        root.AddComponent<SoftBodySuite.SquishWindowDrag>();

        float hy = 10f;
        MakeText(root.transform, "Title", "Soft Body Suite",
            outerX, hy, outerW, 22f, 15, TextAnchor.MiddleCenter, FontStyle.Bold);
        hy += 26f;
        MakeText(root.transform, "Label_Status", "load an avatar, enable a mesh, paint a region",
            outerX, hy, outerW, 18f, 11, TextAnchor.MiddleLeft, FontStyle.Italic);

        // scroll area
        GameObject scroll = new GameObject("ScrollView",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(ScrollRect));
        scroll.GetComponent<Image>().color = new Color(0.09f, 0.08f, 0.12f, 1f);
        Place(scroll.transform, root.transform, P, headerH, outerW, viewportH);

        GameObject viewport = new GameObject("Viewport",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
        RectTransform vrt = viewport.GetComponent<RectTransform>();
        vrt.SetParent(scroll.transform, false);
        vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one; vrt.pivot = new Vector2(0f, 1f);
        vrt.offsetMin = Vector2.zero; vrt.offsetMax = new Vector2(-sbW, 0f);
        viewport.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.01f);

        GameObject content = new GameObject("Content", typeof(RectTransform));
        RectTransform crt = content.GetComponent<RectTransform>();
        crt.SetParent(viewport.transform, false);
        crt.anchorMin = new Vector2(0f, 1f); crt.anchorMax = new Vector2(1f, 1f);
        crt.pivot = new Vector2(0f, 1f);
        crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;

        GameObject sbar = DefaultControls.CreateScrollbar(_res);
        sbar.name = "Scrollbar_Vertical";
        Scrollbar sbc = sbar.GetComponent<Scrollbar>();
        sbc.SetDirection(Scrollbar.Direction.BottomToTop, true);
        RectTransform sbrt = sbar.GetComponent<RectTransform>();
        sbrt.SetParent(scroll.transform, false);
        sbrt.anchorMin = new Vector2(1f, 0f); sbrt.anchorMax = new Vector2(1f, 1f);
        sbrt.pivot = new Vector2(1f, 1f);
        sbrt.sizeDelta = new Vector2(sbW, 0f); sbrt.anchoredPosition = Vector2.zero;

        ScrollRect sr = scroll.GetComponent<ScrollRect>();
        sr.horizontal = false; sr.vertical = true;
        sr.movementType = ScrollRect.MovementType.Clamped;
        sr.scrollSensitivity = 24f;
        sr.viewport = vrt; sr.content = crt;
        sr.verticalScrollbar = sbc;
        sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

        Transform c = content.transform;
        const float cX = 6f;
        float cW = outerW - sbW - 2f * cX;
        float y = 6f;

        // ---------- mesh & region ----------
        Header(c, "— Mesh & Region —", cX, cW, ref y);
        MakeText(c, "Lbl_Mesh", "Mesh", cX, y, 70f, 30f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeDropdown(c, "Dropdown_Mesh", cX + 76f, y, cW - 76f, 30f); y += 34f;
        float b2 = (cW - gap) / 2f;
        MakeButton(c, "Button_EnableMesh", "Enable squish on mesh", cX, y, b2, 26f);
        MakeButton(c, "Button_DisableMesh", "Disable", cX + b2 + gap, y, b2, 26f); y += 32f;
        MakeText(c, "Lbl_Region", "Region", cX, y, 70f, 30f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeDropdown(c, "Dropdown_Region", cX + 76f, y, cW - 76f, 30f); y += 34f;
        float b3 = (cW - 2f * gap) / 3f;
        MakeButton(c, "Button_AddRegion", "+ Region", cX, y, b3, 26f);
        MakeButton(c, "Button_RemoveRegion", "Remove", cX + b3 + gap, y, b3, 26f);
        MakeToggle(c, "Toggle_RegionEnabled", "Enabled", cX + 2f * (b3 + gap), y, b3, 24f); y += 30f;
        MakeText(c, "Lbl_RName", "Name", cX, y, 70f, 26f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeInput(c, "Input_RegionName", "region name", cX + 76f, y, cW - 76f, 26f); y += 32f;

        // ---------- painting ----------
        Header(c, "— Weight painting —", cX, cW, ref y);
        MakeToggle(c, "Toggle_Paint", "Paint mode (LMB on the model)", cX, y, cW * 0.6f, 24f);
        MakeToggle(c, "Toggle_Overlay", "Show overlay", cX + cW * 0.62f, y, cW * 0.38f, 24f); y += 28f;
        MakeButton(c, "Button_PaintAdd", "Brush: Add", cX, y, b2, 24f);
        MakeButton(c, "Button_PaintSub", "Brush: Subtract", cX + b2 + gap, y, b2, 24f); y += 30f;
        MakeButton(c, "Button_Undo", "Undo (Ctrl+Z)", cX, y, b3, 24f);
        MakeButton(c, "Button_Redo", "Redo (Ctrl+Shift+Z)", cX + b3 + gap, y, b3, 24f);
        MakeButton(c, "Button_BlurWeights", "Blur weights", cX + 2f * (b3 + gap), y, b3, 24f); y += 30f;
        SliderRow(c, "radius", "Brush radius", cX, cW, ref y);
        SliderRow(c, "strength", "Brush strength", cX, cW, ref y);
        SliderRow(c, "overlayop", "Overlay opacity", cX, cW, ref y);
        MakeToggle(c, "Toggle_GroupChildren", "Also include child bones down the branch", cX, y, cW, 24f); y += 28f;
        SliderRow(c, "groupthr", "Group threshold", cX, cW, ref y);
        MakeButton(c, "Button_PickGroups", "Pick vertex groups… (multi-select)", cX, y, b2, 24f);
        MakeButton(c, "Button_ClearWeights", "Clear weights", cX + b2 + gap, y, b2, 24f); y += 30f;
        MakeButton(c, "Button_ApplyMulti", "Apply region to other meshes…", cX, y, cW, 26f); y += 32f;
        MakeToggle(c, "Toggle_GravityPose", "Gravity only when the parent bone leaves rest", cX, y, cW, 24f); y += 28f;
        MakeText(c, "Lbl_RefBone", "Ref bone", cX, y, 70f, 28f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeDropdown(c, "Dropdown_RefBone", cX + 76f, y, cW - 76f, 28f); y += 34f;

        // ---------- colliders (shared by all three stages) ----------
        Header(c, "— Colliders —", cX, cW, ref y);
        MakeText(c, "Lbl_ColMesh", "Mesh", cX, y, 70f, 28f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeDropdown(c, "Dropdown_ColMesh", cX + 76f, y, cW - 76f - b3 - gap, 28f);
        MakeButton(c, "Button_AddMeshCol", "+ Add", cX + cW - b3, y, b3, 26f); y += 32f;
        MakeText(c, "Lbl_ColBone", "Bone", cX, y, 70f, 28f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeDropdown(c, "Dropdown_ColBonePick", cX + 76f, y, cW - 76f - 94f - b3 - 2f * gap, 28f);
        MakeInput(c, "Input_ColRadius", "0.015", cX + cW - b3 - gap - 88f, y, 88f, 26f);
        MakeButton(c, "Button_AddCollider", "+ Add", cX + cW - b3, y, b3, 26f); y += 32f;
        MakeDropdown(c, "Dropdown_Collider", cX, y, cW - b3 - gap, 28f);
        MakeButton(c, "Button_RemoveCollider", "Remove", cX + cW - b3, y, b3, 26f); y += 32f;
        MakeButton(c, "Button_ShowCol", "Show / hide colliders (F10)", cX, y, cW, 26f); y += 32f;

        // ---------- WOBBLE ----------
        Header(c, "— Wobble —", cX, cW, ref y);
        SubHeader(c, "Motion", cX, cW, ref y);
        SliderRow(c, "wob_jiggle", "Jiggle level", cX, cW, ref y);
        SliderRow(c, "wob_stiffness", "Stiffness", cX, cW, ref y);
        SliderRow(c, "wob_damping", "Damping", cX, cW, ref y);
        SliderRow(c, "wob_bounce", "Drag (inertia)", cX, cW, ref y);
        SliderRow(c, "wob_maxoff", "Max deform (m)", cX, cW, ref y);
        SubHeader(c, "Gravity", cX, cW, ref y);
        SliderRow(c, "wob_gravity", "Gravity level", cX, cW, ref y);
        SubHeader(c, "Waves and ripples", cX, cW, ref y);
        SliderRow(c, "wob_cloth", "Cloth ripple", cX, cW, ref y);
        SliderRow(c, "wob_clothsize", "Cloth ripple size", cX, cW, ref y);
        SliderRow(c, "wob_jello", "Jell-o wobble", cX, cW, ref y);
        SliderRow(c, "wob_jellosize", "Jell-o size", cX, cW, ref y);
        SliderRow(c, "wob_jellospeed", "Jell-o speed", cX, cW, ref y);
        SliderRow(c, "wob_jellorand", "Jell-o random size", cX, cW, ref y);
        SliderRow(c, "wob_jellorandsp", "Jell-o random speed", cX, cW, ref y);
        SliderRow(c, "wob_ropepull", "Rope pull", cX, cW, ref y);
        SliderRow(c, "wob_ropeease", "Rope pull ease", cX, cW, ref y);
        SliderRow(c, "wob_liquid", "Liquid ripple", cX, cW, ref y);
        SliderRow(c, "wob_liquidsize", "Liquid ripple size", cX, cW, ref y);
        SliderRow(c, "wob_wavespeed", "Liquid ripple speed", cX, cW, ref y);
        SubHeader(c, "Extra jiggle modes", cX, cW, ref y);
        SliderRow(c, "wob_sway", "Sway (pendulum)", cX, cW, ref y);
        SliderRow(c, "wob_swayspeed", "Sway speed", cX, cW, ref y);
        SliderRow(c, "wob_swaydamp", "Sway damping", cX, cW, ref y);
        SliderRow(c, "wob_twistj", "Twist wobble", cX, cW, ref y);
        SliderRow(c, "wob_twistspeed", "Twist speed", cX, cW, ref y);
        SliderRow(c, "wob_twistdamp", "Twist damping", cX, cW, ref y);
        SliderRow(c, "wob_pulse", "Pulse (breathe)", cX, cW, ref y);
        SliderRow(c, "wob_pulserate", "Pulse rate", cX, cW, ref y);
        SliderRow(c, "wob_stretch", "Squash and stretch", cX, cW, ref y);
        SliderRow(c, "wob_turb", "Turbulence", cX, cW, ref y);
        SliderRow(c, "wob_turbsize", "Turbulence size", cX, cW, ref y);
        SubHeader(c, "Surface", cX, cW, ref y);
        SliderRow(c, "wob_cellulite", "Cellulite level", cX, cW, ref y);
        SliderRow(c, "wob_cellsize", "Cellulite size", cX, cW, ref y);

        // ---------- JELL-O ----------
        Header(c, "— Jell-o —", cX, cW, ref y);
        SubHeader(c, "Sim cage", cX, cW, ref y);
        MakeToggle(c, "Toggle_useremesh", "Simulate on a remeshed cage (smoother)", cX, y, cW, 22f); y += 26f;
        SliderRow(c, "remeshsize", "Cage edge length (m)", cX, cW, ref y);
        SliderRow(c, "remeshpasses", "Remesh passes", cX, cW, ref y);
        SliderRow(c, "projavg", "Softness (projection averaging)", cX, cW, ref y);
        SliderRow(c, "proxysmooth", "Peak smoothing", cX, cW, ref y);
        SubHeader(c, "Edge seam (painted / unpainted)", cX, cW, ref y);
        SliderRow(c, "seamlevel", "Seam smoothing level", cX, cW, ref y);
        SliderRow(c, "seamrange", "Seam smoothing range (m)", cX, cW, ref y);
        SliderRow(c, "seammax", "Seam max stretch (m)", cX, cW, ref y);
        SubHeader(c, "Contact boost and slap", cX, cW, ref y);
        SliderRow(c, "boost", "Boost strength (x pen)", cX, cW, ref y);
        SliderRow(c, "boostspread", "Boost spread (passes)", cX, cW, ref y);
        SliderRow(c, "boostmax", "Max boost depth (m)", cX, cW, ref y);
        SliderRow(c, "slapsens", "Slap sensitivity (m/s)", cX, cW, ref y);
        SliderRow(c, "slappower", "Slap power", cX, cW, ref y);
        MakeButton(c, "Button_ShowRemesh", "Show / hide sim cage", cX, y, cW, 26f); y += 32f;
        SubHeader(c, "Motion", cX, cW, ref y);
        SliderRow(c, "jel_damping", "Damping", cX, cW, ref y);
        SliderRow(c, "jel_gravity", "Gravity sag", cX, cW, ref y);
        SliderRow(c, "jel_xattach", "Stay near skeleton spot", cX, cW, ref y);
        SliderRow(c, "jel_xmaxstretch", "Max stretch (m)", cX, cW, ref y);
        SliderRow(c, "jel_xcolrelax", "Get out of the collider", cX, cW, ref y);
        SubHeader(c, "Advanced solver (rarely needed)", cX, cW, ref y);
        SliderRow(c, "jel_xiter", "Solver iterations", cX, cW, ref y);
        SliderRow(c, "jel_xpressure", "Volume pressure", cX, cW, ref y);

        // ---------- SQUISH ----------
        Header(c, "— Squish —", cX, cW, ref y);
        SubHeader(c, "Contact squish", cX, cW, ref y);
        SliderRow(c, "sq_squish", "Squish level", cX, cW, ref y);
        SliderRow(c, "sq_squishdepth", "Squish depth (m, 0=auto)", cX, cW, ref y);
        SliderRow(c, "sq_bulge", "Bulge level", cX, cW, ref y);
        SliderRow(c, "sq_selfsquish", "Region self-squish", cX, cW, ref y);
        SubHeader(c, "Penetration limit", cX, cW, ref y);
        SliderRow(c, "sq_maxdent", "Max dent before give-way (m)", cX, cW, ref y);
        SliderRow(c, "sq_evacbone", "Evacuate: move bones", cX, cW, ref y);
        SliderRow(c, "sq_evacall", "Evacuate: ALL bones (nested too)", cX, cW, ref y);
        SliderRow(c, "sq_evacblob", "Evacuate: shift blob", cX, cW, ref y);

        // ---------- CLOTHES ----------
        Header(c, "— Clothes —", cX, cW, ref y);
        MakeText(c, "Note_Cloth", "Runs once, after all three stages: the clothes are fitted to the finished body, then anything poking through is tucked back in.", cX, y, cW, 30f, 10, TextAnchor.UpperLeft, FontStyle.Italic); y += 34f;
        SubHeader(c, "Move the clothes", cX, cW, ref y);
        MakeToggle(c, "Toggle_cagedrive", "Move the clothes onto the body", cX, y, cW, 22f); y += 26f;
        MakeToggle(c, "Toggle_cagewhole", "Bind clothes to the WHOLE body surface", cX, y, cW, 22f); y += 26f;
        MakeToggle(c, "Toggle_bonefilter", "Only cloth on the region bones", cX, y, cW, 22f); y += 26f;
        MakeToggle(c, "Toggle_clothout", "Keep clothes off the skin (cloth only)", cX, y, cW, 22f); y += 26f;
        SliderRow(c, "cagefollow", "Cloth follow range (m)", cX, cW, ref y);
        SliderRow(c, "cagefit", "Follow strength (fit)", cX, cW, ref y);
        SliderRow(c, "cageinflate", "Inflate driven clothes (m)", cX, cW, ref y);
        SliderRow(c, "cageinflatedyn", "Dynamic inflate (x outward)", cX, cW, ref y);
        SliderRow(c, "fillmax", "Fill gaps up to (size)", cX, cW, ref y);
        SliderRow(c, "anchormin", "Anchor areas at least (size)", cX, cW, ref y);
        SliderRow(c, "cageminclear", "Min cloth gap (m)", cX, cW, ref y);
        SliderRow(c, "cagefolsharp", "Cloth field sharpness", cX, cW, ref y);
        SliderRow(c, "cagefolsm", "Cloth field smoothing", cX, cW, ref y);
        SubHeader(c, "Tuck the body back in", cX, cW, ref y);
        MakeToggle(c, "Toggle_clipguard", "Tuck flesh back inside the clothes", cX, y, cW, 24f); y += 26f;
        SliderRow(c, "clipclear", "Tuck: extra clearance (m)", cX, cW, ref y);
        SliderRow(c, "cliprange", "Tuck: bind range (m)", cX, cW, ref y);
        SliderRow(c, "clipstr", "Tuck: strength", cX, cW, ref y);
        SliderRow(c, "cliprimfade", "Tuck: fade at hems (m)", cX, cW, ref y);

        // ---------- PERFORMANCE ----------
        Header(c, "— Performance and native bones —", cX, cW, ref y);
        SubHeader(c, "Substeps per stage", cX, cW, ref y);
        SliderRow(c, "wob_substeps", "Wobble substeps", cX, cW, ref y);
        SliderRow(c, "jel_substeps", "Jell-o substeps", cX, cW, ref y);
        SliderRow(c, "sq_substeps", "Squish substeps", cX, cW, ref y);
        SubHeader(c, "Frame-rate savers", cX, cW, ref y);
        MakeToggle(c, "Toggle_wob_HalfRate", "Wobble: half-rate physics", cX, y, cW, 24f); y += 26f;
        MakeToggle(c, "Toggle_wob_HalfRateLerp", "Wobble: half-rate + smooth blend", cX, y, cW, 24f); y += 26f;
        MakeToggle(c, "Toggle_wob_AsyncSim", "Wobble: async physics (worker thread)", cX, y, cW, 24f); y += 26f;
        MakeToggle(c, "Toggle_jel_HalfRate", "Jell-o: half-rate physics", cX, y, cW, 24f); y += 26f;
        MakeToggle(c, "Toggle_jel_HalfRateLerp", "Jell-o: half-rate + smooth blend", cX, y, cW, 24f); y += 26f;
        MakeToggle(c, "Toggle_sq_HalfRate", "Squish: half-rate physics", cX, y, cW, 24f); y += 26f;
        MakeToggle(c, "Toggle_sq_HalfRateLerp", "Squish: half-rate + smooth blend", cX, y, cW, 24f); y += 26f;
        MakeToggle(c, "Toggle_sq_AsyncSim", "Squish: async physics (worker thread)", cX, y, cW, 24f); y += 26f;
        SubHeader(c, "Native bone physics", cX, cW, ref y);
        MakeToggle(c, "Toggle_NativeOff", "Disable spring/dynamic bones (they fight the squish)", cX, y, cW, 24f); y += 26f;
        MakeToggle(c, "Toggle_NativeScoped", "      only bones driving painted regions", cX, y, cW, 24f); y += 26f;
        MakeButton(c, "Button_SharpOverlay", "Sharpness overlay (red = jagged)", cX, y, b2, 26f);
        MakeButton(c, "Button_HideMeshes", "Hide meshes...", cX + b2 + gap, y, b2, 26f); y += 32f;

        // ---------- PRESETS (last: one preset covers all three stages) ----------
        Header(c, "— Presets —", cX, cW, ref y);
        MakeDropdown(c, "Dropdown_Preset", cX, y, cW, 28f); y += 32f;
        MakeInput(c, "Input_presetname", "preset name", cX, y, cW, 24f); y += 28f;
        float pb = (cW - 8f) / 2f;
        MakeButton(c, "Button_PresetSave", "Save preset", cX, y, pb, 26f);
        MakeButton(c, "Button_PresetLoad", "Load preset", cX + pb + 8f, y, pb, 26f); y += 30f;
        MakeButton(c, "Button_PresetBind", "Auto-load for this model", cX, y, pb, 26f);
        MakeButton(c, "Button_PresetDelete", "Delete preset", cX + pb + 8f, y, pb, 26f); y += 30f;
        MakeInput(c, "Input_autoname", "model name includes...", cX, y, pb, 26f);
        MakeButton(c, "Button_PresetBindName", "Auto-load when name includes", cX + pb + 8f, y, pb, 26f); y += 30f;
        MakeButton(c, "Button_ManagePresets", "Manage presets...", cX, y, pb, 26f);
        MakeButton(c, "Button_ManageRules", "Manage auto-load...", cX + pb + 8f, y, pb, 26f); y += 30f;
        MakeToggle(c, "Toggle_presetauto", "Auto-load presets on model change", cX, y, cW, 22f); y += 26f;

        MakeText(c, "Hint", "Paint with LMB while Paint mode is on (blue = 0, red = 1).\nClick a section title to open or close it. F10 shows colliders.",
            cX, y, cW, 34f, 10, TextAnchor.UpperLeft, FontStyle.Italic); y += 40f;

        crt.sizeDelta = new Vector2(0f, y);

        // ---------- footer ----------
        float fy = headerH + viewportH + 12f;
        float f3 = (outerW - 2f * gap) / 3f;
        MakeButton(root.transform, "Button_Reload", "Reload", P, fy, f3, 28f);
        MakeButton(root.transform, "Button_Save", "Save", P + f3 + gap, fy, f3, 28f);
        MakeButton(root.transform, "Button_Close", "Close", P + 2f * (f3 + gap), fy, f3, 28f);

        // global enable pinned into the header row
        MakeToggle(root.transform, "Toggle_Enabled", "On", W - 64f, 10f, 56f, 22f);

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);
        return prefab;
    }

    static void Header(Transform c, string label, float x, float w, ref float y)
    {
        y += 4f;
        MakeText(c, "Hdr_" + label, label, x, y, w, 20f, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
        y += 24f;
    }

    static void SubHeader(Transform c, string label, float x, float w, ref float y)
    {
        y += 6f;
        MakeText(c, "Sub_" + label, label, x + 4f, y, w, 18f, 11, TextAnchor.MiddleLeft, FontStyle.Bold);
        y += 20f;
    }

    static void SliderRow(Transform c, string key, string label, float x, float w, ref float y)
    {
        MakeText(c, "Lbl_" + key, label, x, y, 120f, 22f, 11, TextAnchor.MiddleLeft, FontStyle.Normal);
        GameObject sl = DefaultControls.CreateSlider(_res);
        sl.name = "Slider_" + key;
        Place(sl.transform, c, x + 126f, y + 3f, w - 126f - 62f, 18f);
        // manual-entry value box (type a number, press enter)
        GameObject inp = DefaultControls.CreateInputField(_res);
        inp.name = "Value_" + key;
        Place(inp.transform, c, x + w - 58f, y, 58f, 22f);
        InputField f = inp.GetComponent<InputField>();
        if (f != null)
        {
            f.contentType = InputField.ContentType.DecimalNumber;
            StyleInputText(f.textComponent, TextAnchor.MiddleRight, new Color(0.05f, 0.05f, 0.07f, 1f));
            Text pt = f.placeholder as Text;
            if (pt != null) { pt.text = "0"; StyleInputText(pt, TextAnchor.MiddleRight, new Color(0.4f, 0.4f, 0.4f, 0.6f)); }
        }
        y += 26f;
    }

    static RectTransform Place(Transform t, Transform parent, float x, float y, float w, float h)
    {
        RectTransform rt = t as RectTransform;
        if (rt == null) rt = t.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x, -y);
        return rt;
    }

    static Text MakeText(Transform parent, string name, string text,
        float x, float y, float w, float h, int size, TextAnchor anchor, FontStyle style)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        Text t = go.GetComponent<Text>();
        t.text = text;
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = size; t.fontStyle = style; t.alignment = anchor;
        t.color = Color.white;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        Place(go.transform, parent, x, y, w, h);
        return t;
    }

    static void MakeToggle(Transform parent, string name, string label, float x, float y, float w, float h)
    {
        GameObject go = DefaultControls.CreateToggle(_res);
        go.name = name;
        Place(go.transform, parent, x, y, w, h);
        Transform lbl = go.transform.Find("Label");
        if (lbl != null)
        {
            Text lt = lbl.GetComponent<Text>();
            if (lt != null) { lt.text = label; lt.color = Color.white; lt.fontSize = 11; }
        }
    }

    static void MakeButton(Transform parent, string name, string label, float x, float y, float w, float h)
    {
        GameObject go = DefaultControls.CreateButton(_res);
        go.name = name;
        Place(go.transform, parent, x, y, w, h);
        Text bt = go.GetComponentInChildren<Text>(true);
        if (bt != null) { bt.text = label; bt.color = Color.black; bt.fontSize = 12; }
    }

    static void MakeDropdown(Transform parent, string name, float x, float y, float w, float h)
    {
        GameObject dd = DefaultControls.CreateDropdown(_res);
        dd.name = name;
        Place(dd.transform, parent, x, y, w, h);
    }

    static void MakeInput(Transform parent, string name, string placeholder, float x, float y, float w, float h)
    {
        GameObject inp = DefaultControls.CreateInputField(_res);
        inp.name = name;
        Place(inp.transform, parent, x, y, w, h);
        InputField field = inp.GetComponent<InputField>();
        if (field != null)
        {
            StyleInputText(field.textComponent, TextAnchor.MiddleLeft, new Color(0.05f, 0.05f, 0.07f, 1f));
            Text pt = field.placeholder as Text;
            if (pt != null) { pt.text = placeholder; StyleInputText(pt, TextAnchor.MiddleLeft, new Color(0.4f, 0.4f, 0.4f, 0.6f)); }
        }
    }

    static void StyleInputText(Text t, TextAnchor anchor, Color color)
    {
        if (t == null) return;
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = 12; t.alignment = anchor; t.color = color;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        RectTransform rt = t.rectTransform;
        rt.offsetMin = new Vector2(8f, 4f);
        rt.offsetMax = new Vector2(-8f, -4f);
    }
}
