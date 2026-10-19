# Recast Customizer — Falcon Glove
<img width="1500" height="843" alt="project_cover_image" src="https://github.com/user-attachments/assets/f9ec928f-7139-4e2d-9cbc-772b65dfd89d" />


Three authored designs, four modular parts, and every recombination of them guaranteed
to work. A content team builds new designs in a browser, and Unity generates the assets
from the file they send back.

**[Try it in your browser](https://play.unity.com/api/v1/games/game/f5c98ea7-d099-4096-888b-c55dacaadd11/build/latest/frame)** · Unity 6 · URP 17.0.4 · WebGL

---

## What it is for

An artist authors three complete designs. That is the quality bar, and the only work
that needs an artist's hands.

The glove is a modular mesh, and every part can wear any of those designs, so the parts
recombine into pieces the artist never made. The guarantee matters more than the count:
every combination is valid, with no missing map and no broken assignment, whichever way
the parts are swapped. On top of a chosen combination, five material values refine the
look.

A design worth keeping is named, saved and exported as one file. In Unity an importer
reads it, generates the materials and maps that design needs, writes a prefab, and
registers it alongside the authored three. Nobody retypes a number and nobody hand-builds
an asset.

```
Save  →  Export JSON  →  Import in Unity  →  Materials + Maps + Prefab  →  New design
```

The value is not the size of the design space. It is that a content team can walk it
without an artist, keep what is worth shipping, and have the engine reproduce it exactly.

---

## What is in this repository

Source only: the C# that drives the tool, the shaders written for it, the Python and
headless-Blender pipeline that checks the asset, and the browser plugin that carries a
file out of the WebGL sandbox. The asset, the scene and the materials are not here — see
[What is not here](#what-is-not-here).

### Runtime

| File | What it does |
|---|---|
| `GloveCustomizerUI.cs` | The whole interface, built in UI Toolkit and driven from the variant data rather than from the scene. |
| `SavedDesign.cs` | A design as data: a variant per part plus that part's tuned values, with the library that keeps them and the flag that tells a swapped part from a tuned one. |
| `MaterialTuner.cs` | Five live material parameters per part. Captures and restores a whole design. Cannot reach a material asset — see below. |
| `GloveVariantSet.cs` | The ScriptableObject describing parts and their variants. Adding a design means editing a list. |
| `ModularGloveAssembly.cs` | Resolves parts to child renderers and applies a chosen variant. |
| `ViewModeController.cs` | Channel isolation view modes. Non-destructive: originals are held and restored. |
| `OrbitCamera.cs` | Framing, asymmetric orbit limits, zoom and pan. |
| `CustomizerTour.cs` | The six-step first-run guide. |
| `SecondaryMotion.cs` | Tassel sway, rotating about the mesh-bounds top so the attachment point never moves. |
| `RuntimeValidator.cs` | The checks, run in the build on the asset in the viewport: triangles, materials, texture sizes and memory, empty map slots, UVs off the sheet or overlapping, a colour map that is really a normal map, and a mesh whose triangle count no longer matches the record. A coroutine, so the progress bar follows real work. |
| `ValidationReport.cs` | The recorded report, for the three checks a browser cannot make: file naming, unused textures, import settings. |
| `CustomizerChrome.cs`, `GloveCustomizerOverlay.cs` | Supporting interface pieces. |

### Shaders

| File | What it does |
|---|---|
| `Recast_HSVAdjust.shader` | A texture pass. Shifts hue, saturation and brightness on a base map before an untouched Lit material ever reads it. |
| `Recast_Inspect.shader` | Five inspection modes: albedo, tangent-space normal, smoothness, UV checker, world normal. |
| `BlurredSkybox.shader` | Backdrop. |

### Editor tooling

Twenty-one menu items. This is the half of the work nobody sees, and the part that
separates a tool from a demo.

| File | What it does |
|---|---|
| `DesignImporter.cs` | Reads a design file and generates the assets: materials only where a part was tuned, a baked map where the colour moved, a prefab, and a new entry in the variant set. |
| `DesignRemover.cs` | The un-import. Takes a generated design back out, and empties the browser-side design library. |
| `LookFileImporter.cs` | The review leg. Applies a returned file onto existing materials, listing every one it will change and waiting for confirmation. |
| `ThumbnailBaker.cs` | Bakes the variant thumbnails so a design is chosen by image, not by filename. |
| `WebBuildSetup.cs` | One-click WebGL build with the platform settings already applied. |
| `StudioSceneBuilder.cs` | Applies the studio lighting rig documented in [`docs/LIGHTING_SETUP.md`](docs/LIGHTING_SETUP.md). |
| `DesignValidator.cs` | The budget checks on a design, reading the AssetDatabase and the texture importers rather than shelling out to the pipeline, so they run on every import without needing Python or Blender. |
| `DesignImportWindow.cs` | The result, for one design or twenty: failures first, passes collapsed behind a count. |
| `DesignChecker.cs` | Re-measures designs already in the project, because a design checked once is not a design still correct. |
| `ValidationWindow.cs` | Runs the offline pipeline from the editor and shows its output, so nobody has to open a terminal. |
| `ValidationReportExporter.cs` | Carries the report and the limits into `Resources`, which is how the running build knows the rules. |
| `BrokenShowcaseBuilder.cs` | Turns the deliberately faulty kit into a viewable asset, for demonstrating the checks against something that fails. |
| `DesignRemoverBridge.cs` | Hands the running tool a way to delete a design. Runtime code cannot call the editor -- the editor assembly is built second -- so the dependency runs the other way. |
| `GloveAssemblyBuilder.cs`, `CustomizerUIBuilder.cs`, `AttributeSeeder.cs`, `AmbientMotesBuilder.cs`, `BackdropTool.cs` | Scene and data scaffolding. |

### Browser plugin

`RecastDownload.jslib` — a WebGL build has no filesystem, so the usual write call silently
does nothing. This copies the bytes out of the engine heap into a blob and clicks a
temporary link, which produces an ordinary download.

---

### The asset pipeline

`pipeline/` — Python and headless Blender, outside `Assets/` so Unity ignores it.

One command measures the kit and checks it against a rules file, writing an HTML report,
a JSON report, and a non-zero exit code when anything fails:

    python pipeline/check.py

Every limit records where it came from, in the rules file itself. A limit left `null` is
measured and reported but not judged, which is how a limit gets chosen rather than
guessed. Ten generated fixtures prove each check fires for the right reason, and a
deliberately faulty copy of the real glove is derived by script rather than hand-made.

The same checks run in three places, which is the point of it:

| | |
|---|---|
| A terminal | unattended, with an exit code |
| The Unity editor | on a design, as it is imported |
| **The running tool** | in the browser, on the asset in the viewport |

That last one measures triangles, materials, texture sizes and memory, empty map slots,
UVs off the sheet or overlapping, a colour map that is really a normal map, and a mesh
whose triangle count no longer matches what was recorded. Anything that fails is **ringed
on the glove**, and a fault with a place — faces missing from a part — is ringed at that
place rather than around the whole part, by finding the boundary loop the missing faces
left.

The editor and browser checks are C# reading the AssetDatabase; the offline ones are
Python reading files. Both take their limits from one generated file, so they cannot
describe different rules. Measured against the authored kit they arrive at 33.23 MB
independently.

`pipeline/comfyui/` holds two custom ComfyUI nodes for restyling one part's texture in
place, with a composite step that proves on every run that nothing outside the target
region changed. The art it produced was not good enough to ship, which is written up
rather than hidden.

---

## Four decisions worth reading

**Adjust the texture, not the shader.** A stock URP Lit material can only multiply its
base colour, and a multiply can tint but never rotate a hue or drain saturation.
Rewriting Lit would have put the calibrated lighting at risk. So the base map is blitted
through an HSV pass into a render texture, and an untouched Lit material reads that
instead. The lighting stays exactly as measured, and hue becomes a real control.

**Colourize what has no hue to rotate.** A grey pixel has no position on the colour
wheel, so the near-black variant ignored the hue slider entirely — correctly. A second
path, chosen per pixel by how neutral that pixel already is, gives grey pixels a hue
while keeping their own luminance, so every scratch and grain in the map survives. No
mode switch, no second slider.

```hlsl
float  neutral = 1.0 - saturate(hsv.y / _NeutralCut);
float  inject  = saturate(_Saturation - 1.0);
float3 tinted  = HsvToRgb(float3(frac(_Hue + 1.0), inject, hsv.z * _Brightness));

return float4(lerp(rotated, tinted, neutral * inject), src.a);
```

**Write only the difference.** A part that was merely swapped already has a finished
material on disk, so a generated design references it and nothing is created. Only a
tuned part costs an asset, so one design might produce a single material rather than
four. A full material set per design multiplies assets fast, and on a live-service
project that is a real cost rather than an argument about tidiness.

**It cannot touch what the artist made.** Every value a designer moves is applied to a
runtime material instance, and the lookup that resolves one returns nothing outside play
mode, so there is no code path from a slider to a saved asset. Generated assets are
written to their own folder, never into the authored one. See
[`docs/DESIGN_FILE_LAYOUT.md`](docs/DESIGN_FILE_LAYOUT.md).

---

## Four bugs worth keeping

Each of these is a mistake you only make by shipping in Unity, and only find the same way.

**Code changes had no effect on the running scene.** A value already serialized into the
scene beats a default written in the script. The code was correct; the scene never asked
it. Set the value through the editor's own serialization, or rename the field so it
arrives as new and picks up the default.

**The normal map view came out bright pink.** Unity stores normal maps in a compressed
layout that moves X and Y into the alpha and green channels. Sampling the colour directly
shows the packing, not the normal. `UnpackNormal` before display — and beyond looking
wrong, the raw read was presenting data unrelated to the surface being inspected.

**A generated prefab destroyed its own materials on load.** The prefab was built by
copying the assembly from the scene, which carried `ModularGloveAssembly` with it. That
component re-applies materials from its own variant selection in `OnEnable` and
`OnValidate`, so the four assigned materials were correct on disk and overwritten the
instant the prefab entered a scene. A handoff prefab is a finished asset, not an instance
of the tool, so the runtime components are stripped before anything is assigned.

**Turning on wireframe took the whole editor down.** The render hook fired for every
camera Unity draws, which in the editor includes the hidden preview cameras that generate
Project window thumbnails. Forcing wireframe onto those is not survivable. Narrowing the
hook to the one camera that wants it, in play mode only, removes the entire class of
problem:

```csharp
private static bool IsViewportCamera(Camera cam)
{
    if (!Application.isPlaying) return false;
    if (cam == null) return false;
    if (cam.cameraType != CameraType.Game) return false;   // not SceneView, not Preview
    return cam == Camera.main;
}
```

---

## Numbers

| | |
|---|---|
| Engine | Unity 6000.0.58f2 |
| Pipeline | URP 17.0.4, linear colour space |
| Interface | UI Toolkit |
| Swappable parts | 4 |
| Designs per part | 3 |
| Live material parameters | 5 |
| Scripts | 31 |
| Python modules | 23 |
| Shaders written | 3 |
| Editor menu tools | 21 |
| Checks on the asset | 77 passing |
| Triangles | 5,003 against a budget of 6,000 |
| Texture memory per design | 33.23 MB |
| Build size | 36 MB |
| Team | 1 |

---

## What is not here

This is a reading repository, not a buildable project. Dropping it into an empty Unity
project will not produce the tool, and that is deliberate — the code is the interesting
part, and a 300 MB clone is not.

Not included: the glove mesh and its textures, the materials, the scene, the prefabs, the
variant set asset, the generated reports and UV masks, and the `.meta` files Unity
generates. If you want the running tool,
use the [WebGL build](https://play.unity.com/api/v1/games/game/f5c98ea7-d099-4096-888b-c55dacaadd11/build/latest/frame).

If you want to run something like it against your own asset: the tool is asset-agnostic by
design. It reads parts and variants from a `GloveVariantSet`, so a different mesh with
named child renderers and a matching variant set works without a code change.

---

## Credit and licence

Asset, shaders, tooling, interface and build by Chris Yan. The falcon glove mesh, textures
and material parameters are my own work.

No licence is granted for reuse. This is published to be read.
