using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Builds the Wobble Studio window prefab + starter prefab and packs them into the
// VNyan .vnobj asset bundle. Invoked via:
//   Unity.exe -batchmode -quit -executeMethod SquishBuild.Build
public static class SquishBuild
{
    static DefaultControls.Resources _res;

    public static void Build()
    {
        const string windowPrefabPath = "Assets/SquishWindow.prefab";
        const string starterPrefabPath = "Assets/WobbleStudioStarter.prefab";
        const string bundleName = "wobblestudio_bundle";
        const string outDir = "AssetBundles";

        GameObject windowAsset = BuildWindowPrefab(windowPrefabPath);

        GameObject go = new GameObject("VNyanTemp");
        WobbleStudio.SquishPlugin plugin = go.AddComponent<WobbleStudio.SquishPlugin>();
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
        string final = Path.Combine(outDir, "WobbleStudio.vnobj");
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

        root.AddComponent<WobbleStudio.SquishWindowDrag>();

        float hy = 10f;
        MakeText(root.transform, "Title", "Wobble Studio — Soft-Body Mesh Deformation",
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

        // ---------- mesh & region (regions are OWNED by Squish Studio and mirrored here) ----------
        Header(c, "— Mesh & Region —", cX, cW, ref y);
        MakeText(c, "Note_NeedsSquish",
            "Regions are created && painted in SQUISH STUDIO (required —\ninstall it too). This plugin mirrors its regions automatically.",
            cX, y, cW, 30f, 10, TextAnchor.UpperLeft, FontStyle.Italic); y += 36f;
        MakeText(c, "Lbl_Mesh", "Mesh", cX, y, 70f, 30f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeDropdown(c, "Dropdown_Mesh", cX + 76f, y, cW - 76f, 30f); y += 34f;
        float b2 = (cW - gap) / 2f;
        MakeButton(c, "Button_EnableMesh", "Enable wobble on mesh", cX, y, b2, 26f);
        MakeButton(c, "Button_DisableMesh", "Disable", cX + b2 + gap, y, b2, 26f); y += 32f;
        float b3 = (cW - 2f * gap) / 3f;
        MakeText(c, "Lbl_Region", "Region", cX, y, 70f, 30f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeDropdown(c, "Dropdown_Region", cX + 76f, y, cW - 76f - b3 - gap, 30f);
        MakeToggle(c, "Toggle_RegionEnabled", "Enabled", cX + cW - b3, y, b3, 24f); y += 36f;

        // ---------- motion ----------
        Header(c, "— Motion —", cX, cW, ref y);
        SliderRow(c, "jiggle", "Jiggle level", cX, cW, ref y);
        SliderRow(c, "stiffness", "Stiffness", cX, cW, ref y);
        SliderRow(c, "damping", "Damping", cX, cW, ref y);
        SliderRow(c, "bounce", "Drag (inertia)", cX, cW, ref y);
        SliderRow(c, "maxoff", "Max deform (m)", cX, cW, ref y);

        // ---------- gravity ----------
        Header(c, "— Gravity —", cX, cW, ref y);
        SliderRow(c, "gravity", "Gravity level", cX, cW, ref y);
        MakeToggle(c, "Toggle_GravityPose", "Only when the parent bone leaves its rest pose", cX, y, cW, 24f); y += 28f;
        MakeText(c, "Lbl_RefBone", "Ref bone", cX, y, 70f, 28f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeDropdown(c, "Dropdown_RefBone", cX + 76f, y, cW - 76f, 28f); y += 34f;

        // ---------- waves ----------
        Header(c, "— Waves & ripples —", cX, cW, ref y);
        SliderRow(c, "cloth", "Cloth ripple", cX, cW, ref y);
        SliderRow(c, "clothsize", "Cloth ripple size", cX, cW, ref y);
        SliderRow(c, "jello", "Jell-o wobble", cX, cW, ref y);
        SliderRow(c, "jellosize", "Jell-o size", cX, cW, ref y);
        SliderRow(c, "jellorand", "Jell-o randomizer", cX, cW, ref y);
        SliderRow(c, "liquid", "Liquid ripple", cX, cW, ref y);
        SliderRow(c, "liquidsize", "Liquid ripple size", cX, cW, ref y);
        SliderRow(c, "wavespeed", "Wave speed", cX, cW, ref y);

        // ---------- extra jiggle modes ----------
        Header(c, "— Extra jiggle modes —", cX, cW, ref y);
        SliderRow(c, "sway", "Sway (pendulum)", cX, cW, ref y);
        SliderRow(c, "twistj", "Twist wobble", cX, cW, ref y);
        SliderRow(c, "pulse", "Pulse (breathe)", cX, cW, ref y);
        SliderRow(c, "pulserate", "Pulse rate", cX, cW, ref y);
        SliderRow(c, "stretch", "Squash & stretch", cX, cW, ref y);
        SliderRow(c, "turb", "Turbulence", cX, cW, ref y);
        SliderRow(c, "turbsize", "Turbulence size", cX, cW, ref y);

        // ---------- surface ----------
        Header(c, "— Surface —", cX, cW, ref y);
        SliderRow(c, "cellulite", "Cellulite level", cX, cW, ref y);
        SliderRow(c, "cellsize", "Cellulite size", cX, cW, ref y);

        // ---------- native bone physics ----------
        Header(c, "— Native bone physics —", cX, cW, ref y);
        MakeToggle(c, "Toggle_HalfRate", "Half-rate physics (lighter on slow PCs)", cX, y, cW, 24f); y += 26f;
        MakeToggle(c, "Toggle_HalfRateLerp", "Half-rate + smooth blend (lerp)", cX, y, cW, 24f); y += 26f;
        MakeToggle(c, "Toggle_NativeOff", "Disable spring/dynamic bones (they fight the squish)", cX, y, cW, 24f); y += 26f;
        MakeToggle(c, "Toggle_NativeScoped", "      only bones driving painted regions", cX, y, cW, 24f); y += 30f;

        MakeText(c, "Hint", "Regions mirror from Squish Studio automatically (edited there).\nSave writes wobblestudio.json.",
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
