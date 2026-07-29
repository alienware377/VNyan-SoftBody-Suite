# Install Guide

## Requirements

- **VNyan** 1.3+ (Windows). The suite is tested on current VNyan releases.
- A `.vsfavatar` (or VRM loaded through VNyan). No special rig requirements — messy,
  nested, or unusual bone layouts are fine.
- Plugins must be enabled in VNyan: **Settings → Misc → Allow 3rd-party plugins**.

## Installing the plugins

1. Download the latest release zip(s) from the Releases page.
2. Close VNyan.
3. For each studio you want, create a folder under VNyan's plugin directory and copy the
   two files in:

```
C:\Program Files\VNyan\Items\Assemblies\SquishStudio\
    SquishStudio.dll
    SquishStudio.vnobj

C:\Program Files\VNyan\Items\Assemblies\WobbleStudio\
    WobbleStudio.dll
    WobbleStudio.vnobj

C:\Program Files\VNyan\Items\Assemblies\JelloStudio\
    JelloStudio.dll
    JelloStudio.vnobj
```

   (If VNyan is installed elsewhere, use that path. If VNyan lives in Program Files you
   will need admin rights for the copy.)

4. Start VNyan and load your avatar. Each studio registers a window under the VNyan
   plugins menu.
5. Open a studio window and flip its **On** toggle (top-right). The toggle state persists.

> **Which studios do I need?**
> Start with **Squish Studio** alone (collision + region painting). Add **Jello Studio**
> for the soft-body jiggle layer. Add **Wobble Studio** if you want the stylized wave /
> jell-o / cloth modes underneath. They chain automatically in the right order.

## Updating

Replace the `.dll` and `.vnobj` in the plugin's folder with the new versions while VNyan
is closed. Settings are stored separately (see below) and survive updates.

## Where settings live

Each studio saves its configuration as JSON in VNyan's data folder:

```
%USERPROFILE%\AppData\LocalLow\Suvidriel\VNyan\
    squishstudio.json
    wobblestudio.json
    jellostudio.json
```

Back these up along with the plugins if you want to preserve painted regions, colliders,
and slider values. **Squish Studio owns regions and colliders** — the other studios mirror
them automatically from `squishstudio.json`.

## Uninstalling / temporarily disabling

Delete (or rename to `.bak`) the plugin's folder contents under
`Items\Assemblies\<StudioName>\` while VNyan is closed. Renaming to `.bak` lets you
re-enable later by renaming back.

## Building from source

The runtime DLL is plain C# compiled against VNyan's bundled Unity assemblies:

```
csc.exe -noconfig -target:library -optimize+
  -reference:<VNyan>\VNyan_Data\Managed\netstandard.dll
  -reference:<VNyan>\VNyan_Data\Managed\System.dll
  -reference:<VNyan>\VNyan_Data\Managed\System.Core.dll
  -reference:<VNyan>\VNyan_Data\Managed\UnityEngine.CoreModule.dll
  -reference:<VNyan>\VNyan_Data\Managed\UnityEngine.InputLegacyModule.dll
  -reference:<VNyan>\VNyan_Data\Managed\UnityEngine.AnimationModule.dll
  -reference:<VNyan>\VNyan_Data\Managed\UnityEngine.UI.dll
  -reference:<VNyan>\VNyan_Data\Managed\UnityEngine.UIModule.dll
  -reference:<VNyan>\VNyan_Data\Managed\UnityEngine.TextRenderingModule.dll
  -reference:<VNyan>\VNyan_Data\Managed\UnityEngine.PhysicsModule.dll
  -reference:<VNyan>\VNyan_Data\Managed\VNyanInterface.dll
  -reference:<VNyan>\VNyan_Data\Managed\Newtonsoft.Json.dll
  -out:<StudioName>.dll  Scripts\*.cs
```

The `.vnobj` is a Unity AssetBundle containing the UI window prefab. Build it with
**Unity 2022.3 LTS**: make an empty project, drop the compiled DLL into `Assets/Plugins/`,
the studio's `Editor/SquishBuild.cs` into `Assets/Editor/` (plus the studio's `.shader`
file into `Assets/` where present), then run:

```
Unity.exe -batchmode -quit -projectPath <project> -executeMethod SquishBuild.Build
```

The bundle is written to `AssetBundles\<StudioName>.vnobj`.
