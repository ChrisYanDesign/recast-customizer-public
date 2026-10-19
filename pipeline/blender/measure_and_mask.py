"""Measure the glove mesh and generate one UV mask per segment.

Run headless:
    blender --background --python pipeline/blender/measure_and_mask.py

Writes:
    pipeline/out/mesh_stats.json   per-segment triangle counts, UV bounds, overlap
    pipeline/masks/<segment>.png   white where the segment's UV island covers the map

The mask is rasterised from the mesh UVs rather than painted, so it stays correct if the
model is ever re-unwrapped. Rasterising also gives the overlap check for free: any texel
covered by more than one triangle is an overlap.
"""

import json
import os
import sys

import bpy
import numpy as np

# --- paths -------------------------------------------------------------------

HERE = os.path.dirname(os.path.realpath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))  # the Unity project root
def _arg(flag, default):
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    return argv[argv.index(flag) + 1] if flag in argv else default


# --manifest and --out let the demo kit be measured without overwriting the real one
MANIFEST = os.path.join(PROJECT, _arg("--manifest",
                                      "Assets/RecastCustomizer/glove_manifest.json"))
with open(MANIFEST, encoding="utf-8") as _fh:
    OBJ = os.path.join(PROJECT, json.load(_fh)["source_mesh"])
_BASE = os.path.join(PROJECT, _arg("--out", "pipeline"))
OUT_DIR = os.path.join(_BASE, "out")
MASK_DIR = os.path.join(_BASE, "masks")

RES = 2048  # match the authored diffuse maps

# Group names in the OBJ are not all identical to the segment ids in the manifest.
SEGMENTS = {"top": "top", "bottom": "bottom", "thumb": "thumb", "tassel": "tassel"}


def log(msg):
    print("[measure] " + msg, flush=True)


# --- scene -------------------------------------------------------------------


def load_obj():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.wm.obj_import(filepath=OBJ, use_split_objects=True, use_split_groups=True)
    objs = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    log("imported %d mesh objects: %s" % (len(objs), ", ".join(o.name for o in objs)))
    return objs


def segment_of(name):
    """Map an imported object name onto a manifest segment id, or None."""
    low = name.lower()
    for seg in SEGMENTS:
        # the group may be bare ("top1") or prefixed ("falcon_glove_top"), so match
        # on containment rather than assuming either spelling
        if seg in low:
            return seg
    return None


# --- rasteriser --------------------------------------------------------------


def rasterise(tris, res):
    """Count how many triangles cover each texel. tris is (n, 3, 2) in UV space.

    Half-open barycentric fill at texel centres. Returns a uint16 coverage count.
    """
    counts = np.zeros((res, res), dtype=np.uint16)
    if len(tris) == 0:
        return counts

    # UV origin is bottom-left; image row 0 is the bottom row, which is how Blender
    # stores pixels too, so no flip is needed here.
    pts = tris * res

    for tri in pts:
        (x0, y0), (x1, y1), (x2, y2) = tri

        min_x = max(int(np.floor(min(x0, x1, x2))), 0)
        max_x = min(int(np.ceil(max(x0, x1, x2))), res - 1)
        min_y = max(int(np.floor(min(y0, y1, y2))), 0)
        max_y = min(int(np.ceil(max(y0, y1, y2))), res - 1)
        if min_x > max_x or min_y > max_y:
            continue

        denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
        if abs(denom) < 1e-12:
            continue  # degenerate in UV space

        xs = np.arange(min_x, max_x + 1) + 0.5
        ys = np.arange(min_y, max_y + 1) + 0.5
        gx, gy = np.meshgrid(xs, ys)

        a = ((y1 - y2) * (gx - x2) + (x2 - x1) * (gy - y2)) / denom
        b = ((y2 - y0) * (gx - x2) + (x0 - x2) * (gy - y2)) / denom
        c = 1.0 - a - b
        inside = (a >= 0) & (b >= 0) & (c >= 0)

        counts[min_y:max_y + 1, min_x:max_x + 1] += inside.astype(np.uint16)

    return counts


