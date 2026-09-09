using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Recast Customizer ▸ Build Customizer UI
    ///
    /// Wires the whole interactive scene in one click:
    ///   - drops falcon_glove_assembly into the open scene if it is not already there
    ///   - puts an OrbitCamera on the Main Camera and frames the glove
    ///   - creates the PanelSettings (and a default runtime theme, if the project has none)
    ///   - creates a "Customizer UI" object with UIDocument + GloveCustomizerUI
    ///
    /// Everything it makes is a normal scene object, so it can be tweaked or deleted by hand.
    /// </summary>
    public static class CustomizerUIBuilder
    {
        private const string Root = "Assets/RecastCustomizer";
        private const string SettingsFolder = "Assets/Settings";
        private const string PanelSettingsPath = SettingsFolder + "/Recast_PanelSettings.asset";
        private const string ThemeFolder = "Assets/UI Toolkit";
        private const string ThemePath = ThemeFolder + "/UnityDefaultRuntimeTheme.tss";
        private const string AssemblyPrefabPath = Root + "/Prefabs/falcon_glove_assembly.prefab";

        [MenuItem("Recast Customizer/Build Customizer UI")]
        public static void BuildCustomizerUI()
        {
            var assembly = EnsureAssemblyInScene();
            if (assembly == null) return;

            SetupCamera(assembly);

            var panelSettings = EnsurePanelSettings();
            if (panelSettings == null) return;

            var uiGo = GameObject.Find("Customizer UI") ?? new GameObject("Customizer UI");
            Undo.RegisterCreatedObjectUndo(uiGo, "Create customizer UI");

            var doc = uiGo.GetComponent<UIDocument>() ?? uiGo.AddComponent<UIDocument>();
            doc.panelSettings = panelSettings;

            var ui = uiGo.GetComponent<GloveCustomizerUI>() ?? uiGo.AddComponent<GloveCustomizerUI>();
            var so = new SerializedObject(ui);
            so.FindProperty("assembly").objectReferenceValue = assembly;
            so.ApplyModifiedPropertiesWithoutUndo();

            // the IMGUI overlay would draw on top of the new panel
            var legacy = assembly.GetComponent<GloveCustomizerOverlay>();
            if (legacy != null)
            {
                Object.DestroyImmediate(legacy);
                Debug.Log("[CustomizerUIBuilder] Removed the old IMGUI overlay — the UI Toolkit panel replaces it.");
            }

            EditorUtility.SetDirty(uiGo);
            Selection.activeObject = uiGo;
            Debug.Log("[CustomizerUIBuilder] Customizer UI ready. Press Play.\n" +
                      "  Left-drag orbits, scroll zooms, middle-drag pans.");
        }

        private static ModularGloveAssembly EnsureAssemblyInScene()
        {
            var existing = Object.FindFirstObjectByType<ModularGloveAssembly>();
            if (existing != null) return existing;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssemblyPrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[CustomizerUIBuilder] falcon_glove_assembly.prefab not found. " +
                               "Run Recast Customizer ▸ Build Glove Assembly Prefab first.");
                return null;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(instance, "Add glove assembly");
            Debug.Log("[CustomizerUIBuilder] Added falcon_glove_assembly to the scene.");
            return instance.GetComponent<ModularGloveAssembly>();
        }

        private static void SetupCamera(ModularGloveAssembly assembly)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[CustomizerUIBuilder] No camera tagged MainCamera — skipping orbit setup.");
                return;
            }

            var orbit = cam.GetComponent<OrbitCamera>() ?? cam.gameObject.AddComponent<OrbitCamera>();
            var so = new SerializedObject(orbit);
            so.FindProperty("target").objectReferenceValue = assembly.transform;
            so.ApplyModifiedPropertiesWithoutUndo();
            orbit.FrameTarget();
            EditorUtility.SetDirty(orbit);
        }

        private static PanelSettings EnsurePanelSettings()
        {
            var existing = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (existing != null && existing.themeStyleSheet != null) return existing;

            Directory.CreateDirectory(SettingsFolder);
            var settings = existing;
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(settings, PanelSettingsPath);
            }

            settings.themeStyleSheet = EnsureTheme();
            if (settings.themeStyleSheet == null)
            {
                Debug.LogError(
                    "[CustomizerUIBuilder] Could not find or create a runtime theme.\n" +
                    "  Fix: Assets ▸ Create ▸ UI Toolkit ▸ Panel Settings Asset (Unity makes the theme for you), " +
                    "then run this menu item again.");
                return null;
            }

            // scale the UI with the window rather than snapping to pixel size
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1920, 1080);

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return settings;
        }

        private static ThemeStyleSheet EnsureTheme()
        {
            // reuse any theme already in the project
            var guid = AssetDatabase.FindAssets("t:ThemeStyleSheet").FirstOrDefault();
            if (!string.IsNullOrEmpty(guid))
            {
                var found = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(AssetDatabase.GUIDToAssetPath(guid));
                if (found != null) return found;
            }

            // otherwise write the same one-line theme Unity generates by default
            Directory.CreateDirectory(ThemeFolder);
            File.WriteAllText(ThemePath, "@import url(\"unity-theme://default\");\n");
            AssetDatabase.ImportAsset(ThemePath, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
        }
    }
}
