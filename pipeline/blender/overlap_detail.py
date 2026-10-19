"""Show an overlap in context: on the texture, and on the UV wireframe.

    blender --background --python pipeline/blender/overlap_detail.py -- <segment> [variant]

The full-map view says an overlap exists; this says which edge to grab. It writes a
three-panel image zoomed on the overlap: the authored diffuse, the UV wireframe, and the
two combined, with the overlapping texels marked in each.

Writes pipeline/out/overlap_<segment>_detail.png.
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
PAD = 90          # context around the overlap, in texels
SCALE_TO = 620    # each panel is upscaled to roughly this many pixels

WIRE = np.array([0.30, 0.78, 0.95], dtype=np.float32)   # UV edges
MARK = np.array([1.00, 0.16, 0.12], dtype=np.float32)   # the overlap
GROUND = np.array([0.07, 0.07, 0.08], dtype=np.float32)


def log(msg):
    print("[detail] " + msg, flush=True)


def args_after_dashes():
    return sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def uv_triangles(obj):
    mesh = obj.data
    mesh.calc_loop_triangles()
    layer = mesh.uv_layers.active
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


def draw_wire(tris, res):
    """Every triangle edge, sampled densely enough to leave no gaps."""
    wire = np.zeros((res, res), dtype=bool)
    pts = tris * res
    for tri in pts:
        for i in range(3):
            x0, y0 = tri[i]
            x1, y1 = tri[(i + 1) % 3]
            n = int(max(abs(x1 - x0), abs(y1 - y0))) + 2
            xs = np.clip(np.linspace(x0, x1, n).astype(int), 0, res - 1)
            ys = np.clip(np.linspace(y0, y1, n).astype(int), 0, res - 1)
            wire[ys, xs] = True
    return wire


def load_image(rel):
    img = bpy.data.images.load(os.path.join(PROJECT, rel))
    w, h = img.size
    px = np.zeros(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    return px.reshape(h, w, 4)[..., :3]      # bottom-up, matching the rasteriser


def save(path, rgb):
    h, w = rgb.shape[:2]
    rgba = np.ones((h, w, 4), dtype=np.float32)
    rgba[..., :3] = np.clip(rgb, 0, 1)
    img = bpy.data.images.new("out", width=w, height=h, alpha=False, float_buffer=False)
    img.pixels.foreach_set(rgba.ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)
    log("wrote " + os.path.relpath(path, PROJECT))


def main():
    args = args_after_dashes()
    segment = args[0] if args else "bottom"
    variant = args[1] if len(args) > 1 else "01"

    with open(MANIFEST, encoding="utf-8") as fh:
        manifest = json.load(fh)
    seg = next(s for s in manifest["segments"] if s["id"] == segment)
    diffuse_rel = seg["maps"][variant]["diffuse"]

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.wm.obj_import(filepath=os.path.join(PROJECT, manifest["source_mesh"]),
                          use_split_objects=True, use_split_groups=True)
    obj = next(o for o in bpy.context.scene.objects
               if o.type == "MESH" and segment in o.name.lower())

    tris = uv_triangles(obj)
    counts = rasterise(tris, RES)
    over = counts > 1
    if not over.any():
        log("%s has no overlap" % segment)
        return

    wire = draw_wire(tris, RES)
    diffuse = load_image(diffuse_rel)

    ys, xs = np.nonzero(over)
    cy, cx = (ys.min() + ys.max()) // 2, (xs.min() + xs.max()) // 2
    half = max(PAD, (max(ys.max() - ys.min(), xs.max() - xs.min()) // 2) + PAD)
    y0, y1 = max(0, cy - half), min(RES, cy + half)
    x0, x1 = max(0, cx - half), min(RES, cx + half)

    log("%s: %d overlapping texels, %d wide by %d tall"
        % (segment, int(over.sum()), xs.max() - xs.min() + 1, ys.max() - ys.min() + 1))
    log("crop x %d-%d, y %d-%d (UV space, counting from the bottom)" % (x0, x1, y0, y1))

    cut = (slice(y0, y1), slice(x0, x1))
    tex = diffuse[cut].copy()
    w = wire[cut]
    o = over[cut]

    panel_wire = np.tile(GROUND, (y1 - y0, x1 - x0, 1))
    panel_wire[w] = WIRE

    panel_both = tex.copy()
    panel_both[w] = panel_both[w] * 0.35 + WIRE * 0.65

    for panel in (tex, panel_wire, panel_both):
        panel[o] = MARK

    scale = max(1, SCALE_TO // (y1 - y0))
    panels = [np.kron(p, np.ones((scale, scale, 1), dtype=np.float32))
              for p in (tex, panel_wire, panel_both)]
    gutter = np.zeros((panels[0].shape[0], 14, 3), dtype=np.float32)

    out = np.concatenate([panels[0], gutter, panels[1], gutter, panels[2]], axis=1)
    save(os.path.join(OUT_DIR, "overlap_%s_detail.png" % segment), out[::-1])


main()
