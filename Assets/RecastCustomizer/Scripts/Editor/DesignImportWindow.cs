using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// What came of an import: every design, every check, in the validator's own words.
    ///
    /// The console would do for one design. It does not do for twenty, which is the case
    /// this exists for -- a content team exports a folder of designs and the useful answer
    /// is "nineteen are fine, this one is 4 MB over", not two hundred log lines to scroll.
    ///
    /// Failures come first and stay open; everything that passed is collapsed behind a
    /// count. Someone who imported twenty designs wants to see the one that is wrong.
    /// </summary>
    public class DesignImportWindow : EditorWindow
    {
        public class Result
        {
            public string design;
            public string folder;
            public List<ValidationRow> rows = new();
            public string error;                 // set when the design could not be imported

            public int Failures => rows.Count(r => r.status == "fail");
            public bool Ok => error == null && Failures == 0;
        }

        private static readonly List<Result> Results = new();
        private Vector2 _scroll;
        private readonly HashSet<string> _expanded = new();

        public static void ShowResults(IEnumerable<Result> results)
        {
            Results.Clear();
            Results.AddRange(results);
            var window = GetWindow<DesignImportWindow>(true, "Design import", true);
            window.minSize = new Vector2(620, 320);
            window.Focus();
        }

        private void OnGUI()
        {
            if (Results.Count == 0)
            {
                EditorGUILayout.HelpBox("Nothing imported yet.", MessageType.Info);
                return;
            }

            int failed = Results.Count(r => !r.Ok);
            int ok = Results.Count - failed;

            EditorGUILayout.Space(6);
            var style = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
            EditorGUILayout.LabelField(
                failed == 0
                    ? ok + (ok == 1 ? " design imported and passed." : " designs imported, all passed.")
                    : failed + " of " + Results.Count + " need attention.",
                style);

            EditorGUILayout.Space(4);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            // Anything wrong first. On a large batch the failures are the whole point.
            foreach (var result in Results.Where(r => !r.Ok)) Draw(result, true);
            foreach (var result in Results.Where(r => r.Ok)) Draw(result, false);

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Copy as text", GUILayout.Height(24)))
                    EditorGUIUtility.systemCopyBuffer = AsText();
                if (GUILayout.Button("Close", GUILayout.Height(24))) Close();
            }
            EditorGUILayout.Space(4);
        }

        private void Draw(Result result, bool openByDefault)
        {
            string key = result.design;
            bool open = _expanded.Contains(key) || (openByDefault && !_expanded.Contains("!" + key));

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    int warnings = result.rows.Count(r => r.status == "warn");
                    string mark = result.error != null ? "not imported"
                                : result.Failures > 0
                                    ? result.Failures + (result.Failures == 1 ? " failure" : " failures")
                                : warnings > 0
                                    ? "passed, " + warnings + " worth a look"
                                : "passed";

                    if (GUILayout.Button((open ? "▾  " : "▸  ") + result.design + "   —   " + mark,
                            EditorStyles.boldLabel))
                    {
                        if (open) { _expanded.Remove(key); _expanded.Add("!" + key); }
                        else { _expanded.Add(key); _expanded.Remove("!" + key); }
                    }

                    if (!string.IsNullOrEmpty(result.folder) &&
                        GUILayout.Button("Show", GUILayout.Width(60)))
                    {
                        var asset = AssetDatabase.LoadAssetAtPath<Object>(result.folder);
                        if (asset != null) EditorGUIUtility.PingObject(asset);
                    }
                }

                if (!open) return;

                if (result.error != null)
                {
                    EditorGUILayout.HelpBox(result.error, MessageType.Error);
                    return;
                }

                foreach (var row in result.rows.OrderBy(
                             r => r.status == "fail" ? 0 : r.status == "warn" ? 1 : 2))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var colour = GUI.color;
                        if (row.status == "fail") GUI.color = new Color(1f, 0.5f, 0.45f);
                        else if (row.status == "warn") GUI.color = new Color(1f, 0.78f, 0.35f);
                        EditorGUILayout.LabelField(
                            row.status == "fail" ? "fail" : row.status == "warn" ? "note" : "ok",
                            GUILayout.Width(36));
                        GUI.color = colour;
                        EditorGUILayout.LabelField(row.check, GUILayout.Width(120));
                        EditorGUILayout.LabelField(Subject(row, result.design),
                            EditorStyles.miniLabel, GUILayout.Width(190));
                        EditorGUILayout.LabelField(row.message, EditorStyles.wordWrappedLabel);
                    }
                }
            }
        }

        /// <summary>The texture or material a row is about, where it is about one.</summary>
        private static string Subject(ValidationRow row, string designName)
        {
            if (string.IsNullOrEmpty(row.target) || row.target == designName) return "";
            int cut = row.target.IndexOf(" / ", System.StringComparison.Ordinal);
            return cut >= 0 ? row.target.Substring(cut + 3) : row.target;
        }

        private static string AsText()
        {
            var text = new System.Text.StringBuilder();
            foreach (var result in Results)
            {
                text.AppendLine(result.design + (result.error != null
                    ? "  —  not imported: " + result.error
                    : result.Failures == 0 ? "  —  passed"
                    : "  —  " + result.Failures + " failure(s)"));
                foreach (var row in result.rows)
                    text.AppendLine("  " + row.status.PadRight(5) + row.check.PadRight(18)
                                    + Subject(row, result.design).PadRight(32) + row.message);
                text.AppendLine();
            }
            return text.ToString();
        }
    }
}
