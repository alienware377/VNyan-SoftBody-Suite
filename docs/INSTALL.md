# Install Guide

## Requirements

- **VNyan** 1.3+ (Windows). The suite is tested on current VNyan releases.
- A `.vsfavatar` (or VRM loaded through VNyan). No special rig requirements: messy,
  nested, or unusual bone layouts are fine.
- Plugins must be enabled in VNyan: **Settings → Misc → Allow 3rd-party plugins**.

> **Where is my VNyan folder?** VNyan doesn't come with an installer. It runs from
> wherever you unzipped it (or wherever Steam / the itch app put it). Your VNyan folder is
> simply **the folder with `VNyan.exe` inside it**. Everything below says
> `<your VNyan folder>` for that.

## Option A: installer (recommended)

1. Download **`VNyan-SoftBody-Suite-v3.0.0.zip`** and unzip it anywhere.
2. Close VNyan and double-click **`install.bat`**. (If VNyan is still open, the installer
   waits and asks you to close it, because VNyan locks its plugin files.)
3. The installer **looks for VNyan by itself**: a VNyan that's open, the log VNyan keeps
   from the last time it ran, your Steam libraries, the apps Windows lists as installed,
   and the itch app.
   - Found it? It shows you where and asks you to confirm. If it finds VNyan in more than
     one place, it lists them and you type the number of the one you want.
   - Didn't find it, or you said no? It asks you to **open your VNyan folder**: the one
     with `VNyan.exe` inside. (Picking a folder just inside or around it works too.)
4. If the **classic Soft Body Suite** (v2, the `Items\Assemblies\SoftBodySuite` folder) or
   the old separate **Squish / Wobble / Jello Studio** plugins are installed, it offers to
   move them to `<your VNyan folder>\RetiredPlugins\`. Please say yes: VNyan loads every
   folder in `Items\Assemblies`, so if they stay, two soft-body plugins move the same body
   twice. They're moved, never deleted.
5. Admin rights are requested **only if your VNyan folder is write-protected**. Most VNyan
   folders never see a Windows admin prompt.
6. Start VNyan, open **Soft Body Suite** from the plugins menu and tick **On**. Done.

For scripts or unattended installs:

```
powershell -ExecutionPolicy Bypass -File install.ps1 -Target "<your VNyan folder>" -Yes
powershell -ExecutionPolicy Bypass -File install.ps1 -ListFound
```

`-Target` skips the search, `-Yes` answers yes to every question, and `-ListFound` only
prints the VNyan folders the search finds.

## Option B: manual copy

1. Close VNyan.
2. Copy the two files from the zip's `SoftFleshField` folder into:

```
<your VNyan folder>\Items\Assemblies\SoftFleshField\
    SoftFleshField.dll
    SoftFleshField.vnobj
```

   Keep the names exactly as they are. The DLL **must** be called `SoftFleshField.dll`,
   because the `.vnobj` finds its scripts by that name.

3. **Retire the older soft-body plugins.** Move these folders **out of** `Items\Assemblies`
   if you have them (for example into `<your VNyan folder>\RetiredPlugins\`). Renaming them
   isn't enough: VNyan loads every folder in there.

```
Items\Assemblies\SoftBodySuite\    (the classic suite, v2)
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

## Going back to the classic suite

Install the **v2.0.1 Final classic edition** release, and move
`<your VNyan folder>\Items\Assemblies\SoftFleshField\` out of `Items\Assemblies` so only
one soft-body plugin is loaded. Soft Body Suite 3 never changed your classic settings files,
so the classic suite picks up exactly where you left it. Anything you set up in Soft Body
Suite 3 stays in its own files (below) for when you come back.

## Where settings live

```
%USERPROFILE%\AppData\LocalLow\Suvidriel\VNyan\
    softfleshfield.json                regions, colliders and every slider
    softfleshfield.presets.json        presets and auto-load rules
    softfleshfield.regionpresets.json  region presets
    softfleshfield.migration.log       what was brought over (first start only)
```

**Coming from the classic suite (v2):** the first time Soft Body Suite 3 starts and finds no
`softfleshfield.json`, it reads `softbodysuite.json` and `softbodysuite.presets.json` **once**
and brings everything over: regions, paint, colliders, every slider, presets and auto-load
rules. Those files are never changed, so going back to the classic suite still works. Every
region starts with **Field Mix** at 0, so your avatar looks exactly as it did until you turn
the Field stage up.

**Coming straight from v1 (the separate studios):** if there is no classic
`softbodysuite.json` either, it reads `squishstudio.json`, `jellostudio.json` and
`wobblestudio.json` instead. Those aren't changed either. Presets saved in the old Jello
Studio aren't carried over: save them again in the suite.

Back these files up if you want to keep your painted regions, colliders and presets.

## Diagnostics (optional)

Create an **empty file** with one of these names next to the settings (in the folder above)
to switch a diagnostic on. Delete it again and restart VNyan to switch it off.

| File | What it does |
|---|---|
| `softfleshfield.perf.on` | Writes a performance line (fps and the time each stage takes) to VNyan's `Player.log` every 4 seconds |
| `softfleshfield.followdiag.on` | Turns on the clothes follow logger, which writes a `softfleshfield.followdiag_<garment>.csv` per garment next to the settings |

**F10** (or **Show / hide colliders**) draws the collider capsules on the avatar.

## Uninstalling / temporarily disabling

Close VNyan and move `<your VNyan folder>\Items\Assemblies\SoftFleshField\` somewhere outside
`Items\Assemblies`. Move it back to re-enable.

## Building from source

The runtime DLL is plain **C# 5** compiled against VNyan's bundled assemblies (Framework
`csc` works). The sources are every `.cs` file under `SoftBodySuite\Scripts\`, **including the
`Field\` subfolder**. The output **must** be named `SoftFleshField.dll`, because the `.vnobj`
looks the scripts up by that assembly name.

```
csc.exe -noconfig -target:library -optimize+ -langversion:5
  -reference:<your VNyan folder>\VNyan_Data\Managed\netstandard.dll
  -reference:<your VNyan folder>\VNyan_Data\Managed\System.dll
  -reference:<your VNyan folder>\VNyan_Data\Managed\System.Core.dll
  -reference:<your VNyan folder>\VNyan_Data\Managed\VNyanInterface.dll
  -reference:<your VNyan folder>\VNyan_Data\Managed\Newtonsoft.Json.dll
  -reference:<your VNyan folder>\VNyan_Data\Managed\UnityEngine*.dll   (every UnityEngine module)
  -out:SoftFleshField.dll  -recurse:SoftBodySuite\Scripts\*.cs
```

The `.vnobj` is a Unity AssetBundle holding the window prefab. Build it with
**Unity 2022.3 LTS**: make an empty project, put `SoftFleshField.dll`, `VNyanInterface.dll`
and `Newtonsoft.Json.dll` in `Assets/Plugins/` and `SoftBodySuite/Editor/SuiteBuild.cs` in
`Assets/Editor/`, then run:

```
Unity.exe -batchmode -quit -projectPath <project> -executeMethod SuiteBuild.Build
```

The bundle is written to `AssetBundles\SoftFleshField.vnobj`.

The Field stage's rows are not part of the window prefab: they are declared in code
(`SoftBodySuite\Scripts\Field\`) and added to the window when the plugin starts, so changing
them only needs a new DLL.

The source of the classic v2 suite is in `legacy\SoftBodySuite-v2\`, and the old separate
v1 studios are in `legacy\`.
