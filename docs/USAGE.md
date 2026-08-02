# Usage Guide

Every control in every studio has a **`?` chip** — click it for an in-app explanation.
This guide covers the workflow and what the important sections do.

## The chain (how the studios cooperate)

```
skinned avatar mesh
   └─ Wobble Studio   (order 19000)  — stylized jiggle/waves
        └─ Jello Studio (order 20600) — soft-body cage simulation
             └─ Squish Studio (order 20700, FINAL) — collision squish + evacuation
```

Each stage reads the previous stage's output mesh and only the **final** enabled stage
renders. Chaining is automatic — enable any subset and they wire themselves. Regions and
colliders are authored once in **Squish Studio** and mirror to the others within ~2 s.

## 1. Paint a region (Squish Studio)

1. Pick your body **Mesh** in the dropdown and click *Enable soft-body on mesh*.
2. Create/select a **Region**, then either:
   - **Paint** — brush weights directly on the model (radius/strength sliders, blur), or
   - **Select from bones** — pick bone(s) from the multi-select list to auto-weight the
     region from skinning (supports unions of several bones).
3. The **overlay** shows painted weight as a heatmap. Paint generously past the area you
   want to move — weights taper the motion smoothly at the boundary.

### Copy regions to other meshes

Clothing/accessory meshes covering the same body part should jiggle with it. Click
**Apply to other meshes** and pick a method:

- **Auto: same bones as the painted area** (default) — the plugin reads which bones
  actually skin your painted verts (full multi-weight data, strongest first) and selects
  the matching verts on each target mesh. The **weight-share ≥ %** stepper controls how
  dominant a bone must be to count.
- **By bone group** — reuses your last manual bone-group pick.
- **By surface transfer** — projects the painted area through space onto each target
  (**projection radius** stepper); best when skeletons/skinning differ.

Tick target meshes (**All meshes**/**None**), optionally **Copy ALL regions of this
mesh** to move every region in one click (Auto/surface methods). Copies carry all sim
params + colliders; existing paint on a target is only overwritten when the method
actually found vertices there, and every overwrite is undoable (Ctrl+Z).

A successful apply auto-saves, so Squish sims the new regions instantly and Wobble /
Jello mirror them within ~2 s (including a background rebuild of the remeshed sim
cage). One caveat: a mesh that's *brand-new* to Wobble/Jello arrives there disabled —
enable it once in that studio's mesh dropdown and it stays in sync from then on.

## 2. Colliders (Squish Studio)

- **Mesh collider** (recommended): pick a skinned mesh (your body, or *(all meshes)*) —
  arm/hand/finger bone chains are auto-reduced to smooth capsules, with merged hand
  "mitts" so fingers can't slip between capsules. Painted verts are excluded so a region
  never collides with itself.
- **Bone collider**: a single sphere/capsule on any bone (pick from the bone dropdown,
  set radius/length). Good for props.
- **F10** shows every active collider as translucent capsules + logs perf numbers.

## 3. Make it squish (Squish Studio)

Key sliders:

- **Squish depth / Max dent** — how deep a press dents before the flesh "gives way".
- **Evacuate: move bones** — past the dent limit, the region's own driver bones translate
  away from the press (auto-detected; only whole chains move, so messy rigs can't fight).
- **Evacuate: ALL bones (nested too)** — nested child bones also evacuate individually
  for deeper, more local get-out-of-the-way.
- **Evacuate: shift blob** — whole-region water-balloon shift.
- **Self squish** — regions press each other (e.g. chest vs arm regions).

## 4. Make it jiggle (Jello Studio)

Enable **Sim on remeshed proxy mesh (all regions)**. The plugin duplicates your painted
regions, remeshes the copy to uniform triangles in the background (~2 s), simulates that
cage, and projects the deformation back — your mesh's actual topology never touches the
solver, which is where the smoothness comes from.

The recipe that works well as a starting point:

- Interior kept loose: *jiggle 1*, solver iterations 1, most stiffness sliders at 0
- **Projection averaging range** ≈ 9 (the big smoothness knob)
- **Seam smoothing level** ≈ 20, **range** ≈ 0.018 m (the painted↔unpainted boundary)
- **Seam max stretch** ≈ 0.01–0.05 (limits how fast movement ramps up away from the seam)
- **Max stretch** small (≈ 0.01–0.06) to leash the interior

Wobble Studio's modes (jell-o, waves, cloth, liquid, sway, pulse, turbulence + the
**jell-o randomizer**) layer *underneath* Jello/Squish and are all per-region sliders.

## 5. Performance options

- **Sleep gate** (Squish, automatic): the collision pipeline only runs while a collider is
  near a region. Idle cost is near zero.
- **Half-rate physics**: compute every 2nd frame, hold the result. Cheapest.
- **Half-rate + smooth blend (lerp)**: same cost, held frames blend between ticks.
- **Async physics (worker thread)** (Squish): the entire sim runs on a background thread —
  best FPS during contact, one frame of physics latency.
- The three rate options are mutually exclusive and untick each other.
- Suggested slower-PC setup: Squish **async ON**, Jello + Wobble **half-rate lerp ON**.

## 6. Native bone physics

Spring/dynamic bones driving the same body parts fight the mesh simulation. Every studio
has **Disable spring/dynamic bones** (scoped to painted regions by default) — leave it on
unless you know you want both.

## Troubleshooting tools

- **F10** — collider visualisation + per-frame `bake+chain` / `sim+write` ms in the log.
- **F11** — dumps the displaced mesh, rest mesh, and per-node field CSVs to
  `AppData\LocalLow\Suvidriel\VNyan\<studio>debug\` for offline analysis.
- **Sharpness overlay** — paints polygon-angle acuteness (red = jagged) live on the model.
- **Show / hide remeshed mesh** (Jello) — displays the sim cage floating just above the
  skin, colored by paint weight.
- The VNyan log (`AppData\LocalLow\Suvidriel\VNyan\Player.log`) reports chain wiring,
  cage builds, collider construction, and any instability resets.
