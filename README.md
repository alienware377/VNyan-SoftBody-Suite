# VNyan SoftBody Suite — Mesh Soft-Body, Jiggle & Squish Physics Plugins for VTubers

**Real mesh-level soft-body physics for [VNyan](https://suvidriel.itch.io/vnyan) avatars: squishy collision, buttery jiggle, cartoon-style squash — on any rig, however messy.**

The suite deforms the avatar **mesh** directly instead of bending bones, so it works with
nested/overlapping bone chains, dense sub-millimeter detail geometry, and any `.vsfavatar`
without re-rigging. Blendshapes and face tracking keep working, because everything happens
after the skinning bake.

## The Studios

| Plugin | What it does |
|---|---|
| **Wobble Studio** | Jiggle, waves, jell-o wobble (with randomizer), cloth ripple, liquid, sway, pulse — stylized motion modes on painted regions |
| **Jello Studio** | XPBD soft-body simulation on a *remeshed proxy cage* — ultra-smooth detached jiggle physics that never sees your mesh's messy topology |
| **Squish Studio** | Collision squish: depth-field marshmallow dents, penetration limiting, bone + whole-blob evacuation (flesh gets *out of the way*), region painting & collider authoring |
| **SoftBody Studio** | Attached-mode XPBD variant of Jello Studio (experimental, ships hibernated) |

They **chain**: `skinned mesh → Wobble (jiggle) → Jello (soft body) → Squish (collision)` —
each stage reads the previous stage's output, and only the final stage renders. Run one,
two, or all three; they find each other automatically.

## Highlights

- 🫠 **Marshmallow collision** — soft, smooth, shard-free dents from hand/arm capsule
  colliders auto-fitted from any skinned mesh, with occlusion-biased contact so grazing
  edges never tear
- 🍮 **Remeshed sim cage** — physics runs on a uniform, auto-remeshed proxy of your painted
  regions (isotropic remeshing at runtime), then projects back onto the render mesh:
  mixed 0.4 mm–8 mm topology stops mattering
- 🖌️ **Paint your regions** in-app (brush, bone-select, blur), share them across all
  studios automatically
- 🦴 **Messy-rig safe** — evacuation translates only whole bone chains (optional per-bone
  mode), never scales, never fights animation or tracking
- ⚡ **Performance modes** — contact sleep-gating, half-rate physics (hold or smooth-blend),
  and an async worker-thread mode for slower PCs
- 🎛️ **Everything is a slider** — with `?` tooltips on every control explaining exactly
  what it does

## Quick Start

1. Download the latest release and copy each plugin folder into
   `C:\Program Files\VNyan\Items\Assemblies\` (see [INSTALL](docs/INSTALL.md)).
2. Launch VNyan, open **Squish Studio** from the plugins menu, toggle **On**.
3. Paint a region (or use *Select from bones*), add a mesh collider set, and poke yourself.
4. Add **Jello Studio** for the soft-body jiggle layer and enable
   *Sim on remeshed proxy mesh*.

Full walkthroughs: [Install Guide](docs/INSTALL.md) · [Usage Guide](docs/USAGE.md) ·
[FAQ](docs/FAQ.md)

## Building from Source

Each studio folder contains the runtime scripts (`Scripts/`) and the Unity editor build
script (`Editor/SquishBuild.cs`) that packs the UI prefab into a `.vnobj` asset bundle.
See [docs/INSTALL.md](docs/INSTALL.md#building-from-source) for the compiler flags and
Unity version.

## Credits

- Built on the plugin loading conventions of the VNyan SDK by Suvidriel.
- UI window framework originally derived (with permission) from Jayo's open VNyan plugin
  projects — thank you!

## License

MIT — see [LICENSE](LICENSE).

---

*Keywords: VNyan plugin, VTuber physics, soft body physics, jiggle physics, breast physics,
squish, mesh deformation, VSFAvatar, Unity, avatar collision, bone physics alternative,
XPBD, spring bones.*
