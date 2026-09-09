using UnityEngine;

namespace RecastCustomizer
{
    /// <summary>
    /// Zero-setup on-screen controls for the modular glove — no Canvas, no prefab wiring.
    /// Drop it next to a <see cref="ModularGloveAssembly"/> and press Play: one row of
    /// buttons per segment, plus Randomize and a copyable combination code.
    ///
    /// Intended for quick in-editor review and screen recordings. Replace with a proper
    /// UI Toolkit / uGUI panel for a polished build.
    /// </summary>
    [RequireComponent(typeof(ModularGloveAssembly))]
    public class GloveCustomizerOverlay : MonoBehaviour
    {
        [SerializeField] private bool showInEditMode = false;
        [SerializeField] private int panelWidth = 260;

        private ModularGloveAssembly _assembly;

        private void Awake() => _assembly = GetComponent<ModularGloveAssembly>();

        private void OnGUI()
        {
            if (!Application.isPlaying && !showInEditMode) return;
            if (_assembly == null) _assembly = GetComponent<ModularGloveAssembly>();
            var set = _assembly.VariantSet;
            if (set == null) return;

            GUILayout.BeginArea(new Rect(12, 12, panelWidth, Screen.height - 24), GUI.skin.box);
            GUILayout.Label($"<b>{set.assetName}</b>", RichLabel());

            for (int s = 0; s < set.segments.Count; s++)
            {
                var seg = set.segments[s];
                GUILayout.Space(6);
                GUILayout.Label(seg.DisplayLabel);
                GUILayout.BeginHorizontal();
                for (int v = 0; v < seg.VariantCount; v++)
                {
                    bool active = _assembly.Selection[s] == v;
                    GUI.backgroundColor = active ? Color.cyan : Color.white;
                    if (GUILayout.Button(seg.VariantId(v)))
                        _assembly.SetVariantAt(s, v);
                }
                GUI.backgroundColor = Color.white;
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(12);
            if (GUILayout.Button("Randomize")) _assembly.Randomize();
            GUILayout.Space(4);
            GUILayout.Label(_assembly.GetCombinationCode(), RichLabel());
            GUILayout.EndArea();
        }

        private static GUIStyle RichLabel()
        {
            return new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
        }
    }
}
