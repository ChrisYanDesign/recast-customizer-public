# Pipeline

Tooling around the Recast Customizer asset kit: measurement, validation, and (later) the
ComfyUI restyle graph. Python and headless Blender; nothing here is a Unity script, and
Unity ignores this folder because it sits outside `Assets/`.

## Run it

One command checks a kit end to end -- measure, update the manifest, validate:

    python pipeline/check.py                             # the glove
    python pipeline/check.py pipeline/demo/broken_glove  # the deliberately broken kit
    python pipeline/check.py --open                      # and open the report

It finds Blender itself; set the `BLENDER` environment variable if it cannot.

The three steps can still be run separately when you want only one of them:

    blender --background --python pipeline/blender/measure_and_mask.py
    python pipeline/extend_manifest.py
    python pipeline/validator/validate.py

The validator exits non-zero when anything fails, so it can run unattended. Add `--quiet`
to print failures only, `--spec <path>` to check against a different profile, or
`--manifest <path>` to validate a different kit.

Prove the checks catch what they claim to:

    python pipeline/tests/test_broken_assets.py

## Layout

| Path | |
|---|---|
| `check.py` | One command: all three steps, on any kit |
| `blender/measure_and_mask.py` | Rasterises each segment's UVs: masks, bounds, overlap |
| `blender/texture_stats.py` | Samples each texture: average colour, and whether it reads as a normal map |
| `extend_manifest.py` | Writes the measured fields into `glove_manifest.json` |
| `validator/spec.json` | The limits the kit is checked against |
| `validator/validate.py` | The checks, the console summary, the exit code |
| `validator/report.py` | The HTML report |
| `masks/` | Generated, committed; the restyle graph reads these |
| `out/` | Generated reports and measurements |
| `tests/test_broken_assets.py` | Builds broken kits and asserts each fails for the right reason |
| `comfyui/recast_nodes.py` | The two ComfyUI nodes the restyle graph needs; see `comfyui/README.md` |
| `tests/test_comfyui_nodes.py` | Drives those nodes without ComfyUI and checks the composite guarantee |
| `demo/make_broken_glove.py` | Derives a faulty copy of the real glove, for demonstrating the checks |
| `reference/` or `backup/` | The dense mesh the normal bake reads |

## In Unity

Two editor tools sit on top of this, under the **Recast Customizer** menu, so nobody has
to open a terminal:

- **Validate Asset** runs `check.py` on the glove or the broken demo kit and shows the
  result in a window. It also copies both reports into `Resources/`, where the running
  tool reads them.
- **Build Broken Showcase** turns the demo kit into a viewable asset: the bottom fails to
  bind and vanishes, the tassel's UVs sit off the map, the thumb loses its colour map and
  the top its normal map. It takes its import settings from the authored glove -- a fresh
  import defaults to a scale of 1 where the glove uses 0.01, which makes the copy a
  hundred times too large and effectively invisible.

## In the running tool

Opening **ASSET VALIDATION** in the tool runs the checks there and then, in the engine, on
whichever version is in the viewport -- triangles, material count, texture sizes, memory,
empty map slots, UVs outside the sheet, UVs on top of each other, and a colour map that is
really a normal map. The UV rasterising and the texture readbacks take a moment, so the
progress bar follows real work.

Three checks have nothing to measure in a build, because they are properties of the
project rather than of the asset: file naming, textures nothing references, and import
settings. Those keep coming from the recorded report, under their own heading, said
plainly rather than passed off as live.

The limits come from `validation_spec.json`, written by the same run that writes the
report, so the live checks and the offline ones cannot describe different rules.

## Two rules

**Nothing here is typed by hand.** Every measurement in the manifest is written by a
script, so a re-unwrap or a new variant is picked up by re-running the first two steps.

**A limit left `null` is reported, not judged.** The validator still measures it and prints
the figure, which is how a limit gets chosen. Each limit records where it came from in the
spec's `sources` block — a public reference or a measurement of this asset, never an
employer's internal numbers.

## On specular maps

A specular map is optional by design. These materials use URP's specular workflow, and the
non-metallic parts -- the leather -- use a flat specular colour rather than a map, because
a map there shifts saturation for no gain. An absent specular map is a decision, not a
defect, so it is not checked.

The opposite case is checked: a texture imported into the project that no material
references still ships in the build and is never sampled.

## Conventions

Bounding boxes are stored in **image coordinates**: origin top-left, y increasing
downwards, matching how every image library reads the PNG. UV space counts from the
bottom, so the rasteriser flips once on the way out. Everything downstream can assume
image coordinates.
