using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Checks an imported design against the same limits the authored kit is held to.
    ///
    /// The importer could already build a design that costs more than the budget allows and
    /// say nothing about it. The cost would surface weeks later, in a build, attributed to
    /// nobody. This closes that: a design is measured as it enters the project, and the
    /// person who imported it is told before they move on.
    ///
    /// It deliberately does not shell out to the offline pipeline. Those checks need Python
    /// and headless Blender and take the better part of a minute, which is the wrong price
    /// for something that should run on every import. Everything here is read from the
    /// AssetDatabase and the texture importers, so it is immediate and has no dependencies.
    ///
    /// The limits come from the same <c>validation_spec.json</c> the offline validator
    /// writes, so the two cannot drift into describing different rules. What differs is only
    /// the scope: this judges one design, not the whole kit.
    /// </summary>
    public static class DesignValidator
    {
        /// <summary>Shader slots a required map name corresponds to, in preference order.</summary>
        private static readonly Dictionary<string, string[]> Slots = new()
        {
            { "diffuse", new[] { "_BaseMap", "_MainTex" } },
            { "normal",  new[] { "_BumpMap" } },
            { "specular", new[] { "_SpecGlossMap" } },
            { "occlusion", new[] { "_OcclusionMap" } },
        };

        /// <summary>Every slot worth counting towards memory, whether or not it is required.</summary>
        private static readonly string[] CountedSlots =
            { "_BaseMap", "_MainTex", "_BumpMap", "_SpecGlossMap", "_OcclusionMap" };

        /// <summary>
        /// What a design will cost, before anything is written.
        ///
        /// Worth separating from the full check: a confirmation dialog that can say "this
        /// will add 8.3 MB" lets someone decide, where "importing..." does not.
        /// </summary>
        public static float EstimateMegabytes(IEnumerable<Material> materials,
                                              RuntimeValidator.Spec spec)
        {
            return Megabytes(UniqueTextures(materials), spec);
        }

        /// <summary>
        /// Checks one design and returns a row per check, in the validator's own language.
        /// </summary>
        /// <param name="designName">How the design is named in reports.</param>
        /// <param name="folder">The design's own folder, or null when nothing was written.</param>
        /// <param name="materials">One material per segment, authored or generated.</param>
        public static List<ValidationRow> Check(string designName, string folder,
                                                IList<Material> materials,
                                                RuntimeValidator.Spec spec)
        {
            var rows = new List<ValidationRow>();
            var present = materials.Where(m => m != null).ToList();

            // --- materials ---------------------------------------------------------
            // Structural rather than a budget: one material per segment, and the asset has
            // four segments. A design with more means something was created that should
            // have been referenced.
            int distinct = present.Select(m => m.GetInstanceID()).Distinct().Count();
            rows.Add(Row(designName, "Material count",
                spec.maxMaterialsPerDesign < 0 || distinct <= spec.maxMaterialsPerDesign,
                spec.maxMaterialsPerDesign < 0
                    ? distinct + " materials; no limit set."
                    : distinct + " materials, " +
                      (distinct <= spec.maxMaterialsPerDesign ? "within" : "over") +
                      " the limit of " + spec.maxMaterialsPerDesign + "."));

            // --- texture memory ----------------------------------------------------
            var textures = UniqueTextures(present);
            float mb = Megabytes(textures, spec);
            string cost = mb.ToString("0.00") + " MB across " + textures.Count + " maps" +
                (spec.bitsPerPixel > 0 ? " (" + spec.bitsPerPixel.ToString("0.##") +
                    " bits per pixel, with mips)" : "");

            if (spec.maxTextureMemoryMb < 0)
                rows.Add(Row(designName, "Texture memory", true, cost + "; no limit set."));
            else if (mb > spec.maxTextureMemoryMb)
                rows.Add(Row(designName, "Texture memory", false,
                    cost + ", over the limit of " +
                    spec.maxTextureMemoryMb.ToString("0.##") + "."));
            else if (spec.warnTextureMemoryMb > 0 && mb > spec.warnTextureMemoryMb)
                rows.Add(Warn(designName, "Texture memory",
                    cost + ". Within the limit of " +
                    spec.maxTextureMemoryMb.ToString("0.##") + ", but past " +
                    spec.warnTextureMemoryMb.ToString("0.##") + " -- worth knowing."));
            else
                rows.Add(Row(designName, "Texture memory", true,
                    cost + ", within the limit of " +
                    spec.maxTextureMemoryMb.ToString("0.##") + "."));

            // --- texture sizes -----------------------------------------------------
            // The size the importer will build, not the size of the file on disk. A 2048
            // source capped at 1024 ships as 1024 and costs a quarter as much, and the two
            // numbers disagree often enough to be worth saying which one this is.
            foreach (var texture in textures)
            {
                int built = BuiltSize(texture);
                rows.Add(Row(designName + " / " + texture.name, "Texture size",
                    spec.maxTextureSize < 0 || built <= spec.maxTextureSize,
                    built + " px as built" +
                    (spec.maxTextureSize < 0 ? "; no limit set."
                        : ", " + (built <= spec.maxTextureSize ? "within" : "over") +
                          " the limit of " + spec.maxTextureSize + ".")));
            }

            // --- required maps -----------------------------------------------------
            foreach (var material in present)
            {
                var missing = new List<string>();
                foreach (var required in spec.requireMaps ?? Array.Empty<string>())
                {
                    if (!Slots.TryGetValue(required, out var slots)) continue;
                    bool found = slots.Any(s => material.HasProperty(s) &&
                                                material.GetTexture(s) != null);
                    if (!found) missing.Add(required);
                }
                rows.Add(Row(designName + " / " + material.name, "Required maps",
                    missing.Count == 0,
                    missing.Count == 0
                        ? "All required maps present."
                        : "No " + string.Join(" or ", missing) + " map."));
            }

            // --- what was written --------------------------------------------------
            // A file in the design's folder that none of its materials reference will still
            // be imported and still weigh on the repository, and nobody can tell later
            // whether it was abandoned or forgotten.
            if (!string.IsNullOrEmpty(folder) && AssetDatabase.IsValidFolder(folder))
            {
                var referenced = new HashSet<string>(
                    textures.Select(AssetDatabase.GetAssetPath));
                var orphans = Directory
                    .GetFiles(ToAbsolute(folder), "*.png", SearchOption.TopDirectoryOnly)
                    .Select(p => folder + "/" + Path.GetFileName(p))
                    .Where(p => !referenced.Contains(p))
                    .ToList();

                rows.Add(Row(designName, "Unused textures", orphans.Count == 0,
                    orphans.Count == 0
                        ? "Every texture written is used by this design."
                        : orphans.Count + " written but unused: " +
                          string.Join(", ", orphans.Select(Path.GetFileName))));
            }

            return rows;
        }

        /// <summary>
        /// Leaves the checks beside the design they describe.
        ///
        /// A design folder that carries its own report can be zipped, handed on or reviewed
        /// without the project it came from, and a result nobody can find later is a result
        /// nobody acts on. Plain text on purpose: the reader is a person.
        /// </summary>
        public static void WriteReport(string folder, string designName,
                                       IList<ValidationRow> rows)
        {
            int failures = rows.Count(r => r.status == "fail");
            int warnings = rows.Count(r => r.status == "warn");

            var text = new System.Text.StringBuilder();
            text.AppendLine(designName);
            text.AppendLine("checked " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            text.AppendLine(failures > 0
                ? failures + " check(s) failed"
                : warnings > 0 ? "passed, " + warnings + " worth a look"
                : "passed every check");
            text.AppendLine();
            foreach (var row in rows)
                text.AppendLine(row.status.PadRight(6) + row.check.PadRight(18)
                                + Subject(row, designName).PadRight(32) + row.message);

            var path = folder + "/validation.txt";
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), path),
                    text.ToString());
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            catch (IOException e)
            {
                Debug.LogWarning("[Design] Could not write " + path + ": " + e.Message);
            }
        }

        /// <summary>
        /// Which texture or material a row is about, when it is about one in particular.
        ///
        /// Fourteen lines reading "2048 px as built" say nothing about which texture was
        /// measured, and a report nobody can act on is a report nobody reads.
        /// </summary>
        public static string Subject(ValidationRow row, string designName)
        {
            if (string.IsNullOrEmpty(row.target) || row.target == designName) return "";
            int cut = row.target.IndexOf(" / ", StringComparison.Ordinal);
            return cut >= 0 ? row.target.Substring(cut + 3) : row.target;
        }

        public static ValidationSummary Summarise(IEnumerable<ValidationRow> rows)
        {
            var summary = new ValidationSummary();
            foreach (var row in rows)
            {
                if (row.status == "fail") summary.fail++;
                else summary.pass++;
            }
            return summary;
        }

        // ------------------------------------------------------------------ internals

        private static List<Texture> UniqueTextures(IEnumerable<Material> materials)
        {
            var seen = new Dictionary<int, Texture>();
            foreach (var material in materials)
            {
                if (material == null) continue;
                foreach (var slot in CountedSlots)
                {
                    if (!material.HasProperty(slot)) continue;
                    var texture = material.GetTexture(slot);
                    if (texture != null) seen[texture.GetInstanceID()] = texture;
                }
            }
            return seen.Values.ToList();
        }

        private static float Megabytes(IList<Texture> textures, RuntimeValidator.Spec spec)
        {
            float bpp = spec.bitsPerPixel > 0 ? spec.bitsPerPixel : 8f;
            float mips = spec.mipmapMultiplier > 0 ? spec.mipmapMultiplier : 1f;
            float bytes = 0f;
            foreach (var texture in textures)
            {
                int size = BuiltSize(texture);
                bytes += size * (float)size * bpp / 8f * mips;
            }
            return bytes / (1024f * 1024f);
        }

        /// <summary>
        /// The longest edge this texture will have in a build.
        ///
        /// Read from the DefaultTexturePlatform settings rather than the importer's own
        /// maxTextureSize. Unity does not keep the two in step -- the second has been seen
        /// reading 1024 on textures the editor reports as 2048 -- so the other one gives the
        /// wrong answer.
        /// </summary>
        private static int BuiltSize(Texture texture)
        {
            int longest = Mathf.Max(texture.width, texture.height);
            var path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path)) return longest;
            if (AssetImporter.GetAtPath(path) is not TextureImporter importer) return longest;

            var settings = importer.GetPlatformTextureSettings("DefaultTexturePlatform");
            int cap = settings != null && settings.maxTextureSize > 0
                ? settings.maxTextureSize
                : importer.maxTextureSize;
            return cap > 0 ? Mathf.Min(longest, cap) : longest;
        }

        private static string ToAbsolute(string assetPath)
        {
            return Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")), assetPath);
        }

        /// <summary>A pass that is worth mentioning. Never counted as a failure.</summary>
        private static ValidationRow Warn(string target, string check, string message)
        {
            var row = Row(target, check, true, message);
            row.status = "warn";
            return row;
        }

        private static ValidationRow Row(string target, string check, bool ok, string message)
        {
            return new ValidationRow
            {
                scope = "design",
                target = target,
                check = check,
                status = ok ? "pass" : "fail",
                message = message,
            };
        }
    }
}
