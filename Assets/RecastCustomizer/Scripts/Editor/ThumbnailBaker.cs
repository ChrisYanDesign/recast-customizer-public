using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Recast Customizer ▸ Bake Variant Thumbnails
    ///
    /// Photographs each segment/variant in the open scene and saves the result as a PNG,
    /// then wires the images into the variant set so the customizer swatches show a real
    /// preview of the part.
    ///
    /// It borrows the scene's own Main Camera rather than spawning a fresh one. A camera
    /// created from scratch does not carry the render pipeline's per-camera settings, and
    /// renders noticeably darker than what you see in the Game view. Reusing the real
    /// camera means the thumbnails match the app exactly. Every property it touches is
    /// restored afterwards.
    ///
    /// Re-run it whenever the materials or lighting change.
    /// </summary>
    public static class ThumbnailBaker
    {
        private const string Root = "Assets/RecastCustomizer";
        private const string VariantSetPath = Root + "/falcon_glove_variant_set.asset";
        private const string ThumbFolder = Root + "/Thumbnails";

        private const int Resolution = 256;

        /// <summary>
        /// How much of the segment fills the frame. 1 fits the whole part; below 1 pushes in
        /// and crops. These swatches identify a material, not a silhouette, so they crop
        /// tight onto the surface detail.
        /// </summary>
        private const float FrameZoom = 0.42f;

        /// <summary>Viewing angle for the crop — a slight three-quarter turn reads better than flat on.</summary>
        private static readonly Vector3 ViewAngle = new(14f, -22f, 0f);

        /// <summary>
        /// Extra light mounted on the camera during the bake only. The scene key light is a
        /// dim 0.2 with the HDRI doing the work, which is right for the hero view but leaves
        /// a tight crop flat. 0 disables it.
        /// </summary>
        private const float FillLightIntensity = 0.8f;

        /// <summary>Final exposure lift on the saved image. Raise if swatches still read dark.</summary>
        private const float Brightness = 1.15f;

        /// <summary>Matches the customizer panel's chip colour so swatches sit flush.</summary>
        private static readonly Color Background = new(0.11f, 0.12f, 0.13f, 1f);

        [MenuItem("Recast Customizer/Bake Variant Thumbnails")]
        public static void BakeThumbnails()
        {
            var set = AssetDatabase.LoadAssetAtPath<GloveVariantSet>(VariantSetPath);
            if (set == null)
            {
                Debug.LogError("[ThumbnailBaker] No variant set found. " +
                               "Run Recast Customizer ▸ Rebuild Variant Set From Folder first.");
                return;
            }

            var assembly = Object.FindFirstObjectByType<ModularGloveAssembly>();
            if (assembly == null)
            {
                Debug.LogError("[ThumbnailBaker] No ModularGloveAssembly in the open scene.\n" +
                               "  Open glove_studio and try again.");
                return;
            }

            // Ask before overwriting. These files can be hand-authored -- a crop from a
            // proper render rather than a snapshot of the viewport -- and a generated
            // preview is a poor substitute for one an artist made. Losing them to a menu
            // click nobody thought twice about is exactly the kind of accident worth a
            // dialog.
            int existing = 0;
            foreach (var seg in set.segments)
                for (int v = 0; v < seg.VariantCount; v++)
                    if (seg.VariantThumbnail(v) != null) existing++;

            if (existing > 0 && !EditorUtility.DisplayDialog(
                    "Overwrite existing thumbnails?",
                    "This regenerates every variant preview by rendering the asset in the "
                    + "current scene, replacing " + existing + " image(s) already in the "
                    + "project." + System.Environment.NewLine + System.Environment.NewLine
                    + "If any of them were authored by hand, they will be lost. Undo does "
                    + "not recover an overwritten file on disk.",
                    "Overwrite", "Cancel"))
            {
                Debug.Log("[ThumbnailBaker] Cancelled. No thumbnail was changed.");
                return;
            }

            var cam = Camera.main;
            if (cam == null)
            {
                Debug.LogError("[ThumbnailBaker] No camera tagged MainCamera in the open scene.");
                return;
            }

            Directory.CreateDirectory(ThumbFolder);

            // ---- remember everything we are about to touch ----
            var allRenderers = assembly.GetComponentsInChildren<Renderer>(true);
            var wasEnabled = new Dictionary<Renderer, bool>();
            var originalMaterials = new Dictionary<Renderer, Material>();
            foreach (var r in allRenderers)
            {
                wasEnabled[r] = r.enabled;
                originalMaterials[r] = r.sharedMaterial;
            }

            var camTransform = cam.transform;
            var savedPosition = camTransform.position;
            var savedRotation = camTransform.rotation;
            var savedFov = cam.fieldOfView;
            var savedClear = cam.clearFlags;
            var savedBg = cam.backgroundColor;
            var savedTarget = cam.targetTexture;
            var savedNear = cam.nearClipPlane;
            var savedFar = cam.farClipPlane;

            // sRGB is explicit: the project renders in Linear colour space, and a default
            // render target would hand back un-converted values that look about half as bright.
            var rt = new RenderTexture(Resolution, Resolution, 24,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };

            Light fill = null;
            var written = new List<string>();

            try
            {
                cam.targetTexture = rt;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Background;
                cam.fieldOfView = 34f;

                if (FillLightIntensity > 0f)
                {
                    var fillGo = new GameObject("~ThumbnailFill") { hideFlags = HideFlags.HideAndDontSave };
                    fillGo.transform.SetParent(camTransform, false);
                    fill = fillGo.AddComponent<Light>();
                    fill.type = LightType.Directional;
                    fill.intensity = FillLightIntensity;
                    fill.shadows = LightShadows.None;
                    fill.transform.localRotation = Quaternion.Euler(10f, 6f, 0f);
                }

                foreach (var segment in set.segments)
                {
                    var target = FindRenderer(allRenderers, segment.childRenderer);
                    if (target == null)
                    {
                        Debug.LogWarning($"[ThumbnailBaker] No renderer named '{segment.childRenderer}' — skipping '{segment.id}'.");
                        continue;
                    }

                    // isolate: the swatch should show only the part being chosen
                    foreach (var r in allRenderers) r.enabled = r == target;

                    NormalizeList(segment.variantThumbnails, segment.VariantCount);

                    for (int v = 0; v < segment.VariantCount; v++)
                    {
                        var mat = segment.variants[v];
                        if (mat == null) continue;

                        target.sharedMaterial = mat;
                        FrameRenderer(cam, target);

                        var path = $"{ThumbFolder}/{set.assetName}_{segment.id}_{segment.VariantId(v)}.png";
                        RenderToPng(cam, rt, path);
                        written.Add(path);
                    }
                }
            }
            finally
            {
                // always restore, even if a render throws
                if (fill != null) Object.DestroyImmediate(fill.gameObject);

                cam.targetTexture = savedTarget;
                cam.clearFlags = savedClear;
                cam.backgroundColor = savedBg;
                cam.fieldOfView = savedFov;
                cam.nearClipPlane = savedNear;
                cam.farClipPlane = savedFar;
                camTransform.SetPositionAndRotation(savedPosition, savedRotation);

                RenderTexture.active = null;
                Object.DestroyImmediate(rt);

                foreach (var r in allRenderers)
                {
                    if (wasEnabled.TryGetValue(r, out var e)) r.enabled = e;
                    if (originalMaterials.TryGetValue(r, out var m)) r.sharedMaterial = m;
                }
                assembly.ApplyAll();
            }

            AssetDatabase.Refresh();
            foreach (var path in written) ConfigureImporter(path);
            AssetDatabase.Refresh();

            int assigned = 0;
            foreach (var segment in set.segments)
            {
                NormalizeList(segment.variantThumbnails, segment.VariantCount);
                for (int v = 0; v < segment.VariantCount; v++)
                {
                    var path = $"{ThumbFolder}/{set.assetName}_{segment.id}_{segment.VariantId(v)}.png";
                    var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (tex == null) continue;
                    segment.variantThumbnails[v] = tex;
                    assigned++;
                }
            }

            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();

            Debug.Log($"[ThumbnailBaker] Rendered {written.Count} swatches, assigned {assigned}.\n" +
                      $"  {ThumbFolder}");
            Selection.activeObject = set;
        }

        private static Renderer FindRenderer(Renderer[] renderers, string name)
        {
            foreach (var r in renderers)
                if (r.gameObject.name == name) return r;
            return null;
        }

        /// <summary>Points the camera at one renderer and crops in on its surface.</summary>
        private static void FrameRenderer(Camera cam, Renderer target)
        {
            var bounds = target.bounds;
            float radius = Mathf.Max(bounds.extents.magnitude, 0.0001f);
            float distance = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * FrameZoom;

            var dir = Quaternion.Euler(ViewAngle) * Vector3.back;
            cam.transform.position = bounds.center + dir * distance;
            cam.transform.LookAt(bounds.center);

            cam.nearClipPlane = Mathf.Max(0.0001f, distance * 0.01f);
            cam.farClipPlane = distance * 10f;
        }

        private static void RenderToPng(Camera cam, RenderTexture rt, string path)
        {
            cam.Render();

            var previous = RenderTexture.active;
            RenderTexture.active = rt;

            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, linear: false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();

            RenderTexture.active = previous;

            if (!Mathf.Approximately(Brightness, 1f))
            {
                var pixels = tex.GetPixels();
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i].r = Mathf.Clamp01(pixels[i].r * Brightness);
                    pixels[i].g = Mathf.Clamp01(pixels[i].g * Brightness);
                    pixels[i].b = Mathf.Clamp01(pixels[i].b * Brightness);
                    pixels[i].a = 1f;
                }
                tex.SetPixels(pixels);
                tex.Apply();
            }

            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        private static void ConfigureImporter(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;

            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.maxTextureSize = Resolution;
            importer.SaveAndReimport();
        }

        private static void NormalizeList<T>(List<T> list, int count)
        {
            while (list.Count < count) list.Add(default);
            while (list.Count > count) list.RemoveAt(list.Count - 1);
        }
    }
}
