using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace RecastCustomizer
{
    /// <summary>
    /// The asset checks, run in the engine on whatever is actually on screen.
    ///
    /// The offline pipeline reads project files -- names, import settings, which textures
    /// nothing references. None of that exists in a build, so those checks ship as a
    /// recorded result. Everything else can be measured here and now: triangles, material
    /// count, texture sizes, memory, empty map slots, UVs outside the sheet, UVs sitting
    /// on top of each other, and a colour map that is really a normal map.
    ///
    /// It runs as a coroutine and yields between parts, so the work is real and the
    /// progress shown is the actual progress rather than a timer.
    /// </summary>
    public static class RuntimeValidator
    {
        public const string SpecResource = "validation_spec";

        [Serializable]
        public class Spec
        {
            public string profile = "";
            public int maxTrianglesPerAsset = -1;
            public int maxMaterialsPerDesign = -1;
            public int maxTextureSize = -1;
            public float maxTextureMemoryMb = -1f;
            // Passes, but says so. Not a second limit: a figure between this and the
            // ceiling is within budget and must not read as a failure.
            public float warnTextureMemoryMb = -1f;
            public int maxUvOverlapTexels = -1;
            public float bitsPerPixel = -1f;
            public float mipmapMultiplier = 1f;
            public string[] requireMaps = Array.Empty<string>();
            // What each part measured when the asset was last checked offline. Not a
            // limit: a record, so a mesh that has drifted from what was measured can be
            // noticed here. Nothing else catches geometry that is merely wrong rather
            // than over a budget.
            public string[] segmentNames = Array.Empty<string>();
            public int[] segmentTriangles = Array.Empty<int>();

            /// <summary>The recorded triangle count for a part, or -1 if none was recorded.</summary>
            public int RecordedTriangles(string part)
            {
                if (segmentNames == null || segmentTriangles == null) return -1;
                for (int i = 0; i < segmentNames.Length && i < segmentTriangles.Length; i++)
                    if (segmentNames[i] == part) return segmentTriangles[i];
                return -1;
            }
            public string[] overlapAllowed = Array.Empty<string>();

            public static Spec Load()
            {
                var text = Resources.Load<TextAsset>(SpecResource);
                if (text == null) return null;
                try { return JsonUtility.FromJson<Spec>(text.text); }
                catch (Exception e)
                {
                    Debug.LogWarning("[RuntimeValidator] could not read the spec: " + e.Message);
                    return null;
                }
            }

            public bool Allows(string part)
            {
                foreach (var p in overlapAllowed) if (p == part) return true;
                return false;
            }
        }

        private const int OverlapResolution = 512;   // enough to find a real overlap
        private const int SampleResolution = 64;     // for reading a texture back off the GPU

        // The slots a URP/Lit material keeps its maps in, and the names the spec uses.
        private static readonly (string map, string property)[] Slots =
        {
            ("diffuse", "_BaseMap"),
            ("normal", "_BumpMap"),
            // Not required, but they still cost memory, and leaving them out made the
            // live total disagree with the offline one for no good reason.
            ("specular", "_SpecGlossMap"),
            ("occlusion", "_OcclusionMap"),
        };

        private static ValidationRow Row(string scope, string target, string check,
                                         string status, string message) =>
            new ValidationRow
            {
                scope = scope, target = target, check = check,
                status = status, message = message,
            };

        private static string Fmt(int v) => v.ToString("N0");

        /// <summary>
        /// Walk the glove and report. <paramref name="onStage"/> is called with a label
        /// and a fraction so a progress bar can follow along.
        /// </summary>
        public static IEnumerator Run(Transform glove, Spec spec, List<ValidationRow> into,
                                      Action<string, float> onStage)
        {
            into.Clear();
            if (glove == null)
            {
                into.Add(Row("asset", "glove", "Asset present", "fail",
                             "Nothing is loaded to check."));
                yield break;
            }

            var renderers = glove.GetComponentsInChildren<Renderer>(true);
            float steps = Mathf.Max(1, renderers.Length * 3 + 2);
            float done = 0f;

            void Stage(string label) => onStage?.Invoke(label, Mathf.Clamp01(done / steps));

            // --- the kit as a whole -------------------------------------------------
            Stage("Counting geometry");
            yield return null;

            int totalTris = 0;
            var materials = new HashSet<Material>();
            foreach (var r in renderers)
            {
                var filter = r.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                    totalTris += filter.sharedMesh.triangles.Length / 3;
                if (r.sharedMaterial != null) materials.Add(r.sharedMaterial);
            }
            done += 1f;

            if (spec.maxTrianglesPerAsset > 0)
                into.Add(totalTris > spec.maxTrianglesPerAsset
                    ? Row("asset", "glove", "Triangle count", "fail",
                          Fmt(totalTris) + " triangles, over the limit of "
                          + Fmt(spec.maxTrianglesPerAsset) + ".")
                    : Row("asset", "glove", "Triangle count", "pass",
                          Fmt(totalTris) + " triangles, within the limit of "
                          + Fmt(spec.maxTrianglesPerAsset) + "."));
            else
                into.Add(Row("asset", "glove", "Triangle count", "unenforced",
                             Fmt(totalTris) + " triangles. No limit set."));

            if (spec.maxMaterialsPerDesign > 0)
                into.Add(materials.Count > spec.maxMaterialsPerDesign
                    ? Row("asset", "glove", "Material count", "fail",
                          materials.Count + " materials, over the limit of "
                          + spec.maxMaterialsPerDesign + ".")
                    : Row("asset", "glove", "Material count", "pass",
                          materials.Count + " materials, within the limit of "
                          + spec.maxMaterialsPerDesign + "."));

            // --- each part ----------------------------------------------------------
            long texels = 0;
            var counted = new HashSet<Texture>();

            foreach (var r in renderers)
            {
                string part = r.gameObject.name;
                var filter = r.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;

                Stage("Checking " + part);
                yield return null;

                if (mesh == null)
                {
                    into.Add(Row("segment", part, "Mesh bound", "fail",
                                 "This part has no mesh, so it draws nothing. Usually a "
                                 + "renderer pointing at a name the export did not use."));
                    done += 3f;
                    continue;
                }

                into.Add(Row("segment", part, "Mesh bound", "pass",
                             "Drawing " + Fmt(mesh.triangles.Length / 3) + " triangles."));
                int tris = mesh.triangles.Length / 3;
                into.Add(Row("segment", part, "Triangle count", "unenforced",
                             Fmt(tris) + " triangles. No per-part limit."));

                // Against the record rather than against a budget. A part can be well
                // inside every limit and still be the wrong mesh -- faces deleted, a stale
                // export, the wrong file picked up -- and a count that no longer matches
                // what was measured is the only sign of it from in here.
                int recorded = spec.RecordedTriangles(part);
                if (recorded > 0)
                {
                    int drift = tris - recorded;
                    into.Add(drift == 0
                        ? Row("segment", part, "Matches the record", "pass",
                              Fmt(tris) + " triangles, the same as when this asset was measured.")
                        : Row("segment", part, "Matches the record", "fail",
                              Fmt(tris) + " triangles, but " + Fmt(recorded) + " were recorded"
                              + " -- " + (drift < 0 ? Fmt(-drift) + " missing" : Fmt(drift) + " extra")
                              + ". This is not the mesh that was checked."));
                }
                done += 1f;

                // UVs outside the sheet
                var uv = mesh.uv;
                int outside = 0;
                for (int i = 0; i < uv.Length; i++)
                    if (uv[i].x < -0.001f || uv[i].x > 1.001f
                        || uv[i].y < -0.001f || uv[i].y > 1.001f) outside++;

                into.Add(outside > 0
                    ? Row("segment", part, "UVs inside the map", "fail",
                          Fmt(outside) + " of " + Fmt(uv.Length) + " UV coordinates sit "
                          + "outside the 0-1 square, so those faces sample the wrong part "
                          + "of the texture.")
                    : Row("segment", part, "UVs inside the map", "pass",
                          "All " + Fmt(uv.Length) + " UV coordinates sit inside the sheet."));
                done += 1f;

                // UVs on top of each other
                Stage("Measuring " + part + " UVs");
                yield return null;

                int overlap = 0;
                yield return CountOverlap(mesh, result => overlap = result);

                if (spec.Allows(part))
                    into.Add(Row("segment", part, "UV overlap", "pass",
                                 Fmt(overlap) + " overlapping texels. This part is declared "
                                 + "as mirrored, so faces sharing texture space is intended."));
                else if (spec.maxUvOverlapTexels >= 0 && overlap > spec.maxUvOverlapTexels)
                    into.Add(Row("segment", part, "UV overlap", "fail",
                                 Fmt(overlap) + " texels are covered by more than one face. "
                                 + "Overlapping faces share pixels, so painting one changes "
                                 + "the other."));
                else
                    into.Add(Row("segment", part, "UV overlap", "pass",
                                 Fmt(overlap) + " overlapping texels."));
                done += 1f;

                // the maps on this part
                var mat = r.sharedMaterial;
                if (mat == null)
                {
                    into.Add(Row("segment", part, "Material", "fail", "No material assigned."));
                    continue;
                }

                foreach (var (map, property) in Slots)
                {
                    if (!mat.HasProperty(property)) continue;
                    var tex = mat.GetTexture(property);

                    bool required = Array.IndexOf(spec.requireMaps, map) >= 0;
                    if (tex == null)
                    {
                        into.Add(required
                            ? Row("segment", part, "Required maps", "fail",
                                  "No " + map + " map. The slot is empty.")
                            : Row("segment", part, map + " map", "unenforced",
                                  "No " + map + " map, which is allowed."));
                        continue;
                    }

                    if (counted.Add(tex)) texels += (long)tex.width * tex.height;

                    int longest = Mathf.Max(tex.width, tex.height);
                    bool pot = longest > 0 && (longest & (longest - 1)) == 0;
                    if (spec.maxTextureSize > 0 && longest > spec.maxTextureSize)
                        into.Add(Row("segment", part, map + " size", "fail",
                                     tex.width + " x " + tex.height + ", over the limit of "
                                     + spec.maxTextureSize + "."));
                    else if (!pot || tex.width != tex.height)
                        into.Add(Row("segment", part, map + " size", "fail",
                                     tex.width + " x " + tex.height
                                     + ": hardware expects square, power-of-two maps."));
                    else
                        into.Add(Row("segment", part, map + " size", "pass",
                                     tex.width + " x " + tex.height + "."));

                    // is this colour map really a normal map?
                    if (map == "diffuse")
                    {
                        Stage("Reading " + part + " colour map");
                        yield return null;

                        float unit = UnitVectorFraction(tex);
                        into.Add(unit > 0.10f
                            ? Row("segment", part, "Map content", "fail",
                                  "This colour map reads as a normal map: "
                                  + Mathf.RoundToInt(unit * 100f) + "% of its pixels decode "
                                  + "to unit-length vectors, which is what a normal map "
                                  + "stores and what artwork never does.")
                            : Row("segment", part, "Map content", "pass",
                                  "Reads as artwork, not as a map in the wrong slot."));
                    }
                }
                done += 1f;
            }

            // --- what the whole thing costs ----------------------------------------
            Stage("Totalling texture memory");
            yield return null;

            if (spec.bitsPerPixel > 0f)
            {
                float mb = texels * spec.bitsPerPixel / 8f
                           * Mathf.Max(1f, spec.mipmapMultiplier) / (1024f * 1024f);
                string how = " across " + counted.Count + " maps, estimated for a compressed "
                             + "mobile build";
                if (spec.maxTextureMemoryMb > 0f)
                {
                    if (mb > spec.maxTextureMemoryMb)
                        into.Add(Row("asset", "glove", "Texture memory", "fail",
                            mb.ToString("0.00") + " MB" + how + ", over the limit of "
                            + spec.maxTextureMemoryMb.ToString("0.##") + "."));
                    else if (spec.warnTextureMemoryMb > 0f && mb > spec.warnTextureMemoryMb)
                        into.Add(Row("asset", "glove", "Texture memory", "warn",
                            mb.ToString("0.00") + " MB" + how + ". Within the limit of "
                            + spec.maxTextureMemoryMb.ToString("0.##") + ", but past "
                            + spec.warnTextureMemoryMb.ToString("0.##")
                            + " -- worth knowing it grew."));
                    else
                        into.Add(Row("asset", "glove", "Texture memory", "pass",
                            mb.ToString("0.00") + " MB" + how + ", within the limit of "
                            + spec.maxTextureMemoryMb.ToString("0.##") + "."));
                }
                else
                    into.Add(Row("asset", "glove", "Texture memory", "unenforced",
                                 mb.ToString("0.00") + " MB" + how + "."));
            }

            done = steps;
            Stage("Done");
        }

        /// <summary>
        /// How many texels more than one triangle covers, rasterised at a coarse
        /// resolution. Spread over frames: a part can carry a couple of thousand
        /// triangles and doing them all at once drops a frame.
        /// </summary>
        private static IEnumerator CountOverlap(Mesh mesh, Action<int> result)
        {
            var uv = mesh.uv;
            var tris = mesh.triangles;
            if (uv == null || uv.Length == 0) { result(0); yield break; }

            var counts = new byte[OverlapResolution * OverlapResolution];
            int overlapping = 0;

            for (int t = 0; t < tris.Length; t += 3)
            {
                if (t % 900 == 0) yield return null;

                Vector2 a = uv[tris[t]] * OverlapResolution;
                Vector2 b = uv[tris[t + 1]] * OverlapResolution;
                Vector2 c = uv[tris[t + 2]] * OverlapResolution;

                int minX = Mathf.Max(Mathf.FloorToInt(Mathf.Min(a.x, b.x, c.x)), 0);
                int maxX = Mathf.Min(Mathf.CeilToInt(Mathf.Max(a.x, b.x, c.x)), OverlapResolution - 1);
                int minY = Mathf.Max(Mathf.FloorToInt(Mathf.Min(a.y, b.y, c.y)), 0);
                int maxY = Mathf.Min(Mathf.CeilToInt(Mathf.Max(a.y, b.y, c.y)), OverlapResolution - 1);
                if (minX > maxX || minY > maxY) continue;

                float den = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
                if (Mathf.Abs(den) < 1e-6f) continue;

                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        float w0 = ((b.y - c.y) * (px - c.x) + (c.x - b.x) * (py - c.y)) / den;
                        float w1 = ((c.y - a.y) * (px - c.x) + (a.x - c.x) * (py - c.y)) / den;
                        if (w0 < 0f || w1 < 0f || 1f - w0 - w1 < 0f) continue;

                        int i = y * OverlapResolution + x;
                        if (counts[i] == 1) overlapping++;
                        if (counts[i] < 2) counts[i]++;
                    }
                }
            }

            result(overlapping);
        }

        /// <summary>
        /// The share of a texture's pixels that decode to unit-length vectors.
        ///
        /// Read back off the GPU, because a shipped texture is not readable from the CPU.
        /// A thumbnail is plenty: this is measuring what kind of data the image holds, not
        /// looking at its detail.
        /// </summary>
        private static float UnitVectorFraction(Texture texture)
        {
            RenderTexture rt = null;
            Texture2D readback = null;
            var previous = RenderTexture.active;
            try
            {
                rt = RenderTexture.GetTemporary(SampleResolution, SampleResolution, 0,
                                                RenderTextureFormat.ARGB32,
                                                RenderTextureReadWrite.Linear);
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;

                readback = new Texture2D(SampleResolution, SampleResolution,
                                         TextureFormat.RGBA32, false, true);
                readback.ReadPixels(new Rect(0, 0, SampleResolution, SampleResolution), 0, 0);
                readback.Apply(false);

                var pixels = readback.GetPixels();
                int unit = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    float x = pixels[i].r * 2f - 1f;
                    float y = pixels[i].g * 2f - 1f;
                    float z = pixels[i].b * 2f - 1f;
                    if (z <= 0f) continue;
                    float len = Mathf.Sqrt(x * x + y * y + z * z);
                    if (Mathf.Abs(len - 1f) < 0.12f) unit++;
                }
                return pixels.Length == 0 ? 0f : (float)unit / pixels.Length;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuntimeValidator] could not read " + texture.name
                                 + ": " + e.Message);
                return 0f;
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (readback != null) UnityEngine.Object.Destroy(readback);
            }
        }
    }
}
