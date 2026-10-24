# FAQ

**Q: What's new in Soft Body Suite 3?**
A fourth stage (**Field**) with one cage and one solver per painted region, ripples that start
from the touch point, a jolt, the nipples or points you click, contact shaping (Splay,
Push-out Ring, Lift), a skin guard for clothes, overall jiggle sliders, region presets, and a
lot more speed. See the [README](../README.md#whats-new-in-soft-body-suite-3).

**Q: Why did the installer move my classic Soft Body Suite?**
Soft Body Suite 3 replaces it, and two soft-body plugins running at once would move the same
body twice. The classic plugin folder (and the old v1 Squish / Wobble / Jello Studio folders)
were moved to `<your VNyan folder>\RetiredPlugins\`, never deleted. If you prefer the classic
suite, install the **v2.0.1 Final classic edition** release instead (see
[INSTALL](INSTALL.md#going-back-to-the-classic-suite)).

**Q: I used the classic suite (v2). Do I lose my settings?**
No. On first start, Soft Body Suite 3 reads your `softbodysuite.json` and
`softbodysuite.presets.json` once and brings the regions, paint, colliders, slider values,
presets and auto-load rules over (see `softfleshfield.migration.log`). The old files are
never changed, so the classic suite still works if you go back. Every region starts with
**Field Mix** at 0 (and the Field stage itself off), so your avatar looks exactly as before
until you turn the Field on.

**Q: I used the v1 studios. Do I lose my settings?**
No. If there's no classic `softbodysuite.json`, the suite reads your old `squishstudio.json`,
`jellostudio.json` and `wobblestudio.json` instead and brings the regions, colliders and
slider values over. The old files aren't touched. Presets saved in the old Jello Studio
aren't carried over: save them again in the suite.

**Q: Why is it one plugin instead of separate studios?**
Mostly for the clothes. As separate plugins, each one fitted clothes on its own, in the
middle of the chain, a frame apart, so garments lagged or poked through. Merged, the stages
hand their result straight to each other in the same frame and the clothes are fitted once,
at the very end, to the body you actually see. You also get one window and presets that
cover everything.

**Q: Where did SoftBody Studio go?**
It was an experimental variant and isn't part of the suite. Its source is in `legacy/` and
the plugin is still in the v1.2.0 release. If you keep it installed, keep it off while the
suite's Jell-o section is on: they both move the same body.

**Q: The installer couldn't find my VNyan.**
That's okay: VNyan doesn't have an installer, so it can live anywhere. The installer will
ask you to open your VNyan folder: pick **the folder with `VNyan.exe` inside it**. Picking a
folder just inside it (like `VNyan_Data` or `Items`) or the folder holding it works too.
Tip: starting VNyan once before installing helps the search, because VNyan's log remembers
where it ran from.

**Q: Why does the installer ask for admin rights?**
Only because your VNyan folder is somewhere Windows write-protects (for example, if you
put it inside Program Files). It checks first and only asks when the copy would otherwise
fail.

**Q: Can I rename the plugin files?**
No. The DLL must stay `SoftFleshField.dll` inside `Items\Assemblies\SoftFleshField\`, next to
`SoftFleshField.vnobj`: the `.vnobj` finds its scripts by that name.

**Q: Do I need to re-rig or re-weight my avatar?**
No. The suite deforms the mesh after skinning. Nested/duplicate breast bones, weird
hierarchies, >4 bone influences baked down: all fine. Regions are painted in-app.

**Q: Does it work with face tracking / blendshapes / animations?**
Yes. The bake happens after blendshapes and animation each frame, and bone evacuation adds
on top of animation (it undoes itself every frame, so nothing builds up).

**Q: VSeeFace / other apps?**
VNyan only. The plugin uses VNyan's plugin loading and UI systems.

**Q: I turned up Field Mix and nothing changes.**
First check the **Field · Master** tick is on: it switches the whole Field stage. Then check
that the region is painted and *Enabled*: each enabled, painted region gets its own
Field solver. Give the cage a few seconds to build after the stage starts or after you
repaint. Ripple and Fat Jiggle only move when something sets them off: a touch, or your own
movement once **Motion Sensitivity** (in **Field · Impulse**) is above 0. And if the region
barely moves at any setting, click **Normalize weights**: paint picked from bone weights can
top out near 20%.

**Q: How do I get ripples when I jump or dance, with nothing touching?**
In **Field · Impulse**, raise **Motion Sensitivity**. In **Field · Ripple**, tick **Ripple**
and keep *Start: outermost point (on a jolt)* on. If normal moves don't set it off, lower
*Move needed (lower = easier)*. If webcam or VMC tracking makes the flesh twitch all the
time, raise *Jerk Filter*.

**Q: The ripples start in the wrong place.**
Pick where they start in **Field · Ripple**: the outermost point on a jolt, where it's
touched, the nipples (auto-detected), or your own points. For your own, press **Pick ripple
points (click the body)**, click up to 6 spots on the region, press the button again, and
tick *Start: my picked points*. Cyan dots are your points, pink dots the found nipples.

**Q: Everything jiggles too much, or for too long.**
Use **Overall jiggle (all stages)** at the top of the window: **Jiggle strength** makes all of
it smaller with the same timing, **Jiggle settle time** makes it calm down sooner. Your
per-region tuning stays as it is.

**Q: My clothes clip into the body / get left behind.**
Open **Clothes**, tick the section on and turn on **Move the clothes onto the body**. Raise
**Cloth follow range** if some parts of a garment don't follow, and use **Min cloth gap** or
**Keep clothes off the skin** for tight spots. Keep **Push clothes out of the skin** (the skin
guard) on: it lifts cloth back out of the body you see. If the model's clothes already dip
into the skin (often under and between the breasts), raise **Guard: fix cloth sunk in skin**
slowly. As a last resort, **Tuck the body back in** pushes flesh inside the garment instead.

**Q: A bow or ribbon on my outfit tears apart when I move.**
Set **Floating parts: move as one** to 1 so floating accessories move as one rigid piece,
and raise **Floating if further than** if the accessory sits close to the body.

**Q: Tuck the body back in does nothing.**
It only runs while **Squish** is on and the **Field** stage is off. Moving the clothes with
the skin guard is the better fix in most cases anyway, because it leaves your body's shape
alone.

**Q: My avatar's chest clips through my hands.**
1. In **Colliders**, add a **mesh collider** for your body mesh (or *(all meshes)*): hands
and arms become capsules automatically. 2. In **Squish**, raise **squish depth / max dent**.
3. Add **Evacuate: move bones** (and *ALL bones* for deep presses). 4. Press **F10** to check
the capsules actually cover your hands. With the Field stage on, **Contact Hardness** and
**Collision Offset** in **Field · Contact** control how much overlap it allows.

**Q: The deformation looks jagged/sharp.**
Turn on **Simulate on a remeshed cage** in Jell-o: it exists exactly because dense,
mixed-density meshes give jagged results. Then raise **Softness (projection averaging)**
and the **Seam smoothing** sliders. In the Field stage, keep the Ripple and Fat Jiggle
**Amplitude** small (they are fractions of the region's own size, and big values fold the
surface through itself), and raise their **Smoothing**.

**Q: The seam where the painted area meets the body is visible.**
In Jell-o's *Edge seam* part: raise **Seam smoothing level**, widen **range**, and use
**Seam max stretch** to cap how fast movement ramps up at the boundary. Painting a wider,
softer weight falloff also helps.

**Q: It's slow on my PC.**
Soft Body Suite 3 is a lot faster than the classic suite: most heavy work now runs on all
your CPU cores (on the test avatar, Wobble + Jell-o + clothes went from about 45 fps to a
steady 60). If you still need more, see the performance section in [USAGE](USAGE.md):
**async physics** for Squish, **half-rate + smooth blend** for Wobble and Jell-o,
**Async Sim** for the Field stage, and fewer substeps. A lighter cage helps too: fewer
**remesh passes** and a longer **cage edge length** in Jell-o, a bigger **Cage Edge** in
**Field · Cage**.

**Q: My preset didn't auto-load.**
Check **Auto-load presets on model change** is ticked. Open **Manage auto-load...**: it shows
what the current model is called, and marks rules that can never match. Adding a rule with
**Auto-load when name includes** is the easiest fix.

**Q: I loaded an old preset and my Field settings are still there.**
That's on purpose. Presets brought over from the classic suite were saved before the Field
stage existed, so loading one leaves your current Field settings alone instead of wiping
them. Save the preset again in Soft Body Suite 3 and it will include the Field stage too.

**Q: How do I give another region the same tuning?**
In **Mesh & Region**, either save the region with **Save this region** and use **Apply here**
on the other one, or pick the source in **Copy from** and click **Copy here**. Paint,
colliders and picked ripple points stay with their own region, and Ctrl+Z undoes it.

**Q: Where are my settings saved? Can I share them?**
`%USERPROFILE%\AppData\LocalLow\Suvidriel\VNyan\softfleshfield.json` (plus
`softfleshfield.presets.json` for presets and `softfleshfield.regionpresets.json` for region
presets). They're plain JSON: copy them to another PC or back them up freely.

**Q: Two copies of my body are rendering!**
Make sure the classic `SoftBodySuite` folder and the old Squish / Wobble / Jello Studio
folders aren't still inside `Items\Assemblies` (the installer offers to move them).
Otherwise, toggle the master **On** off and on once, or hit **Reload**.

**Q: Spring bones / dynamic bones fight the effect.**
Leave **Disable spring/dynamic bones** on (scoped). It restores them when the suite is
turned off.

**Q: Can I use this on non-chest regions (belly, thighs, tail)?**
Yes: paint any region anywhere. Evacuation finds whichever bones mainly drive that region,
and blob evacuation works even with no dedicated bones. In the Field stage every region has
its own solver, so thighs and breasts can use completely different effects.

**Q: Something exploded / the mesh vanished.**
The solvers reset themselves if they go unstable (watch the log for "destabilised"). If a
region looks stuck, hit **Reload**. F11 dumps plus the log are the right things to share
when you ask for help.
