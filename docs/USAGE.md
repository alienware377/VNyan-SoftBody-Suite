# Usage Guide

Most controls have a **`?` chip** — click it for an in-app explanation. This guide covers
the workflow and what the important sections do.

## The window

Open **Soft Body Suite** from VNyan's plugins menu. The **On** tick at the top is the master
switch. Below it is one long list of sections, all closed at first — **click a section title
to open or close it**:

| Section | What's in it |
|---|---|
| Mesh & Region | Which mesh to simulate, and its regions |
| Weight painting | Brush, bone-group picking, copying regions to other meshes |
| Colliders | What can press into the body (hands, arms, props) |
| **Wobble** ☐ | Stylized motion stage |
| **Jell-o** ☐ | Soft-body cage stage |
| **Squish** ☐ | Collision stage |
| **Clothes** ☐ | Clothes fitting, done once at the end |
| Performance and native bones | Substeps, frame-rate savers, spring bones |
| Presets | Save / load / auto-load everything |

The four stage sections have a **tick in their title** that turns that stage on or off. The
footer has **Reload** (re-read from disk), **Save** and **Close** (the plugin keeps running).

## The chain

```
skinned avatar mesh
   └─ Wobble    — stylized jiggle / waves
        └─ Jell-o  — soft-body cage simulation
             └─ Squish  — collision squish + evacuation
                  └─ Clothes — fitted once, after everything else
```

Each stage hands its result straight to the next one in the same frame, and only the last
running stage draws. Turn any stage off and the others close the gap by themselves.

## 1. Mesh & Region

1. Pick your body **Mesh** and click *Enable squish on mesh*.
2. Add a **Region** (*+ Region*) and give it a name.
3. A region is painted **once**, but each stage keeps **its own values** for it — so the
   Wobble, Jell-o and Squish sliders always edit the region picked here.

## 2. Weight painting

- **Paint mode** — brush weights directly on the model with the left mouse button
  (radius / strength sliders, *Blur weights* to soften the edges, Ctrl+Z to undo).
- **Pick vertex groups…** — pick bone(s) from a multi-select list to fill the region from
  the skinning (tick *Also include child bones* for whole branches).
- The **overlay** shows the paint as colors: blue = 0, red = 1. Paint generously past the
  area you want to move — weights taper the motion smoothly at the boundary.

### Copy regions to other meshes

Click **Apply region to other meshes…** and pick a method:

- **Auto: same bones as the painted area** (default) — reads which bones actually skin
  your painted verts and selects the matching verts on each target mesh. The
  **weight-share ≥ %** stepper controls how dominant a bone must be to count.
- **By bone group** — reuses your last bone-group pick.
- **By surface transfer** — projects the painted area through space onto each target
  (**projection radius** stepper); best when skeletons/skinning differ.

Tick the target meshes, and optionally **Copy ALL regions of this mesh**. Copies carry every
stage's values and the colliders; every overwrite is undoable (Ctrl+Z).

## 3. Colliders

Colliders are shared by all three stages.

- **Mesh collider** (recommended): pick a skinned mesh (your body, or *(all meshes)*) and
  click *+ Add* — arm/hand/finger bones are turned into smooth capsules, with merged hand
  "mitts" so fingers can't slip between them. Painted verts are left out so a region never
  collides with itself.
- **Bone collider**: pick any bone, set a radius, *+ Add*. Good for props.
- **Show / hide colliders** (or **F10**) draws every active collider as a see-through capsule.

## 4. Wobble

Stylized motion, all per region:

- **Motion** — jiggle level, stiffness, damping, drag, max deform.
- **Gravity** — how much the region sags.
- **Waves and ripples** — cloth ripple, **jell-o wobble** (with random size and speed),
  **rope pull** (a trailing swing when you move that settles without a wobbly tail; *ease*
  softens it), liquid ripple.
- **Extra jiggle modes** — sway (pendulum), twist, pulse (breathe), squash and stretch,
  turbulence.
- **Surface** — cellulite.

## 5. Jell-o

Tick **Simulate on a remeshed cage**. The plugin copies your painted regions, remeshes the
copy into even triangles in the background (~2 s), simulates that cage, and projects the
movement back — your mesh's real topology never touches the solver, which is where the
smoothness comes from. **Show / hide sim cage** lets you see it.

