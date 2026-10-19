using System.IO;
using UnityEditor;
using UnityEngine;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Copies the latest validation report into Resources so it ships with the build.
    ///
    /// The checks run outside Unity and write to pipeline/out/, which is not part of the
    /// project. A web build cannot reach that folder, or run the checks itself, so the
    /// result has to be carried in as an asset. This is the only generated file that
    /// enters Assets/, and it goes to its own folder rather than near the authored art.
    /// </summary>
    public static class ValidationReportExporter
    {
        private const string Source = "pipeline/out/validation.json";
        private const string BrokenSource = "pipeline/demo/broken_glove/out/validation.json";
        private const string Folder = "Assets/RecastCustomizer/Resources";
        private const string Target = Folder + "/" + ValidationReport.ResourceName + ".json";
        private const string BrokenTarget =
            Folder + "/" + ValidationReport.BrokenResourceName + ".json";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        [MenuItem("Recast Customizer/Copy Validation Report Into The Build")]
        public static void CopyMenu()
        {
            if (Copy(out string message)) Debug.Log("[Recast] " + message);
            else Debug.LogWarning("[Recast] " + message);
        }

        /// <summary>True when the asset's own report was copied; message explains either way.</summary>
        public static bool Copy(out string message)
        {
            var source = Path.Combine(ProjectRoot, Source);
            if (!File.Exists(source))
            {
                message = "No report at " + Source + ". Run the checks first.";
                return false;
            }

            Directory.CreateDirectory(Path.Combine(ProjectRoot, Folder));

            try
            {
                File.Copy(source, Path.Combine(ProjectRoot, Target), true);
            }
            catch (IOException e)
            {
                message = "Could not copy the report: " + e.Message;
                return false;
            }
            AssetDatabase.ImportAsset(Target, ImportAssetOptions.ForceUpdate);

            // The limits the in-game checks judge against, written by the same run that
            // produced the report, so the two cannot describe different rules.
            var specSource = Path.Combine(ProjectRoot, "pipeline/out/validation_spec.json");
            if (File.Exists(specSource))
            {
                string specTarget = Folder + "/" + RuntimeValidator.SpecResource + ".json";
                File.Copy(specSource, Path.Combine(ProjectRoot, specTarget), true);
                AssetDatabase.ImportAsset(specTarget, ImportAssetOptions.ForceUpdate);
            }

            // The broken kit's report rides along when it exists, so the running tool can
            // show the checks catching something rather than only reporting all-clear.
            var brokenSource = Path.Combine(ProjectRoot, BrokenSource);
            string also = "";
            if (File.Exists(brokenSource))
            {
                try
                {
                    File.Copy(brokenSource, Path.Combine(ProjectRoot, BrokenTarget), true);
                    AssetDatabase.ImportAsset(BrokenTarget, ImportAssetOptions.ForceUpdate);
                    also = " The broken demo kit's report went with it.";
                }
                catch (IOException e)
                {
                    also = " The broken kit's report could not be copied: " + e.Message;
                }
            }
            else
            {
                also = " No broken-kit report found, so the comparison will not be offered.";
            }

            message = "Validation report copied into the build (" + Target + ")." + also;
            return true;
        }
    }
}
