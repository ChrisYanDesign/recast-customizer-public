using UnityEditor;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Lets the running tool delete an imported design while it is playing in the editor.
    ///
    /// The tool cannot call the editor directly. Unity builds the runtime assembly first and
    /// the editor assembly second, so a reference from the tool to <see cref="DesignRemover"/>
    /// points at something that does not exist yet. The dependency has to run the other way:
    /// the editor hands the tool a delegate on load.
    ///
    /// In a player build nothing assigns it and it stays null, which is the honest state --
    /// a build has no project files to delete, so its dismiss button hides instead and says
    /// so.
    /// </summary>
    public static class DesignRemoverBridge
    {
        [InitializeOnLoadMethod]
        private static void Install()
        {
            GloveCustomizerUI.RemoveImportedDesign = DesignRemover.RemoveImported;
        }
    }
}
