using System;
using System.Collections.Generic;
using UnityEngine;

namespace RecastCustomizer
{
    /// <summary>
    /// Runtime driver for the modular falconer's glove. Sits on the assembly root and,
    /// for every segment in <see cref="variantSet"/>, resolves the matching child
    /// Renderer and swaps its material to the currently selected variant.
    ///
    /// This is the Unity-side mirror of the web customizer's useConfiguratorState hook:
    /// one selection array is the source of truth, everything else reads from it.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class ModularGloveAssembly : MonoBehaviour
    {
        [SerializeField] private GloveVariantSet variantSet;

        [Tooltip("Parallel to variantSet.segments: selected variant index per segment.")]
        [SerializeField] private int[] selection = Array.Empty<int>();

        [Tooltip("Use sharedMaterial (edits the asset) instead of an instanced material. " +
                 "Leave off at runtime; turn on only for authoring baked prefab variants.")]
        [SerializeField] private bool editShared = false;

        private readonly Dictionary<string, Renderer> _rendererCache = new();

        public GloveVariantSet VariantSet => variantSet;
        public IReadOnlyList<int> Selection => selection;

        public event Action SelectionChanged;

        private void OnEnable()
        {
            NormalizeSelectionLength();
            ApplyAll();
        }

        private void OnValidate()
        {
            NormalizeSelectionLength();
            if (isActiveAndEnabled) ApplyAll();
        }

        /// <summary>Set one segment (by id) to a variant index, wrapping out-of-range values.</summary>
        public void SetVariant(string segmentId, int variantIndex)
        {
            if (variantSet == null) return;
            int segIndex = variantSet.segments.FindIndex(s => s.id == segmentId);
            if (segIndex < 0) return;
            SetVariantAt(segIndex, variantIndex);
        }

        public void SetVariantAt(int segmentIndex, int variantIndex)
        {
            if (variantSet == null || segmentIndex < 0 || segmentIndex >= variantSet.segments.Count)
                return;

            var segment = variantSet.segments[segmentIndex];
            int count = Mathf.Max(1, segment.VariantCount);
            variantIndex = ((variantIndex % count) + count) % count;

            NormalizeSelectionLength();
            if (selection[segmentIndex] == variantIndex && Application.isPlaying) return;

            selection[segmentIndex] = variantIndex;
            ApplySegment(segmentIndex);
            SelectionChanged?.Invoke();
        }

        public void CycleVariant(string segmentId, int delta = 1)
        {
            if (variantSet == null) return;
            int segIndex = variantSet.segments.FindIndex(s => s.id == segmentId);
            if (segIndex < 0) return;
            SetVariantAt(segIndex, selection[segIndex] + delta);
        }

        public void Randomize()
        {
            if (variantSet == null) return;
            NormalizeSelectionLength();
            for (int i = 0; i < variantSet.segments.Count; i++)
            {
                int count = Mathf.Max(1, variantSet.segments[i].VariantCount);
                selection[i] = UnityEngine.Random.Range(0, count);
            }
            ApplyAll();
            SelectionChanged?.Invoke();
        }

        /// <summary>e.g. "top02-bottom01-thumb03-tassel01" — handy for screenshots / filenames.</summary>
        public string GetCombinationCode()
        {
            if (variantSet == null) return string.Empty;
            var parts = new List<string>(variantSet.segments.Count);
            for (int i = 0; i < variantSet.segments.Count; i++)
            {
                var seg = variantSet.segments[i];
                parts.Add($"{seg.id}{seg.VariantId(selection[i])}");
            }
            return string.Join("-", parts);
        }

        public void ApplyAll()
        {
            if (variantSet == null) return;
            for (int i = 0; i < variantSet.segments.Count; i++)
                ApplySegment(i);
        }

        private void ApplySegment(int segmentIndex)
        {
            var segment = variantSet.segments[segmentIndex];
            if (segment.VariantCount == 0) return;

            var renderer = ResolveRenderer(segment.childRenderer);
            if (renderer == null)
            {
                Debug.LogWarning($"[ModularGloveAssembly] No child renderer named '{segment.childRenderer}' under {name}.", this);
                return;
            }

            int index = Mathf.Clamp(selection[segmentIndex], 0, segment.VariantCount - 1);
            var material = segment.variants[index];
            if (material == null) return;

            if (editShared || !Application.isPlaying)
                renderer.sharedMaterial = material;
            else
                renderer.material = material;
        }

        private Renderer ResolveRenderer(string childName)
        {
            if (string.IsNullOrEmpty(childName)) return null;
            if (_rendererCache.TryGetValue(childName, out var cached) && cached != null)
                return cached;

            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r.gameObject.name != childName) continue;
                _rendererCache[childName] = r;
                return r;
            }
            return null;
        }

        private void NormalizeSelectionLength()
        {
            int need = variantSet != null ? variantSet.segments.Count : 0;
            if (selection.Length == need) return;
            var resized = new int[need];
            Array.Copy(selection, resized, Mathf.Min(selection.Length, need));
            selection = resized;
        }
    }
}
