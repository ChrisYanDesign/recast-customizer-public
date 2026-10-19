"""Draw where a part's UVs overlap, and compare against another mesh in the same place.

    blender --background --python pipeline/blender/overlap_map.py -- bottom [other.obj]

An overlap of a hundred texels in a four-megapixel map is invisible at full size, so this
writes two images: the whole layout with the overlap dilated enough to find, and a zoom on
the cluster with the comparison mesh beside it.

Writes to pipeline/out/overlap_<segment>_full.png and _zoom.png.
"""

import json
import os
import sys

import bpy
import numpy as np

HERE = os.path.dirname(os.path.realpath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
MANIFEST = os.path.join(PROJECT, "Assets", "RecastCustomizer", "glove_manifest.json")
OUT_DIR = os.path.join(PROJECT, "pipeline", "out")

RES = 2048
SEGMENTS = ("top", "bottom", "thumb", "tassel")

# colours: unused map, the part's island, and the overlap
BG = (0.06, 0.06, 0.07)
ISLAND = (0.22, 0.23, 0.26)
OVERLAP = (0.95, 0.22, 0.17)


def argv_after_dashes():
    return sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def uv_triangles(obj):
    mesh = obj.data
    mesh.calc_loop_triangles()
    layer = mesh.uv_layers.active
    if layer is None:
        return np.zeros((0, 3, 2))
    uvs = np.zeros((len(mesh.loops), 2))
    layer.data.foreach_get("uv", uvs.ravel())
    loops = np.zeros((len(mesh.loop_triangles), 3), dtype=np.int32)
    mesh.loop_triangles.foreach_get("loops", loops.ravel())
    return uvs[loops]


def rasterise(tris, res):
    counts = np.zeros((res, res), dtype=np.uint16)
    for tri in tris * res:
        (x0, y0), (x1, y1), (x2, y2) = tri
        mnx, mxx = max(int(np.floor(min(x0, x1, x2))), 0), min(int(np.ceil(max(x0, x1, x2))), res - 1)
        mny, mxy = max(int(np.floor(min(y0, y1, y2))), 0), min(int(np.ceil(max(y0, y1, y2))), res - 1)
        if mnx > mxx or mny > mxy:
            continue
        den = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
        if abs(den) < 1e-12:
            continue
        gx, gy = np.meshgrid(np.arange(mnx, mxx + 1) + 0.5, np.arange(mny, mxy + 1) + 0.5)
        a = ((y1 - y2) * (gx - x2) + (x2 - x1) * (gy - y2)) / den
        b = ((y2 - y0) * (gx - x2) + (x0 - x2) * (gy - y2)) / den
        counts[mny:mxy + 1, mnx:mxx + 1] += ((a >= 0) & (b >= 0) & (1 - a - b >= 0)).astype(np.uint16)
    return counts


def load_segment(obj_path, segment):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.wm.obj_import(filepath=obj_path, use_split_objects=True, use_split_groups=True)
    for o in bpy.context.scene.objects:
        if o.type == "MESH" and segment in o.name.lower():
            return rasterise(uv_triangles(o), RES)
    raise SystemExit("no object matching '%s' in %s" % (segment, obj_path))


def dilate(mask, radius):
    out = mask.copy()
    for _ in range(radius):
        out[1:, :] |= out[:-1, :]
        out[:-1, :] |= out[1:, :]
        out[:, 1:] |= out[:, :-1]
        out[:, :-1] |= out[:, 1:]
    return out


def colourise(counts, dilate_overlap=0):
    """Island on a dark ground, overlap in red."""
    img = np.empty(counts.shape + (3,), dtype=np.float32)
    img[...] = BG
    img[counts > 0] = ISLAND
    over = counts > 1
    if dilate_overlap:
        over = dilate(over, dilate_overlap)
    img[over] = OVERLAP
    return img


def save(path, rgb):
    """rgb is top-down; Blender stores pixels bottom-up, so flip on the way out."""
    h, w = rgb.shape[:2]
    rgba = np.ones((h, w, 4), dtype=np.float32)
    rgba[..., :3] = rgb[::-1]
    img = bpy.data.images.new("out", width=w, height=h, alpha=False, float_buffer=False)
    img.pixels.foreach_set(rgba.ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)
    print("[overlap] wrote " + os.path.relpath(path, PROJECT))


def clusters(mask, gap=24):
    """Rough grouping of overlap pixels, so the zoom can be aimed at the biggest one."""
    ys, xs = np.nonzero(mask)
    groups = []
    for y, x in zip(ys, xs):
        for g in groups:
            if abs(g["cy"] - y) < gap and abs(g["cx"] - x) < gap:
                g["pts"].append((y, x))
                g["cy"] = sum(p[0] for p in g["pts"]) / len(g["pts"])
                g["cx"] = sum(p[1] for p in g["pts"]) / len(g["pts"])
                break
        else:
            groups.append({"pts": [(y, x)], "cy": float(y), "cx": float(x)})
    groups.sort(key=lambda g: -len(g["pts"]))
    return groups


def main():
    args = argv_after_dashes()
    segment = args[0] if args else "bottom"
    if segment not in SEGMENTS:
        raise SystemExit("segment must be one of " + ", ".join(SEGMENTS))

    with open(MANIFEST, encoding="utf-8") as fh:
        current = os.path.join(PROJECT, json.load(fh)["source_mesh"])
    # compare against the dense mesh the lowpoly was derived from
    other = (os.path.join(PROJECT, args[1]) if len(args) > 1
             else os.path.join(PROJECT, "backup", "falcon_glove_highpoly.obj"))

    os.makedirs(OUT_DIR, exist_ok=True)

    now = load_segment(current, segment)
    over = now > 1
    total = int(over.sum())
    print("[overlap] %s: %d overlapping texels in %s"
          % (segment, total, os.path.basename(current)))

    save(os.path.join(OUT_DIR, "overlap_%s_full.png" % segment), colourise(now, dilate_overlap=6))

    if total == 0:
        return

    groups = clusters(over)
    print("[overlap] %d cluster(s); largest holds %d texels" % (len(groups), len(groups[0]["pts"])))

    # aim the zoom at the largest cluster, with room around it
    ys = [p[0] for p in groups[0]["pts"]]
    xs = [p[1] for p in groups[0]["pts"]]
    half = max(96, max(max(ys) - min(ys), max(xs) - min(xs)))
    cy, cx = (min(ys) + max(ys)) // 2, (min(xs) + max(xs)) // 2
    y0, x0 = max(0, cy - half), max(0, cx - half)
    y1, x1 = min(RES, cy + half), min(RES, cx + half)
    print("[overlap] zoom on x=%d..%d y=%d..%d (image coordinates)" % (x0, x1, y0, y1))

    before = load_segment(other, segment)
    print("[overlap] same part in %s: %d overlapping texels"
          % (os.path.basename(other), int((before > 1).sum())))

    crops = [colourise(before, dilate_overlap=2)[y0:y1, x0:x1],
             colourise(now, dilate_overlap=2)[y0:y1, x0:x1]]

    scale = max(1, 560 // crops[0].shape[0])
    crops = [np.kron(c, np.ones((scale, scale, 1), dtype=np.float32)) for c in crops]

    gutter = np.zeros((crops[0].shape[0], 16, 3), dtype=np.float32)
    save(os.path.join(OUT_DIR, "overlap_%s_zoom.png" % segment),
         np.concatenate([crops[0], gutter, crops[1]], axis=1))


main()
