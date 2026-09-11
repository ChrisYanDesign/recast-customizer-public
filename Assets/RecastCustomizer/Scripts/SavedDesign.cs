using System;
using System.Collections.Generic;
using UnityEngine;

namespace RecastCustomizer
{
    /// <summary>
    /// A whole design the content team made and decided to keep.
    ///
    /// This is the unit the pipeline actually carries. The three authored presets are the
    /// artist's templates; a SavedDesign is what somebody built out of them -- a choice of
    /// variant per part, plus whatever look adjustments were made on top -- and it is what
    /// travels to Unity as a file.
    ///
    /// Every part is recorded, not only the adjusted ones. That is the whole difference
    /// between this and the review notes: notes describe a change, a design describes a
    /// finished thing. The <see cref="SavedDesignPart.tuned"/> flag is what lets the importer
    /// tell the two apart afterwards, so it can reuse an existing material for a part that
    /// was merely swapped and only write a new asset where one is genuinely needed.
    /// </summary>
    [Serializable]
    public class SavedDesign
    {
        public string name = "";
        public string asset = "";
        public string combination = "";
        public string savedUtc = "";
        public List<SavedDesignPart> parts = new();

        /// <summary>
        /// How many new textures this design would cost if it were imported.
        ///
        /// Smoothness and normal strength are material values and cost nothing. Hue,
        /// saturation and brightness cannot be expressed as a material value on a stock Lit
        /// shader, so each part that carries one has to become pixels -- a new map at the
        /// source resolution. That is the expensive half of the five knobs, and the number a
        /// content team should be able to see before it saves.
        /// </summary>
        public int BakeCount
        {
            get
            {
                int n = 0;
                foreach (var p in parts) if (p != null && p.NeedsBake) n++;
                return n;
            }
        }

        /// <summary>Parts that differ from the artist's material and so need a new asset.</summary>
        public int TunedCount
        {
            get
            {
                int n = 0;
                foreach (var p in parts) if (p != null && p.tuned) n++;
                return n;
            }
        }

        public SavedDesignPart Part(string segmentId)
        {
            foreach (var p in parts) if (p != null && p.segment == segmentId) return p;
            return null;
        }
    }

    /// <summary>One part of a saved design: which variant it uses, and how it was adjusted.</summary>
    [Serializable]
    public class SavedDesignPart
    {
        public string segment = "";      // stable segment id, e.g. "top"
        public string variant = "";      // variant id, e.g. "02"
        public int variantIndex;         // resolved index, a hint only -- variant id wins

        [Tooltip("False when the part was only swapped. The importer reuses the artist's " +
                 "material in that case and writes nothing.")]
        public bool tuned;

        public float hue = 0f;
        public float saturation = 1f;
        public float brightness = 1f;
        public float smoothness = 0.5f;
        public float normal = 1f;

        /// <summary>
        /// True when the colour was moved, which is the case that costs a texture. Kept as
        /// one property so the tool, the exporter and the importer cannot disagree about
        /// what counts as a colour change.
        /// </summary>
        public bool NeedsBake =>
            !Mathf.Approximately(hue, 0f)
            || !Mathf.Approximately(saturation, 1f)
            || !Mathf.Approximately(brightness, 1f);
    }

    /// <summary>
    /// The designs kept during this session, and across the next one.
    ///
    /// A web build cannot write to the filesystem, but PlayerPrefs works there -- Unity backs
    /// it with the browser's own storage -- so a content designer who reloads the page does
    /// not lose an afternoon of work. That is worth the few lines it costs.
    ///
    /// This is deliberately not the handoff mechanism. Browser storage reaches nobody else;
    /// exporting the file is what moves a design to another person.
    /// </summary>
    /// <summary>
    /// Designs the person using the tool has dismissed from the list.
    ///
    /// These are designs that already exist in the variant set, imported into the project by
    /// an engineer. The tool cannot delete a project asset -- it has no path to one, by
    /// design -- so clicking the X hides the design from the list instead and remembers that
    /// choice. Removing the assets themselves is the editor's job, through
    /// Recast Customizer > Remove Imported Design.
    ///
    /// Hiding rather than deleting is also the safer default: a content designer dismissing a
    /// design they are not working on should not be able to destroy an engineer's work.
    /// </summary>
    public static class HiddenDesigns
    {
        private const string Key = "recast.hiddendesigns.v1";
        private static HashSet<string> _hidden;

