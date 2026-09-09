using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Web build configuration and one-click build.
    ///
    ///   Recast Customizer ▸ Web Build ▸ 1. Configure And Switch Platform
    ///       Sets the scene list, colour space and Web publishing settings, then switches
    ///       the active build target. Scripts recompile afterwards, which is why the build
    ///       itself is a separate step.
    ///
    ///   Recast Customizer ▸ Web Build ▸ 2. Build
    ///       Builds to a folder you pick, then reports the resulting size.
    ///
    ///   Recast Customizer ▸ Web Build ▸ 2b. Build To Default Folder
    ///       The same build with no folder prompt, going to recast-web-build next to the
    ///       project. Exists so the build can be driven from a script or from automation,
    ///       which a modal folder dialog makes impossible.
    ///
    /// Settings applied (see docs/WEBGL_BUILD_CHECKLIST.md for why):
    ///   Colour Space          Linear   — keeps lighting matching the editor
    ///   Compression           Gzip     — works on any static host
    ///   Decompression Fallback ON      — no server headers needed (GitHub Pages etc.)
    ///   Data Caching          ON       — fast repeat visits
    /// </summary>
    public static class WebBuildSetup
    {
        private const string SceneName = "glove_studio";
        private const string BuildFolderKey = "RecastCustomizer.WebBuildFolder";

        [MenuItem("Recast Customizer/Web Build/1. Configure And Switch Platform")]
        public static void ConfigureAndSwitch()
        {
            if (!ConfigureSceneList()) return;

            // Linear keeps the web build looking like the editor. Changing this triggers a
            // full asset reimport, so it is set before the platform switch, not after.
            if (PlayerSettings.colorSpace != ColorSpace.Linear)
            {
                PlayerSettings.colorSpace = ColorSpace.Linear;
                Debug.Log("[WebBuildSetup] Colour space set to Linear.");
            }

            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.stripEngineCode = true;

            AssetDatabase.SaveAssets();

            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL)
            {
                Debug.Log("[WebBuildSetup] Settings applied. Already on Web — run step 2 to build.");
                return;
            }

            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
            {
                Debug.LogError(
                    "[WebBuildSetup] Web Build Support is not installed.\n" +
                    "  Unity Hub ▸ Installs ▸ gear on 6000.0.58f2 ▸ Add Modules ▸ Web Build Support.");
                return;
            }

            Debug.Log("[WebBuildSetup] Switching to Web — every texture re-encodes, this takes a few minutes...");
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);
        }

        [MenuItem("Recast Customizer/Web Build/2. Build")]
        public static void BuildWeb()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
            {
                Debug.LogError("[WebBuildSetup] Not on the Web platform yet. Run step 1 first.");
                return;
            }

            if (!ConfigureSceneList()) return;

            var previous = EditorPrefs.GetString(BuildFolderKey, "");
            var folder = EditorUtility.SaveFolderPanel(
                "Choose an output folder (outside the project)",
                string.IsNullOrEmpty(previous) ? "" : Path.GetDirectoryName(previous),
                "recast-web-build");

            if (string.IsNullOrEmpty(folder)) return;

            if (folder.Replace('\\', '/').StartsWith(
                    Directory.GetCurrentDirectory().Replace('\\', '/') + "/Assets"))
            {
                Debug.LogError("[WebBuildSetup] Do not build into the Assets folder — pick a folder outside the project.");
                return;
            }

            EditorPrefs.SetString(BuildFolderKey, folder);
            Build(folder);
        }

        /// <summary>The build folder used when nobody picks one: a sibling of the project.</summary>
        public static string DefaultFolder =>
            Path.Combine(
                Directory.GetParent(Directory.GetCurrentDirectory())!.FullName,
                "recast-web-build").Replace('\\', '/');

        [MenuItem("Recast Customizer/Web Build/2b. Build To Default Folder")]
        public static void BuildWebDefault()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
            {
                Debug.LogError("[WebBuildSetup] Not on the Web platform yet. Run step 1 first.");
                return;
            }

            if (!ConfigureSceneList()) return;

            Directory.CreateDirectory(DefaultFolder);
            Build(DefaultFolder);
        }

        private static void Build(string folder)
        {
            var options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = folder,
                target = BuildTarget.WebGL,
                options = BuildOptions.None
            };

            Debug.Log("[WebBuildSetup] Building to " + folder + " — first build can take 10-30 minutes.");
            var report = BuildPipeline.BuildPlayer(options);

            if (report.summary.result == BuildResult.Succeeded)
            {
                float mb = report.summary.totalSize / 1048576f;
                Debug.Log(
                    $"[WebBuildSetup] Build succeeded — {mb:F1} MB in {report.summary.totalTime.TotalMinutes:F1} min.\n" +
                    $"  {folder}\n" +
                    "  Do NOT open index.html directly; serve it over http. From that folder:\n" +
                    "    python -m http.server 8000     then open http://localhost:8000");
                EditorUtility.RevealInFinder(folder);
            }
            else
            {
                Debug.LogError($"[WebBuildSetup] Build {report.summary.result} with " +
                               $"{report.summary.totalErrors} error(s). See the Console above.");
            }
        }

        /// <summary>
        /// Points the build at glove_studio and nothing else — the usual cause of a build
        /// that opens to an empty grey scene is SampleScene still sitting in this list.
        /// </summary>
        private static bool ConfigureSceneList()
        {
            var guid = AssetDatabase.FindAssets($"{SceneName} t:Scene").FirstOrDefault();
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogError($"[WebBuildSetup] Could not find a scene named '{SceneName}'. " +
                               "Save your studio scene under that name first.");
                return false;
            }

            var path = AssetDatabase.GUIDToAssetPath(guid);
            var current = EditorBuildSettings.scenes;

            bool alreadyCorrect = current.Length == 1 && current[0].path == path && current[0].enabled;
            if (alreadyCorrect) return true;

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(path, true) };
            Debug.Log($"[WebBuildSetup] Build scene list set to: {path}");
            return true;
        }
    }
}
