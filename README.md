# VNyan Soft Body Suite — Mesh Soft-Body, Jiggle & Squish Physics Plugin for VTubers

**Real mesh-level soft-body physics for [VNyan](https://suvidriel.itch.io/vnyan) avatars: squishy collision, buttery jiggle, cartoon-style wobble, and clothes that stay on — on any rig, however messy.**

The suite deforms the avatar **mesh** directly instead of bending bones, so it works with
nested/overlapping bone chains, dense sub-millimeter detail geometry, and any `.vsfavatar`
without re-rigging. Blendshapes and face tracking keep working, because everything happens
after the skinning bake.

## One plugin, one window (new in v2)

Soft Body Suite used to be three separate plugins. It's now **one plugin** with one window
and four stages that run in order:

| Stage | What it does |
|---|---|
| **Wobble** | Stylized motion: jiggle, jell-o wobble (with randomizer), rope pull, cloth/liquid ripples, sway, twist, pulse, squash & stretch, turbulence |
| **Jell-o** | Soft-body simulation on a *remeshed proxy cage* — ultra-smooth jiggle that never sees your mesh's messy topology |
| **Squish** | Collision squish: marshmallow dents, bulge, penetration limiting, bone + whole-blob evacuation (flesh gets *out of the way*) |
| **Clothes** | Runs **once, at the very end**: fits your clothes onto the finished body, then (optionally) tucks anything poking through back in |

```
skinned mesh → Wobble → Jell-o → Squish → Clothes
```

Every stage has its own on/off tick in its section title. Turn any of them off and the rest
keep working — and the clothes keep following.

## Highlights

- 🫠 **Marshmallow collision** — soft, smooth, shard-free dents from hand/arm capsule
  colliders auto-fitted from any skinned mesh
- 🍮 **Remeshed sim cage** — physics runs on a uniform, auto-remeshed copy of your painted
  regions, then projects back onto the real mesh: mixed 0.4 mm–8 mm topology stops mattering
- 👕 **Clothes that stay on** — garments ride the exact same movement as the body, fitted
  once after all three stages, so nothing lags a frame behind or clips through
- 🖌️ **Paint your regions once** in-app (brush, bone-select, blur) — each stage keeps its own
  tuned values for the same region
- 💾 **Presets that follow your model** — one preset holds every stage; auto-load it by the
  model's file name, or by any text the model name includes, and manage it all in two
  little windows
- 🦴 **Messy-rig safe** — evacuation translates only whole bone chains, never scales, never
  fights animation or tracking
- ⚡ **Performance modes per stage** — substeps, half-rate physics (hold or smooth-blend),
  and async worker-thread physics for slower PCs
- 🎛️ **Everything is a slider** — with `?` tooltips explaining what each control does

## Quick Start

1. Download **`VNyan-SoftBody-Suite-v2.0.0.zip`** from
   [Releases](https://github.com/alienware377/VNyan-SoftBody-Suite/releases), unzip it,
   close VNyan and run **`install.bat`**. It looks for VNyan by itself and asks you to
   confirm — or asks you to **open your VNyan folder** (the one with `VNyan.exe` inside).
   Prefer doing it by hand? See [INSTALL](docs/INSTALL.md).
2. Start VNyan, open **Soft Body Suite** from the plugins menu and tick **On**.
3. In **Mesh & Region**, pick your body mesh, enable it, and paint a region
   (or pick bone groups).
4. In **Colliders**, add a mesh collider. Then open **Wobble**, **Jell-o** or **Squish**
   and tick the ones you want.

**Upgrading from v1 (the separate studios)?** The installer moves the old Squish / Wobble /
Jello Studio folders out of VNyan's plugin folder (moved, not deleted), and the suite brings
your old regions, colliders and slider values over by itself the first time it starts.

Full walkthroughs: [Install Guide](docs/INSTALL.md) · [Usage Guide](docs/USAGE.md) ·
[FAQ](docs/FAQ.md)

## Building from Source

`SoftBodySuite/Scripts/` holds the runtime code and `SoftBodySuite/Editor/SuiteBuild.cs`
builds the window into the `.vnobj` bundle. See
[docs/INSTALL.md](docs/INSTALL.md#building-from-source) for the compiler flags and Unity
version. The source of the old separate v1 studios lives in [`legacy/`](legacy/).

## Credits

- Built on the plugin loading conventions of the VNyan SDK by Suvidriel.
- UI window framework originally derived (with permission) from Jayo's open VNyan plugin
  projects — thank you!

## License

MIT — see [LICENSE](LICENSE).

---

*Keywords: VNyan plugin, VTuber physics, soft body physics, jiggle physics, breast physics,
squish, mesh deformation, cloth fitting, VSFAvatar, Unity, avatar collision, bone physics
alternative, XPBD, spring bones.*
