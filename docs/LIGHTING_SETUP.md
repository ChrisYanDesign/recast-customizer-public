# Studio Lighting Setup

The lighting configuration this project standardised on, and why. Every value here is
applied by **Recast Customizer ▸ Setup Studio Lighting** (`StudioSceneBuilder.cs`), so this
document and the code should always agree.

A review tool is only as trustworthy as its render. If the viewport flatters the asset, a
reviewer approves a look that falls apart in engine. So the lighting was settled first,
before any of the interface existed, and it has not been touched since.

---

## Environment (RenderSettings)

| Setting | Value |
|---|---|
| Ambient Mode | `Skybox` (m_AmbientMode: 0) |
| **Ambient Intensity** | **1.1** |
| Reflection Source | Skybox |
| Reflection Intensity | 1.0 |
| Reflection Bounces | 1 |
| Fog | off |

Baked ambient fallback colours (unused while ambient mode is Skybox):
sky `0.212, 0.227, 0.259` · equator `0.114, 0.125, 0.133` · ground `0.047, 0.043, 0.035`

## Skybox material

Built-in **Skybox/Cubemap** shader, created as
`Assets/Settings/Recast_Studio_Skybox.mat`.

| Property | Value |
|---|---|
| Tint | `0.5, 0.5, 0.5` (a 0.5) |
| Exposure | 1.0 |
| Rotation | 0 |
| Cubemap | supply your own, or a CC0 studio HDRI |

The tint is deliberately neutral grey. The sky is here to supply light, not colour: a
tinted sky would push every material's hue and make the review dishonest.

## Key light (`dominant_directional`)

| Setting | Value |
|---|---|
| Type | Directional |
| Colour | white `1,1,1` |
| **Intensity** | **0.2** |
| Indirect multiplier | 1.0 |
| Shadows | Soft (type 2) |
| Mode | Realtime |
| Rotation | `50, -30, 0` |

One directional light only. No fill, no rim. Extra lights are how a viewport starts
flattering an asset.

## Post-processing

**Deliberately empty.** The Global Volume is created with no profile at all.

| Override | Intended state |
|---|---|
| Tonemapping | None |
| Bloom | off |
| Vignette | off |
| Film Grain | off |
| Color Adjustments | no grading: exposure 0, contrast 0, saturation 0, hue 0 |
| Split Toning | none |
| Lift / Gamma / Gain | neutral |

Since every one of those is already the default, an empty volume is the honest way to
express it. Adding a profile full of zeroes would only invite someone to nudge one.

---

## The takeaway

This setup is **HDRI-driven, not post-driven**. A studio HDRI supplies almost all the
light at ambient 1.1, and a single very dim directional at 0.2 adds shadow definition.
No tonemapping, no bloom, no grading.

That is also why the glove looks wrong in a default Unity scene. Unity's default
directional light sits at intensity 1.0, five times this key light, with no HDRI to fill
around it. The asset is not the problem in that case; the rig is.

## Reproducing it

1. Run **Recast Customizer ▸ Setup Studio Lighting**. It applies every value above to the
   current scene and creates the skybox material.
2. Supply the one missing piece, an HDRI. Free CC0 options are at
   <https://polyhaven.com/hdris/studio>.
3. Import it with **Texture Shape = Cube**, then drop it into the skybox material's
   Cubemap slot.
