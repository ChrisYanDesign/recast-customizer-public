"""Fold measured data into glove_manifest.json.

Run after pipeline/blender/measure_and_mask.py:
    python pipeline/extend_manifest.py

The manifest is the one description of the asset that is not a Unity file, so it is what
Python and ComfyUI can read. Everything added here is measured or found on disk -- nothing
is typed by hand, so re-running after a re-unwrap or a new variant keeps it true.

Hand-authored fields (segments, variant ids, naming patterns) are preserved untouched.
"""

import json
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.realpath(__file__)))
import common

HERE = os.path.dirname(os.path.realpath(__file__))
PROJECT = os.path.dirname(HERE)
ASSET_DIR = os.path.join(PROJECT, "Assets", "RecastCustomizer")
GLOVE_DIR = os.path.join(ASSET_DIR, "GloveAsset")
def _arg(flag, default):
    return sys.argv[sys.argv.index(flag) + 1] if flag in sys.argv else default


MANIFEST = os.path.join(PROJECT, _arg("--manifest",
                                      "Assets/RecastCustomizer/glove_manifest.json"))
STATS = os.path.join(PROJECT, _arg("--stats", "pipeline/out/mesh_stats.json"))
TEXTURE_STATS = os.path.join(PROJECT, _arg("--texture-stats",
                                          "pipeline/out/texture_stats.json"))

MAPS = {"diffuse": "diff", "normal": "norm", "specular": "spec"}


def png_size(path):
    """Width and height from a PNG header, so there is no Pillow dependency."""
    with open(path, "rb") as fh:
        head = fh.read(24)
    if head[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG: " + path)
    return struct.unpack(">II", head[16:24])


def rel(path):
    return os.path.relpath(path, PROJECT).replace("\\", "/")


def main():
    for p in (MANIFEST, STATS):
        if not os.path.exists(p):
            sys.exit("missing " + p + " -- run the Blender step first")

    with open(MANIFEST, encoding="utf-8") as fh:
        manifest = json.load(fh)

    # Textures sit beside the mesh, which the manifest names. Deriving it this way lets a
    # kit live anywhere -- the demo kit is not in GloveAsset.
    global GLOVE_DIR
    GLOVE_DIR = os.path.dirname(os.path.join(PROJECT, manifest["source_mesh"]))
    with open(STATS, encoding="utf-8") as fh:
        stats = json.load(fh)

    # What each texture actually contains. Absent when the sampling step has not run,
    # in which case the content checks simply do not fire.
    looks = {}
    if os.path.exists(TEXTURE_STATS):
        with open(TEXTURE_STATS, encoding="utf-8") as fh:
            looks = json.load(fh)

    asset = manifest["asset"]
    sizes = set()
    missing = []
    # What each texture is on disk, and what the engine will actually build it at.
    textures = {}

    for seg in manifest["segments"]:
        sid = seg["id"]
        measured = stats["segments"].get(sid)
        if measured is None:
            sys.exit("no measurement for segment " + sid)

        # recorded verbatim, so the validator can judge the name the measure
        # step was deliberately lenient about matching
        seg["object"] = measured["object"]
        seg["triangles"] = measured["triangles"]
        seg["uv_mask"] = measured["uv_mask"]
        seg["uv_bbox"] = measured["uv_bbox"]
        seg["uv_coverage_percent"] = measured["uv_coverage_percent"]
        seg["uv_overlap_texels"] = measured["uv_overlap_texels"]
        seg["uv_out_of_bounds"] = measured["uv_out_of_bounds"]

        maps = {}
        for variant in seg["variants"]:
            entry = {}
            for name, suffix in MAPS.items():
                fn = "%s_%s_%s_%s.png" % (asset, sid, variant, suffix)
                path = os.path.join(GLOVE_DIR, fn)
                if os.path.exists(path):
                    w, h = png_size(path)
                    sizes.add((w, h))
                    entry[name] = rel(path)
                    textures[rel(path)] = {
                        "file": [w, h],
                        "max_import_size": common.texture_import(path)["max_size"],
                        "ships_at": common.shipped_size(path, w, h),
                        "looks_like_normal_map":
                            looks.get(rel(path), {}).get("looks_like_normal_map"),
                    }
                else:
                    missing.append("%s_%s_%s" % (sid, variant, suffix))
            maps[variant] = entry

        ao = os.path.join(GLOVE_DIR, "%s_%s_ao.png" % (asset, sid))
        if os.path.exists(ao):
            seg["occlusion"] = rel(ao)
            aw, ah = png_size(ao)
            textures[rel(ao)] = {
                "file": [aw, ah],
                "max_import_size": common.texture_import(ao)["max_size"],
                "ships_at": common.shipped_size(ao, aw, ah),
            }

        seg["maps"] = maps

    manifest["textures"] = textures
    manifest["source_mesh"] = stats["source_mesh"]
    manifest["total_triangles"] = stats["total_triangles"]
    manifest["texture_size"] = list(max(sizes)) if sizes else None
    manifest["mask_resolution"] = stats["mask_resolution"]
    manifest["generated_fields"] = (
        "object, triangles, uv_mask, uv_bbox, uv_coverage_percent, uv_overlap_texels, "
        "uv_out_of_bounds, maps, "
        "occlusion, textures, source_mesh, total_triangles, texture_size and mask_resolution are "
        "written by pipeline/extend_manifest.py. Do not edit them by hand."
    )

    with open(MANIFEST, "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2)
        fh.write("\n")

    print("updated %s" % rel(MANIFEST))
    print("  %d triangles, textures %s" % (manifest["total_triangles"], manifest["texture_size"]))
    if len(sizes) > 1:
        print("  NOTE: mixed texture sizes on disk: %s" % sorted(sizes))
    if missing:
        print("  %d map(s) absent: %s" % (len(missing), ", ".join(missing)))


main()
