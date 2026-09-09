using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// One-click setup for the modular glove:
    ///
    ///   Recast Customizer ▸ Rebuild Variant Set From Folder
    ///       Scans GloveAsset/ for materials named falcon_glove_{segment}_{variant}_mat
    ///       and (re)builds falcon_glove_variant_set.asset.
    ///
    ///   Recast Customizer ▸ Build Glove Assembly Prefab
    ///       Instantiates falcon_glove_01_p, attaches ModularGloveAssembly +
    ///       GloveCustomizerOverlay, links the variant set, and saves
    ///       Prefabs/falcon_glove_assembly.prefab.
    /// </summary>
    public static class GloveAssemblyBuilder
    {
        private const string Root = "Assets/RecastCustomizer";
        private const string GloveAssetFolder = Root + "/GloveAsset";
        private const string PrefabFolder = Root + "/Prefabs";
        private const string VariantSetPath = Root + "/falcon_glove_variant_set.asset";
        private const string BasePrefabName = "falcon_glove_01_p";

        // segment id -> child GameObject name inside the source prefab
        private static readonly (string id, string label, string child)[] Segments =
        {
            ("top", "Top", "top"),
            ("bottom", "Bottom", "bottom"),
            ("thumb", "Thumb", "thumb"),
            ("tassel", "Tassel", "tassel"),
        };

        [MenuItem("Recast Customizer/Rebuild Variant Set From Folder")]
        public static void RebuildVariantSet()
        {
            var set = AssetDatabase.LoadAssetAtPath<GloveVariantSet>(VariantSetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<GloveVariantSet>();
                AssetDatabase.CreateAsset(set, VariantSetPath);
            }

            set.assetName = "falcon_glove";
            var priorSegments = set.segments ?? new List<GloveVariantSet.Segment>();
            set.segments = new List<GloveVariantSet.Segment>();

            var matGuids = AssetDatabase.FindAssets("t:Material falcon_glove", new[] { GloveAssetFolder });
            var allMats = matGuids
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<Material>)
                .Where(m => m != null)
                .ToList();

            foreach (var (id, label, child) in Segments)
            {
                var seg = new GloveVariantSet.Segment { id = id, label = label, childRenderer = child };

                var matched = allMats
                    .Where(m => m.name.StartsWith($"falcon_glove_{id}_") && m.name.EndsWith("_mat"))
                    .OrderBy(m => m.name)
                    .ToList();

                // keep any display names the user already typed, keyed by variant id
                var existingLabels = new Dictionary<string, string>();
                var previous = priorSegments.Find(s => s.id == id);
                if (previous != null)
                    for (int i = 0; i < previous.variantIds.Count; i++)
                        existingLabels[previous.variantIds[i]] = previous.VariantLabel(i);

                foreach (var mat in matched)
                {
                    // falcon_glove_top_02_mat -> "02"
                    var token = mat.name.Replace($"falcon_glove_{id}_", "").Replace("_mat", "");
                    seg.variants.Add(mat);
                    seg.variantIds.Add(token);
                    seg.variantLabels.Add(existingLabels.TryGetValue(token, out var savedLabel) ? savedLabel : "");
                }

                if (matched.Count == 0)
                    Debug.LogWarning($"[GloveAssemblyBuilder] No materials matched segment '{id}'.");

                set.segments.Add(seg);
            }

            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[GloveAssemblyBuilder] Variant set rebuilt: {set.segments.Count} segments, " +
                      $"{set.CombinationCount} combinations.");
            Selection.activeObject = set;
        }

        [MenuItem("Recast Customizer/Build Glove Assembly Prefab")]
        public static void BuildAssemblyPrefab()
        {
            var set = AssetDatabase.LoadAssetAtPath<GloveVariantSet>(VariantSetPath);
            if (set == null)
            {
                RebuildVariantSet();
                set = AssetDatabase.LoadAssetAtPath<GloveVariantSet>(VariantSetPath);
            }

            var baseGuid = AssetDatabase.FindAssets($"{BasePrefabName} t:Prefab", new[] { GloveAssetFolder }).FirstOrDefault();
            if (string.IsNullOrEmpty(baseGuid))
            {
                Debug.LogError($"[GloveAssemblyBuilder] Could not find {BasePrefabName}.prefab in {GloveAssetFolder}. " +
                               "Import the GloveAsset folder first.");
                return;
            }

            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(baseGuid));
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            instance.name = "falcon_glove_assembly";

            var assembly = instance.GetComponent<ModularGloveAssembly>() ?? instance.AddComponent<ModularGloveAssembly>();
            var so = new SerializedObject(assembly);
            so.FindProperty("variantSet").objectReferenceValue = set;
            so.ApplyModifiedPropertiesWithoutUndo();

            if (instance.GetComponent<GloveCustomizerOverlay>() == null)
                instance.AddComponent<GloveCustomizerOverlay>();

            Directory.CreateDirectory(PrefabFolder);
            var outPath = $"{PrefabFolder}/falcon_glove_assembly.prefab";
            var saved = PrefabUtility.SaveAsPrefabAsset(instance, outPath);
            Object.DestroyImmediate(instance);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[GloveAssemblyBuilder] Saved {outPath}");
            Selection.activeObject = saved;
            EditorGUIUtility.PingObject(saved);
        }
    }
}
