using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Recast Customizer ▸ Check Imported Designs
    ///
    /// Re-runs the budget checks on designs already in the project.
    ///
    /// Checking at import is necessary but not sufficient. A design is checked once, and
    /// then the limits change, or a texture's import settings are edited, or someone
    /// swaps a map by hand -- and the report sitting in the folder still describes the day
    /// it arrived rather than the state it is in. This re-measures and rewrites it.
    ///
    /// A design's materials are read from the variant set rather than from its folder,
    /// because that is what the design actually is: one variant index across every
    /// segment. A design that only swapped a part has no material of its own for that
    /// part, and reading the folder would miss it entirely.
    /// </summary>
    public static class DesignChecker
    {
        [MenuItem("Recast Customizer/Check Imported Designs")]
        public static void CheckAll()
        {
            var assembly = Object.FindFirstObjectByType<ModularGloveAssembly>();
            if (assembly == null || assembly.VariantSet == null)
            {
                EditorUtility.DisplayDialog("Nothing to check against",
                    "No ModularGloveAssembly with a variant set in the open scene. "
                  + "Open glove_studio and try again.", "OK");
                return;
            }

            var set = assembly.VariantSet;
            var spec = RuntimeValidator.Spec.Load() ?? new RuntimeValidator.Spec();
            var results = new List<DesignImportWindow.Result>();

            // Only the variants that have a folder of their own. The authored three are
            // checked by the offline pipeline against the whole kit, which is a different
            // and stricter job than checking one design.
            foreach (var id in VariantIds(set))
            {
                string folder = DesignImporter.NewDesignsRoot + "/design_" + id;
                if (!AssetDatabase.IsValidFolder(folder)) continue;

                var materials = new List<Material>();
                foreach (var segment in set.segments)
                {
                    int index = segment.variantIds.IndexOf(id);
                    if (index >= 0) materials.Add(segment.VariantMaterial(index));
                }

                string label = LabelFor(set, id) + " (design " + id + ")";
                var rows = DesignValidator.Check(label, folder, materials, spec);
                DesignValidator.WriteReport(folder, label, rows);

                results.Add(new DesignImportWindow.Result
                {
                    design = label,
                    folder = folder,
                    rows = rows,
                });
            }

            if (results.Count == 0)
            {
                EditorUtility.DisplayDialog("No imported designs",
                    "Nothing under " + DesignImporter.NewDesignsRoot + " to check. Import a "
                  + "design first.", "OK");
                return;
            }

            AssetDatabase.Refresh();
            DesignImportWindow.ShowResults(results);
        }

        private static IEnumerable<string> VariantIds(GloveVariantSet set)
        {
            var ids = new List<string>();
            foreach (var segment in set.segments)
                foreach (var id in segment.variantIds)
                    if (!ids.Contains(id)) ids.Add(id);
            ids.Sort(System.StringComparer.Ordinal);
            return ids;
        }

        /// <summary>The name the designer gave it, rather than its number.</summary>
        private static string LabelFor(GloveVariantSet set, string id)
        {
            foreach (var segment in set.segments)
            {
                int index = segment.variantIds.IndexOf(id);
                if (index >= 0 && index < segment.variantLabels.Count &&
                    !string.IsNullOrWhiteSpace(segment.variantLabels[index]))
                    return segment.variantLabels[index];
            }
            return "design " + id;
        }
    }
}
