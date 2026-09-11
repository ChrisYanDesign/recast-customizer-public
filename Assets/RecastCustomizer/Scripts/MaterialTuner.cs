using System;
using System.Collections.Generic;
using UnityEngine;

namespace RecastCustomizer
{
    /// <summary>
    /// Live look adjustment for the equipped materials, and the thing that makes the
    /// customizer a review tool rather than a viewer.
    ///
    /// A reviewer without Unity opens the build, drags these five values until the look is
    /// right, and exports a small file. An artist drops that file back into Unity and the
    /// editor tool bakes the result onto the real assets. The loop closes, which is the whole
    /// point: today that conversation happens over screenshots and vague notes.
    ///
    /// Two of the five cannot be done with material properties at all. Hue and saturation
    /// need the base map itself rewritten, so those run the texture through a small shader
    /// into an offscreen copy and hand that to the material. The material stays a stock URP
    /// Lit, which is what keeps the lighting identical to the reference.
    ///
    /// Nothing here touches an asset on disk. Runtime materials are per-renderer instances
    /// and the adjusted textures are temporary. Exporting is the only thing that persists.
    /// </summary>
    [RequireComponent(typeof(ModularGloveAssembly))]
    [DisallowMultipleComponent]
    public class MaterialTuner : MonoBehaviour
    {
        /// <summary>
        /// The five a reviewer actually asks for, in the order they think about them:
        /// colour first, then surface.
        /// </summary>
        public enum Knob
        {
            Hue        = 0,   // "wrong colour"
            Saturation = 1,   // "too washed out"
            Brightness = 2,   // "too dark"
            Smoothness = 3,   // "too shiny"
            Normal     = 4    // "the grain is too strong"
        }

        [Serializable]
        public class Look
        {
            public float hue = 0f;           // -0.5 - 0.5, a full turn of the colour wheel
            public float saturation = 1f;    // 0 - 2, 0 is grey
            public float brightness = 1f;    // 0.4 - 1.8
            public float smoothness = 0.5f;  // absolute, 0 - 1
            public float normal = 1f;        // absolute bump scale, 0 - 2

            public float Get(Knob k) => k switch
            {
                Knob.Hue => hue,
                Knob.Saturation => saturation,
                Knob.Brightness => brightness,
                Knob.Smoothness => smoothness,
                Knob.Normal => normal,
                _ => 0f
            };

            public void Set(Knob k, float v)
            {
                switch (k)
                {
                    case Knob.Hue: hue = v; break;
                    case Knob.Saturation: saturation = v; break;
                    case Knob.Brightness: brightness = v; break;
                    case Knob.Smoothness: smoothness = v; break;
                    case Knob.Normal: normal = v; break;
                }
            }

            /// <summary>True when the colour needs the texture rebuilt rather than a property set.</summary>
            public bool ColourTouched =>
                !Mathf.Approximately(hue, 0f)
                || !Mathf.Approximately(saturation, 1f)
                || !Mathf.Approximately(brightness, 1f);
        }

        private class Baseline
        {
            public Texture baseMap;
            public float smoothness = 0.5f;
            public float normal = 1f;
        }

        public static readonly (Knob knob, string label, float min, float max)[] Knobs =
        {
            (Knob.Hue,        "HUE",        -0.50f, 0.50f),
            (Knob.Saturation, "SATURATION",  0.00f, 2.00f),
            (Knob.Brightness, "BRIGHTNESS",  0.40f, 1.80f),
            (Knob.Smoothness, "SMOOTHNESS",  0.00f, 1.00f),
            (Knob.Normal,     "NORMAL",      0.00f, 2.00f)
        };

        private ModularGloveAssembly _assembly;
        private Material _hsv;

        // keyed by "segmentId/variantId" so a look follows the variant it was tuned against,
        // not the slot -- swapping away and back must not silently drop the reviewer's work
        private readonly Dictionary<string, Look> _looks = new();
        private readonly Dictionary<string, Baseline> _baselines = new();
        private readonly Dictionary<string, RenderTexture> _adjusted = new();

        public event Action Changed;

        private void OnEnable()
        {
            _assembly = GetComponent<ModularGloveAssembly>();
            _assembly.SelectionChanged += ApplyAll;
            ApplyAll();
        }

        private void OnDisable()
        {
            if (_assembly != null) _assembly.SelectionChanged -= ApplyAll;
            ReleaseAll();
        }

        private void OnDestroy() => ReleaseAll();

        private void ReleaseAll()
        {
            foreach (var rt in _adjusted.Values)
                if (rt != null) rt.Release();
            _adjusted.Clear();
        }

        public string KeyFor(int segmentIndex)
        {
            var set = _assembly != null ? _assembly.VariantSet : null;
            if (set == null || segmentIndex < 0 || segmentIndex >= set.segments.Count) return null;
            var seg = set.segments[segmentIndex];
            return seg.id + "/" + seg.VariantId(_assembly.Selection[segmentIndex]);
        }

