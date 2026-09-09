using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Recast Customizer ▸ Backdrop
    ///
    /// Lets the visible backdrop be darkened and blurred without touching how the asset is
    /// lit.
    ///
    /// Normally those are the same control: the scene is lit by the skybox (ambient 1.1,
    /// key light only 0.2), so lowering the skybox exposure dims the glove too. This breaks
    /// the link. It reads the light the skybox is currently producing, freezes that as a
    /// fixed ambient value, and only then dims the skybox — so the glove keeps exactly the
    /// light it has now while the background falls away behind it.
    ///
    ///   Darken Backdrop        — freeze the lighting, then dim and blur the sky
    ///   Restore Skybox Lighting — hand ambient back to the skybox and undim
    ///
    /// After freezing, changing the HDRI or its rotation no longer changes the lighting.
    /// Run Restore, make the change, then Darken again.
    /// </summary>
    public static class BackdropTool
    {
        private const string SkyboxMatPath = "Assets/Settings/Recast_Studio_Skybox.mat";

        /// <summary>How much of the sky's brightness is left on screen. 1 = untouched.</summary>
        private const float DarkenedExposure = 0.5f;

        /// <summary>Mip level sampled by Recast/Blurred Skybox. Higher is softer.</summary>
        private const float DarkenedBlur = 5.5f;

        /// <summary>Cools the backdrop slightly so the warm leather separates from it.</summary>
        private static readonly Color DarkenedTint = new(0.46f, 0.49f, 0.56f, 0.5f);

        private const float DefaultExposure = 1f;
        private const float DefaultBlur = 0f;
        private static readonly Color DefaultTint = new(0.5f, 0.5f, 0.5f, 0.5f);

        [MenuItem("Recast Customizer/Backdrop/Darken Backdrop")]
        public static void DarkenBackdrop()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SkyboxMatPath);
            if (mat == null)
            {
                Debug.LogError($"[BackdropTool] {SkyboxMatPath} not found. " +
                               "Run Recast Customizer ▸ Setup Studio Lighting first.");
                return;
            }

            // 1. make sure the probe reflects the sky as it looks right now
            RenderSettings.ambientMode = AmbientMode.Skybox;
            DynamicGI.UpdateEnvironment();

            // 2. take a copy of that light and pin it. With Custom, Unity stops recomputing
            //    ambient from the skybox and just uses this — which is what frees the sky.
            var frozen = RenderSettings.ambientProbe;
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = frozen;

            // 3. now the sky can be dimmed without consequence
            Undo.RecordObject(mat, "Darken backdrop");
            if (mat.HasFloat("_Exposure")) mat.SetFloat("_Exposure", DarkenedExposure);
            if (mat.HasColor("_Tint")) mat.SetColor("_Tint", DarkenedTint);

            if (mat.HasFloat("_Blur"))
            {
                mat.SetFloat("_Blur", DarkenedBlur);
            }
            else
            {
                Debug.LogWarning("[BackdropTool] The skybox material has no Blur slider.\n" +
                                 "  Set its Shader to Recast ▸ Blurred Skybox for a soft backdrop.");
            }

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            MarkSceneDirty();

            Debug.Log($"[BackdropTool] Backdrop darkened (exposure {DarkenedExposure}, blur {DarkenedBlur}).\n" +
                      "  Ambient is frozen at its current value, so the glove is lit exactly as before.\n" +
                      "  Note: while frozen, changing the HDRI no longer changes the lighting — " +
                      "run Restore Skybox Lighting first if you swap it.");
        }

        [MenuItem("Recast Customizer/Backdrop/Restore Skybox Lighting")]
        public static void RestoreBackdrop()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SkyboxMatPath);
            if (mat != null)
            {
                Undo.RecordObject(mat, "Restore backdrop");
                if (mat.HasFloat("_Exposure")) mat.SetFloat("_Exposure", DefaultExposure);
                if (mat.HasColor("_Tint")) mat.SetColor("_Tint", DefaultTint);
                if (mat.HasFloat("_Blur")) mat.SetFloat("_Blur", DefaultBlur);
                EditorUtility.SetDirty(mat);
                AssetDatabase.SaveAssets();
            }

            RenderSettings.ambientMode = AmbientMode.Skybox;
            DynamicGI.UpdateEnvironment();
            MarkSceneDirty();

            Debug.Log("[BackdropTool] Skybox restored and ambient handed back to it.");
        }

        private static void MarkSceneDirty()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (scene.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
