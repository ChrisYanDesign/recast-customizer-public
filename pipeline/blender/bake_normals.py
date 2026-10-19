"""Bake the detail lost in decimation from the dense mesh onto the lowpoly.

    blender --background --python pipeline/blender/bake_normals.py -- [segment ...]

With no arguments it bakes every part. Output goes to pipeline/out/bake/, never into the
authored GloveAsset folder.

What this captures is only the *geometric* difference between the two meshes -- the
rounding the decimator flattened. It is not the painted surface detail, which already
lives in the authored normal maps. Combining the two is a separate step; see blend_normal.py.
"""

import json
import os
import sys

import bpy
import numpy as np

HERE = os.path.dirname(os.path.realpath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
GLOVE = os.path.join(PROJECT, "Assets", "RecastCustomizer", "GloveAsset")
OUT = os.path.join(PROJECT, "pipeline", "out", "bake")

# The dense mesh is no longer a Unity asset -- the lowpoly replaced it under that
# name -- but the bake still needs something to bake from, so it is kept in
# backup/ where Unity will not import it.
HIGH = os.path.join(PROJECT, "backup", "falcon_glove_highpoly.obj")
LOW = os.path.join(GLOVE, "falcon_glove.obj")

RES = 2048
MARGIN = 24            # bleed past the island edge, so mipmaps do not pull in background
SEGMENTS = ("top", "bottom", "thumb", "tassel")


def log(msg):
    print("[bake] " + msg, flush=True)


def argv_after_dashes():
    return sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def segment_of(name):
    low = name.lower()
    for seg in SEGMENTS:
        if seg in low:
            return seg
    return None


def import_tagged(path, tag):
    """Import an OBJ and return {segment: object}, renamed so the two sets cannot clash."""
    before = set(bpy.context.scene.objects)
    bpy.ops.wm.obj_import(filepath=path, use_split_objects=True, use_split_groups=True)
    found = {}
    for obj in set(bpy.context.scene.objects) - before:
        if obj.type != "MESH":
            continue
        seg = segment_of(obj.name)
        if seg is None:
            bpy.data.objects.remove(obj, do_unlink=True)
            continue
        obj.name = "%s_%s" % (tag, seg)
        found[seg] = obj
    return found


def diagonal(obj):
    co = np.zeros(len(obj.data.vertices) * 3)
    obj.data.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    return float(np.linalg.norm(co.max(0) - co.min(0)))


def bake_segment(seg, high, low, ext_mul, ray_mul, suffix=""):
    img = bpy.data.images.new("bake_" + seg, width=RES, height=RES,
                              alpha=False, float_buffer=False)
    img.colorspace_settings.name = "Non-Color"

    mat = bpy.data.materials.new("bake_mat_" + seg)
    mat.use_nodes = True
    node = mat.node_tree.nodes.new("ShaderNodeTexImage")
    node.image = img
    mat.node_tree.nodes.active = node
    low.data.materials.clear()
    low.data.materials.append(mat)

    bpy.ops.object.select_all(action="DESELECT")
    high.select_set(True)
    low.select_set(True)
    bpy.context.view_layer.objects.active = low

    # The two meshes sit on top of each other, so rays only need to cross the gap the
    # decimator opened. Scaled to the part rather than fixed, so a small part is not
    # given a ray long enough to hit the far side of itself.
    span = diagonal(low)
    bake = bpy.context.scene.render.bake
    bake.use_selected_to_active = True
    bake.cage_extrusion = span * ext_mul
    bake.max_ray_distance = span * ray_mul
    bake.margin = MARGIN
    bake.use_clear = True

    log("%s: extrusion=%.3f ray=%.3f (part diagonal %.2f)"
        % (seg, bake.cage_extrusion, bake.max_ray_distance, span))
    bpy.ops.object.bake(type="NORMAL")

    path = os.path.join(OUT, "falcon_glove_%s_baked_norm%s.png" % (seg, suffix))
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()

    px = np.zeros(RES * RES * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    px = px.reshape(-1, 4)[:, :3]
    # A flat tangent-space normal is (0.5, 0.5, 1). Anything else is captured detail.
    moved = np.linalg.norm(px - np.array([0.5, 0.5, 1.0]), axis=1)
    log("%s: wrote %s -- %.1f%% of texels carry detail, strongest %.3f"
        % (seg, os.path.relpath(path, PROJECT),
           100.0 * (moved > 0.02).mean(), float(moved.max())))

    bpy.data.images.remove(img)


def main():
    args = argv_after_dashes()
    wanted = [a for a in args if a in SEGMENTS] or list(SEGMENTS)

    def opt(flag, default):
        return float(args[args.index(flag) + 1]) if flag in args else default

    ext_mul = opt("--ext", 0.004)
    ray_mul = opt("--ray", 0.008)
    suffix = args[args.index("--suffix") + 1] if "--suffix" in args else ""
    os.makedirs(OUT, exist_ok=True)

    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 1
    scene.cycles.use_denoising = False

    highs = import_tagged(HIGH, "high")
    lows = import_tagged(LOW, "low")

    for seg in wanted:
        if seg not in highs or seg not in lows:
            log("%s: missing from one of the meshes, skipped" % seg)
            continue
        bake_segment(seg, highs[seg], lows[seg], ext_mul, ray_mul, suffix)


main()
