using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace RecastCustomizer.EditorTools
{
    /// <summary>
    /// Recast Customizer ▸ Validate Asset
    ///
    /// Runs the asset checks and shows what they found, without anyone touching a
    /// terminal. The checks themselves live in pipeline/, as Python and headless Blender,
    /// because they have to run unattended in a build as well -- this window is a front
    /// door onto them, not a second implementation.
    /// </summary>
    public class ValidationWindow : EditorWindow
    {
        private const string PythonKey = "recast.validator.python";

        [Serializable] private class Row
        {
            public string scope, target, check, status, message;
        }

        [Serializable] private class Summary
        {
            public int pass, fail, unenforced, info;
        }

        [Serializable] private class Report
        {
            public string asset, profile;
            public Summary summary;
            public Row[] rows;
        }

        private static readonly (string label, string kit, string reportDir)[] Kits =
        {
            ("The glove", "", "pipeline/out"),
            ("The broken demo kit", "pipeline/demo/broken_glove", "pipeline/demo/broken_glove/out"),
        };

        private Process _process;

        // Output is drained as it arrives. Waiting until the program exits and reading it
        // in one go deadlocks: Blender prints enough to fill the pipe, and it then blocks
        // forever waiting for someone to empty it, so the run never finishes.
        private readonly System.Text.StringBuilder _buffer = new System.Text.StringBuilder();
        private readonly object _bufferLock = new object();
        private double _startedAt;

        private string _status = "";
        private string _log = "";
        private Report _report;
        private int _kit;
        private bool _showPassing;
        private Vector2 _scroll;

        [MenuItem("Recast Customizer/Validate Asset")]
        public static void Open()
        {
            var w = GetWindow<ValidationWindow>(false, "Validate Asset");
            w.minSize = new Vector2(540, 420);
            w.LoadReport();
            w.Show();
        }

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string Python
        {
            get
            {
                var saved = EditorPrefs.GetString(PythonKey, "");
                if (!string.IsNullOrEmpty(saved)) return saved;
                var found = FindPython();
                EditorPrefs.SetString(PythonKey, found);
                return found;
            }
            set => EditorPrefs.SetString(PythonKey, value);
        }

        /// <summary>
        /// Where Python actually is.
        ///
        /// Relying on the name alone is fragile: on Windows a Microsoft Store placeholder
        /// answers to "python" and does nothing useful, and an installer can leave the real
        /// one off the PATH entirely. So the usual install folders are tried first, and the
        /// bare name is only the fallback.
        /// </summary>
        private static string FindPython()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new List<string>
            {
                Path.Combine(home, "Python", "bin", "python.exe"),
            };

            var programs = Path.Combine(home, "Programs", "Python");
            if (Directory.Exists(programs))
                foreach (var dir in Directory.GetDirectories(programs))
                    candidates.Add(Path.Combine(dir, "python.exe"));

            foreach (var version in new[] { "314", "313", "312", "311" })
            {
                candidates.Add(@"C:\Python" + version + @"\python.exe");
                candidates.Add(@"C:\Program Files\Python" + version + @"\python.exe");
            }

            candidates.Add("/usr/bin/python3");
            candidates.Add("/usr/local/bin/python3");

            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            return "python";
        }

        // --- running -----------------------------------------------------------

        private void Run()
        {
            if (_process != null) return;

            var kit = Kits[_kit];
            var args = "pipeline/check.py";
            if (!string.IsNullOrEmpty(kit.kit)) args += " \"" + kit.kit + "\"";

            var info = new ProcessStartInfo(Python, args)
            {
                WorkingDirectory = ProjectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            lock (_bufferLock) _buffer.Length = 0;

            try
            {
                _process = Process.Start(info);
                _process.OutputDataReceived += OnOutput;
                _process.ErrorDataReceived += OnOutput;
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            catch (Exception e)
            {
                _status = "Could not start Python (" + Python + "). " + e.Message;
                _process = null;
                return;
            }

            _log = "";
            _report = null;
            _startedAt = EditorApplication.timeSinceStartup;
            _status = "Checking " + kit.label.ToLowerInvariant() + "...";
            EditorApplication.update += Poll;
        }

        private void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (_bufferLock) _buffer.AppendLine(e.Data);
        }

        private void Cancel()
        {
            if (_process == null) return;
            try { _process.Kill(); } catch (Exception) { }
            EditorApplication.update -= Poll;
            _process = null;
            _status = "Stopped.";
            Repaint();
        }

        private void Poll()
        {
            if (_process == null) { EditorApplication.update -= Poll; return; }
            if (!_process.HasExited)
            {
                _status = string.Format("Checking {0}... {1:0}s",
                    Kits[_kit].label.ToLowerInvariant(),
                    EditorApplication.timeSinceStartup - _startedAt);
                Repaint();
                return;
            }

            EditorApplication.update -= Poll;
            lock (_bufferLock) _log = _buffer.ToString();
            int code = _process.ExitCode;
            _process = null;

            LoadReport();
            if (_report == null)
            {
                _status = "The checks ran but no report was produced. See the log below.";
            }
            else
            {
                _status = code == 0
                    ? "Everything passed."
                    : _report.summary.fail + (_report.summary.fail == 1
                        ? " check failed." : " checks failed.");

                // The running tool shows the last result, so keep the copy it reads in step
                // with what was just measured. Only for the glove: the broken demo kit is
                // not what the build is about.
                if (_kit == 0 && ValidationReportExporter.Copy(out string note))
                    _status += "  " + note;
            }
            Repaint();
        }

        private void OnFocus()
        {
            // the report may have been rewritten from the command line meanwhile
            if (_process == null) LoadReport();
        }

        private void LoadReport()
        {
            var path = Path.Combine(ProjectRoot, Kits[_kit].reportDir, "validation.json");
            if (!File.Exists(path)) { _report = null; return; }
            try
            {
                _report = JsonUtility.FromJson<Report>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                _report = null;
                Debug.LogWarning("[Validate] could not read " + path + ": " + e.Message);
            }
        }

        private void OpenHtml()
        {
            var path = Path.Combine(ProjectRoot, Kits[_kit].reportDir, "validation.html");
            if (File.Exists(path)) Application.OpenURL("file:///" + path.Replace("\\", "/"));
            else _status = "No report yet. Run the check first.";
        }

        // --- drawing -----------------------------------------------------------

        private static Color Tint(string status)
        {
            switch (status)
            {
                case "fail": return new Color(0.93f, 0.38f, 0.33f);
                case "pass": return new Color(0.45f, 0.80f, 0.56f);
                default: return new Color(0.85f, 0.73f, 0.36f);
            }
        }

        private static string Label(string status)
        {
            switch (status)
            {
                case "fail": return "FAIL";
                case "pass": return "pass";
                case "unenforced": return "no limit";
                default: return "note";
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                var labels = new string[Kits.Length];
                for (int i = 0; i < Kits.Length; i++) labels[i] = Kits[i].label;

                EditorGUI.BeginChangeCheck();
                _kit = EditorGUILayout.Popup(_kit, labels, GUILayout.Width(200));
                if (EditorGUI.EndChangeCheck()) { LoadReport(); _status = ""; }

                if (_process == null)
                {
                    if (GUILayout.Button("Check it", GUILayout.Height(24))) Run();
                }
                else if (GUILayout.Button("Stop", GUILayout.Height(24)))
                {
                    Cancel();
                }

                using (new EditorGUI.DisabledScope(_report == null))
                {
                    if (GUILayout.Button("Open full report", GUILayout.Width(130),
                                         GUILayout.Height(24)))
                        OpenHtml();
                }
            }

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_status,
                    _report != null && _report.summary.fail > 0 ? MessageType.Warning
                    : _process != null ? MessageType.Info : MessageType.None);
            }

            if (_report != null) DrawReport();
            else DrawEmpty();

            if (!string.IsNullOrEmpty(_log) && _report == null)
            {
                EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
                EditorGUILayout.TextArea(_log, GUILayout.Height(120));
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Python", GUILayout.Width(48));
                var p = EditorGUILayout.TextField(Python);
                if (p != Python) Python = p;
            }
            EditorGUILayout.LabelField(
                "The checks run in Python and headless Blender, so they also run unattended.",
                EditorStyles.miniLabel);
        }

        private void DrawEmpty()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(
                "No report yet. Press Check it.", EditorStyles.centeredGreyMiniLabel);
            GUILayout.FlexibleSpace();
        }

        private void DrawReport()
        {
            var s = _report.summary;
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                Tile(s.pass.ToString(), "passed", Tint("pass"));
                Tile(s.fail.ToString(), "failed", Tint("fail"));
                Tile(s.unenforced.ToString(), "no limit set", Tint("unenforced"));
                Tile(_report.profile ?? "-", "profile", Color.white);
            }

            EditorGUILayout.Space(6);
            _showPassing = EditorGUILayout.ToggleLeft(
                "Show checks that passed", _showPassing);
            EditorGUILayout.Space(2);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            string scope = null;
            foreach (var row in Ordered())
            {
                if (row.scope != scope)
                {
                    scope = row.scope;
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField(Heading(scope), EditorStyles.boldLabel);
                }
                DrawRow(row);
            }
            EditorGUILayout.EndScrollView();
        }

        private IEnumerable<Row> Ordered()
        {
            // failures first: the point of opening this window is to find them
            foreach (var r in _report.rows) if (r.status == "fail") yield return r;
            foreach (var r in _report.rows)
            {
                if (r.status == "fail") continue;
                if (!_showPassing && r.status == "pass") continue;
                yield return r;
            }
        }

        private static string Heading(string scope)
        {
            switch (scope)
            {
                case "asset": return "The kit as a whole";
                case "segment": return "Parts";
                case "variant": return "Variants";
                case "design": return "Designs";
                case "files": return "Files";
                default: return scope;
            }
        }

        private static void Tile(string value, string caption, Color tint)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var style = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 18,
                    normal = { textColor = tint },
                };
                EditorGUILayout.LabelField(value, style, GUILayout.Height(24));
                EditorGUILayout.LabelField(caption, EditorStyles.miniLabel);
            }
        }

        private static void DrawRow(Row row)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var pill = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    normal = { textColor = Tint(row.status) },
                    alignment = TextAnchor.UpperLeft,
                };
                EditorGUILayout.LabelField(Label(row.status), pill, GUILayout.Width(58));
                EditorGUILayout.LabelField(row.target, EditorStyles.miniBoldLabel,
                                           GUILayout.Width(130));
                EditorGUILayout.LabelField(row.check, EditorStyles.miniLabel,
                                           GUILayout.Width(130));
                EditorGUILayout.LabelField(row.message, EditorStyles.wordWrappedMiniLabel);
            }
        }
    }
}