        private static HashSet<string> All
        {
            get
            {
                if (_hidden != null) return _hidden;
                _hidden = new HashSet<string>();
                try
                {
                    var raw = PlayerPrefs.GetString(Key, "");
                    if (!string.IsNullOrEmpty(raw))
                        foreach (var id in raw.Split('|'))
                            if (!string.IsNullOrEmpty(id)) _hidden.Add(id);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[HiddenDesigns] Could not read: " + e.Message);
                }
                return _hidden;
            }
        }

        public static bool IsHidden(string id) =>
            !string.IsNullOrEmpty(id) && All.Contains(id);

        public static void Hide(string id)
        {
            if (string.IsNullOrEmpty(id) || !All.Add(id)) return;
            Flush();
        }

        public static void ShowAll()
        {
            All.Clear();
            Flush();
        }

        private static void Flush()
        {
            try
            {
                PlayerPrefs.SetString(Key, string.Join("|", new List<string>(All).ToArray()));
                PlayerPrefs.Save();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[HiddenDesigns] Could not save: " + e.Message);
            }
        }
    }

    public static class DesignLibrary
    {
        private const string Key = "recast.saveddesigns.v1";
        private const int MaxDesigns = 24;

        [Serializable]
        private class Wrapper { public List<SavedDesign> designs = new(); }

        private static List<SavedDesign> _cache;

        public static List<SavedDesign> All
        {
            get
            {
                if (_cache != null) return _cache;
                _cache = new List<SavedDesign>();
                try
                {
                    var raw = PlayerPrefs.GetString(Key, "");
                    if (!string.IsNullOrEmpty(raw))
                    {
                        var w = JsonUtility.FromJson<Wrapper>(raw);
                        if (w?.designs != null) _cache = w.designs;
                    }
                }
                catch (Exception e)
                {
                    // Corrupt or unreadable storage is not worth failing the tool over. An
                    // empty library is a perfectly good starting state.
                    Debug.LogWarning("[DesignLibrary] Could not read saved designs: " + e.Message);
                    _cache = new List<SavedDesign>();
                }
                return _cache;
            }
        }

        public static int Count => All.Count;

        public static SavedDesign At(int index) =>
            (index >= 0 && index < All.Count) ? All[index] : null;

        /// <summary>
        /// Append, never insert.
        ///
        /// Designs are numbered by their position in this list, so putting a new one at the
        /// front would renumber every design already saved. Design 4 has to stay design 4
        /// after design 5 exists, or a name written in a ticket stops meaning anything.
        /// </summary>
        public static bool Add(SavedDesign design)
        {
            if (design == null) return false;
            if (All.Count >= MaxDesigns)
            {
                Debug.LogWarning($"[DesignLibrary] {MaxDesigns} designs is the limit. Export the "
                               + "ones worth keeping, then clear some out.");
                return false;
            }
            All.Add(design);
            Flush();
            return true;
        }

        public static void RemoveAt(int index)
        {
            if (index < 0 || index >= All.Count) return;
            All.RemoveAt(index);
            Flush();
        }

        public static void Clear()
        {
            All.Clear();
            Flush();
        }

        /// <summary>
        /// A name nobody has used yet. "Design 4" is not something anyone can reference in a
        /// ticket, but it is a far better default than an empty field, and it keeps the save
        /// button one click rather than a form.
        /// </summary>
        public static string SuggestName(int authoredPresets)
        {
            for (int n = authoredPresets + 1; n < authoredPresets + 200; n++)
            {
                string candidate = "DESIGN " + n;
                bool taken = false;
                foreach (var d in All)
                    if (d != null && string.Equals(d.name, candidate, StringComparison.OrdinalIgnoreCase))
                    { taken = true; break; }
                if (!taken) return candidate;
            }
            return "DESIGN";
        }

        private static void Flush()
        {
            try
            {
                PlayerPrefs.SetString(Key, JsonUtility.ToJson(new Wrapper { designs = All }));
                PlayerPrefs.Save();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DesignLibrary] Could not save designs: " + e.Message);
            }
        }
    }
}
