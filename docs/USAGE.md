# Usage Guide

Most controls have a **`?` chip**: click it for an in-app explanation. This guide covers
the workflow and what the main sections do.

## The window

Open **Soft Body Suite** from VNyan's plugins menu. The **On** tick at the top is the master
switch: off fully detaches the plugin from the avatar, so the original mesh shows again.
Below it is one long list of sections, all closed at first. **Click a section title to open
or close it** (`[+]` = closed, `[-]` = open):

| Section | What's in it |
|---|---|
| Overall jiggle (all stages) | Two sliders that scale every stage at once |
| Mesh & Region | Which mesh to simulate, its regions, region presets, copying settings between regions |
| Weight painting | Brush, bone-group picking, normalizing, copying regions to other meshes |
| Colliders | What can press into the body (hands, arms, props) |
| **Wobble** ☐ | Stylized motion stage |
| **Jell-o** ☐ | Soft-body cage stage |
| **Squish** ☐ | Collision stage |
| **Clothes** ☐ | Clothes fitting, skin guard and tuck, done once at the end |
| Performance and native bones | Substeps, frame-rate savers, spring bones |
| Presets | Save / load / auto-load everything |
| **Field · Master** | The Field stage: **Field Mix**, with every other Field section folded inside it |

Every stage section has a **tick in its title** that turns that stage on or off. For the
Field stage it is the **Field · Master** header; how much each region gets is then set with
**Field Mix** (see section 8).
The line under the window title tells you what just happened. The footer has **Reload**
(re-read from disk), **Save** and **Close** (the plugin keeps running).

## The chain

```
skinned avatar mesh
   └─ Wobble    : stylized jiggle / waves
        └─ Jell-o  : soft-body cage simulation
             └─ Squish  : collision squish + evacuation
                  └─ Field   : one effect cage per painted region
                       └─ Clothes : fitted once, after everything else
```

Each stage hands its result straight to the next one in the same frame, and only the last
running stage draws. Turn any stage off and the others close the gap by themselves.
Jell-o always runs: with its simulation off it simply passes the body through, because the
clothes are fitted from it.

## 1. Overall jiggle (all stages)

Two sliders at the very top that change **every stage at once** (Wobble, Jell-o, Squish and
Field, and the clothes riding them), without touching your tuning:

- **Jiggle strength** (0 to 1): how **big** all the jiggle is. 1 = your tuned look, 0.5 =
  the same motion at half the size. Only the size changes: the timing and the shape of the
  movement stay exactly the same.
- **Jiggle settle time** (0.1 to 1): how **long** everything keeps going after a movement.
  1 = your tuned look, 0.5 = every bounce, wobble, sway and ripple dies down in about half
  the time. The speed of each wobble stays the same, only how quickly it fades changes.

Handy for calming everything down for one stream without retuning a single region.

## 2. Mesh & Region

1. Pick your body **Mesh** and click *Enable squish on mesh*. Meshes in use get a ● in the
   list.
2. Add a **Region** (*+ Region*) and give it a name. *Enabled* switches a region off without
   deleting it.
3. A region is painted **once**, but each stage keeps **its own values** for it, so the
   Wobble, Jell-o, Squish and Field sliders always edit the region picked here.

### Region settings presets and Copy from (new)

- Type a name under **Region settings presets** and click **Save this region**: it stores the
  selected region's whole tuning (Wobble, Jell-o, Squish and Field settings). Leave the name
  empty to overwrite the preset picked in the list.
- Pick a preset in the list and click **Apply here** to put it on the selected region.
  **Delete preset** removes the picked one.
- **Copy from**: pick any other region (on any mesh) and click **Copy here** to copy every
  setting from it into the selected region.
- Paint, colliders and picked ripple points always stay with their own region. *Apply here*
  and *Copy here* can both be undone (Ctrl+Z).

Region presets live in `softfleshfield.regionpresets.json`, separate from the full presets
in section 11.

## 3. Weight painting

- **Paint mode**: brush weights directly on the model with the left mouse button
  (radius / strength sliders, *Blur weights* to soften the edges, Ctrl+Z to undo,
  Ctrl+Shift+Z to redo).