        public Look LookFor(int segmentIndex)
        {
            var key = KeyFor(segmentIndex);
            if (key == null) return null;

            if (!_looks.TryGetValue(key, out var look))
            {
                var b = BaselineFor(segmentIndex);
                look = new Look
                {
                    hue = 0f,
                    saturation = 1f,
                    brightness = 1f,
                    smoothness = b != null ? b.smoothness : 0.5f,
                    normal = b != null ? b.normal : 1f
                };
                _looks[key] = look;
            }
            return look;
        }

        public void SetValue(int segmentIndex, Knob knob, float value)
        {
            var look = LookFor(segmentIndex);
            if (look == null) return;
            look.Set(knob, value);
            Apply(segmentIndex);
            Changed?.Invoke();
        }

        public void Reset(int segmentIndex)
        {
            var key = KeyFor(segmentIndex);
            if (key != null) _looks.Remove(key);
            Apply(segmentIndex);
            Changed?.Invoke();
        }

        public void ResetAll()
        {
            _looks.Clear();
            ApplyAll();
            Changed?.Invoke();
        }

        public bool IsTouched(int segmentIndex)
        {
            var key = KeyFor(segmentIndex);
            if (key == null || !_looks.TryGetValue(key, out var look)) return false;
            var b = BaselineFor(segmentIndex);
            if (b == null) return true;
            return look.ColourTouched
                || !Mathf.Approximately(look.smoothness, b.smoothness)
                || !Mathf.Approximately(look.normal, b.normal);
        }

        public void ApplyAll()
        {
            var set = _assembly != null ? _assembly.VariantSet : null;
            if (set == null) return;
            for (int s = 0; s < set.segments.Count; s++) Apply(s);
        }

        private void Apply(int segmentIndex)
        {
            var mat = LiveMaterial(segmentIndex);
            var b = BaselineFor(segmentIndex);
            if (mat == null || b == null) return;

            var key = KeyFor(segmentIndex);
            var look = key != null && _looks.TryGetValue(key, out var l) ? l : null;

            float smooth = look != null ? look.smoothness : b.smoothness;
            float normal = look != null ? look.normal : b.normal;

            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smooth);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smooth);
            if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", normal);

            if (look == null || !look.ColourTouched)
            {
                SetBaseMap(mat, b.baseMap);
                return;
            }

