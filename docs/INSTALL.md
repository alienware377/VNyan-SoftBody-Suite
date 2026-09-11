# Install Guide

## Requirements

- **VNyan** 1.3+ (Windows). The suite is tested on current VNyan releases.
- A `.vsfavatar` (or VRM loaded through VNyan). No special rig requirements — messy,
  nested, or unusual bone layouts are fine.
- Plugins must be enabled in VNyan: **Settings → Misc → Allow 3rd-party plugins**.

> **Where is my VNyan folder?** VNyan doesn't come with an installer — it runs from
> wherever you unzipped it (or wherever Steam / the itch app put it). Your VNyan folder is
> simply **the folder with `VNyan.exe` inside it**. Everything below says
> `<your VNyan folder>` for that.

## Option A — installer (recommended)

1. Download **`VNyan-SoftBody-Suite-v2.0.0.zip`** and unzip it anywhere.
2. Close VNyan and double-click **`install.bat`**.
3. The installer **looks for VNyan by itself** — a VNyan that's open, the log VNyan keeps
   from the last time it ran, your Steam libraries, and the itch app.
   - Found it? It shows you where and asks you to confirm.
   - Didn't find it, or you said no? It asks you to **open your VNyan folder** — the one
     with `VNyan.exe` inside. (Picking a folder just inside or around it works too.)
4. If the old separate **Squish / Wobble / Jello Studio** plugins are installed, it offers to
   move them to `<your VNyan folder>\RetiredPlugins\`. Please say yes: VNyan loads every
   folder in `Items\Assemblies`, so if they stay the body gets moved twice. They're moved,
   never deleted.
5. Admin rights are requested **only if your VNyan folder is write-protected**. Most VNyan
   folders never see a Windows admin prompt.
6. Start VNyan — done.

For scripts or unattended installs:

```
powershell -ExecutionPolicy Bypass -File install.ps1 -Target "D:\Apps\VNyan" -Yes
powershell -ExecutionPolicy Bypass -File install.ps1 -ListFound
```

`-ListFound` only prints the VNyan folders the search finds.

## Option B — manual copy

1. Close VNyan.
2. Copy the two files from the zip's `SoftBodySuite` folder into:

```
<your VNyan folder>\Items\Assemblies\SoftBodySuite\
    SoftBodySuite.dll
    SoftBodySuite.vnobj
```

3. **Upgrading from v1?** Move these folders **out of** `Items\Assemblies` (for example into
   `<your VNyan folder>\RetiredPlugins\`) — renaming them isn't enough, VNyan loads every
   folder in there:

```
Items\Assemblies\SquishStudio\
Items\Assemblies\WobbleStudio\
Items\Assemblies\JelloStudio\
```

4. Start VNyan, load your avatar, and open **Soft Body Suite** from the plugins menu. Tick
   **On** at the top of the window.

If your VNyan folder is write-protected, Windows will ask for admin rights for the copy.

## Updating

Run the new `install.bat`, or replace the two files while VNyan is closed. Settings are
stored separately (see below) and survive updates.

## Where settings live

```
%USERPROFILE%\AppData\LocalLow\Suvidriel\VNyan\
    softbodysuite.json            regions, colliders and every slider
    softbodysuite.presets.json    presets and auto-load rules
    softbodysuite.migration.log   what was brought over from v1 (first start only)
```

**Coming from v1:** the first time the suite starts and finds no `softbodysuite.json`, it
reads `squishstudio.json`, `jellostudio.json` and `wobblestudio.json` and brings everything
over. The old files aren't changed, so going back to v1.2.0 still works. Presets saved in
the old Jello Studio aren't carried over — save them again in the suite.

Back these files up if you want to keep your painted regions, colliders and presets.

## Uninstalling / temporarily disabling

Close VNyan and move `<your VNyan folder>\Items\Assemblies\SoftBodySuite\` somewhere outside
`Items\Assemblies`. Move it back to re-enable.

## Building from source

The runtime DLL is plain C# 5 compiled against VNyan's bundled assemblies (Framework `csc`
works). The output **must** be named `SoftBodySuite.dll` — the `.vnobj` looks the scripts up
by that assembly name.

```
csc.exe -noconfig -target:library -optimize+
  -reference:<VNyan>\VNyan_Data\Managed\netstandard.dll
  -reference:<VNyan>\VNyan_Data\Managed\System.dll
  -reference:<VNyan>\VNyan_Data\Managed\System.Core.dll
  -reference:<VNyan>\VNyan_Data\Managed\VNyanInterface.dll
  -reference:<VNyan>\VNyan_Data\Managed\Newtonsoft.Json.dll
  -reference:<VNyan>\VNyan_Data\Managed\UnityEngine*.dll   (every UnityEngine module)
  -out:SoftBodySuite.dll  SoftBodySuite\Scripts\*.cs
```

The `.vnobj` is a Unity AssetBundle holding the window prefab. Build it with
**Unity 2022.3 LTS**: make an empty project, put `SoftBodySuite.dll`, `VNyanInterface.dll`
and `Newtonsoft.Json.dll` in `Assets/Plugins/` and `SoftBodySuite/Editor/SuiteBuild.cs` in
`Assets/Editor/`, then run:

```
Unity.exe -batchmode -quit -projectPath <project> -executeMethod SuiteBuild.Build
```

The bundle is written to `AssetBundles\SoftBodySuite.vnobj`.
