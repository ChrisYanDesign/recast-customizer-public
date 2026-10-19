"""Build deliberately broken asset kits and prove each one fails for the right reason.

    python pipeline/tests/test_broken_assets.py

Each fixture is a complete miniature kit -- manifest, textures, a material -- with exactly
one defect introduced. The test asserts two things per fixture: that the expected check
fails, and that nothing else does. The second half is what stops a check from passing for
an accidental reason, and it is why a healthy fixture is included as the first case.

Fixtures are generated, not committed, so they cannot drift away from the generator.
"""

import hashlib
import json
import os
import shutil
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.realpath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), "validator"))

import common  # noqa: E402
import validate  # noqa: E402

FIXTURES = os.path.join(HERE, "fixtures")

TEST_SPEC = {
    "profile": "fixture",
    "limits": {
        "max_triangles_per_asset": 1000,
        "max_triangles_per_segment": 600,
        "max_materials_per_design": 4,
        "max_texture_memory_mb_per_design": 20,
        "max_texture_size": 64,
        "min_uv_coverage_percent": 20,
        "max_uv_overlap_texels": 0,
        "require_power_of_two": True,
        "require_square_textures": True,
        "require_maps": ["diffuse", "normal"],
        "require_8_bit_channels": True,
        "forbid_orphan_textures": True,
    },
    "segment_overrides": {},
    "memory_assumptions": {
        "format": "ASTC 6x6", "bits_per_pixel": 3.56,
        "mipmaps": True, "mipmap_multiplier": 1.3333,
    },
}


# --- fixture building --------------------------------------------------------


