using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Recast Customizer ▸ Import Design From File
    ///
    /// The handoff leg. A content designer builds a design in the web build out of the
    /// artist's templates, names it, and exports one JSON file. This reads that file and
    /// turns it into real assets: the materials the design needs, the maps those materials
    /// need, a prefab of the finished glove, and an entry in the variant set so the design
    /// appears alongside the authored ones in both the tool and the engine.
    ///
    /// Three rules shape all of it.
    ///
    /// **The authored folder is read-only.** Nothing is ever written into
    /// <c>GloveAsset</c>. That folder is the artist's, it is the default the whole tool is
    /// measured against, and a generated design must never be able to touch it. Everything
    /// this writes goes under <see cref="NewDesignsRoot"/>, in a folder of its own per design,
    /// so a design can be inspected, zipped or deleted as one unit.
    ///
    /// **Nothing is written that can be referenced instead.** A part the designer only
    /// swapped already has a finished material on disk, so the new design points straight at
    /// it and no asset is created. Only a part that was actually tuned costs anything. One
    /// design might therefore produce a single material rather than four.
    ///
    /// **Names carry the design number, not a suffix.** A design is numbered, and that number
    /// replaces the variant number in every name it produces:
    ///
    ///   authored   falcon_glove_bottom_02_diff.png     falcon_glove_bottom_02_mat.mat
    ///   design 06  falcon_glove_bottom_06_diff.png     falcon_glove_bottom_06_mat.mat
    ///
    /// The earlier scheme appended to the source name instead, producing
    /// <c>..._02_diff_adjusted</c> and <c>..._02_mat_design6</c>. That does not survive a
    /// second design: the suffixes stack, the variant number stops meaning anything, and two
    /// designs built on the same part end up with names that sort next to each other and read
    /// as versions of one thing. Substituting the number keeps every name the same shape and
    /// the same length as the ones the artist wrote.
    /// </summary>
    public static class DesignImporter
    {
        /// <summary>Everything generated lives under here, never in the authored folder.</summary>
        public const string NewDesignsRoot = "Assets/RecastCustomizer/NewDesigns";

        [MenuItem("Recast Customizer/Import Design From File")]
        public static void Import()
        {
            var path = EditorUtility.OpenFilePanel(
                "Choose a design file exported from the customizer", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            ImportFile(path);
        }

        /// <summary>The menu item without the modal picker, so it can be scripted or tested.</summary>
        public static void ImportFile(string path)
        {
            var assembly = UnityEngine.Object.FindFirstObjectByType<ModularGloveAssembly>();
            if (assembly == null || assembly.VariantSet == null)
            {
                Debug.LogError("[Design] No ModularGloveAssembly with a variant set in the open "
                             + "scene. Open glove_studio and try again.");
                return;
            }

            SavedDesign design;
            try
            {
                design = JsonUtility.FromJson<SavedDesign>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogError("[Design] Could not read that file: " + e.Message);
                return;
            }

            if (design == null || design.parts == null || design.parts.Count == 0)
            {
                Debug.LogError("[Design] That file describes no parts, so there is no design "
                             + "in it to import.");
                return;
            }

            var set = assembly.VariantSet;

            if (!string.IsNullOrEmpty(design.asset) && design.asset != set.assetName)
            {
                if (!EditorUtility.DisplayDialog("Different asset",
                        $"That design was exported for '{design.asset}', but this scene has "
                      + $"'{set.assetName}'.\n\nImport it anyway?", "Import", "Cancel"))
                    return;
            }

            string name = string.IsNullOrWhiteSpace(design.name)
                ? "Imported " + DateTime.Now.ToString("HH:mm")
                : design.name.Trim();

            int number = DesignNumber(name, set);
            string nn = number.ToString("00");
            string folder = NewDesignsRoot + "/design_" + nn;

            if (VariantIdExists(set, nn))
            {
                Debug.LogError($"[Design] This set already has a variant numbered '{nn}'. "
                             + "Remove that design first, or rename this one so it takes a "
                             + "different number.");
                return;
            }

            // ------------------------------------------------------------------ plan first
            //
            // Work out everything that will be created before creating any of it, so the
            // confirmation can name it. "This will write 6 assets" is a decision someone can
            // make; "importing..." is not.
            var plan = new List<PartPlan>();
            foreach (var seg in set.segments)
            {
                var part = design.Part(seg.id);
                int sourceIndex = part != null ? IndexOfVariant(seg, part.variant) : 0;
                if (sourceIndex < 0)
                    sourceIndex = Mathf.Clamp(part.variantIndex, 0, Mathf.Max(0, seg.VariantCount - 1));

                plan.Add(new PartPlan
                {
                    segment = seg,
                    part = part,
                    sourceIndex = sourceIndex,
                    sourceMaterial = seg.VariantMaterial(sourceIndex),
                    needsMaterial = part != null && part.tuned,
                    needsBake = part != null && part.tuned && part.NeedsBake
                });
            }

            int newMaterials = 0, newMaps = 0;
            var lines = new StringBuilder();
            foreach (var p in plan)
            {
                if (p.needsMaterial) newMaterials++;
                if (p.needsBake) newMaps++;

                lines.AppendLine("  " + p.segment.DisplayLabel.PadRight(8)
                    + " variant " + p.segment.VariantId(p.sourceIndex)
                    + (p.needsMaterial
                        ? (p.needsBake ? "  ->  new material + map" : "  ->  new material")
                        : "  ->  reuses the authored material"));
            }

            if (!EditorUtility.DisplayDialog($"Import '{name}' as design {nn}?",
                    $"{plan.Count} part(s):\n\n{lines}\n"
                  + $"Writes {newMaterials} material(s), {newMaps} texture(s) and 1 prefab into\n"
                  + $"{folder}\n\n"
                  + "Named on the design number, so falcon_glove_bottom_02_mat becomes\n"
                  + $"falcon_glove_bottom_{nn}_mat.\n\n"
                  + "Nothing in GloveAsset is touched.",
                    "Import design", "Cancel"))
            {
                Debug.Log("[Design] Cancelled. Nothing was written.");
                return;
            }

            // ------------------------------------------------------------------ execute
            EnsureFolder(folder);

            var written = new List<string>();
            var materialsForPrefab = new Dictionary<string, Material>();

            foreach (var p in plan)
            {
                Material material = p.sourceMaterial;

                if (p.needsMaterial && p.sourceMaterial != null)
                {
                    material = CreateTunedMaterial(p, set, nn, folder, written);
                    if (material == null) material = p.sourceMaterial;   // bake failed; reuse
                }

                materialsForPrefab[p.segment.id] = material;

                // Append the design as a genuine extra variant on every segment. Because a
                // design in this data model is "the same index on every part", this makes the
                // imported design selectable exactly like the authored ones, with no special
                // case anywhere.
                p.segment.variants.Add(material);
                p.segment.variantIds.Add(nn);
                p.segment.variantLabels.Add(name);
                p.segment.variantThumbnails.Add(null);
                p.segment.variantStats.Add(CopyStats(p.segment, p.sourceIndex));
            }

            EditorUtility.SetDirty(set);

            string prefabPath = CreatePrefab(assembly, set, materialsForPrefab, nn, folder);
            if (!string.IsNullOrEmpty(prefabPath)) written.Add(prefabPath);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var report = new StringBuilder();
            report.AppendLine($"[Design] Imported '{name}' as design {nn}.");
            foreach (var w in written) report.AppendLine("  created: " + w);
            report.AppendLine($"  Everything above is under {folder}. GloveAsset was not touched.");
            report.AppendLine($"  The set now expresses {set.CombinationCount} combinations.");
            report.AppendLine("  Run 'Bake Variant Thumbnails' so the new design gets a preview.");
            Debug.Log(report.ToString());

            Selection.activeObject = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(folder);
        }

        private class PartPlan
        {
            public GloveVariantSet.Segment segment;
            public SavedDesignPart part;
            public int sourceIndex;
            public Material sourceMaterial;
            public bool needsMaterial;
            public bool needsBake;
        }

        // ---------------------------------------------------------------- generation

        /// <summary>
        /// A new material carrying this part's tuned values, written into the design's own
        /// folder and named on the design number.
        ///
        /// Smoothness and normal strength go straight onto the material, because a Lit shader
        /// can hold them. Hue, saturation and brightness cannot be expressed that way at all,
        /// so the base map is re-rendered through the same HSV pass the browser used and saved
        /// as a real texture. Same shader, same maths, so what the designer approved is what
        /// the engine gets rather than an approximation of it.
        /// </summary>
        private static Material CreateTunedMaterial(PartPlan p, GloveVariantSet set,
                                                    string nn, string folder,
                                                    List<string> written)
        {
            var source = p.sourceMaterial;
            var material = new Material(source) { name = AssetStem(set, p.segment, nn) + "_mat" };

            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", p.part.smoothness);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", p.part.smoothness);
            if (material.HasProperty("_BumpScale")) material.SetFloat("_BumpScale", p.part.normal);

            if (p.needsBake)
            {
                var mapPath = folder + "/" + AssetStem(set, p.segment, nn) + "_diff.png";
                var baked = LookFileImporter.BakeAdjustedMap(source, p.part, mapPath);
                if (baked == null)
                {
                    Debug.LogWarning($"[Design] Colour bake failed for {p.segment.DisplayLabel}. "
                                   + "Its surface values were still applied.");
                }
                else
                {
                    if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", baked);
                    if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", baked);
                    written.Add(AssetDatabase.GetAssetPath(baked));
                }
            }

            var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + material.name + ".mat");
            AssetDatabase.CreateAsset(material, path);
            written.Add(path);
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        /// <summary>
        /// The design as a prefab an engineer can drop into a scene, in the design's own
        /// folder beside the materials it uses.
        ///
        /// Built by copying the assembly in the open scene, so it inherits the hierarchy
        /// rather than reconstructing it and getting something subtly different. The tool's
        /// own components are stripped first: <see cref="ModularGloveAssembly"/> re-applies
        /// materials from its own variant selection in both OnEnable and OnValidate, and left
        /// in place it overwrites every material assigned here the moment the prefab is
        /// dragged into a scene. A handoff prefab is a finished asset, not an instance of the
        /// tool.
        /// </summary>
        private static string CreatePrefab(ModularGloveAssembly assembly, GloveVariantSet set,
                                           Dictionary<string, Material> materials,
                                           string nn, string folder)
        {
            var temp = UnityEngine.Object.Instantiate(assembly.gameObject);
            temp.name = set.assetName + "_" + nn + "_p";

            try
            {
                StripToolComponents(temp);

                foreach (var seg in set.segments)
                {
                    Material material;
                    if (!materials.TryGetValue(seg.id, out material) || material == null) continue;

                    var target = FindChild(temp.transform, seg.childRenderer);
                    var renderer = target != null ? target.GetComponent<Renderer>() : null;
                    if (renderer == null)
                    {
                        Debug.LogWarning($"[Design] No renderer named '{seg.childRenderer}' in the "
                                       + "assembly, so that part kept the material it had.");
                        continue;
                    }
                    renderer.sharedMaterial = material;
                }

                var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + temp.name + ".prefab");
                PrefabUtility.SaveAsPrefabAsset(temp, path);
                return path;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Design] Could not save a prefab for design {nn}: {e.Message}");
                return null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temp);
            }
        }

        /// <summary>
        /// Remove the customizer's runtime components from a generated prefab.
        ///
        /// Ordered deliberately: MaterialTuner declares a RequireComponent on the assembly, so
        /// removing the assembly first is refused. SecondaryMotion stays, because the tassel
        /// sway belongs to the asset rather than the tool and never touches a material.
        /// </summary>
        private static void StripToolComponents(GameObject root)
        {
            foreach (var c in root.GetComponentsInChildren<MaterialTuner>(true))
                if (c != null) UnityEngine.Object.DestroyImmediate(c);

            foreach (var c in root.GetComponentsInChildren<ViewModeController>(true))
                if (c != null) UnityEngine.Object.DestroyImmediate(c);

            foreach (var c in root.GetComponentsInChildren<ModularGloveAssembly>(true))
                if (c != null) UnityEngine.Object.DestroyImmediate(c);
        }

        // ---------------------------------------------------------------- naming

        /// <summary>
        /// The shared stem for every asset a design writes for one part:
        /// <c>falcon_glove_bottom_06</c>. Callers append <c>_mat</c> or <c>_diff</c>, which is
        /// exactly how the authored assets are named.
        /// </summary>
        private static string AssetStem(GloveVariantSet set, GloveVariantSet.Segment seg, string nn)
            => set.assetName + "_" + seg.id + "_" + nn;

        /// <summary>
        /// The number a design takes.
        ///
        /// Read from the design's own name where it has one, so "DESIGN 6" becomes 06 and the
        /// files match what the person who made it called it. A design named something real
        /// like "Sun Bleached" gets the next free number instead. Either way the result is
        /// checked against the set, so two designs can never claim the same number.
        /// </summary>
        private static int DesignNumber(string name, GloveVariantSet set)
        {
            int parsed = TrailingNumber(name);
            int next = HighestNumberedVariant(set) + 1;
            int number = parsed > 0 ? parsed : next;

            while (VariantIdExists(set, number.ToString("00")) || number < 1) number++;
            return number;
        }

        private static int TrailingNumber(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            int end = name.Length;
            while (end > 0 && !char.IsDigit(name[end - 1])) end--;
            int start = end;
            while (start > 0 && char.IsDigit(name[start - 1])) start--;
            if (start == end) return 0;

            int value;
            return int.TryParse(name.Substring(start, end - start), out value) ? value : 0;
        }

        private static int HighestNumberedVariant(GloveVariantSet set)
        {
            int highest = 0;
            foreach (var seg in set.segments)
                for (int i = 0; i < seg.VariantCount; i++)
                {
                    int v;
                    if (int.TryParse(seg.VariantId(i), out v)) highest = Mathf.Max(highest, v);
                }
            return highest;
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Create a folder and every parent it needs, then return its path.</summary>
        private static string EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return path;

            var parts = path.Split('/');
            var current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
            return current;
        }

        private static Transform FindChild(Transform root, string childName)
        {
            if (string.IsNullOrEmpty(childName)) return null;
            if (root.name == childName) return root;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == childName) return t;
            return null;
        }

        private static int IndexOfVariant(GloveVariantSet.Segment seg, string variantId)
        {
            for (int i = 0; i < seg.VariantCount; i++)
                if (seg.VariantId(i) == variantId) return i;
            return -1;
        }

        private static bool VariantIdExists(GloveVariantSet set, string id)
        {
            foreach (var seg in set.segments)
                for (int i = 0; i < seg.VariantCount; i++)
                    if (string.Equals(seg.VariantId(i), id, StringComparison.OrdinalIgnoreCase))
                        return true;
            return false;
        }

        /// <summary>
        /// Carry the source variant's attribute values across.
        ///
        /// These are design values rather than measurements, so an imported design inherits
        /// them from whichever variant it was built on and can be hand-tuned afterwards.
        /// Leaving them empty would read as zero and quietly flatten the attribute display.
        /// </summary>
        private static GloveVariantSet.VariantStats CopyStats(GloveVariantSet.Segment seg, int index)
        {
            var copy = new GloveVariantSet.VariantStats();
            if (seg.variantStats != null && index >= 0 && index < seg.variantStats.Count)
            {
                var src = seg.variantStats[index];
                if (src != null && src.values != null) copy.values = new List<float>(src.values);
            }
            return copy;
        }
    }
}
