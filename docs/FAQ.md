# FAQ

**Q: Why is it one plugin now instead of three?**
Mostly for the clothes. As separate plugins, each one fitted clothes on its own, in the
middle of the chain, a frame apart — so garments lagged or poked through. Merged, the stages
hand their result straight to each other in the same frame and the clothes are fitted once,
at the very end, to the body you actually see. You also get one window and presets that
cover everything. Each stage still has its own on/off tick.

**Q: I used the v1 studios. Do I lose my settings?**
No. On first start the suite reads your old `squishstudio.json`, `jellostudio.json` and
`wobblestudio.json` and brings the regions, colliders and slider values over (see
`softbodysuite.migration.log`). The old files aren't touched. Presets saved in the old Jello
Studio aren't carried over — save them again in the suite.

**Q: Where did SoftBody Studio go?**
It was an experimental variant and isn't part of the suite. Its source is in `legacy/` and
the plugin is still in the v1.2.0 release. If you keep it installed, keep it off while the
suite's Jell-o section is on — they both move the same body.

**Q: The installer couldn't find my VNyan.**
That's okay — VNyan doesn't have an installer, so it can live anywhere. The installer will
ask you to open your VNyan folder: pick **the folder with `VNyan.exe` inside it**. Picking a
folder just inside it (like `VNyan_Data` or `Items`) or the folder holding it works too.
Tip: starting VNyan once before installing helps the search, because VNyan's log remembers
where it ran from.

**Q: Why does the installer ask for admin rights?**
Only because your VNyan folder is somewhere Windows write-protects (for example, if you
put it inside Program Files). It checks first and only asks when the copy would otherwise
fail.

**Q: Do I need to re-rig or re-weight my avatar?**
No. The suite deforms the mesh after skinning. Nested/duplicate breast bones, weird
hierarchies, >4 bone influences baked down — all fine. Regions are painted in-app.

**Q: Does it work with face tracking / blendshapes / animations?**
Yes. The bake happens after blendshapes and animation each frame, and bone evacuation adds
on top of animation (it undoes itself every frame, so nothing builds up).

**Q: VSeeFace / other apps?**
VNyan only. The plugin uses VNyan's plugin loading and UI systems.

**Q: My clothes clip into the body / get left behind.**
Open **Clothes**, tick the section on and turn on **Move the clothes onto the body**. Raise
**Cloth follow range** if some parts of a garment don't follow, and use **Min cloth gap** or
**Keep clothes off the skin** for tight spots. As a last resort, **Tuck the body back in**
pushes flesh inside the garment instead.

**Q: My avatar's chest clips through my hands.**
1. In **Colliders**, add a **mesh collider** for your body mesh (or *(all meshes)*) — hands
and arms become capsules automatically. 2. In **Squish**, raise **squish depth / max dent**.
3. Add **Evacuate: move bones** (and *ALL bones* for deep presses). 4. Press **F10** to check
the capsules actually cover your hands.

**Q: The deformation looks jagged/sharp.**
Turn on **Simulate on a remeshed cage** in Jell-o — it exists exactly because dense,
mixed-density meshes give jagged results. Then raise **Softness (projection averaging)**
and the **Seam smoothing** sliders. The **sharpness overlay** shows where the jaggedness is.

**Q: The seam where the painted area meets the body is visible.**
In Jell-o's *Edge seam* part: raise **Seam smoothing level**, widen **range**, and use
**Seam max stretch** to cap how fast movement ramps up at the boundary. Painting a wider,
softer weight falloff also helps.

**Q: It's slow on my PC.**
See the performance section in [USAGE](USAGE.md): **async physics** for Squish and
**half-rate + smooth blend** for the others, and fewer substeps. The collision part already
sleeps when nothing is near. A lighter Jell-o cage (fewer **remesh passes**, longer **cage
edge length**) helps too.

**Q: My preset didn't auto-load.**
Check **Auto-load presets on model change** is ticked. Open **Manage auto-load…** — it shows
what the current model is called, and marks rules that can never match. Adding a rule with
**Auto-load when name includes** is the easiest fix.

**Q: Where are my settings saved? Can I share them?**
`%USERPROFILE%\AppData\LocalLow\Suvidriel\VNyan\softbodysuite.json` (and
`softbodysuite.presets.json` for presets). They're plain JSON — copy them to another PC or
back them up freely.

**Q: Two copies of my body are rendering!**
Make sure the old Squish / Wobble / Jello Studio folders aren't still inside
`Items\Assemblies` (the installer offers to move them). Otherwise, toggle the master **On**
off and on once, or hit **Reload**.

**Q: Spring bones / dynamic bones fight the effect.**
Leave **Disable spring/dynamic bones** on (scoped). It restores them when the suite is
turned off.

**Q: Can I use this on non-chest regions (belly, thighs, tail)?**
Yes — paint any region anywhere. Evacuation finds whichever bones mainly drive that region;
blob evacuation works even with no dedicated bones.

**Q: Something exploded / the mesh vanished.**
The solvers reset themselves if they go unstable (watch the log for "destabilised"). If a
region looks stuck, hit **Reload**. F11 dumps plus the log are the right things to share in
a bug report.
