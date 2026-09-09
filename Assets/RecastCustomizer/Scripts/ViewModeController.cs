using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RecastCustomizer
{
    /// <summary>
    /// Channel isolation for the 3D view, the way Marmoset Toolbag and most look-dev tools
    /// offer it: show one input at a time, unlit, so it can be judged on its own.
    ///
    /// This is the difference between a viewer and a review tool. A lit render answers
    /// "does this look good". These modes answer "is this correct" -- is the normal map
    /// facing the right way, is the albedo carrying baked lighting it should not have, are
    /// the UVs stretched. Lighting actively hides all three.
    ///
    /// Nothing here is destructive. The original materials are held and put back the moment
    /// the view returns to Lit; the inspection materials are throwaway instances.
    /// </summary>
    [DisallowMultipleComponent]
    public class ViewModeController : MonoBehaviour
    {
        public enum Mode
        {
            Lit = 0,          // the real material, lit as normal
            Albedo = 1,       // base colour alone
            Normal = 2,       // the tangent-space normal map as authored
            Smoothness = 3,   // the gloss channel as greyscale
            UV = 4,           // a checker, for stretching and seams
            Wireframe = 5     // topology, drawn over the lit view
        }

        [SerializeField] private ModularGloveAssembly assembly;

        private readonly Dictionary<Renderer, Material[]> _original = new();
        private readonly List<Material> _temporary = new();
        private Shader _inspect;
        private bool _wireHooked;
        private CameraClearFlags _clearFlags;
        private Color _clearColour;
        private bool _clearStashed;

        public Mode Current { get; private set; } = Mode.Lit;
        public event Action Changed;

        private void OnEnable()
        {
            if (assembly == null) assembly = GetComponent<ModularGloveAssembly>();
            if (assembly == null) assembly = FindFirstObjectByType<ModularGloveAssembly>();
        }

        private void OnDisable() => Set(Mode.Lit);

        public void Set(Mode mode)
        {
            RestoreMaterials();
            HookWireframe(false);

            // Wireframe is a play-mode view. Outside play mode the request is honoured as
            // Lit, rather than installing a global render hook into a live editor.
            if (mode == Mode.Wireframe && !Application.isPlaying) mode = Mode.Lit;
            Current = mode;

            if (mode == Mode.Lit) { Changed?.Invoke(); return; }

            if (mode == Mode.Wireframe)
            {
                // Wireframe is drawn over the real materials rather than replacing them.
                // Seeing topology against the shaded form is more useful than seeing it in
                // isolation, because the question is usually whether the silhouette has
                // enough edges to hold up, not what the mesh looks like alone.
                HookWireframe(true);
                Changed?.Invoke();
                return;
            }

            if (_inspect == null) _inspect = Shader.Find("Recast/Inspect");
            if (_inspect == null)
            {
                Debug.LogWarning("[ViewMode] Shader 'Recast/Inspect' not found. If this is a " +
                                 "build, add it to Project Settings > Graphics > Always Included.");
                Current = Mode.Lit;
                Changed?.Invoke();
                return;
            }

            foreach (var r in Renderers())
            {
                if (!_original.ContainsKey(r)) _original[r] = r.sharedMaterials;

                var swapped = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < swapped.Length; i++)
                {
                    var src = _original[r][i];
                    var m = new Material(_inspect) { hideFlags = HideFlags.HideAndDontSave };

                    // Carry the source maps across. A replacement shader on its own would
                    // draw every part identically, which tells you nothing.
                    if (src != null)
                    {
                        if (src.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", src.GetTexture("_BaseMap"));
                        if (src.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", src.GetTexture("_BumpMap"));
                        if (src.HasProperty("_SpecGlossMap")) m.SetTexture("_SpecGlossMap", src.GetTexture("_SpecGlossMap"));
                        if (src.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", src.GetFloat("_Smoothness"));
                    }
                    m.SetFloat("_Mode", (int)mode);
                    swapped[i] = m;
                    _temporary.Add(m);
                }
                r.materials = swapped;
            }
            Changed?.Invoke();
        }

        private IEnumerable<Renderer> Renderers()
        {
            if (assembly == null) yield break;
            foreach (var r in assembly.GetComponentsInChildren<Renderer>(true))
                if (!(r is ParticleSystemRenderer)) yield return r;
        }

        private void RestoreMaterials()
        {
            foreach (var pair in _original)
                if (pair.Key != null) pair.Key.sharedMaterials = pair.Value;
            _original.Clear();

            foreach (var m in _temporary) if (m != null) DestroyImmediate(m);
            _temporary.Clear();
        }

        // ---------------------------------------------------------------- wireframe

        private void HookWireframe(bool on)
        {
            if (on == _wireHooked) return;
            _wireHooked = on;

            if (on)
            {
                RenderPipelineManager.beginCameraRendering += WireOn;
                RenderPipelineManager.endCameraRendering += WireOff;
                StashBackdrop();
            }
            else
            {
                RenderPipelineManager.beginCameraRendering -= WireOn;
                RenderPipelineManager.endCameraRendering -= WireOff;
                GL.wireframe = false;
                RestoreBackdrop();
            }
        }

        /// <summary>
        /// Only the game camera, and only in play mode.
        ///
        /// These callbacks fire for every camera Unity renders, which in the editor includes
        /// the Scene view and the hidden preview cameras that draw material and asset
        /// thumbnails in the Project window. Forcing wireframe onto those is unstable and
        /// took the editor down. Narrowing it to the one camera that actually wants it costs
        /// nothing and removes the whole class of problem.
        /// </summary>
        private static bool IsViewportCamera(Camera cam)
        {
            if (!Application.isPlaying) return false;
            if (cam == null) return false;
            if (cam.cameraType != CameraType.Game) return false;   // excludes SceneView and Preview
            return cam == Camera.main;
        }

        private static void WireOn(ScriptableRenderContext ctx, Camera cam)
        {
            if (IsViewportCamera(cam)) GL.wireframe = true;
        }

        // Turned off again the moment the camera is done, unconditionally. Leaving it set
        // would draw the interface itself as wireframe, since everything after this shares
        // the same GL state.
        private static void WireOff(ScriptableRenderContext ctx, Camera cam) => GL.wireframe = false;

        /// <summary>
        /// Wireframe applies to everything the camera draws, and the sky is geometry too, so
        /// it comes out as a mesh of triangles behind the asset. That is noise, not
        /// information. Swapping to a flat clear for the duration leaves only the topology
        /// that is actually being inspected.
        /// </summary>
        private void StashBackdrop()
        {
            var cam = Camera.main;
            if (cam == null || _clearStashed) return;
            _clearFlags = cam.clearFlags;
            _clearColour = cam.backgroundColor;
            _clearStashed = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.20f, 1f);
        }

        private void RestoreBackdrop()
        {
            var cam = Camera.main;
            if (cam == null || !_clearStashed) return;
            cam.clearFlags = _clearFlags;
            cam.backgroundColor = _clearColour;
            _clearStashed = false;
        }

        private void OnDestroy()
        {
            HookWireframe(false);
            RestoreMaterials();
        }
    }
}
