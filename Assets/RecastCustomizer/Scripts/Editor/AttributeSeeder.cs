using UnityEditor;
using UnityEngine;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Recast Customizer ▸ Seed Attributes From Materials
    ///
    /// Fills in the five falconry attributes for every variant so the equipment screen has
    /// something meaningful to show, instead of leaving sixty empty fields to type by hand.
    ///
    /// The seed values are derived from the materials themselves — shader smoothness and
    /// occlusion, plus the average colour and colour spread of the base map — so different
    /// leathers genuinely score differently, and a plain hide never scores the same as the
    /// gilded Egyptian panel.
    ///
    /// These are a starting point, not a measurement. Open the variant set afterwards and
    /// tune them by hand: the trade-offs between designs are a design decision.
    /// </summary>
    public static class AttributeSeeder
    {
        private const string VariantSetPath = "Assets/RecastCustomizer/falcon_glove_variant_set.asset";

        private static readonly (string label, string blurb, Color color)[] Attributes =
        {
            ("PROTECTION", "Guards the forearm against talon and beak.", new Color(0.90f, 0.38f, 0.32f)),
            ("BOND",       "How readily the falcon settles and stays.",  new Color(0.36f, 0.80f, 0.98f)),
            ("AGILITY",    "Freedom of movement when casting off.",      new Color(0.45f, 0.92f, 0.60f)),
            ("ENDURANCE",  "How long the arm holds without fatigue.",    new Color(1.00f, 0.78f, 0.13f)),
            ("PRESTIGE",   "Standing earned at the falconers' meet.",    new Color(0.85f, 0.42f, 0.95f)),
        };

        [MenuItem("Recast Customizer/Seed Attributes From Materials")]
        public static void Seed()
        {
            var set = AssetDatabase.LoadAssetAtPath<GloveVariantSet>(VariantSetPath);
            if (set == null)
            {
                Debug.LogError("[AttributeSeeder] No variant set found. " +
                               "Run Recast Customizer ▸ Rebuild Variant Set From Folder first.");
                return;
            }

            // (re)declare the attribute list, preserving any labels already edited by hand
            var existing = set.attributes;
            set.attributes = new System.Collections.Generic.List<GloveVariantSet.AttributeDef>();
            for (int i = 0; i < Attributes.Length; i++)
            {
                var def = (existing != null && i < existing.Count && !string.IsNullOrWhiteSpace(existing[i].label))
                    ? existing[i]
                    : new GloveVariantSet.AttributeDef();

                if (string.IsNullOrWhiteSpace(def.label) || def.label == "ATTRIBUTE") def.label = Attributes[i].label;
                if (string.IsNullOrWhiteSpace(def.blurb)) def.blurb = Attributes[i].blurb;
                if (def.color == Color.black) def.color = Attributes[i].color;

                set.attributes.Add(def);
            }

            int seeded = 0;

            foreach (var segment in set.segments)
            {
                while (segment.variantStats.Count < segment.VariantCount)
                    segment.variantStats.Add(new GloveVariantSet.VariantStats());
                while (segment.variantStats.Count > segment.VariantCount)
                    segment.variantStats.RemoveAt(segment.variantStats.Count - 1);

                for (int v = 0; v < segment.VariantCount; v++)
                {
                    var mat = segment.variants[v];
                    if (mat == null) continue;

                    float smooth = mat.HasFloat("_Smoothness") ? mat.GetFloat("_Smoothness") : 0.5f;
                    float occ = mat.HasFloat("_OcclusionStrength") ? mat.GetFloat("_OcclusionStrength") : 0.5f;
                    Sample(mat, out float warmth, out float saturation, out float luminance);

                    // Each attribute leans on a different property, so the five figures move
                    // independently rather than all tracking one slider.
                    float protection = 0.70f * (1f - smooth) + 0.30f * occ;
                    float bond       = 0.65f * warmth + 0.35f * (1f - occ);
                    float agility    = 0.60f * smooth + 0.40f * (1f - occ);
                    float endurance  = 0.55f * occ + 0.45f * (1f - luminance);
                    float prestige   = 0.75f * saturation + 0.25f * luminance;

                    var stats = segment.variantStats[v];
                    stats.values = new System.Collections.Generic.List<float>
                    {
                        Shape(protection), Shape(bond), Shape(agility), Shape(endurance), Shape(prestige)
                    };
                    seeded++;
                }
            }

            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();

            Debug.Log($"[AttributeSeeder] Seeded {seeded} variants across {set.attributes.Count} attributes.\n" +
                      "  Values are derived from smoothness, occlusion and the base map's colour.\n" +
                      "  Open the variant set and hand-tune — the trade-offs are a design call.");
            Selection.activeObject = set;
        }

        /// <summary>Pushes values off the middle so the bars read as distinct rather than all mid-grey.</summary>
        private static float Shape(float v)
        {
            v = Mathf.Clamp01(v);
            return Mathf.Clamp01(0.14f + Mathf.SmoothStep(0f, 1f, v) * 0.82f);
        }

        /// <summary>
        /// Average warmth, colour spread and brightness of a material's base map.
        /// Blitted through a render target so it works whatever the texture's import
        /// settings are — no need to flag every source texture readable.
        /// </summary>
        private static void Sample(Material mat, out float warmth, out float saturation, out float luminance)
        {
            warmth = 0.5f; saturation = 0.5f; luminance = 0.5f;

            var src = mat.mainTexture;
            if (src == null) return;

            var rt = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);

            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);

            var px = tex.GetPixels();
            Object.DestroyImmediate(tex);
            if (px.Length == 0) return;

            float r = 0f, g = 0f, b = 0f, sat = 0f;
            foreach (var c in px)
            {
                r += c.r; g += c.g; b += c.b;
                float mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                float mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                sat += mx > 0.001f ? (mx - mn) / mx : 0f;
            }

            int n = px.Length;
            r /= n; g /= n; b /= n; sat /= n;

            warmth = Mathf.Clamp01(0.5f + (r - b));
            saturation = Mathf.Clamp01(sat);
            luminance = Mathf.Clamp01(r * 0.299f + g * 0.587f + b * 0.114f);
        }
    }
}
