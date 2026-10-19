# Recast Pipeline — revised plan

The original brief is `3.0_newplan_files/recast-pipeline-project-plan.md`. This is that
plan reconciled against the real repo (see `ARCHITECTURE_NOTES.md`). Where the two differ,
this file wins.

## Purpose, unchanged

Extend the published Recast Customizer into a small documented pipeline that shows
technical-artist skill: tool building, automated validation, engine integration and
AI-assisted texture generation under production constraints.

**Hard constraints, unchanged.** Original assets only, no employer assets or data. Every
claim in the write-up backed by a measured number. Small scope — a finished pair of
modules beats four half-finished ones.

## What changed from the brief, and why

**1. The ComfyUI integration lives in the Unity Editor, not the web build.**
The brief's §5.5 adds a "Generate variant" button to a web dashboard that calls a local
ComfyUI server. There is no web dashboard, and a WebGL page cannot reach a server on the
author's machine — wrong computer, and the browser blocks it regardless. The editor has
unrestricted file and network access, and is where a studio would use the tool anyway.

**2. Masks are generated, not authored.**
The brief assumes one atlas with authored per-segment masks and bounding boxes. The real
asset has one 2048x2048 texture per segment-variant, each a UV layout with a single large
irregular island. A mask is still required — the restyle must stay inside the island — but
Blender renders it from the mesh UVs. Nothing is hand-painted.

**3. Module 3 is extended, not built.**
`DesignImporter.cs` already generates materials, maps and a prefab from an exported design.
What is missing is running validation during import, and handling a folder of variants in
one pass.

**4. The build target does not change.**
Nothing in modules 1–3 is a Unity player build. Module 1 and 2 are Python; Module 3 is an
editor tool. WebGL stays, because it is the only version a stranger runs in one click. A
Windows build stays an option for demoing the live loop on a laptop, decided after Module 2
works.

## Shared data

Extend `Assets/RecastCustomizer/glove_manifest.json` rather than inventing a second
manifest. Fields to add, all computed and committed, not hand-written:

- per segment: `uv_mask` (generated PNG), `uv_bbox`, `texture_size`
- per segment-variant: the diffuse map path
- mesh statistics the validator measures once

Designs continue to travel as `SavedDesign` JSON. The new modules read that format; they
do not define another.

## Module 1 — Validation and budget reports

**Risk: low. Everything it needs is installed.**

A Python program that reads the manifest, opens the mesh through headless Blender, and
checks every design against a rules file: triangle count, material count, texture memory,
overlapping UVs, naming, pivot and scale, texture dimensions and colour space.

Outputs: a console summary with a non-zero exit code on failure, an HTML report with one
row per variant and each failure explained in plain language, and a JSON report the other
modules read.

**Thresholds.** The brief says these must be real production numbers. They must also not
be an employer's internal figures — a public repo quoting those is a confidentiality
problem. Derive them from a published public source plus measurements of this asset, and
record the origin of each number in the rules file itself.

**Acceptance.** Runs on the glove kit and reports correctly. Five deliberately broken test
assets, each caught for the right reason. Report text readable by an artist.

## Module 2 — ComfyUI restyle

**Risk: medium. This is the showpiece and the part that may return a worse answer than
hoped.**

A reference image is restyled into the glove's painted style and written into one
segment's UV island. Everything outside the island is untouched. The result is exported as
a design, imported by Module 3, and checked by Module 1.

**Graph order**

1. Load the reference image.
2. Remove the background.
3. Fit the subject to the island's bounding box, aspect-aware with padding.
4. Extract structure from the existing texture (edge or line preprocessor), so the panel's
   shape and stitching survive.
5. Generate: base model + ControlNet, image-to-image, with a **restyle strength** control.
   A style LoRA is added last, if at all.
6. Composite into the base diffuse inside the mask only, with edge padding against seam
   bleed.
7. Export, update the manifest, run Module 1.

**Two custom nodes.** `RecastSegmentLoader` (manifest path + segment id → mask, base
diffuse, bbox, panel size) and `RecastCompositeExport` (generated image + base + mask +
bbox → composited texture, output path, validation report path). Everything else is stock.

**Known constraints**

- The GPU is an RTX 2080, 8 GB. Generating at 1024 and upscaling is comfortable; working
  directly at 2048 is not. The graph includes an upscale step.
- SDXL LoRA training on 8 GB is painful. Three options when we reach it: train small and
  slow locally, rent a cloud GPU for a few hours, or skip the LoRA and report honestly
  that base model plus ControlNet was sufficient. The brief's comparison table already
  permits the third.
- Painting in UV space is the real technical risk. The island is an irregular silhouette,
  not a rectangle, and a result that reads well flat can warp on the model. Every result is
  checked in 3D, and one failure case goes in the write-up.

**Acceptance.** Pixels outside the mask plus padding identical to the input, proven by an
automated image diff. Three restyle strengths give three visibly different results. Output
passes Module 1. No visible seam or stretching in the 3D view. Three different references
work.

## Module 3 — Unity editor importer

**Risk: low. Roughly two-thirds exists.**

Extend `DesignImporter`: run the Module 1 report as part of import, show failures in the
editor, and accept a folder of variants in one pass. Keep every existing convention —
nothing written into `GloveAsset`, one folder per design, the design number substituted
into names rather than appended.

**Acceptance.** One click imports a variant set, and validation failures are visible in
the editor.

## Module 4 — Procedural material

Optional. Only after 1–3 are finished.

## How this reaches the portfolio

The rule: **a web page can show a result, not a process running on the author's machine.**
ComfyUI, headless Blender and the Unity Editor all need a GPU, an install and local files.
What they produce is files, and files publish fine.

**Interactive in the browser**
- The existing configurator.
- The AI-generated variants, baked into the WebGL build as additional designs, so a
  visitor wears them in 3D. This is the payoff: the pipeline's output experienced live,
  without the pipeline running on the visitor's machine.
- Module 1's HTML report, hosted as its own page. It is already a web page; it costs
  nothing extra.

**Embedded media**
- A 90-second recording of the loop: generate, graph runs, texture lands on the glove,
  validator passes.
- One annotated screenshot of the node graph.

**Text**
- The comparison table: minutes of cleanup, pass rates, palette drift. Measured.

## Order of work

1. Module 1, including the manifest extension and the generated UV masks.
2. Module 2, LoRA last and optional.
3. Module 3.
4. Module 4 only if time allows.

Applications can start once Module 1 exists; it is a credible artifact on its own.

## Open items for the owner

1. Approve the threshold sourcing approach above, or supply numbers with a citable origin.
2. Install ComfyUI and download the base model and ControlNet when Module 2 starts.
3. Decide the LoRA question: local, cloud, or skip and report.
4. Judge the art. Seams and alignment are measurable; whether a result reads as the right
   style is not.
5. Record the demo clips at the end.