            SetBaseMap(mat, Recolour(key, b.baseMap, look));
        }

        /// <summary>
        /// Run the source map through the HSV pass into an offscreen copy. One blit per
        /// change, so dragging a slider stays responsive even on a 2048 map.
        /// </summary>
        private Texture Recolour(string key, Texture source, Look look)
        {
            if (source == null) return null;

            if (_hsv == null)
            {
                var shader = Shader.Find("Recast/HSVAdjust");
                if (shader == null)
                {
                    Debug.LogWarning("[MaterialTuner] Shader 'Recast/HSVAdjust' not found. " +
                                     "Hue and saturation will do nothing. If this is a build, " +
                                     "add the shader to Project Settings > Graphics > Always Included.");
                    return source;
                }
                _hsv = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            if (!_adjusted.TryGetValue(key, out var rt) || rt == null
                || rt.width != source.width || rt.height != source.height)
            {
                if (rt != null) rt.Release();
                rt = new RenderTexture(source.width, source.height, 0,
                                       RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = "recast_adjusted_" + key.Replace('/', '_'),
                    useMipMap = true,
                    autoGenerateMips = true,
                    wrapMode = TextureWrapMode.Clamp
                };
                rt.Create();
                _adjusted[key] = rt;
            }

            _hsv.SetFloat("_Hue", look.hue);
            _hsv.SetFloat("_Saturation", look.saturation);
            _hsv.SetFloat("_Brightness", look.brightness);
            Graphics.Blit(source, rt, _hsv);
            return rt;
        }

        private static void SetBaseMap(Material mat, Texture tex)
        {
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
        }

        private Material LiveMaterial(int segmentIndex)
        {
            var set = _assembly != null ? _assembly.VariantSet : null;
            if (set == null || segmentIndex < 0 || segmentIndex >= set.segments.Count) return null;

            // Play mode only, deliberately. Outside play mode a renderer hands back the
            // shared material, which IS the asset on disk, so tuning would quietly rewrite
            // the artist's source files. The reviewer's edits are meant to be temporary and
            // to leave the project exactly as they found it; the exported look file is the
            // only thing allowed to outlive the session.
            if (!Application.isPlaying) return null;

            var seg = set.segments[segmentIndex];
            foreach (var r in _assembly.GetComponentsInChildren<Renderer>(true))
            {
                if (r.gameObject.name != seg.childRenderer) continue;
                return r.material;
            }
            return null;
        }

        private Baseline BaselineFor(int segmentIndex)
        {
            var key = KeyFor(segmentIndex);
            if (key == null) return null;
            if (_baselines.TryGetValue(key, out var cached)) return cached;

            var set = _assembly.VariantSet;
            var seg = set.segments[segmentIndex];
            var source = seg.VariantMaterial(_assembly.Selection[segmentIndex]);
            if (source == null) return null;

            var b = new Baseline
            {
                baseMap = source.HasProperty("_BaseMap") ? source.GetTexture("_BaseMap") : source.mainTexture,
                smoothness = source.HasProperty("_Smoothness") ? source.GetFloat("_Smoothness") : 0.5f,
                normal = source.HasProperty("_BumpScale") ? source.GetFloat("_BumpScale") : 1f
            };
            _baselines[key] = b;
            return b;
        }

        // ---------------------------------------------------------------- designs

        /// <summary>
        /// Everything the tool currently shows, as one design.
        ///
        /// Every part is written, not only the adjusted ones, because a design is a finished
        /// thing rather than a list of changes. The tuned flag on each part is what lets the
        /// importer reuse the artist's material where nothing was altered and write a new
        /// asset only where something was.
        /// </summary>
        public SavedDesign CaptureDesign(string name)
        {
            var set = _assembly.VariantSet;
            var design = new SavedDesign
            {
                name = string.IsNullOrWhiteSpace(name) ? "" : name.Trim(),
                asset = set.assetName,
                combination = _assembly.GetCombinationCode(),
                savedUtc = DateTime.UtcNow.ToString("u")
            };

            for (int s = 0; s < set.segments.Count; s++)
            {
                var seg = set.segments[s];
                var look = LookFor(s);
                int index = _assembly.Selection[s];

                design.parts.Add(new SavedDesignPart
                {
                    segment = seg.id,
                    variant = seg.VariantId(index),
                    variantIndex = index,
                    tuned = IsTouched(s),
                    hue = look.hue,
                    saturation = look.saturation,
                    brightness = look.brightness,
                    smoothness = look.smoothness,
                    normal = look.normal
                });
            }
            return design;
        }

        /// <summary>
        /// Put a saved design back on the glove: the part choices first, then the look values
        /// on top of them.
        ///
        /// Parts are matched by variant id rather than by the stored index, because an index
        /// stops meaning the same thing the moment a variant is inserted or reordered in the
        /// set. The index is kept in the file only as a fallback.
        /// </summary>
        public void ApplyDesign(SavedDesign design)
        {
            if (design == null || _assembly == null) return;
            var set = _assembly.VariantSet;
            if (set == null) return;

            for (int s = 0; s < set.segments.Count; s++)
            {
                var seg = set.segments[s];
                var part = design.Part(seg.id);
                if (part == null) continue;

                int index = -1;
                for (int v = 0; v < seg.VariantCount; v++)
                    if (seg.VariantId(v) == part.variant) { index = v; break; }
                if (index < 0) index = Mathf.Clamp(part.variantIndex, 0, Mathf.Max(0, seg.VariantCount - 1));

                _assembly.SetVariantAt(s, index);
            }

            // Looks are set after the swaps, because a swap re-resolves the live material and
            // would otherwise discard the values written a moment earlier.
            for (int s = 0; s < set.segments.Count; s++)
            {
                var part = design.Part(set.segments[s].id);
                if (part == null) continue;

                var look = LookFor(s);
                look.hue = part.hue;
                look.saturation = part.saturation;
                look.brightness = part.brightness;
                look.smoothness = part.smoothness;
                look.normal = part.normal;
            }
            ApplyAll();
        }

        /// <summary>
        /// How many new textures the current state would cost on import.
        ///
        /// Smoothness and normal strength are material values and are free. Hue, saturation
        /// and brightness have to become pixels, so each part carrying one costs a new map at
        /// the source resolution. Showing this while somebody is still choosing is the
        /// difference between a cost they decided to pay and one an engineer finds later.
        /// </summary>
        public int PendingBakeCount()
        {
            var set = _assembly != null ? _assembly.VariantSet : null;
            if (set == null) return 0;
            int n = 0;
            for (int s = 0; s < set.segments.Count; s++)
                if (IsTouched(s) && LookFor(s).ColourTouched) n++;
            return n;
        }

        // ---------------------------------------------------------------- export

        /// <summary>
        /// The current design as a file.
        ///
        /// One format serves both jobs. The handoff importer reads every part and rebuilds
        /// the design; the review importer reads the same file and looks only at the parts
        /// marked tuned. Two formats would have drifted apart within a week.
        /// </summary>
        public string ToJson() => JsonUtility.ToJson(CaptureDesign(null), true);

        /// <summary>The named variant of the above, used when a design is saved by hand.</summary>
        public string ToJson(string designName) =>
            JsonUtility.ToJson(CaptureDesign(designName), true);

        public int TouchedCount()
        {
            var set = _assembly != null ? _assembly.VariantSet : null;
            if (set == null) return 0;
            int n = 0;
            for (int s = 0; s < set.segments.Count; s++) if (IsTouched(s)) n++;
            return n;
        }
    }
}