def uv_triangles(obj):
    """Every loop triangle's UVs as an (n, 3, 2) array."""
    mesh = obj.data
    mesh.calc_loop_triangles()
    uv_layer = mesh.uv_layers.active
    if uv_layer is None:
        return np.zeros((0, 3, 2), dtype=np.float64)

    uvs = np.zeros((len(mesh.loops), 2), dtype=np.float64)
    uv_layer.data.foreach_get("uv", uvs.ravel())

    loops = np.zeros((len(mesh.loop_triangles), 3), dtype=np.int32)
    mesh.loop_triangles.foreach_get("loops", loops.ravel())

    return uvs[loops]


# --- output ------------------------------------------------------------------


def save_mask(counts, path):
    """Write coverage as a white-on-black PNG through Blender's image writer."""
    res = counts.shape[0]
    mask = (counts > 0).astype(np.float32)

    img = bpy.data.images.new("mask", width=res, height=res, alpha=False, float_buffer=False)
    rgba = np.empty((res, res, 4), dtype=np.float32)
    rgba[..., 0] = mask
    rgba[..., 1] = mask
    rgba[..., 2] = mask
    rgba[..., 3] = 1.0
    img.pixels.foreach_set(rgba.ravel())

    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    os.makedirs(MASK_DIR, exist_ok=True)

    objs = load_obj()
    stats = {"source_mesh": os.path.relpath(OBJ, PROJECT).replace("\\", "/"),
             "mask_resolution": RES,
             "segments": {},
             "unmapped_objects": []}

    total_tris = 0

    for obj in objs:
        seg = segment_of(obj.name)
        if seg is None:
            mesh = obj.data
            mesh.calc_loop_triangles()
            n = len(mesh.loop_triangles)
            total_tris += n
            if n:
                stats["unmapped_objects"].append({"name": obj.name, "triangles": n})
            continue

        tris = uv_triangles(obj)
        n_tris = len(tris)
        total_tris += n_tris

        counts = rasterise(tris, RES)
        covered = int((counts > 0).sum())
        overlapping = int((counts > 1).sum())

        # The bbox is reported in image coordinates -- origin top-left, y increasing
        # downwards -- because that is what every image library reads the PNG as. The
        # rasteriser works bottom-up to match UV space, so y is flipped here once.
        ys, xs = np.nonzero(counts)
        if len(xs):
            h = int(ys.max() - ys.min() + 1)
            bbox = [int(xs.min()), int(RES - (ys.min() + h)),
                    int(xs.max() - xs.min() + 1), h]
        else:
            bbox = [0, 0, 0, 0]

        flat = tris.reshape(-1, 2)
        mask_path = os.path.join(MASK_DIR, seg + ".png")
        save_mask(counts, mask_path)

        stats["segments"][seg] = {
            "object": obj.name,
            "triangles": n_tris,
            "uv_mask": os.path.relpath(mask_path, PROJECT).replace("\\", "/"),
            "uv_bbox": bbox,  # x, y, w, h in image space (origin top-left)
            "uv_coverage_percent": round(100.0 * covered / (RES * RES), 2),
            "uv_overlap_texels": overlapping,
            "uv_out_of_bounds": bool((flat < -1e-6).any() or (flat > 1 + 1e-6).any()),
        }
        log("%-7s %5d tris  bbox=%s  coverage=%.2f%%  overlap=%d texels"
            % (seg, n_tris, bbox, stats["segments"][seg]["uv_coverage_percent"], overlapping))

    stats["total_triangles"] = total_tris

    out = os.path.join(OUT_DIR, "mesh_stats.json")
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(stats, fh, indent=2)
    log("wrote " + os.path.relpath(out, PROJECT))

    missing = [s for s in SEGMENTS if s not in stats["segments"]]
    if missing:
        log("WARNING: no object matched these segments: " + ", ".join(missing))
        sys.exit(1)


main()
