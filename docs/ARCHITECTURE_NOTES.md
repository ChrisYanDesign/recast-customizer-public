# Architecture notes

Step 0 of the Recast Pipeline plan: what the tool is today, before anything is added.
Written at v2.2. Companion to `PROJECT_STATE.md`, which covers conventions and traps.

## 1. There is no web dashboard

The pipeline plan assumes a web front end that a "Generate variant" button can be added
to. There isn't one. The browser tool **is the Unity project**, compiled to WebGL:

- UI is C# using Unity's UI Toolkit, built at runtime in `GloveCustomizerUI.cs` (2,884
  lines, the largest file in the project).
- The 3D view is the same URP scene that renders in the editor, `Assets/Scenes/glove_studio.unity`.
- There is no HTML, no JavaScript framework, no server. The only hand-written browser
  code is `Assets/Plugins/WebGL/RecastDownload.jslib`, eleven lines that hand a string to
  the browser as a file download. That is how Export works.

Consequence for the plan: anything the plan wants the "dashboard" to do — call a local
ComfyUI server, read a folder, write a texture — a WebGL build cannot do. Those belong in
the **Unity Editor**, which has no such restrictions. See `PIPELINE_PLAN.md`.

## 2. Where the data lives

Three layers, in order of authority.

**`falcon_glove_variant_set.asset`** — a ScriptableObject, and the real source of truth at
runtime. Per segment it holds direct references to materials, thumbnails and variant ids,
plus five gameplay attribute values per variant. Direct asset references, so nothing
depends on string lookup at runtime. Defined in `GloveVariantSet.cs`.

**`glove_manifest.json`** — a human-readable mirror of the same structure: segments, their
child renderer names, variant ids, and the material/AO naming patterns. Nothing reads it
at runtime; it documents the convention. **This is the file the new pipeline should
extend**, because Python and ComfyUI can read it and cannot read a Unity asset.

**`SavedDesign`** (`SavedDesign.cs`) — the travelling unit. A named design records every
part, not only the changed ones: segment, variant id, a `tuned` flag, and five values
(hue, saturation, brightness, smoothness, normal). Serialised with `JsonUtility`, saved to
PlayerPrefs in the browser, and exported as a `.json` file. This is the existing handoff
format and the new modules should read it rather than inventing another.

## 3. Segment and material structure

Four segments — `top`, `bottom`, `thumb`, `tassel` — each a separate child renderer under
`falcon_glove_assembly.prefab` with its own material. Three authored variants each, so
3^4 = 81 combinations.

Materials are stock **URP/Lit**, no custom shader dependency. Naming is
`falcon_glove_{segment}_{variant}_mat`, with a shared occlusion map per segment.

**Texture layout, which the pipeline plan gets wrong.** There is no shared atlas. Each
segment-variant pair has its own 2048x2048 diffuse map — twelve in total. Each of those is
a UV layout: one large irregular island (the flattened panel, with its painted detail)
surrounded by edge-bleed padding.

So the plan's `uv_mask` concept is still needed — an AI restyle must repaint inside the
island only — but the mask is **derivable from the mesh UVs**, not something an artist
authors. Blender can render one per segment. The plan's `uv_bbox` is likewise computed,
not authored.

The source mesh is `GloveAsset/falcon_glove.obj`, 4,935 triangles across four named
groups matching the segment ids. It replaced a denser 13,122-triangle version of the same
glove, a copy of which is kept at `backup/falcon_glove_highpoly.obj` because the normal bake needs
something to bake from.

## 4. Runtime code

| File | Role |
|---|---|
| `GloveCustomizerUI.cs` | The whole interface: segment rows, design library, sliders, export |
| `MaterialTuner.cs` | Applies the five look values; captures and restores a `SavedDesign` |
| `ModularGloveAssembly.cs` | Holds the current per-segment selection and assigns materials |
| `OrbitCamera.cs`, `ViewModeController.cs`, `SecondaryMotion.cs` | Camera, view modes, tassel sway |
| `Recast_HSVAdjust.shader` | Hue/saturation/brightness, applied through `Graphics.Blit` |

Two of the five look controls are free: smoothness and normal strength are material
values. Hue, saturation and brightness cannot be expressed on a stock Lit material, so
each tuned part needs a newly baked texture. `SavedDesign.BakeCount` is what the UI
reports as "new maps if saved".

## 5. Editor code

Thirteen editor scripts under `Scripts/Editor`, all exposed under a single
**Recast Customizer** menu. The ones that matter to the pipeline:

- **`DesignImporter.cs`** — takes an exported design file and generates materials, baked
  maps and a prefab into `NewDesigns/design_NN/`, then registers the design as a new
  variant in the set. Writes only what differs; a merely-swapped part reuses the authored
  material. This is most of Module 3 already.
- **`LookFileImporter.cs`** — the older single-look path, still used by *Apply Look File*.
- **`DesignRemover.cs`** — deletes a generated design and its folder.
- **`WebBuildSetup.cs`** — the WebGL build, as menu items 1, 2 and 2b.
- **`ThumbnailBaker.cs`** — regenerates every variant thumbnail. Destructive; see
  `PROJECT_STATE.md`.

## 6. Build and run

**Run in the editor:** open `Assets/Scenes/glove_studio.unity` and press Play.

**Web build:** Recast Customizer ▸ Web Build ▸ *1. Configure And Switch Platform*, then
*2b. Build To Default Folder*. Output is a standard Unity WebGL folder, uploaded to Unity
Play by hand.

There is no command-line build, no package manifest beyond Unity's own, and no test suite.
Unity 6000.0.58f2, URP 17.0.4, Linear colour space.

## 7. Available locally

Blender 5.1 and Python 3.14.5 are both installed and on PATH, so Module 1 can be built and
run without any new installation. ComfyUI is not installed yet.

The Unity editor bridge (MCP for Unity 3.4.7) runs on `127.0.0.1:8080` when Unity is open.

## 8. What this means for the plan

Four reconciliations, carried into `PIPELINE_PLAN.md`:

1. No web dashboard exists — the ComfyUI integration belongs in the editor.
2. The manifest already exists and should be extended, not replaced.
3. There is no texture atlas; masks are generated from mesh UVs instead of authored.
4. Module 3 is substantially built; extend `DesignImporter` rather than starting over.