- **Pick vertex groups…**: pick bone(s) from a multi-select list to fill the region from
  the skinning (tick *Also include child bones down the branch* for whole branches).
- **Normalize weights (strongest = 100%)** (new): raises the region's paint so its strongest
  spot is 100%, keeping the shape. Use it when a region barely moves at any setting: paint
  picked from bone weights can top out near 20%, and every stage multiplies by the paint.
- The **overlay** shows the paint as colors: blue = 0, red = 1. Paint generously past the
  area you want to move: weights taper the motion smoothly at the boundary.
- **Gravity only when the parent bone leaves rest** and **Ref bone**: gravity sag waits until
  the reference bone moves away from its rest pose. *(auto: highest weight)* picks the bone
  for you.

**Paint edits now reach every stage.** A moment after you stop painting, the Wobble, Jell-o
and Field stages rebuild with the new paint (a cage build can take a few seconds), instead
of waiting for a restart.

### Copy regions to other meshes

Click **Apply region to other meshes…** and pick a method:

- **Auto: same bones as the painted area** (default): reads which bones actually skin
  your painted verts and selects the matching verts on each target mesh. The
  **weight share ≥ %** stepper controls how dominant a bone must be to count.
- **By bone group**: reuses your last bone-group pick.
- **By surface transfer**: projects the painted area through space onto each target
  (**projection radius** stepper). Best when skeletons/skinning differ.

Tick the target meshes, and optionally **Copy ALL regions of this mesh**. Copies carry every
stage's values and the colliders, and every overwrite is undoable (Ctrl+Z).

## 4. Colliders

Colliders are shared by all stages.

- **Mesh collider** (recommended): pick a skinned mesh (your body, or *(all meshes)*) and
  click *+ Add*. Arm/hand/finger bones are turned into smooth capsules, with merged hand
  "mitts" so fingers can't slip between them. Painted verts are left out so a region never
  collides with itself.
- **Bone collider**: pick any bone, set a radius, *+ Add*. Good for props.
- **Show / hide colliders (F10)** draws every active collider as a see-through capsule. The
  Field stage draws its own capsules in orange.
- In the Field stage, the colliders of every enabled region on a mesh work together, so a
  hand added on the breast region also dents a thigh region.

## 5. Wobble

Stylized motion, all per region:

- **Motion**: jiggle level, stiffness, damping, drag, max deform.
- **Gravity**: how much the region sags.
- **Waves and ripples**: cloth ripple, **jell-o wobble** (with random size and speed),
  **rope pull** (a trailing swing when you move that settles without a wobbly tail; *ease*
  softens it), liquid ripple.
- **Extra jiggle modes**: sway (pendulum), twist, pulse (breathe), squash and stretch,
  turbulence.
- **Surface**: cellulite.

## 6. Jell-o

Tick **Simulate on a remeshed cage (smoother)**. The plugin copies your painted regions,
remeshes the copy into even triangles in the background (about 2 s), simulates that cage,
and projects the movement back. Your mesh's real topology never touches the solver, which is
where the smoothness comes from. **Show / hide sim cage** lets you see it.

A recipe that works well as a starting point:

- Interior kept loose: solver iterations 1, most stiffness sliders at 0
- **Softness (projection averaging)** ≈ 9 (the big smoothness knob)
- **Seam smoothing level** ≈ 20, **range** ≈ 0.018 m (the painted ↔ unpainted boundary)
- **Seam max stretch** ≈ 0.01 to 0.05 (limits how fast movement ramps up away from the seam)
- **Max stretch** small (≈ 0.01 to 0.06) to leash the interior

**Contact boost and slap** adds an extra push-out under a deep press (*Boost* sliders) and
a kick for fast hits (*Slap* sliders, *Slap power* 0 = off).

## 7. Squish

