using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Builds the Squish Studio window prefab + starter prefab and packs them into the
// VNyan .vnobj asset bundle. Invoked via:
//   Unity.exe -batchmode -quit -executeMethod SquishBuild.Build
public static class SquishBuild
{
    static DefaultControls.Resources _res;

    public static void Build()
    {
        const string windowPrefabPath = "Assets/SquishWindow.prefab";
        const string starterPrefabPath = "Assets/SquishStudioStarter.prefab";
        const string bundleName = "squishstudio_bundle";
        const string outDir = "AssetBundles";

        GameObject windowAsset = BuildWindowPrefab(windowPrefabPath);

        GameObject go = new GameObject("VNyanTemp");
        SquishStudio.SquishPlugin plugin = go.AddComponent<SquishStudio.SquishPlugin>();
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

        if (manifest == null) { Debug.LogError("[SquishBuild] bundle build failed"); EditorApplication.Exit(2); return; }
        string built = Path.Combine(outDir, bundleName);
        string final = Path.Combine(outDir, "SquishStudio.vnobj");
        if (File.Exists(final)) File.Delete(final);
        File.Copy(built, final);
        Debug.Log("[SquishBuild] wrote " + final);
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

        GameObject root = new GameObject("SquishWindow",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform rrt = root.GetComponent<RectTransform>();
        rrt.anchorMin = new Vector2(0.5f, 0.5f); rrt.anchorMax = new Vector2(0.5f, 0.5f);
        rrt.pivot = new Vector2(0.5f, 0.5f);
        rrt.sizeDelta = new Vector2(W, totalH);
        Image bg = root.GetComponent<Image>();
        bg.sprite = _res.background; bg.type = Image.Type.Sliced;
        bg.color = new Color(0.13f, 0.11f, 0.16f, 0.97f);

        root.AddComponent<SquishStudio.SquishWindowDrag>();

        float hy = 10f;
        MakeText(root.transform, "Title", "Squish Studio — Collision Squish && Bulge",
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

        // ---------- squish ----------
        Header(c, "— Squish —", cX, cW, ref y);
        SliderRow(c, "squish", "Squish level", cX, cW, ref y);
        SliderRow(c, "squishdepth", "Squish depth (m, 0=auto)", cX, cW, ref y);
        SliderRow(c, "bulge", "Bulge level", cX, cW, ref y);
        SliderRow(c, "selfsquish", "Region self-squish", cX, cW, ref y);
        SliderRow(c, "maxdent", "Max dent before give-way (m)", cX, cW, ref y);
        SliderRow(c, "evacbone", "Evacuate: move bones", cX, cW, ref y);
        SliderRow(c, "evacall", "Evacuate: ALL bones (nested too)", cX, cW, ref y);
        SliderRow(c, "evacblob", "Evacuate: shift blob", cX, cW, ref y);

        // ---------- colliders ----------
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

        // ---------- native bone physics ----------
        Header(c, "— Native bone physics —", cX, cW, ref y);
        MakeToggle(c, "Toggle_HalfRate", "Half-rate physics (lighter on slow PCs)", cX, y, cW, 24f); y += 26f;
        MakeToggle(c, "Toggle_HalfRateLerp", "Half-rate + smooth blend (lerp)", cX, y, cW, 24f); y += 26f;
        MakeToggle(c, "Toggle_AsyncSim", "Async physics (worker thread)", cX, y, cW, 24f); y += 26f;
        MakeToggle(c, "Toggle_NativeOff", "Disable spring/dynamic bones (they fight the squish)", cX, y, cW, 24f); y += 26f;
        MakeToggle(c, "Toggle_NativeScoped", "      only bones driving painted regions", cX, y, cW, 24f); y += 30f;

        MakeText(c, "Hint", "Paint with LMB while Paint mode is on (blue = 0, red = 1).\nClick a ? for help. F10 shows colliders. Regions sync to Wobble Studio.",
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
