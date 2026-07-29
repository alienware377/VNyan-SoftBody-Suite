# FAQ

**Q: Why three plugins instead of one?**
Separation of concerns that you can mix per scene: Wobble = stylized motion, Jello =
soft-body simulation, Squish = collision + region/collider authoring. Each is useful
alone; chained together they compose into the full effect. Only the final stage renders,
so there's no double-drawing.

**Q: Do I need to re-rig or re-weight my avatar?**
No. The suite deforms the mesh after skinning. Nested/duplicate breast bones, weird
hierarchies, >4 bone influences baked down — all fine. Regions are painted in-app.

**Q: Does it work with face tracking / blendshapes / animations?**
Yes. The bake happens after blendshapes and animation each frame, and bone evacuation
composes additively with animation (it undoes itself every frame, in parent-local space,
so nothing accumulates).

**Q: VSeeFace / other apps?**
VNyan only. The plugins use VNyan's plugin loading and UI systems.

**Q: My avatar's chest clips through my hands.**
1. In Squish Studio, add a **mesh collider** for your body mesh (or *(all meshes)*) —
   hands/arms become capsules automatically. 2. Raise **squish depth / max dent**.
   3. Add **Evacuate: move bones** (and *ALL bones* for deep presses). 4. Check **F10**
   to see the capsules actually cover your hands.

**Q: The deformation looks jagged/sharp.**
Use Jello Studio's remeshed proxy mode — it exists precisely because dense, mixed-density
meshes produce jagged solver output. Then raise **Projection averaging range** and the
**Seam smoothing** sliders. The **sharpness overlay** shows exactly where the jaggedness
lives while you tune.

**Q: The seam where the painted area meets the body is visible.**
That's the *Edge seam* section: raise **Seam smoothing level**, widen **range**, and use
**Seam max stretch** to cap how quickly displacement ramps up at the boundary. Painting a
wider, softer weight falloff also helps.

**Q: It's slow on my PC.**
See the performance section in [USAGE](USAGE.md): enable **Async physics** in Squish and
**Half-rate + smooth blend** in the others. The collision pipeline already sleeps
automatically when nothing is near. Also lower **Remesh passes** / raise **Cage edge
length** in Jello for a lighter sim cage.

**Q: My VNyan isn't in Program Files — can I still use the installer?**
Yes — that's what it's for. Run `install.bat`, answer `n` when it offers the default
location (or if VNyan isn't found there you go straight to the picker), and browse to
your VNyan folder. Unprotected locations install without any admin prompt.

**Q: Why does the installer ask for admin rights?**
Only because your VNyan folder is somewhere Windows write-protects (like
`C:\Program Files`). The installer probes writability first and elevates only when the
copy would otherwise fail — portable installs never trigger UAC.

**Q: Where are my settings saved? Can I share them?**
`%USERPROFILE%\AppData\LocalLow\Suvidriel\VNyan\<studio>.json`. They're plain JSON —
copy them to another machine (regions/colliders included) or back them up freely.

**Q: The breasts/regions slowly drift or extend over time.**
Update to the latest release — early builds had a bone-offset accumulation bug under
rotating animations, fixed by applying evacuation offsets in parent-local space.

**Q: Two copies of my body are rendering!**
Usually a stale chain after toggling studios very quickly. Toggle the final enabled studio
off/on once, or hit *Reload* in its window. The chain re-wires within a second.

**Q: Spring bones / dynamic bones fight the effect.**
Leave **Disable spring/dynamic bones** on (scoped) in the studios you use. It restores
them automatically when the studio is turned off.

**Q: Can I use this on non-chest regions (belly, thighs, tail)?**
Yes — paint any region anywhere. Evacuation auto-detects whichever bones predominantly
drive that region; blob evacuation works even with no dedicated bones.

**Q: Something exploded / the mesh vanished.**
The solvers auto-reset on instability (watch the log for "destabilised"). If a region
looks stuck, hit *Reload* in the window. F11 dumps + the log are the right thing to share
in a bug report.
