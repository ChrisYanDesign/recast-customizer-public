using System;
using System.Collections.Generic;
using UnityEngine;

namespace RecastCustomizer
{
    /// <summary>
    /// The last result the asset checks produced, as the running tool sees it.
    ///
    /// The checks themselves need Python and Blender, so they cannot run in a browser.
    /// What ships instead is the answer they wrote down: a small file copied into
    /// Resources before the build, which the tool reads and displays. So the panel always
    /// reports a real result, measured on a real machine -- it just cannot take a new
    /// measurement while someone is looking at it.
    /// </summary>
    [Serializable]
    public class ValidationReport
    {
        public const string ResourceName = "validation_report";

        /// <summary>
        /// The same checks run against a deliberately broken copy of the asset.
        ///
        /// Shipping it means a visitor can see the tool catch real problems, not just
        /// report that everything is fine. A clean result on its own proves nothing about
        /// whether the checks work.
        /// </summary>
        public const string BrokenResourceName = "validation_report_broken";

        public string asset;
        public string profile;
        public ValidationSummary summary = new();
        public ValidationRow[] rows = Array.Empty<ValidationRow>();

        /// <summary>A report shipped with this build, or null when none was copied in.</summary>
        public static ValidationReport Load(string resourceName = ResourceName)
        {
            var text = Resources.Load<TextAsset>(resourceName);
            if (text == null) return null;
            try
            {
                var report = JsonUtility.FromJson<ValidationReport>(text.text);
                if (report != null && report.rows == null) report.rows = Array.Empty<ValidationRow>();
                return report;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ValidationReport] could not read the report: " + e.Message);
                return null;
            }
        }

        public bool Passed => summary != null && summary.fail == 0;

        public IEnumerable<ValidationRow> Failures
        {
            get
            {
                foreach (var r in rows) if (r != null && r.status == "fail") yield return r;
            }
        }

        /// <summary>One measured figure, for the headline. Null when the row is absent.</summary>
        public string Figure(string check)
        {
            foreach (var r in rows)
                if (r != null && r.check == check)
                    return r.message;
            return null;
        }
    }

    [Serializable]
    public class ValidationSummary
    {
        public int pass;
        public int fail;
        public int unenforced;
        public int info;
    }

    [Serializable]
    public class ValidationRow
    {
        public string scope;
        public string target;
        public string check;
        public string status;
        public string message;
    }
}
