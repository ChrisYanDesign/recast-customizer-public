"""Check the glove kit against the spec and write the reports.

    python pipeline/validator/validate.py [--spec path] [--quiet]

Exit code is 1 when any check fails, so this can run in automation.

Every check reports a number whether or not a limit is set for it. A limit left null in
the spec is reported as *not enforced*: the measurement still appears, which is how the
limits get chosen in the first place.
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.realpath(__file__))))

import common  # noqa: E402
from report import write_html  # noqa: E402

PASS, FAIL, UNENFORCED, INFO = "pass", "fail", "unenforced", "info"
# Passes, but says so. For a figure inside the budget and far enough above what the
# asset costs today that somebody should know it moved.
WARN = "warn"


class Results:
    """Collected check results, in the order they were run."""

    def __init__(self):
        self.rows = []

    def add(self, scope, target, check, status, message, measured=None, limit=None):
        self.rows.append({
            "scope": scope,
            "target": target,
            "check": check,
            "status": status,
            "message": message,
            "measured": measured,
            "limit": limit,
        })

    def limited(self, scope, target, check, measured, limit, unit,
                worse=lambda m, l: m > l, phrase="over the limit"):
        """Compare a measurement against a limit that may not be set yet."""
        if limit is None:
            self.add(scope, target, check, UNENFORCED,
                     "%s %s. No limit set yet." % (fmt(measured), unit),
                     measured, limit)
        elif worse(measured, limit):
            self.add(scope, target, check, FAIL,
                     "%s %s, which is %s of %s." % (fmt(measured), unit, phrase, fmt(limit)),
                     measured, limit)
        else:
            self.add(scope, target, check, PASS,
                     "%s %s, within the limit of %s." % (fmt(measured), unit, fmt(limit)),
                     measured, limit)

    def counts(self):
        c = {PASS: 0, FAIL: 0, WARN: 0, UNENFORCED: 0, INFO: 0}
        for r in self.rows:
            c[r["status"]] += 1
        return c


def fmt(v):
    if isinstance(v, float):
        return ("%.2f" % v).rstrip("0").rstrip(".")
    if isinstance(v, int):
        return "{:,}".format(v)
    return str(v)


def is_power_of_two(n):
    return n > 0 and (n & (n - 1)) == 0


def override(spec, segment, key, fallback=None):
    """A per-part limit from the spec, falling back to the global one."""
    seg = (spec.get("segment_overrides") or {}).get(segment)
    if isinstance(seg, dict) and key in seg:
        return seg[key]
    return fallback


# --- checks ------------------------------------------------------------------


def check_asset(manifest, spec, res):
    limits = spec["limits"]

    mesh = os.path.join(common.PROJECT, manifest.get("source_mesh", ""))
    if manifest.get("source_mesh") and os.path.exists(mesh):
        res.add("asset", manifest["asset"], "Source mesh present", PASS,
                "Found at %s." % manifest["source_mesh"])
    else:
        res.add("asset", manifest["asset"], "Source mesh present", FAIL,
                "The mesh named in the manifest is not on disk. Re-run the measure step.")

    res.limited("asset", manifest["asset"], "Triangle count",
                manifest.get("total_triangles", 0), limits["max_triangles_per_asset"],
                "triangles")

    combos = 1
    for seg in manifest["segments"]:
        combos *= max(1, len(seg["variants"]))
    res.add("asset", manifest["asset"], "Combinations", INFO,
            "%s distinct assemblies can be built from %d parts."
            % (fmt(combos), len(manifest["segments"])), combos)


def check_segments(manifest, spec, res):
    limits = spec["limits"]

    for seg in manifest["segments"]:
        sid = seg["id"]

        res.limited("segment", sid, "Triangle count", seg["triangles"],
                    limits["max_triangles_per_segment"], "triangles")

        overlap = seg.get("uv_overlap_texels", 0)
        limit = limits["max_uv_overlap_texels"]

        if override(spec, sid, "uv_overlap_allowed", False):
            # Mirroring was declared for this part, so sharing texture space is the
            # intent rather than a mistake. Still reported, so the number stays visible.
            res.add("segment", sid, "UV overlap", PASS,
                    "%s overlapping texels. This part is declared as mirrored, so faces "
                    "sharing texture space is intended here." % fmt(overlap), overlap, None)
        elif limit is None:
            status, msg = (UNENFORCED,
                           "%s texels are covered by more than one face. No limit set yet."
                           % fmt(overlap)) if overlap else (
                          PASS, "No faces share texture space.")
            res.add("segment", sid, "UV overlap", status, msg, overlap, limit)
        elif overlap > limit:
            res.add("segment", sid, "UV overlap", FAIL,
                    "%s texels are covered by more than one face, over the allowance of %s. "
                    "Overlapping faces share pixels, so painting one changes the other."
                    % (fmt(overlap), fmt(limit)), overlap, limit)
        else:
            res.add("segment", sid, "UV overlap", PASS,
                    "%s overlapping texels, within the allowance of %s."
                    % (fmt(overlap), fmt(limit)), overlap, limit)

        if seg.get("uv_out_of_bounds"):
            res.add("segment", sid, "UVs inside the map", FAIL,
                    "Some faces sit outside the 0-1 square, so they sample the wrong part "
                    "of the texture.")
        else:
            res.add("segment", sid, "UVs inside the map", PASS,
                    "All faces sit inside the 0-1 square.")

        # The mesh group's name is what binds a renderer to a prefab. Maya can write a
        # second group name onto a mesh -- an isolate-select set, say -- and the merged
        # name silently stops matching, so the part vanishes in the engine while every
        # geometric measurement still looks correct.
        obj = seg.get("object")
        if obj is not None:
            accepted = [sid, "%s_%s" % (manifest["asset"], sid)]
            if obj in accepted:
                res.add("segment", sid, "Mesh group name", PASS,
                        "Named '%s', which the engine can bind to." % obj)
            else:
                res.add("segment", sid, "Mesh group name", FAIL,
                        "The mesh group is named '%s'. Expected '%s'. A name carrying "
                        "anything extra will not bind to the renderer, and the part "
                        "disappears in the engine even though the geometry is fine."
                        % (obj, "' or '".join(accepted)))

        coverage = seg.get("uv_coverage_percent", 0.0)
        res.limited("segment", sid, "UV coverage", coverage,
                    limits["min_uv_coverage_percent"], "% of the map used",
                    worse=lambda m, l: m < l, phrase="under the minimum")


def check_variants(manifest, spec, res):
    limits = spec["limits"]
    asset = manifest["asset"]
    required = limits["require_maps"] or []

    for seg in manifest["segments"]:
        sid = seg["id"]
        max_size = override(spec, sid, "max_texture_size", limits["max_texture_size"])
        for variant in seg["variants"]:
            target = "%s %s" % (sid, variant)
            maps = seg.get("maps", {}).get(variant, {})

            absent = [m for m in required if m not in maps]
            if absent:
                res.add("variant", target, "Required maps", FAIL,
                        "Missing %s. Expected %s."
                        % (" and ".join(absent),
                           ", ".join(common.texture_name(asset, sid, variant, m)
                                     for m in absent)))
            else:
                res.add("variant", target, "Required maps", PASS,
                        "All required maps present (%s)." % ", ".join(required))

            for name, path in sorted(maps.items()):
                info = common.png_info(os.path.join(common.PROJECT, path))
                label = "%s %s" % (target, name)
                w, h = info["width"], info["height"]

                # A PNG on disk is the source. Unity's Max Size is what the engine
                # actually builds, so that is the number a budget applies to.
                record = (manifest.get("textures") or {}).get(path, {})
                ships = record.get("ships_at", max(w, h))
                on_disk = "%d x %d" % (w, h)
                shipped = ("%s, ships at %d" % (on_disk, ships)
                           if ships != max(w, h) else on_disk)

                problems = []
                if limits["require_square_textures"] and w != h:
                    problems.append("not square (%d x %d)" % (w, h))
                if limits["require_power_of_two"] and not (is_power_of_two(w)
                                                           and is_power_of_two(h)):
                    problems.append("not a power of two")
                if max_size is not None and ships > max_size:
                    problems.append("ships at %d, over the limit of %d" % (ships, max_size))

                if problems:
                    res.add("variant", label, "Texture dimensions", FAIL,
                            "%s: %s. Hardware expects square, power-of-two maps."
                            % (shipped, "; ".join(problems)), ships, max_size)
                elif max_size is None:
                    res.add("variant", label, "Texture dimensions", UNENFORCED,
                            "%s, square and a power of two. No size limit set yet."
                            % shipped, ships, max_size)
                else:
                    res.add("variant", label, "Texture dimensions", PASS,
                            "%s, within the limit of %d." % (shipped, max_size),
                            ships, max_size)

                # A map in the wrong slot passes every check that reads names and
                # sizes: it is the right file, in the right place, correctly named. Only
                # its contents give it away.
                if name == "diffuse" and record.get("looks_like_normal_map"):
                    res.add("variant", label, "Map content", FAIL,
                            "This colour map reads as a normal map: its pixels decode to "
                            "unit-length vectors, which is what a normal map stores and "
                            "what artwork never does. A map saved into the wrong slot is "
                            "named and sized correctly; only its contents show it.")
                elif name == "diffuse" and record.get("looks_like_normal_map") is False:
                    res.add("variant", label, "Map content", PASS,
                            "Reads as artwork, not as a map in the wrong slot.")

                if limits["require_8_bit_channels"] and info["bit_depth"] != 8:
                    res.add("variant", label, "Bit depth", FAIL,
                            "%d bits per channel. 8 is expected; deeper files cost import "
                            "time and memory for no visible gain."
                            % info["bit_depth"], info["bit_depth"], 8)


def check_designs(manifest, spec, res):
    """A design is one variant across every segment -- what a player actually wears."""
    limits = spec["limits"]
    assumptions = spec["memory_assumptions"]
    bpp = assumptions.get("bits_per_pixel")
    mult = assumptions.get("mipmap_multiplier", 1.0) if assumptions.get("mipmaps") else 1.0

    ids = set(manifest["segments"][0]["variants"])
    for seg in manifest["segments"][1:]:
        ids &= set(seg["variants"])

    for variant in sorted(ids):
        target = "design %s" % variant

        materials = len(manifest["segments"])
        res.limited("design", target, "Material count", materials,
                    limits["max_materials_per_design"], "materials")

        refs = common.referenced_guids(common.ASSET_DIR)
        texels = 0
        maps_used = 0
        skipped = 0
        for seg in manifest["segments"]:
            paths = list(seg.get("maps", {}).get(variant, {}).values())
            if seg.get("occlusion"):
                paths.append(seg["occlusion"])
            for p in paths:
                full = os.path.join(common.PROJECT, p)
                guid = common.asset_guid(full)
                # A map no material references is not pulled into a build, so it costs
                # nothing at runtime and must not be charged to the design.
                if guid is not None and guid not in refs:
                    skipped += 1
                    continue
                record = (manifest.get("textures") or {}).get(p, {})
                w, h = common.png_size(full)
                edge = record.get("ships_at", max(w, h))
                texels += edge * edge
                maps_used += 1

        megatexels = texels / 1e6
        if bpp is None:
            res.add("design", target, "Texture memory", UNENFORCED,
                    "%s maps, %s megatexels. Memory cannot be stated until a compression "
                    "format is set in the spec." % (maps_used, fmt(megatexels)),
                    round(megatexels, 2), None)
        else:
            mb = texels * bpp / 8 * mult / (1024 * 1024)
            note = "" if not skipped else ", %d unused map(s) not counted" % skipped
            ceiling = limits["max_texture_memory_mb_per_design"]
            warn_at = limits.get("warn_texture_memory_mb_per_design")
            how = ("MB across %d maps (%s, %s%s)"
                   % (maps_used, assumptions.get("format", "unknown format"),
                      "with mips" if assumptions.get("mipmaps") else "no mips", note))

            if ceiling is not None and mb > ceiling:
                res.add("design", target, "Texture memory", FAIL,
                        "%s %s, over the limit of %s."
                        % (fmt(round(mb, 2)), how, fmt(ceiling)), round(mb, 2), ceiling)
            elif warn_at is not None and mb > warn_at:
                res.add("design", target, "Texture memory", WARN,
                        "%s %s. Within the limit of %s, but past %s -- worth knowing it "
                        "grew." % (fmt(round(mb, 2)), how, fmt(ceiling), fmt(warn_at)),
                        round(mb, 2), ceiling)
            else:
                res.limited("design", target, "Texture memory", round(mb, 2), ceiling, how)


def asset_folder(manifest):
    """Where this manifest's textures live, taken from the manifest rather than assumed,
    so a test fixture elsewhere on disk validates the same way the real kit does."""
    for seg in manifest["segments"]:
        for maps in seg.get("maps", {}).values():
            for path in maps.values():
                return os.path.dirname(os.path.join(common.PROJECT, path))
    return common.GLOVE_DIR


def check_files(manifest, spec, res):
    """Anything in the asset folder that the naming convention does not account for."""
    asset = manifest["asset"]
    folder = asset_folder(manifest)
    label = os.path.basename(folder)
    expected = set()
    for seg in manifest["segments"]:
        for variant in seg["variants"]:
            for name in common.MAP_SUFFIX:
                expected.add(common.texture_name(asset, seg["id"], variant, name))
        expected.add("%s_%s_ao.png" % (asset, seg["id"]))

    found = {f for f in os.listdir(folder) if f.lower().endswith(".png")}
    strays = sorted(found - expected)

    if strays:
        res.add("files", label, "Naming convention", FAIL,
                "%d file(s) do not match the naming convention and will not be found by "
                "the tool: %s." % (len(strays), ", ".join(strays)), len(strays), 0)
    else:
        res.add("files", label, "Naming convention", PASS,
                "All %d textures match the convention." % len(found), 0, 0)

    if spec["limits"].get("require_consistent_import_size", True):
        sizes = {}
        for path, record in (manifest.get("textures") or {}).items():
            sizes.setdefault(record.get("ships_at"), []).append(os.path.basename(path))
        if len(sizes) > 1:
            common_size = max(sizes, key=lambda k: len(sizes[k]))
            odd = sorted(n for k, v in sizes.items() if k != common_size for n in v)
            res.add("files", label, "Import settings", FAIL,
                    "%d of %d textures build at %s, but these build at a different size, "
                    "so one part of the glove carries more detail than its neighbours: %s."
                    % (len(sizes[common_size]), sum(len(v) for v in sizes.values()),
                       common_size, ", ".join(odd)), len(odd), 0)
        elif sizes:
            only = list(sizes)[0]
            res.add("files", label, "Import settings", PASS,
                    "Every texture builds at %s." % only, 0, 0)

    if not spec["limits"].get("forbid_orphan_textures"):
        return

    # A texture Unity has imported but no material names still ships in the build and is
    # never sampled. Materials reference textures by GUID, so the .meta files answer this
    # without the editor being open.
    refs = common.referenced_guids(os.path.dirname(folder))
    orphans = sorted(f for f in found
                     if (common.asset_guid(os.path.join(folder, f)) or "") not in refs
                     and common.asset_guid(os.path.join(folder, f)))

    if orphans:
        res.add("files", label, "Unused textures", FAIL,
                "%d texture(s) are in the project but no material uses them. Unity leaves "
                "them out of a build, so they cost nothing at runtime -- but they carry "
                "import time and repository weight, and nobody can tell whether each was "
                "unhooked on purpose or forgotten: %s." % (len(orphans), ", ".join(orphans)),
                len(orphans), 0)
    else:
        res.add("files", label, "Unused textures", PASS,
                "Every texture in the folder is referenced by a material.", 0, 0)


# --- entry -------------------------------------------------------------------


def runtime_spec(spec, manifest=None):
    """The limits, flattened for the checks that run inside the game.

    Unity's own JSON reader cannot handle the spec's shape -- nested objects keyed by
    part name, limits that may be null -- so the numbers it needs are written out flat,
    with -1 standing for a limit that is not set. Derived from the same spec the offline
    checks use, so the two cannot drift apart.
    """
    limits = spec["limits"]
    memory = spec.get("memory_assumptions") or {}
    overrides = spec.get("segment_overrides") or {}

    def number(value):
        return -1 if value is None else value

    return {
        "profile": spec.get("profile", ""),
        "maxTrianglesPerAsset": number(limits.get("max_triangles_per_asset")),
        "maxMaterialsPerDesign": number(limits.get("max_materials_per_design")),
        "maxTextureSize": number(limits.get("max_texture_size")),
        "maxTextureMemoryMb": number(limits.get("max_texture_memory_mb_per_design")),
        "warnTextureMemoryMb": number(limits.get("warn_texture_memory_mb_per_design")),
        "maxUvOverlapTexels": number(limits.get("max_uv_overlap_texels")),
        "bitsPerPixel": number(memory.get("bits_per_pixel")),
        "mipmapMultiplier": (memory.get("mipmap_multiplier", 1.0)
                             if memory.get("mipmaps") else 1.0),
        # What each part measured when it was last checked. Not a limit -- a fact about
        # this asset, carried into the build so the checks there can notice a mesh that
        # has drifted from what was measured. Nothing else can catch geometry that is
        # merely wrong rather than over a budget.
        "segmentNames": [s["id"] for s in (manifest or {}).get("segments", [])],
        "segmentTriangles": [int(s.get("triangles", -1))
                             for s in (manifest or {}).get("segments", [])],
        "requireMaps": limits.get("require_maps") or [],
        "overlapAllowed": sorted(
            part for part, rule in overrides.items()
            if isinstance(rule, dict) and rule.get("uv_overlap_allowed")),
    }


def run(spec_path, manifest_path=None):
    manifest = common.load_json(manifest_path or common.MANIFEST)
    spec = common.load_json(spec_path)

    if "maps" not in manifest["segments"][0]:
        sys.exit("The manifest has no measured data. Run the Blender step and "
                 "pipeline/extend_manifest.py first.")

    res = Results()
    check_asset(manifest, spec, res)
    check_segments(manifest, spec, res)
    check_variants(manifest, spec, res)
    check_designs(manifest, spec, res)
    check_files(manifest, spec, res)
    return manifest, spec, res


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--spec", default=os.path.join(os.path.dirname(
        os.path.realpath(__file__)), "spec.json"))
    ap.add_argument("--manifest", default=None,
                    help="validate a different kit, e.g. one of the test fixtures")
    ap.add_argument("--out", default=None,
                    help="where to write the reports; defaults to pipeline/out")
    ap.add_argument("--quiet", action="store_true", help="only print failures")
    args = ap.parse_args()

    manifest, spec, res = run(args.spec, args.manifest)
    counts = res.counts()

    for row in res.rows:
        if args.quiet and row["status"] != FAIL:
            continue
        if row["status"] == INFO and args.quiet:
            continue
        mark = {PASS: "ok  ", FAIL: "FAIL", WARN: "warn", UNENFORCED: "--  ",
                INFO: "    "}[row["status"]]
        print("%s %-22s %-22s %s" % (mark, row["target"], row["check"], row["message"]))

    print("")
    # A warning is not a failure and must not read like one, so it is counted separately
    # and only mentioned when there is one. The exit code ignores it entirely: unattended
    # runs should not start failing because a figure moved within its budget.
    line = ("%d passed, %d failed, %d not enforced yet, %d informational"
            % (counts[PASS], counts[FAIL], counts[UNENFORCED], counts[INFO]))
    if counts[WARN]:
        line += ", %d worth a look" % counts[WARN]
    print(line)

    payload = {
        "asset": manifest["asset"],
        "profile": spec.get("profile"),
        "summary": counts,
        "rows": res.rows,
    }
    out_dir = os.path.join(common.PROJECT, args.out) if args.out else common.OUT_DIR
    common.save_json(os.path.join(out_dir, "validation.json"), payload)
    common.save_json(os.path.join(out_dir, "validation_spec.json"),
                     runtime_spec(spec, manifest))

    html = os.path.join(out_dir, "validation.html")
    write_html(html, manifest, spec, res.rows, counts)
    print("reports: %s and %s" % (common.rel(os.path.join(out_dir, "validation.json")),
                                  common.rel(html)))

    sys.exit(1 if counts[FAIL] else 0)


if __name__ == "__main__":
    main()
