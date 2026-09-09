# Recast Customizer — Falcon Glove
<img width="1500" height="843" alt="project_cover_image" src="https://github.com/user-attachments/assets/f9ec928f-7139-4e2d-9cbc-772b65dfd89d" />


A material review tool for modular game assets. It puts an asset in a browser under the
lighting it was authored against, lets anyone swap parts and move five material values,
then writes those changes to a file that an artist applies back in Unity.

**[Try it in your browser](https://play.unity.com/api/v1/games/game/f5c98ea7-d099-4096-888b-c55dacaadd11/build/latest/frame)** · Unity 6 · URP 17.0.4 · WebGL

---

## The problem it solves

Material feedback usually arrives as a sentence in a chat thread. *"Warmer leather, less
shine on the cuff."* The artist reads it, guesses at a number, and sends another render.

This removes the guess. The reviewer needs no engine licence, no project checkout, and no
idea what a smoothness value is. They need an opinion, and this gives them a way to
express it that survives the trip back to the material.

```
Browser  →  recast-look.json  →  Unity menu item  →  the real materials
```

Nothing about that loop is decorative. It is the reason the tool exists, and everything
else in the repository serves it.

---

## What is in this repository

Source only: the C# that drives the tool, the two shaders written for it, and the browser
plugin that carries a file out of the WebGL sandbox. The asset, the scene and the
materials are not here — see [What is not here](#what-is-not-here).

### Runtime

| File | What it does |
|---|---|
| `GloveCustomizerUI.cs` | The whole interface, built in UI Toolkit and driven from the variant data rather than from the scene. |
| `MaterialTuner.cs` | Five live material parameters, keyed per part and per design. Cannot reach a material asset — see below. |
| `GloveVariantSet.cs` | The ScriptableObject describing parts and their variants. Adding a design means editing a list. |
| `ModularGloveAssembly.cs` | Resolves parts to child renderers and applies a chosen variant. |
| `ViewModeController.cs` | Channel isolation view modes. Non-destructive: originals are held and restored. |
| `OrbitCamera.cs` | Framing, asymmetric orbit limits, zoom and pan. |
| `CustomizerTour.cs` | The six-step first-run guide. |
| `SecondaryMotion.cs` | Tassel sway, rotating about the mesh-bounds top so the attachment point never moves. |
| `CustomizerChrome.cs`, `GloveCustomizerOverlay.cs` | Supporting interface pieces. |

### Shaders

| File | What it does |
|---|---|
| `Recast_HSVAdjust.shader` | A texture pass. Shifts hue, saturation and brightness on a base map before an untouched Lit material ever reads it. |
| `Recast_Inspect.shader` | Five inspection modes: albedo, tangent-space normal, smoothness, UV checker, world normal. |
| `BlurredSkybox.shader` | Backdrop. |

### Editor tooling

The half of the work nobody sees, and the part that separates a tool from a demo.

| File | Menu item |
|---|---|
| `LookFileImporter.cs` | Applies a returned look file. Lists every material it will change and waits for confirmation. |
| `ThumbnailBaker.cs` | Bakes the variant thumbnails so a design is chosen by image, not by filename. |
| `WebBuildSetup.cs` | One-click WebGL build with the platform settings already applied. |
| `StudioSceneBuilder.cs` | Applies the studio lighting rig documented in [`docs/LIGHTING_SETUP.md`](docs/LIGHTING_SETUP.md). |
| `GloveAssemblyBuilder.cs`, `CustomizerUIBuilder.cs`, `AttributeSeeder.cs`, `AmbientMotesBuilder.cs`, `BackdropTool.cs` | Scene and data scaffolding. |

### Browser plugin

`RecastDownload.jslib` — a WebGL build has no filesystem, so the usual write call silently
does nothing. This copies the bytes out of the engine heap into a blob and clicks a
temporary link, which produces an ordinary download.

---

## Three decisions worth reading

**Adjust the texture, not the shader.** A stock URP Lit material can only multiply its
base colour, and a multiply can tint but never rotate a hue or drain saturation. Rewriting
Lit would have put the calibrated lighting at risk. So the base map is blitted through an
HSV pass into a render texture, and an untouched Lit material reads that instead. The
lighting stays exactly as measured, and hue becomes a real control.

**Colourize what has no hue to rotate.** A grey pixel has no position on the colour wheel,
so the near-black variant ignored the hue slider entirely — correctly. A second path,
chosen per pixel by how neutral that pixel already is, gives grey pixels a hue while
keeping their own luminance, so every scratch and grain in the map survives. No mode
switch, no second slider.

```hlsl
float  neutral = 1.0 - saturate(hsv.y / _NeutralCut);
float  inject  = saturate(_Saturation - 1.0);
float3 tinted  = HsvToRgb(float3(frac(_Hue + 1.0), inject, hsv.z * _Brightness));

return float4(lerp(rotated, tinted, neutral * inject), src.a);
```

**It cannot touch an asset.** Every value the reviewer moves is applied to a runtime
material instance. The lookup that resolves a live material returns nothing at all outside
play mode, so there is no code path from a slider to a saved asset. On the editor side the
importer confirms before writing. Both guards exist because an early test wrote two values
into real materials, and one guard would not have been enough.

---

## Four bugs worth keeping

Each of these is a mistake you only make by shipping in Unity, and only find the same way.

**Code changes had no effect on the running scene.** A value already serialized into the
scene beats a default written in the script. The code was correct; the scene never asked
it. Set the value through the editor's own serialization, or rename the field so it arrives
as new and picks up the default.

**The normal map view came out bright pink.** Unity stores normal maps in a compressed
layout that moves X and Y into the alpha and green channels. Sampling the colour directly
shows the packing, not the normal. `UnpackNormal` before display — and beyond looking
wrong, the raw read was presenting data unrelated to the surface being inspected.

**Every thumbnail was subtly stretched.** The sources were 965 × 685. Unity rounded them
to the nearest power of two, 1024 × 512, and the aspect ratio went with it. Turning
`npotScale` off at import fixes it. The same constraint later blocked block compression,
which is why the memory pass became a resolution cap rather than a format change — 16.55 MB
down to 3.65 MB.

**Turning on wireframe took the whole editor down.** The render hook fired for every camera
Unity draws, which in the editor includes the hidden preview cameras that generate Project
window thumbnails. Forcing wireframe onto those is not survivable. Narrowing the hook to
the one camera that wants it, in play mode only, removes the entire class of problem:

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
| Design combinations | 81 |
| Live material parameters | 5 |
| Shaders written | 2 |
| Build size | 23 MB |
| Team | 1 |

---

## What is not here

This is a reading repository, not a buildable project. Dropping it into an empty Unity
project will not produce the tool, and that is deliberate — the code is the interesting
part, and a 300 MB clone is not.

Not included: the glove mesh and its textures, the materials, the scene, the prefabs, the
variant set asset, and the `.meta` files Unity generates. If you want the running tool,
use the [WebGL build](https://play.unity.com/api/v1/games/game/f5c98ea7-d099-4096-888b-c55dacaadd11/build/latest/frame).

If you want to run something like it against your own asset: the tool is asset-agnostic by
design. It reads parts and variants from a `GloveVariantSet`, so a different mesh with
named child renderers and a matching variant set works without a code change.

---

## Credit and licence

Asset, shaders, tooling, interface and build by Chris Yan. The falcon glove mesh, textures
and material parameters are my own work.

No licence is granted for reuse. This is published to be read.
