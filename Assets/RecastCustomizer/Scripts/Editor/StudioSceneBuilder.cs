using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Configures the current scene as a neutral product-viz studio for reviewing the
    /// glove: skybox-driven ambient, a single dim key light, and a neutral Global Volume.
    ///
    /// The numbers are the studio setup this project standardised on: ambient at 1.1, a
    /// single key light at 0.2, and no post-processing at all. Nothing is imported — supply
    /// your own cubemap, or a CC0 studio HDRI, for the skybox.
    ///
    /// Menu: Recast Customizer ▸ Setup Studio Lighting
    /// </summary>
    public static class StudioSceneBuilder
    {
        // The calibrated studio values. Changing any of these changes every review render.
        private const float AmbientIntensity = 1.1f;   // RenderSettings m_AmbientIntensity
        private const float ReflectionIntensity = 1f;  // m_ReflectionIntensity
        private const int   ReflectionBounces = 1;     // m_ReflectionBounces
        private const float KeyLightIntensity = 0.2f;  // lighting.prefab dominant_directional
        private const string KeyLightName = "dominant_directional";
        private const string SkyboxMatPath = "Assets/Settings/Recast_Studio_Skybox.mat";

        [MenuItem("Recast Customizer/Setup Studio Lighting")]
        public static void SetupStudioLighting()
        {
            var skybox = EnsureSkyboxMaterial();

            // re-running picks up an HDRI added since the material was first created
            if (skybox.GetTexture("_Tex") == null)
            {
                var hdri = FindCubemap();
                if (hdri != null)
                {
                    skybox.SetTexture("_Tex", hdri);
                    EditorUtility.SetDirty(skybox);
                    Debug.Log($"[StudioSceneBuilder] Assigned HDRI '{hdri.name}' to the existing skybox material.");
                }
                else
                {
                    Debug.LogWarning(
                        "[StudioSceneBuilder] No cubemap found. Import an HDRI (.hdr/.exr), set its " +
                        "Texture Shape to Cube in the Inspector, then run this menu item again.");
                }
            }

            // --- environment ---
            RenderSettings.skybox = skybox;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = AmbientIntensity;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = ReflectionIntensity;
            RenderSettings.reflectionBounces = ReflectionBounces;
            RenderSettings.fog = false;

            // --- key light ---
            var key = FindOrCreateKeyLight();
            key.type = LightType.Directional;
            key.color = Color.white;
            key.intensity = KeyLightIntensity;
            key.shadows = LightShadows.Soft;
            key.bounceIntensity = 1f;
            key.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // --- neutral global volume, so the material is judged and not the grade ---
            EnsureGlobalVolume();

            DynamicGI.UpdateEnvironment();
            EditorApplication.QueuePlayerLoopUpdate();

            Debug.Log(
                "[StudioSceneBuilder] Studio lighting applied.\n" +
                $"  Ambient: Skybox @ {AmbientIntensity}   Key light: {KeyLightIntensity}   Post: neutral\n" +
                "  NEXT: assign an HDRI cubemap to Assets/Settings/Recast_Studio_Skybox.mat.\n" +
                "  Free CC0 studio HDRIs: polyhaven.com/hdris/studio  (import as Texture Shape = Cube)");
        }

        private static Material EnsureSkyboxMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SkyboxMatPath);
            if (mat != null) return mat;

            System.IO.Directory.CreateDirectory("Assets/Settings");
            mat = new Material(Shader.Find("Skybox/Cubemap"))
            {
                name = "Recast_Studio_Skybox"
            };
            // neutral grey tint, exposure 1, no rotation: the sky gives light, not colour
            mat.SetColor("_Tint", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            mat.SetFloat("_Exposure", 1f);
            mat.SetFloat("_Rotation", 0f);

            var hdri = FindCubemap();
            if (hdri != null)
            {
                mat.SetTexture("_Tex", hdri);
                Debug.Log($"[StudioSceneBuilder] Auto-assigned HDRI '{hdri.name}' to the skybox.");
            }

            AssetDatabase.CreateAsset(mat, SkyboxMatPath);
            AssetDatabase.SaveAssets();
            return mat;
        }

        /// <summary>
        /// Finds an imported HDRI to use as the skybox. Prefers anything under
        /// Assets/Settings, otherwise takes the first cubemap in the project.
        /// A .hdr/.exr only shows up here once its importer is set to Texture Shape = Cube.
        /// </summary>
        private static Cubemap FindCubemap()
        {
            var guids = AssetDatabase.FindAssets("t:Cubemap", new[] { "Assets/Settings" });
            if (guids.Length == 0)
                guids = AssetDatabase.FindAssets("t:Cubemap", new[] { "Assets" });

            foreach (var guid in guids)
            {
                var cube = AssetDatabase.LoadAssetAtPath<Cubemap>(AssetDatabase.GUIDToAssetPath(guid));
                if (cube != null) return cube;
            }
            return null;
        }

        private static Light FindOrCreateKeyLight()
        {
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l.type != LightType.Directional) continue;
                l.gameObject.name = KeyLightName;
                return l;
            }

            var go = new GameObject(KeyLightName);
            Undo.RegisterCreatedObjectUndo(go, "Create key light");
            return go.AddComponent<Light>();
        }

        private static void EnsureGlobalVolume()
        {
            foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
                if (v.isGlobal) return;

            var go = new GameObject("Global Volume");
            Undo.RegisterCreatedObjectUndo(go, "Create global volume");
            var vol = go.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 0f;
            // Left with no profile on purpose. Tonemapping off, bloom, vignette and grain
            // at zero, no colour grading. An empty volume is exactly that, and stays honest.
        }
    }
}
