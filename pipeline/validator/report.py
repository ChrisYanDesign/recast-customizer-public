"""Render the validation results as a single self-contained HTML page.

The report is read by artists, not only by whoever wrote the checks, so every row says
what was measured and what that means in words. It is also a portfolio artifact in its own
right -- it can be hosted as-is -- so it carries no external files.
"""

import datetime
import html
import os

SCOPE_TITLES = [
    ("asset", "The kit as a whole", "Checks that apply once, to the whole glove."),
    ("segment", "Parts", "One row per part of the glove, from its mesh and UVs."),
    ("variant", "Variants", "One row per part-and-variant pair, from the textures on disk."),
    ("design", "Designs", "What a finished glove costs: one variant across every part."),
    ("files", "Files", "Whether the asset folder matches the naming convention."),
]

STATUS_LABEL = {
    "pass": "Pass",
    "fail": "Fail",
    "unenforced": "No limit",
    "info": "Note",
}

CSS = """
:root{
  --bg:#f6f5f2; --panel:#fffefb; --ink:#1d1c19; --muted:#6c6a63; --line:#e2dfd7;
  --pass:#2f6f4f; --pass-bg:#e6f0e9; --fail:#a3342a; --fail-bg:#f8e7e4;
  --open:#7a6520; --open-bg:#f4eeda; --accent:#2f5d7c;
}
@media (prefers-color-scheme:dark){
  :root:not([data-theme="light"]){
    --bg:#15171a; --panel:#1d2024; --ink:#e8e6e1; --muted:#9a978f; --line:#2e3237;
    --pass:#7fc29b; --pass-bg:#1c2a23; --fail:#e38a7e; --fail-bg:#2e1f1d;
    --open:#d8c07f; --open-bg:#2a2518; --accent:#8fbcd9;
  }
}
*{box-sizing:border-box}
body{margin:0;padding:0 16px 64px;background:var(--bg);color:var(--ink);
  font:15px/1.55 ui-sans-serif,-apple-system,"Segoe UI",Roboto,sans-serif}
.wrap{max-width:1040px;margin:0 auto}
header{padding:40px 0 24px;border-bottom:1px solid var(--line)}
h1{margin:0 0 6px;font-size:28px;letter-spacing:-.01em}
.sub{color:var(--muted);margin:0;font-size:14px}
.tiles{display:flex;flex-wrap:wrap;gap:10px;margin:22px 0 0}
.tile{flex:1 1 140px;background:var(--panel);border:1px solid var(--line);
  border-radius:8px;padding:12px 14px}
.tile b{display:block;font-size:24px;font-variant-numeric:tabular-nums;line-height:1.2}
.tile span{color:var(--muted);font-size:12px;text-transform:uppercase;letter-spacing:.06em}
.tile.fail b{color:var(--fail)} .tile.pass b{color:var(--pass)} .tile.open b{color:var(--open)}
section{margin:34px 0 0}
h2{font-size:17px;margin:0 0 2px}
.note{color:var(--muted);font-size:13px;margin:0 0 12px}
.scroll{overflow-x:auto;border:1px solid var(--line);border-radius:8px;background:var(--panel)}
table{border-collapse:collapse;width:100%;font-size:14px}
th{text-align:left;font-size:11px;letter-spacing:.07em;text-transform:uppercase;
  color:var(--muted);font-weight:600;padding:10px 14px;border-bottom:1px solid var(--line)}
td{padding:11px 14px;border-top:1px solid var(--line);vertical-align:top;
  overflow-wrap:anywhere}
tr:first-child td{border-top:none}
.target{font-weight:600;white-space:nowrap}
.check{white-space:nowrap;color:var(--muted)}
.pill{display:inline-block;padding:2px 9px;border-radius:999px;font-size:11px;
  font-weight:700;letter-spacing:.04em;white-space:nowrap}
.pill.pass{color:var(--pass);background:var(--pass-bg)}
.pill.fail{color:var(--fail);background:var(--fail-bg)}
.pill.unenforced,.pill.info{color:var(--open);background:var(--open-bg)}
footer{margin:40px 0 0;padding-top:16px;border-top:1px solid var(--line);
  color:var(--muted);font-size:13px}
code{font:13px ui-monospace,Consolas,monospace;color:var(--accent)}
"""


def _rows_html(rows):
    out = []
    for r in rows:
        status = r["status"]
        out.append(
            "<tr><td class='target'>%s</td><td class='check'>%s</td>"
            "<td><span class='pill %s'>%s</span></td><td>%s</td></tr>"
            % (html.escape(str(r["target"])), html.escape(r["check"]),
               status, STATUS_LABEL[status], html.escape(r["message"]))
        )
    return "\n".join(out)


def write_html(path, manifest, spec, rows, counts):
    stamp = datetime.datetime.now().strftime("%d %B %Y, %H:%M")
    asset = manifest.get("displayName") or manifest["asset"]

    sections = []
    for scope, title, blurb in SCOPE_TITLES:
        scoped = [r for r in rows if r["scope"] == scope]
        if not scoped:
            continue
        sections.append(
            "<section><h2>%s</h2><p class='note'>%s</p><div class='scroll'><table>"
            "<thead><tr><th>Target</th><th>Check</th><th>Result</th><th>What it means</th>"
            "</tr></thead><tbody>%s</tbody></table></div></section>"
            % (html.escape(title), html.escape(blurb), _rows_html(scoped))
        )

    unset = [k for k, v in spec["limits"].items() if v is None]
    if unset:
        footnote = ("%d limit(s) are not set yet, so those rows report a measurement "
                    "without judging it: %s." % (len(unset), ", ".join(sorted(unset))))
    else:
        footnote = "Every limit in the spec is set."

    doc = """<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>%(asset)s — validation report</title>
<style>%(css)s</style></head>
<body><div class="wrap">
<header>
  <h1>%(asset)s</h1>
  <p class="sub">Validation report against the <strong>%(profile)s</strong> profile · %(stamp)s</p>
  <div class="tiles">
    <div class="tile pass"><b>%(pass)d</b><span>Passed</span></div>
    <div class="tile fail"><b>%(fail)d</b><span>Failed</span></div>
    <div class="tile open"><b>%(open)d</b><span>No limit set</span></div>
    <div class="tile"><b>%(tris)s</b><span>Triangles</span></div>
    <div class="tile"><b>%(combos)s</b><span>Combinations</span></div>
  </div>
</header>
%(sections)s
<footer>
  <p>%(footnote)s</p>
  <p>Generated by <code>pipeline/validator/validate.py</code> from
  <code>glove_manifest.json</code> and measurements taken in headless Blender.</p>
</footer>
</div></body></html>
""" % {
        "asset": html.escape(asset),
        "css": CSS,
        "profile": html.escape(str(spec.get("profile", "unnamed"))),
        "stamp": stamp,
        "pass": counts["pass"],
        "fail": counts["fail"],
        "open": counts["unenforced"],
        "tris": "{:,}".format(manifest.get("total_triangles", 0)),
        "combos": "{:,}".format(_combinations(manifest)),
        "sections": "\n".join(sections),
        "footnote": html.escape(footnote),
    }

    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(doc)


def _combinations(manifest):
    total = 1
    for seg in manifest["segments"]:
        total *= max(1, len(seg["variants"]))
    return total