A recipe that works well as a starting point:

- Interior kept loose: solver iterations 1, most stiffness sliders at 0
- **Softness (projection averaging)** ≈ 9 (the big smoothness knob)
- **Seam smoothing level** ≈ 20, **range** ≈ 0.018 m (the painted ↔ unpainted boundary)
- **Seam max stretch** ≈ 0.01–0.05 (limits how fast movement ramps up away from the seam)
- **Max stretch** small (≈ 0.01–0.06) to leash the interior

## 6. Squish

- **Squish level / depth** — how strongly and how deep a press dents.
- **Bulge** — flesh flows out around the fingers/arm.
- **Region self-squish** — regions press into each other.
- **Max dent before give-way** — past this, the flesh stops denting and moves instead:
  - **Evacuate: move bones** — the region's own driver bones slide away from the press
    (auto-detected; only whole chains move, so messy rigs can't fight).
  - **Evacuate: ALL bones** — nested child bones also move, for deeper local give.
  - **Evacuate: shift blob** — whole-region water-balloon shift, no bones needed.

## 7. Clothes

Runs **once, after all three stages**, on the finished body.

- **Move the clothes onto the body** — clothing near the painted regions (within **Cloth
  follow range**) binds to the body and replays exactly the same movement every frame, so
  body and clothes can't drift apart. Garments take their resting fit from the body
  *without* the wobble swing, so toggling things mid-motion can't bake a clip in.
  - *Bind to the WHOLE body surface*, *Only cloth on the region bones*, *Keep clothes off
    the skin*, **inflate** and **min cloth gap** fine-tune the fit.
- **Tuck the body back in** (optional) — instead of moving the clothes, pushes flesh back
  inside the garment covering it. It reshapes the body, so prefer moving the clothes; use
  this for garments that aren't moved onto the body.

## 8. Performance and native bones

- **Substeps per stage** — more = steadier physics, more cost.
- **Frame-rate savers**, per stage:
  - **Half-rate physics** — compute every 2nd frame and hold the result. Cheapest.
  - **Half-rate + smooth blend** — same cost, held frames blend between ticks.
  - **Async physics (worker thread)** (Wobble, Squish) — the sim runs on a background
    thread; best FPS, one frame of physics delay.
  - The options for one stage are mutually exclusive and untick each other.
  - Suggested slower-PC setup: Squish **async** on, Jell-o + Wobble **half-rate + smooth**.
- **Disable spring/dynamic bones** — native bone physics on the same body parts fights the
  mesh simulation. Leave it on (scoped to painted regions) unless you want both.
- **Sharpness overlay** paints jagged spots red; **Hide meshes…** hides meshes while you work.

## 9. Presets

A preset holds **everything** — regions, colliders, all three stages, clothes and
performance settings — in one go.

- Type a name, then **Save preset**. **Load preset** / **Delete preset** act on the one
  picked in the list.
- **Auto-load for this model** — remembers the loaded model by its file name.
- **Model name includes…** + **Auto-load when name includes** — type any part of a name
  (for example `Luna`), and every model whose name contains it gets the preset.
- **Auto-load presets on model change** — the switch for all of the above.
- **Manage presets…** opens a small window: rename a preset (type, then **Enter** — its
  auto-load rules follow the new name), **Load**, **Delete** (click twice to confirm).
- **Manage auto-load…** opens another: an on/off tick per rule, editable name text
  (**Enter** saves), click the preset name to switch it to the next preset, **Delete**
  (click twice). It also shows what the current model is called, and marks rules that can
  never match.

Text boxes in those windows save on **Enter** only — clicking away puts the old text back.

## Troubleshooting tools

- **F10** — collider capsules + per-frame timings in the log.
- **F11** — dumps the displaced mesh, rest mesh and per-node field CSVs to
  `AppData\LocalLow\Suvidriel\VNyan\squishdebug\` (and `jellodebug\`) for offline analysis.
- **Sharpness overlay** — polygon-angle jaggedness (red = jagged), live on the model.
- The VNyan log (`AppData\LocalLow\Suvidriel\VNyan\Player.log`) reports the chain wiring,
  cage builds, colliders, which model was recognized and which preset auto-loaded.
