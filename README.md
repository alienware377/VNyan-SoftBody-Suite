# Soft Body Suite 3 for VNyan: Mesh Soft-Body, Jiggle & Squish Physics Plugin for VTubers

**Real mesh-level soft-body physics for [VNyan](https://suvidriel.itch.io/vnyan) avatars: squishy collision, buttery jiggle, cartoon-style wobble, flesh that ripples, and clothes that stay on. On any rig, however messy.**

The suite deforms the avatar **mesh** directly instead of bending bones, so it works with
nested/overlapping bone chains, dense sub-millimeter detail geometry, and any `.vsfavatar`
without re-rigging. Blendshapes and face tracking keep working, because everything happens
after the skinning bake.

## What's new in Soft Body Suite 3

- **A fourth stage: Field.** Every painted region gets its own remeshed cage and its own
  solver, with its own settings and its own motion, so breasts and thighs can behave
  completely differently. Effect families: **Squish**, **Squish & Inflate**, **Ripple**,
  **Fat Jiggle**, **Penetration** and **Squish Organic**, plus twelve one-click presets.
  **Field Mix** sets how much of it each region gets (0 = off).
- **Ripples that start where they should:** from the outermost point when you jolt or stop,
  from the touch point, from auto-found nipples, or from points you click on the body.
  Waves can be set off by your own movement too, with nothing touching you.
- **Contact shaping:** **Splay** lets flesh slide round the sides of an arm or finger,
  **Push-out Ring** raises a soft rim around a dent, and **Lift** carries a push to the
  whole mass (a hand under a breast lifts all of it).
- **Clothes skin guard:** a final pass lifts cloth back out of the body you actually see,
  including cloth the model itself has sunk into the skin. Floating accessories (bows,
  ribbons) move as one rigid piece, and leg garments follow thigh regions even when they
  are rigged to differently named leg bones.
- **Overall jiggle:** two sliders at the top of the window scale every stage at once:
  how big the jiggle is, and how long it keeps ringing.
- **Region presets, Copy from, Normalize weights:** save a region's tuning and put it on
  another region, copy settings straight from another region, and fix regions that barely
  move. Paint edits now rebuild every stage.
- **Much faster:** most heavy loops now use all your CPU cores, a lot of wasted work is gone,
  and Jell-o's cage solve overlaps with the clothes' mesh bake. On the test avatar with
  Wobble + Jell-o + clothes it went from about 45 fps to a steady 60 (VNyan's cap), with the
  same look.

## One plugin, one window, five stages

| Stage | What it does |
|---|---|
| **Wobble** | Stylized motion: jiggle, jell-o wobble (with randomizer), rope pull, cloth/liquid ripples, sway, twist, pulse, squash & stretch, turbulence |
| **Jell-o** | Soft-body simulation on a *remeshed proxy cage*: ultra-smooth jiggle that never sees your mesh's messy topology |
| **Squish** | Collision squish: marshmallow dents, bulge, penetration limiting, bone + whole-blob evacuation (flesh gets *out of the way*) |
| **Field** (new) | One cage and one solver per painted region: contact squish, inflate, travelling ripples, fat jiggle, organic squash & stretch, contact shaping |
| **Clothes** | Runs **once, at the very end**: fits your clothes onto the finished body, lifts cloth out of the skin, then (optionally) tucks anything poking through back in |

```
skinned mesh → Wobble → Jell-o → Squish → Field → Clothes
```

Every stage has an on/off tick in its section title (for the Field stage it is the
**Field · Master** header), and **Field Mix** sets how much of the field each region gets.
Turn any stage off and the rest keep working, and the clothes keep following. Jell-o always runs in the background (with its
simulation off it simply passes the body through), because the clothes are fitted from it.

## Highlights

- 🫠 **Marshmallow collision**: soft, smooth, shard-free dents from hand/arm capsule
  colliders auto-fitted from any skinned mesh
- 🍮 **Remeshed sim cages**: physics runs on uniform, auto-remeshed copies of your painted
  regions, then projects back onto the real mesh: mixed 0.4 mm to 8 mm topology stops mattering
- 🌊 **Flesh that ripples**: waves that travel across the surface from a slap, a jolt or a
  point you picked, plus a fat jiggle effect
- 👕 **Clothes that stay on**: garments ride the exact same movement as the body, fitted
  once after every stage, then the skin guard lifts out anything that sank in
- 🖌️ **Paint your regions once** in-app (brush, bone-select, blur, normalize): each stage
  keeps its own tuned values for the same region
- 💾 **Presets that follow your model**: one preset holds every stage; auto-load it by the
  model's file name, or by any text the model name includes. Region presets move one
  region's tuning to another
- 🦴 **Messy-rig safe**: evacuation translates only whole bone chains, never scales, never
  fights animation or tracking
- ⚡ **Fast**: multi-core helpers, plus per-stage half-rate and background-thread options
  for slower PCs
- 🎛️ **Everything is a slider**: with `?` tooltips explaining what each control does

## Quick Start

1. Download **`VNyan-SoftBody-Suite-v3.0.0.zip`** from
   [Releases](https://github.com/alienware377/VNyan-SoftBody-Suite/releases), unzip it,
   close VNyan and run **`install.bat`**. It looks for VNyan by itself and asks you to
   confirm, or asks you to **open your VNyan folder** (the one with `VNyan.exe` inside).
   Prefer doing it by hand? See [INSTALL](docs/INSTALL.md).
2. Start VNyan, open **Soft Body Suite** from the plugins menu and tick **On**.
3. In **Mesh & Region**, pick your body mesh, enable it, and paint a region
   (or pick bone groups).
4. In **Colliders**, add a mesh collider. Then open **Wobble**, **Jell-o** or **Squish**
   and tick the ones you want.
5. For the new Field stage, tick **Field · Master** at the bottom of the window, raise
   **Field Mix** for your region, and try a button in **Field · Presets**.

**Upgrading from the classic Soft Body Suite (v2)?** The installer moves the classic plugin
folder (and the old v1 Squish / Wobble / Jello Studio folders) into
`<your VNyan folder>\RetiredPlugins\`. They are moved, never deleted: two soft-body plugins
would move the same body twice. The first time Soft Body Suite 3 starts, it brings your
regions, colliders, slider values and presets over by itself. It only reads the classic
files and never changes them, so going back still works. Prefer the classic suite? Install
the **v2.0.1 Final classic edition** release instead.

Full walkthroughs: [Install Guide](docs/INSTALL.md) · [Usage Guide](docs/USAGE.md) ·
[FAQ](docs/FAQ.md)

## Building from Source

`SoftBodySuite/Scripts/` (including its `Field/` subfolder) holds the runtime code and
`SoftBodySuite/Editor/SuiteBuild.cs` builds the window into the `.vnobj` bundle. See
[docs/INSTALL.md](docs/INSTALL.md#building-from-source) for the compiler flags and Unity
version. The source of the classic v2 suite lives in
[`legacy/SoftBodySuite-v2/`](legacy/SoftBodySuite-v2/), and the old separate v1 studios in
[`legacy/`](legacy/).

## Credits

- Built on the plugin loading conventions of the VNyan SDK by Suvidriel.
- UI window framework originally derived (with permission) from Jayo's open VNyan plugin
  projects. Thank you!

## License

MIT. See [LICENSE](LICENSE).

---

*Keywords: Soft Body Suite 3, VNyan plugin, VTuber physics, soft body physics, jiggle physics,
breast physics, squish, mesh deformation, cloth fitting, VSFAvatar, Unity, avatar collision,
bone physics alternative, XPBD, spring bones.*