- **Squish level / depth**: how strongly and how deep a press dents.
- **Bulge**: flesh flows out around the fingers/arm.
- **Region self-squish**: regions press into each other.
- **Max dent before give-way**: past this, the flesh stops denting and moves instead:
  - **Evacuate: move bones**: the region's own driver bones slide away from the press
    (auto-detected; only whole chains move, so messy rigs can't fight).
  - **Evacuate: ALL bones**: nested child bones also move, for deeper local give.
  - **Evacuate: shift blob**: whole-region water-balloon shift, no bones needed.

## 8. Field (new)

The Field stage runs after Squish, on its **own remeshed cage**. Every enabled, painted region
gets its **own cage, solver, settings and motion**, so breasts and thighs can have completely
different effects and move independently. When the stage starts, the cages are built in the
background, so give it a few seconds before the effects show.

All its sections sit at the **bottom of the window**, folded inside **Field · Master**: open
that one to see the rest.

### Field Mix

First tick **Field · Master** to switch the stage on. Then **Field Mix** is set **per
region**: 0 = off (the region looks exactly as the
first three stages leave it), 1 = the full Field effect, and anything in between blends the
two. Drag it on a live avatar to compare. New regions, and everything brought over from the
classic suite, start at 0.

### Start with a preset

**Field · Presets** has twelve buttons. Each one sets the selected region's Field tuning and
picks its **effect family**. It never touches Field Mix. Click the `?` on a button for a
one-line description.

| Effect family | Presets | What it feels like |
|---|---|---|
| **Squish** | Soft Flesh, Memory Foam, Elastic Rubber | Soft dents with natural volume, from mushy foam to a firm, bulging rubber |
| **Squish & Inflate** | Soft Balloon, Bouncy Rubber | Denting one spot visibly inflates the rest |
| **Ripple** | Squishy Jelly, Water Surface | Waves that run across the flesh from the contact |
| **Fat Jiggle** | Glute Jiggle, Heavy Fat | Soft-fat tremor and slosh after an impact |
| **Penetration** | Tight Grip, Wet & Loose | Channel walls open around an axis, grip it and follow it |
| **Squish Organic** | the animated-character flesh preset | Very soft contact with strong squash & stretch and a wobble after impact |

The **Ripple**, **Fat Jiggle**, **Inflate**, **Organic** and **Penetration** sections each have
their own tick. Ticking one adds that effect on top of whatever family the preset picked.

### The Field sections

- **Contact**: how flesh yields to a collider. *Intensity*, *Shape Smoothing* (high = a round,
  soft footprint, low = the collider's own outline), *Spread* (how wide the halo reaches),
  *Volume Preservation* (how much squashed flesh piles up around the dent), *Lateral Flow*,
  *Contact Hardness*, *Corrective Smooth*, *Collision Offset* (skin thickness held above the
  collider), **Splay** and *Self Collision*. **Splay**: 0 = flesh flattens straight back
  under the collider. Higher = flesh near the collider's sides slides round it and bulges up
  beside it instead, which is the natural way out round an arm or finger.
- **Push-out Ring** (tick to turn on): flesh pressed in by a collider comes back up as a soft
  ring around the dent. *Amount* (1 = all of the pressed-in volume), *Distance*, *Width*.
- **Lift** (tick): a push on part of the flesh (a hand under a breast) moves the whole mass,
  not just the touched spot. *Amount*, *Softness* (0 = the whole ball lifts as one firm piece,
  higher = the sides lag and droop), and how much of the push it follows *up/down*,
  *forward/back* (low stops arms shoving the whole breast out in front) and *sideways*.
- **Inflate** (tick): internal pressure, so flesh away from the contact swells as the contact
  squashes it. *Pressure*.
- **Organic** (*Organic Squash* tick): cartoon squash & stretch driven by the body's own
  movement, nothing has to touch you. *Squish Intensity*, *Direction Influence*,
  *Collision Strength / Softness*, *Stiffness* (0 = soft fat, 1 = rigid),
  *Velocity Influence*, *Jiggle*.
- **Penetration** (tick): channel behaviour: the walls open around the axis, grip it and
  follow it. Works best with a *Cage Edge* of 6 mm or finer. *Grip Intensity*, *Tightness*,
  *Reach*, *Clench Amount / Speed* (a pulsing squeeze), *Jiggle*, *Collision Offset*,
  *Smoothing*. *Wetness* is worked out but nothing uses it yet.
- **Impulse**: the shared "energy" that ripples and fat jiggle ride on.
  - *Impact Sensitivity*: how much energy a contact adds. Only speed *relative* to the
    painted area counts, so a slow press makes no wobble.
  - *Relax Time*: seconds for it to fade. The whole field returns exactly to rest.
  - **Motion Sensitivity**: jiggle and waves from the body's **own movement**, with nothing
    touching it. 0 = only touches start them.
  - *Jerk Filter*: raise it if webcam or VMC tracking makes the flesh twitch all the time.
  - *Move needed (lower = easier)*: how hard a move must be to give a full-strength kick.
    At 60 a normal dance barely registers.
- **Ripple** (tick): waves spreading out from an impact. **Where they start** (any mix; each
  one that applies sends its own wave):
  - *Start: outermost point (on a jolt)* (on by default): when you jolt or stop, the flesh
    claps out at its outermost point on the far side of the move, and the wave runs across
    from there.
  - *Start: where it's touched* (on by default): a wave starts where a collider first
    touches.
  - *Start: nipples (auto-detect)*: found from the breast bones, or from the shape when there
    are none.
  - *Start: my picked points*: press **Pick ripple points (click the body)**, then click the
    body to add a point (up to 6 per region). Click a dot again to remove it, and press the
    button again when you're done. **Clear picked points (this region)** removes them all.
    Cyan dots are your points, pink dots the found nipples; they show while picking or while
    colliders are shown.

  Then the wave itself: *Wave Mode* (0 = a continuous train of waves while the contact lasts,
  1 = one expanding ring per impact, the default), *Amplitude* (a fraction of the region's own
  size), *Frequency* (train mode only), *Wave Speed* (2 to 4 reads as a wave, much faster
  looks like the whole ball pulsing), *Decay*, *Wave Width*, *Wave Range* (about 2 reaches the
  opposite side), *Dent Depth* and *Smoothing*.
- **Fat Jiggle** (tick): soft-fat tremor after an impact that returns exactly to rest.
  *Amplitude* (a fraction of the region's own size: 0.06 is already generous),
  *Wobble Scale* (low = a few big masses, high = fine shivering), *Wobble Speed*,
  *Lateral Motion* (sideways slosh), *Normal Motion* (in-and-out shake), *Decay*, *Smoothing*.
- **Cage**: the Field's own cage for every region on the mesh. *Cage Edge* (target edge
  length in metres, 0.008 by default; smaller is finer and much slower), *Remesh Passes*,
  *Projection Average*, *Proxy Smooth*, and the stage's frame-rate savers: *Half Rate*,
  *Half Rate (blend)* and *Async Sim* (the Field maths runs on a background thread while the
  frame renders: much higher fps, the effect shows one frame later, and it replaces Half Rate
  while on).

**Waves when you move, with nothing touching:** raise **Motion Sensitivity** (Impulse), keep
*Start: outermost point (on a jolt)* ticked, and turn on **Ripple** or **Fat Jiggle**. If
nothing happens on normal moves, lower *Move needed*.

## 9. Clothes

Runs **once, after every stage**, on the finished body.

- **Move the clothes onto the body**: clothing near the painted regions (within **Cloth
  follow range**) binds to the body and replays exactly the same movement every frame, so
  body and clothes can't drift apart. Garments take their resting fit from the body
  *without* the wobble swing, so toggling things mid-motion can't bake a clip in.
  - *Bind clothes to the WHOLE body surface*, *Only cloth on the region bones*, *Keep clothes
    off the skin*, **inflate** and **min cloth gap** fine-tune the fit.
  - *Follow zone: side / up/down / forward* shift where each garment looks for body to follow,
    for example forward for cloth that hangs in front of the body.
  - **Floating parts: move as one** and **Floating if further than**: a piece of a garment
    that mostly sits away from the body (a bow, a ribbon, a dangling charm) is moved as **one
    rigid piece** instead of point by point, so it can't tear apart. 1 = fully rigid,
    0 = point by point.
  - Leg garments (a leg harness, for example) follow thigh regions even when they are
    rigged to differently named leg bones than the ones your region was picked from.
    Nothing to set up.
- **Skin guard (push cloth out of the skin)** (new): **Push clothes out of the skin** is the
  last clothes pass, on by default. Any part of a garment near the skin, followed or not, is
  pushed back **out of the body you actually see** this frame, with a soft bulge around each
  push. It only acts where the jiggle moved the body; everywhere else the clothes are left
  exactly as they were. Rigid floating parts (bows) are skipped.
  - *Guard: closest to skin*: how close cloth may come to the skin. Cloth that sat closer when
    it was fitted keeps its own smaller gap, so a tight top is never puffed up.
  - *Guard: watch cloth within*: only cloth this close to the skin is watched.
  - *Guard: softness (spread)*: how far each push spreads.
  - *Guard: strength*: 1 = skin never shows through.
  - **Guard: fix cloth sunk in skin**: for models whose clothes already dip **into** the skin
    (often under and between the breasts). Cloth sunk in by up to this much is lifted to just
    above the skin, even while nothing moves. 0 = leave the model's own clipping alone. Raise
    it slowly: too high can catch a sleeve or strap that really sits behind the body.
- **Tuck the body back in** (optional): instead of moving the clothes, pushes flesh back
  inside the garment covering it. It reshapes the body, so prefer moving the clothes; use
  this for garments that aren't moved onto the body. It only runs while **Squish** is on and
  **Field** is off.

## 10. Performance and native bones

Soft Body Suite 3 already spreads most of its heavy work over all your CPU cores, so try
your avatar with no frame-rate savers first.

- **Substeps per stage** (Wobble, Jell-o, Squish): more = steadier physics, more cost.
  The Field stage doesn't use substeps.
- **Frame-rate savers**, per stage:
  - **Half-rate physics**: compute every 2nd frame and hold the result. Cheapest.
  - **Half-rate + smooth blend**: same cost, held frames blend between ticks.
  - **Async physics (worker thread)** (Wobble, Squish): the sim runs on a background
    thread; best FPS, one frame of physics delay. Jell-o has no worker-thread option.
  - The options for one stage are mutually exclusive and untick each other.
  - The Field stage has its own *Half Rate*, *Half Rate (blend)* and *Async Sim* in
    **Field · Cage**.
  - Suggested slower-PC setup: Squish **async** on, Jell-o + Wobble **half-rate + smooth**,
    Field **Async Sim**.
- **Disable spring/dynamic bones**: native bone physics on the same body parts fights the
  mesh simulation. Leave it on (scoped to painted regions with *only bones driving painted
  regions*) unless you want both.

## 11. Presets

A preset holds **everything**: regions, colliders, every stage including Field, clothes and
performance settings, in one go.

- Type a name, then **Save preset**. **Load preset** / **Delete preset** act on the one
  picked in the list.
- **Auto-load for this model**: remembers the loaded model by its file name.
- **Model name includes...** + **Auto-load when name includes**: type any part of a name
  (for example `Luna`), and every model whose name contains it gets the preset.
- **Auto-load presets on model change**: the switch for all of the above.
- **Manage presets...** opens a small window: rename a preset (type, then **Enter**: its
  auto-load rules follow the new name), **Load**, **Delete** (click twice to confirm).
- **Manage auto-load...** opens another: an on/off tick per rule, editable name text
  (**Enter** saves), click the preset name to switch it to the next preset, **Delete**
  (click twice). It also shows what the current model is called, and marks rules that can
  never match.
- Presets brought over from the classic suite were saved before the Field stage existed.
  Loading one leaves your current Field settings alone instead of resetting them.

Text boxes in those windows save on **Enter** only: clicking away puts the old text back.

## Troubleshooting tools

- **F10** (or **Show / hide colliders**): collider capsules on the avatar (the Field stage's
  in orange), collider details in the log, and the clothes follow logger.
- **F11**: dumps the displaced mesh, rest mesh and per-node field CSVs to
  `AppData\LocalLow\Suvidriel\VNyan\squishdebug\` and `fielddebug\` for offline analysis.
- **Diagnostic files**: an empty `softfleshfield.perf.on` or `softfleshfield.followdiag.on`
  next to the settings switches on the performance line or the clothes follow logger (see
  [INSTALL](INSTALL.md#diagnostics-optional)).
- The VNyan log (`AppData\LocalLow\Suvidriel\VNyan\Player.log`) shows the chain wiring
  (the **STAGES LIVE** line shows which stages really run), cage builds, colliders, which
  model was recognized and which preset auto-loaded.
