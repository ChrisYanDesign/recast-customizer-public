using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Recast Customizer ▸ Apply Look File
    ///
    /// The return leg of the review loop. A reviewer with no Unity licence tunes the look in
    /// the web build and exports a small JSON file; this reads that file and writes the
    /// values onto the real material assets.
    ///
    /// Without this the sliders would only be a nicer version of the material Inspector,
    /// which artists already have. The file coming back is the part that solves a problem
    /// studios actually have, where art direction arrives as screenshots and vague notes.
    ///
    /// Every write goes through Undo, so one Ctrl+Z puts the materials back.
    /// </summary>
    public static class LookFileImporter
    {
        [Serializable]
        private class LookEntry
        {
            public string segment;
            public string variant;
            public float hue, saturation = 1f, brightness = 1f, smoothness = 0.5f, normal = 1f;
        }

        [Serializable]
        private class LookFile
        {
            public string asset;
            public string combination;
            public string savedUtc;
            public List<LookEntry> looks = new();
        }

        [MenuItem("Recast Customizer/Apply Look File")]
        public static void Apply()
        {
            var assembly = UnityEngine.Object.FindFirstObjectByType<ModularGloveAssembly>();
            if (assembly == null || assembly.VariantSet == null)
            {
                Debug.LogError("[LookFile] No ModularGloveAssembly with a variant set in the open scene. " +
                               "Open glove_studio and try again.");
                return;
            }

            var path = EditorUtility.OpenFilePanel("Choose a look file exported from the customizer", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            ApplyFile(path);
        }

        /// <summary>
        /// The menu item without the file dialog. Split out so the import can be driven from
        /// a script or a test, which a modal picker makes impossible.
        /// </summary>
        public static void ApplyFile(string path)
        {
            var assembly = UnityEngine.Object.FindFirstObjectByType<ModularGloveAssembly>();
            if (assembly == null || assembly.VariantSet == null)
            {
                Debug.LogError("[LookFile] No ModularGloveAssembly with a variant set in the open scene.");
                return;
            }

            LookFile file;
            try
            {
                file = JsonUtility.FromJson<LookFile>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogError("[LookFile] Could not read that file: " + e.Message);
                return;
            }

            if (file == null || file.looks == null || file.looks.Count == 0)
            {
                Debug.LogWarning("[LookFile] That file contains no adjustments. Nothing to apply.\n" +
                                 "  An export only records variants the reviewer actually changed.");
                return;
            }

            var set = assembly.VariantSet;
            if (!string.IsNullOrEmpty(file.asset) && file.asset != set.assetName)
            {
                if (!EditorUtility.DisplayDialog(
                        "Different asset",
                        $"That look file was exported for '{file.asset}', but this scene has " +
                        $"'{set.assetName}'.\n\nApply it anyway?",
                        "Apply", "Cancel"))
                    return;
            }

            // This is the one place in the project that writes to material assets, so it
            // asks first. A look file arrives from outside the project, and rewriting an
            // artist's source materials on the strength of it should never be a surprise.
            var names = new List<string>();
            foreach (var e in file.looks) names.Add("  " + e.segment + " " + e.variant);
            string nl = System.Environment.NewLine;
            if (!EditorUtility.DisplayDialog(
                    "Write to material assets?",
                    "This will change " + file.looks.Count + " material asset(s) on disk:"
                    + nl + nl + string.Join(nl, names) + nl + nl
                    + "Undo reverses it. Continue?",
                    "Write materials", "Cancel"))
            {
                Debug.Log("[LookFile] Cancelled. No material was changed.");
                return;
            }

            var applied = new List<string>();
            var skipped = new List<string>();

            foreach (var entry in file.looks)
            {
                var seg = set.FindSegment(entry.segment);
                if (seg == null) { skipped.Add(entry.segment + " (no such segment)"); continue; }

                int index = IndexOfVariant(seg, entry.variant);
                if (index < 0) { skipped.Add(entry.segment + "/" + entry.variant + " (no such variant)"); continue; }

                var mat = seg.VariantMaterial(index);
                if (mat == null) { skipped.Add(entry.segment + "/" + entry.variant + " (no material)"); continue; }

                Undo.RecordObject(mat, "Apply look file");

                // Smoothness and normal are plain material values and go straight on.
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", entry.smoothness);
                if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", entry.smoothness);
                if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", entry.normal);
                EditorUtility.SetDirty(mat);

                // Hue, saturation and brightness cannot live in a material property, because
                // no stock property rotates a hue. They are baked into a new texture beside
                // the original, and the material is pointed at it. The source map is never
                // altered, so the original look is always one reassignment away.
                bool colourChanged = !Mathf.Approximately(entry.hue, 0f)
                                  || !Mathf.Approximately(entry.saturation, 1f)
                                  || !Mathf.Approximately(entry.brightness, 1f);

                if (colourChanged)
                {
                    var baked = BakeAdjustedMap(mat, entry);
                    if (baked != null)
                    {
                        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", baked);
                        if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", baked);
                        EditorUtility.SetDirty(mat);
                        applied.Add(mat.name + "  (+ baked " + baked.name + ")");
                        continue;
                    }
                    skipped.Add(mat.name + " (colour bake failed, surface values still applied)");
                    continue;
                }

                applied.Add(mat.name);
            }

            AssetDatabase.SaveAssets();

            var report = $"[LookFile] Applied {applied.Count} material(s) from {Path.GetFileName(path)}.\n";
            foreach (var a in applied) report += "  written: " + a + "\n";
            foreach (var s in skipped) report += "  SKIPPED: " + s + "\n";
            report += "  Undo puts every material back.\n" +
                      "  Re-run 'Bake Variant Thumbnails' and 'Seed Attributes From Materials' " +
                      "so the previews and figures match the new look.";
            Debug.Log(report);
        }

        /// <summary>
        /// Render the material's base map through the same HSV pass the runtime uses, and
        /// save the result as a real texture asset next to the original.
        ///
        /// This is the step that makes a reviewer's colour call durable. Everything else in
        /// the file is a number a material can hold; a hue rotation is not, so it has to
        /// become pixels.
        /// </summary>
        private static Texture2D BakeAdjustedMap(Material mat, LookEntry entry)
        {
            var source = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : mat.mainTexture;
            if (source == null) return null;

            var shader = Shader.Find("Recast/HSVAdjust");
            if (shader == null)
            {
                Debug.LogError("[LookFile] Shader 'Recast/HSVAdjust' is missing, so the colour " +
                               "cannot be baked.");
                return null;
            }

            var pass = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            pass.SetFloat("_Hue", entry.hue);
            pass.SetFloat("_Saturation", entry.saturation);
            pass.SetFloat("_Brightness", entry.brightness);

            var rt = RenderTexture.GetTemporary(source.width, source.height, 0,
                                                RenderTextureFormat.ARGB32,
                                                RenderTextureReadWrite.sRGB);
            Graphics.Blit(source, rt, pass);

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var readback = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, false);
            readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readback.Apply();
            RenderTexture.active = prev;

            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(pass);

            var sourcePath = AssetDatabase.GetAssetPath(source);
            var dir = string.IsNullOrEmpty(sourcePath) ? "Assets" : Path.GetDirectoryName(sourcePath);
            var stem = string.IsNullOrEmpty(sourcePath)
                ? mat.name + "_adjusted"
                : Path.GetFileNameWithoutExtension(sourcePath) + "_adjusted";
            var outPath = AssetDatabase.GenerateUniqueAssetPath(
                (dir + "/" + stem + ".png").Replace('\\', '/'));

            File.WriteAllBytes(outPath, readback.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(readback);

            AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(outPath) as TextureImporter;
            if (importer != null)
            {
                // Match how the source maps are imported, or the bake will read differently
                // from the texture it replaces.
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.mipmapEnabled = true;
                importer.maxTextureSize = Mathf.Max(source.width, source.height);
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
        }

        private static int IndexOfVariant(GloveVariantSet.Segment seg, string variantId)
        {
            for (int i = 0; i < seg.VariantCount; i++)
                if (seg.VariantId(i) == variantId) return i;
            return -1;
        }
    }
}