def write_png(path, width, height, bit_depth=8):
    """A minimal valid greyscale PNG, written without an image library."""
    row = b"\x00" + bytes([128]) * (width * (2 if bit_depth == 16 else 1))
    raw = row * height

    def chunk(tag, data):
        body = tag + data
        return (struct.pack(">I", len(data)) + body
                + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", width, height, bit_depth, 0, 0, 0, 0)
    with open(path, "wb") as fh:
        fh.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
                 + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


def fake_guid(name):
    return hashlib.md5(name.encode()).hexdigest()


def write_meta(path):
    with open(path + ".meta", "w", encoding="utf-8") as fh:
        fh.write("fileFormatVersion: 2\nguid: %s\n" % fake_guid(os.path.basename(path)))


def build(case, mutate=None):
    """A healthy two-part kit, then one defect applied on top."""
    root = os.path.join(FIXTURES, case)
    tex = os.path.join(root, "GloveAsset")
    shutil.rmtree(root, ignore_errors=True)
    os.makedirs(tex)

    with open(os.path.join(tex, "kit.obj"), "w", encoding="utf-8") as fh:
        fh.write("# fixture mesh\n")

    segments = []
    files = []
    for sid in ("top", "bottom"):
        maps = {}
        for name, suffix in (("diffuse", "diff"), ("normal", "norm")):
            fn = "kit_%s_01_%s.png" % (sid, suffix)
            write_png(os.path.join(tex, fn), 64, 64)
            write_meta(os.path.join(tex, fn))
            files.append(fn)
            maps[name] = common.rel(os.path.join(tex, fn))
        segments.append({
            "id": sid, "label": sid.title(), "childRenderer": sid, "variants": ["01"],
            "object": "kit_%s" % sid,
            "triangles": 400, "uv_mask": "", "uv_bbox": [0, 0, 64, 64],
            "uv_coverage_percent": 55.0, "uv_overlap_texels": 0,
            "uv_out_of_bounds": False, "maps": {"01": maps},
        })

    manifest = {
        "asset": "kit", "displayName": "Fixture kit",
        "source_mesh": common.rel(os.path.join(tex, "kit.obj")),
        "total_triangles": 800, "texture_size": [64, 64],
        "materialNamePattern": "kit_{segment}_{variant}_mat",
        "segments": segments,
    }

    state = {"root": root, "tex": tex, "manifest": manifest, "files": files}
    if mutate:
        mutate(state)

    # One material naming every texture the manifest still points at, so the orphan
    # check has something to compare against.
    referenced = []
    for seg in state["manifest"]["segments"]:
        for maps in seg["maps"].values():
            referenced.extend(os.path.basename(p) for p in maps.values())
    with open(os.path.join(state["tex"], "kit_mat.mat"), "w", encoding="utf-8") as fh:
        fh.write("Material:\n")
        for fn in referenced:
            fh.write("  - m_Texture: {fileID: 2800000, guid: %s, type: 3}\n" % fake_guid(fn))

    path = os.path.join(root, "manifest.json")
    common.save_json(path, state["manifest"])
    return path


# --- the defects -------------------------------------------------------------


def npot(state):
    fn = "kit_top_01_diff.png"
    write_png(os.path.join(state["tex"], fn), 100, 100)


def non_square(state):
    fn = "kit_top_01_diff.png"
    write_png(os.path.join(state["tex"], fn), 64, 32)


def missing_normal(state):
    seg = state["manifest"]["segments"][0]
    path = seg["maps"]["01"].pop("normal")
    os.remove(os.path.join(common.PROJECT, path))
    os.remove(os.path.join(common.PROJECT, path + ".meta"))


def misnamed(state):
    old = os.path.join(state["tex"], "kit_top_01_diff.png")
    new = os.path.join(state["tex"], "kit_top_1_diff.png")   # one digit, not two
    os.rename(old, new)
    os.remove(old + ".meta")
    # Unity keeps a file's GUID across a rename, so the material still resolves it. The
    # defect under test is the name, nothing else, so the meta is reissued to match.
    write_meta(new)
    state["manifest"]["segments"][0]["maps"]["01"]["diffuse"] = common.rel(new)


def orphan(state):
    fn = "kit_bottom_01_spec.png"
    write_png(os.path.join(state["tex"], fn), 64, 64)
    write_meta(os.path.join(state["tex"], fn))   # imported, but no material will name it


def over_budget(state):
    state["manifest"]["total_triangles"] = 5000
    state["manifest"]["segments"][0]["triangles"] = 4600


def uvs_outside_the_map(state):
    state["manifest"]["segments"][1]["uv_out_of_bounds"] = True


def stray_mesh_group_name(state):
    # what Maya's UV editor leaves behind: a second group name merged into the first
    state["manifest"]["segments"][0]["object"] = "textureEditorIsolateSelectSet_kit_top"


def overlapping_uvs(state):
    state["manifest"]["segments"][1]["uv_overlap_texels"] = 4096


CASES = [
    ("healthy", None, []),
    ("npot_texture", npot, [("top 01 diffuse", "Texture dimensions")]),
    ("non_square_texture", non_square, [("top 01 diffuse", "Texture dimensions")]),
    ("missing_normal_map", missing_normal, [("top 01", "Required maps")]),
    ("misnamed_file", misnamed, [("GloveAsset", "Naming convention")]),
    ("orphan_texture", orphan, [("GloveAsset", "Unused textures")]),
    ("over_budget", over_budget, [("kit", "Triangle count"), ("top", "Triangle count")]),
    ("overlapping_uvs", overlapping_uvs, [("bottom", "UV overlap")]),
    ("stray_mesh_group_name", stray_mesh_group_name, [("top", "Mesh group name")]),
    ("uvs_outside_the_map", uvs_outside_the_map, [("bottom", "UVs inside the map")]),
]


# --- the test ----------------------------------------------------------------


def main():
    spec_path = os.path.join(FIXTURES, "spec.json")
    os.makedirs(FIXTURES, exist_ok=True)
    common.save_json(spec_path, TEST_SPEC)

    failures = 0
    for case, mutate, expected in CASES:
        manifest_path = build(case, mutate)
        _, _, res = validate.run(spec_path, manifest_path)
        got = {(r["target"], r["check"]) for r in res.rows if r["status"] == validate.FAIL}

        missed = [e for e in expected if tuple(e) not in got]
        extra = sorted(got - {tuple(e) for e in expected})

        if missed or extra:
            failures += 1
            print("FAIL %-20s" % case)
            for m in missed:
                print("       not caught: %s / %s" % m)
            for e in extra:
                print("       unexpected: %s / %s" % e)
        else:
            summary = ", ".join("%s / %s" % e for e in expected) or "nothing, as intended"
            print("ok   %-20s caught %s" % (case, summary))

    print("")
    print("%d of %d fixtures behaved as expected" % (len(CASES) - failures, len(CASES)))
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
