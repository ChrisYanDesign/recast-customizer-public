using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Recast Customizer ▸ Remove Imported Design
    ///
    /// The un-import. Any pipeline that generates assets needs a way to take them back out,
    /// or a rejected experiment stays in the project because nobody can tell what still
    /// references what.
    ///
    /// Because every design now writes into a folder of its own under
    /// <see cref="DesignImporter.NewDesignsRoot"/>, removing one is mostly a matter of
    /// deleting that folder: the materials, the maps and the prefab are all inside it and
    /// belong to nothing else. Anything the design merely referenced lives in the authored
    /// folder and is never touched, which is the same ownership rule the importer follows in
    /// the other direction.
    ///
    /// Designs imported before the folder layout existed are also handled. Those scattered
    /// their assets through <c>GloveAsset</c> and named them with suffixes, so they are found
    /// and removed by name instead.
    /// </summary>
    public static class DesignRemover
    {
        /// <summary>
        /// Recast Customizer ▸ Clear Saved Designs
        ///
        /// Empties the designs kept in browser storage, and un-hides anything dismissed with
        /// an X. Those live in PlayerPrefs rather than in the project, so they survive play
        /// sessions and domain reloads and cannot be cleaned up by deleting a file. After a
        /// stretch of testing the list fills with designs nobody meant to keep, and this is
        /// the way to get back to just the artist's three.
        ///
        /// It touches no project asset. Designs already imported into the project are removed
        /// with Remove Imported Design instead.
        /// </summary>
        [MenuItem("Recast Customizer/Clear Saved Designs")]
        public static void ClearSaved()
        {
            int count = DesignLibrary.Count;
            if (count == 0)
            {
                EditorUtility.DisplayDialog("Nothing saved",
                    "There are no designs in browser storage. The list starts with the "
                    + "artist's designs only.", "OK");
                return;
            }

            var names = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var d = DesignLibrary.At(i);
                names.Add("  " + (d == null || string.IsNullOrWhiteSpace(d.name) ? "unnamed" : d.name));
            }

            if (!EditorUtility.DisplayDialog($"Clear {count} saved design(s)?",
                    string.Join("\n", names.ToArray())
                  + "\n\nThese are kept in browser storage, not in the project. Nothing on "
                  + "disk is touched.\n\nExport any you want to keep first.",
                    "Clear them", "Cancel"))
                return;

            DesignLibrary.Clear();
            HiddenDesigns.ShowAll();
            Debug.Log($"[Design] Cleared {count} saved design(s). The list is back to the "
                    + "artist's designs. Restart play mode to see it.");
        }

        [MenuItem("Recast Customizer/Remove Imported Design")]
        public static void Remove()
        {
            var assembly = UnityEngine.Object.FindFirstObjectByType<ModularGloveAssembly>();
            if (assembly == null || assembly.VariantSet == null)
            {
                Debug.LogError("[Design] No ModularGloveAssembly with a variant set in the open "
                             + "scene. Open glove_studio and try again.");
                return;
            }

            var set = assembly.VariantSet;
            var imported = FindImportedIds(set);

            if (imported.Count == 0)
            {
                EditorUtility.DisplayDialog("Nothing to remove",
                    "This variant set contains only the authored variants. No imported design "
                    + "was found.", "OK");
                return;
            }

            // With one design there is nothing to choose, so do not make them choose. With
            // several, take the most recent, which is the one just imported.
            string id = imported[imported.Count - 1];
            if (imported.Count > 1)
            {
                if (!EditorUtility.DisplayDialog("Which design?",
                        $"This set contains {imported.Count} imported designs:\n\n  "
                      + string.Join(", ", imported) + "\n\n"
                      + $"Remove the most recent, '{id}'?\n\nRun this again to remove another.",
                        "Remove " + id, "Cancel"))
                    return;
            }

            RemoveDesign(set, id);
        }

        /// <summary>
        /// Which variant ids were generated rather than authored.
        ///
        /// A design is recognised three ways, because the answer has to hold for designs made
        /// before the current layout as well as after it: its material sits under the
        /// generated root, or it has a folder of its own there, or its id is not a number at
        /// all, which is how the first version named them.
        /// </summary>
        private static List<string> FindImportedIds(GloveVariantSet set)
        {
            var found = new List<string>();

            foreach (var seg in set.segments)
                for (int i = 0; i < seg.VariantCount; i++)
                {
                    var id = seg.VariantId(i);
                    if (found.Contains(id)) continue;

                    bool generated = !IsNumeric(id);

                    if (!generated)
                    {
                        var m = seg.VariantMaterial(i);
                        var p = m != null ? AssetDatabase.GetAssetPath(m) : null;
                        if (!string.IsNullOrEmpty(p) && p.StartsWith(DesignImporter.NewDesignsRoot))
                            generated = true;
                    }

                    if (!generated && AssetDatabase.IsValidFolder(FolderFor(id)))
                        generated = true;

                    if (generated) found.Add(id);
                }

            return found;
        }

        private static void RemoveDesign(GloveVariantSet set, string id)
        {
            var folder = FolderFor(id);
            bool hasFolder = AssetDatabase.IsValidFolder(folder);

            // ------------------------------------------------------------------ plan first
            var loose = new List<string>();       // assets outside the design's own folder
            var entries = new List<string>();

            foreach (var seg in set.segments)
            {
                int index = IndexOfVariant(seg, id);
                if (index < 0) continue;
                entries.Add("  " + seg.DisplayLabel + "  variant '" + id + "'");

                var material = seg.VariantMaterial(index);
                if (material == null) continue;

                var mp = AssetDatabase.GetAssetPath(material);
                if (string.IsNullOrEmpty(mp)) continue;
                if (mp.StartsWith(folder)) continue;                 // inside, goes with the folder

                // A material this design owns but which was written before the folder layout.
                // Ownership is still by name: anything else is the artist's and must survive.
                if (!material.name.EndsWith("_" + id)) continue;
                if (!loose.Contains(mp)) loose.Add(mp);

                var baseMap = material.HasProperty("_BaseMap")
                    ? material.GetTexture("_BaseMap") : material.mainTexture;
                if (baseMap == null || !baseMap.name.EndsWith("_adjusted")) continue;
                if (UsedElsewhere(set, baseMap, id)) continue;

                var tp = AssetDatabase.GetAssetPath(baseMap);
                if (!string.IsNullOrEmpty(tp) && !loose.Contains(tp)) loose.Add(tp);
            }

            var loosePrefab = FindLoosePrefab(set, id, folder);
            if (!string.IsNullOrEmpty(loosePrefab)) loose.Add(loosePrefab);

            var instances = FindSceneInstances(set, id);

            var summary = new StringBuilder();
            summary.AppendLine("Variant entries to remove:");
            foreach (var e in entries) summary.AppendLine(e);
            summary.AppendLine();
            if (hasFolder) summary.AppendLine("Folder to delete:\n  " + folder);
            if (loose.Count > 0)
            {
                summary.AppendLine("Assets to delete:");
                foreach (var l in loose) summary.AppendLine("  " + l);
            }
            if (instances.Count > 0)
                summary.AppendLine("Scene objects to delete: " + instances.Count);
            summary.AppendLine();
            summary.AppendLine("Nothing in GloveAsset is touched.");

            if (!EditorUtility.DisplayDialog($"Remove imported design '{id}'?",
                    summary.ToString(), "Remove it", "Cancel"))
            {
                Debug.Log("[Design] Cancelled. Nothing was removed.");
                return;
            }

            // ------------------------------------------------------------------ execute
            //
            // Scene objects, then the variant entries, then the assets, so nothing is ever
            // left pointing at something that has already gone.
            foreach (var go in instances) Undo.DestroyObjectImmediate(go);

            Undo.RecordObject(set, "Remove imported design");
            foreach (var seg in set.segments)
            {
                int index = IndexOfVariant(seg, id);
                if (index < 0) continue;
                RemoveAt(seg.variants, index);
                RemoveAt(seg.variantIds, index);
                RemoveAt(seg.variantLabels, index);
                RemoveAt(seg.variantThumbnails, index);
                RemoveAt(seg.variantStats, index);
            }
            EditorUtility.SetDirty(set);

            var deleted = new List<string>();
            foreach (var p in loose) if (AssetDatabase.DeleteAsset(p)) deleted.Add(p);
            if (hasFolder && AssetDatabase.DeleteAsset(folder)) deleted.Add(folder);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = set;

            var report = new StringBuilder();
            report.AppendLine($"[Design] Removed imported design '{id}'.");
            foreach (var d in deleted) report.AppendLine("  deleted: " + d);
            report.AppendLine($"  The set now expresses {set.CombinationCount} combinations.");
            report.AppendLine("  Save the scene to keep the removal.");
            Debug.Log(report.ToString());
        }

        // ---------------------------------------------------------------- helpers

        private static string FolderFor(string id) =>
            DesignImporter.NewDesignsRoot + "/design_" + id;

        private static bool IsNumeric(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            foreach (var ch in id) if (!char.IsDigit(ch)) return false;
            return true;
        }

        private static int IndexOfVariant(GloveVariantSet.Segment seg, string variantId)
        {
            for (int i = 0; i < seg.VariantCount; i++)
                if (seg.VariantId(i) == variantId) return i;
            return -1;
        }

        private static void RemoveAt<T>(List<T> list, int index)
        {
            if (list != null && index >= 0 && index < list.Count) list.RemoveAt(index);
        }

        /// <summary>
        /// Whether a texture is still referenced by a material outside the design being
        /// removed, so a shared map is never deleted out from under a second design.
        /// </summary>
        private static bool UsedElsewhere(GloveVariantSet set, Texture texture, string id)
        {
            foreach (var seg in set.segments)
                for (int i = 0; i < seg.VariantCount; i++)
                {
                    if (seg.VariantId(i) == id) continue;
                    var m = seg.VariantMaterial(i);
                    if (m == null) continue;
                    var map = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : m.mainTexture;
                    if (map == texture) return true;
                }
            return false;
        }

        /// <summary>
        /// A design prefab left outside the design's folder, which is where the first version
        /// of the importer put them.
        /// </summary>
        private static string FindLoosePrefab(GloveVariantSet set, string id, string folder)
        {
            var names = new[]
            {
                set.assetName + "_" + id + "_p",     // current convention
                set.assetName + "_" + id,            // pre-folder convention
                set.assetName + "_design" + id       // first version, slug named
            };

            foreach (var name in names)
                foreach (var guid in AssetDatabase.FindAssets(name + " t:Prefab"))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.StartsWith(folder)) continue;
                    if (System.IO.Path.GetFileNameWithoutExtension(path) == name) return path;
                }
            return null;
        }

        /// <summary>
        /// Instances of the design left in the open scene. Deleting the prefab under one of
        /// these leaves a broken object that reads as a bug rather than as test leftover.
        /// </summary>
        private static List<GameObject> FindSceneInstances(GloveVariantSet set, string id)
        {
            var found = new List<GameObject>();
            var names = new[]
            {
                set.assetName + "_" + id + "_p",
                set.assetName + "_" + id,
                set.assetName + "_design" + id
            };

            foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            {
                if (go == null || go.transform.parent != null) continue;
                foreach (var n in names)
                    if (go.name == n) { found.Add(go); break; }
            }
            return found;
        }
    }
}
