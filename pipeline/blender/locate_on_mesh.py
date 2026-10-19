"""Show where a UV overlap sits on the model itself.

    blender --background --python pipeline/blender/locate_on_mesh.py -- <segment>

Finds the faces whose UVs cover the overlapping texels, paints them red against the rest
of the glove in grey, and renders a wide view and a close one.

Writes pipeline/out/overlap_<segment>_onmesh_wide.png and _close.png.
"""

import json
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Vector

HERE = os.path.dirname(os.path.realpath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
MANIFEST = os.path.join(PROJECT, "Assets", "RecastCustomizer", "glove_manifest.json")
OUT_DIR = os.path.join(PROJECT, "pipeline", "out")

RES = 2048


def log(msg):
    print("[locate] " + msg, flush=True)


def args_after_dashes():
    return sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def overlap_mask(obj):
    """Which texels more than one triangle covers, and the triangles that caused it."""
    mesh = obj.data
    mesh.calc_loop_triangles()
    layer = mesh.uv_layers.active
    uvs = np.zeros((len(mesh.loops), 2))
    layer.data.foreach_get("uv", uvs.ravel())
    loops = np.zeros((len(mesh.loop_triangles), 3), dtype=np.int32)
    mesh.loop_triangles.foreach_get("loops", loops.ravel())
    tris = uvs[loops]

    counts = np.zeros((RES, RES), dtype=np.uint16)
    boxes = []
    for tri in tris * RES:
        (x0, y0), (x1, y1), (x2, y2) = tri
        mnx, mxx = max(int(np.floor(min(x0, x1, x2))), 0), min(int(np.ceil(max(x0, x1, x2))), RES - 1)
        mny, mxy = max(int(np.floor(min(y0, y1, y2))), 0), min(int(np.ceil(max(y0, y1, y2))), RES - 1)
        boxes.append((mnx, mxx, mny, mxy))
        if mnx > mxx or mny > mxy:
            continue
        den = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
        if abs(den) < 1e-12:
            continue
        gx, gy = np.meshgrid(np.arange(mnx, mxx + 1) + 0.5, np.arange(mny, mxy + 1) + 0.5)
        a = ((y1 - y2) * (gx - x2) + (x2 - x1) * (gy - y2)) / den
        b = ((y2 - y0) * (gx - x2) + (x0 - x2) * (gy - y2)) / den
        counts[mny:mxy + 1, mnx:mxx + 1] += ((a >= 0) & (b >= 0) & (1 - a - b >= 0)).astype(np.uint16)

    over = counts > 1
    ys, xs = np.nonzero(over)
    guilty = set()
    for i, (mnx, mxx, mny, mxy) in enumerate(boxes):
        hit = (xs >= mnx) & (xs <= mxx) & (ys >= mny) & (ys <= mxy)
        if hit.any():
            guilty.add(int(mesh.loop_triangles[i].polygon_index))
    return over, guilty


def flat_material(name, colour):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = False
    mat.diffuse_color = (colour[0], colour[1], colour[2], 1.0)
    return mat


def aim_camera(cam, target, direction, distance, ortho_scale=None):
    """Stand off along `direction` and look back at `target`.

    The close shot uses an orthographic camera: framing a small patch with a perspective
    lens means standing very near the surface, which repeatedly put the camera inside the
    glove and rendered an empty frame.
    """
    d = Vector(direction).normalized()
    cam.location = Vector(target) + d * distance
    cam.rotation_mode = "QUATERNION"
    cam.rotation_quaternion = (-d).to_track_quat("-Z", "Y")
    cam.data.clip_start = 0.1
    cam.data.clip_end = distance * 4.0
    if ortho_scale:
        cam.data.type = "ORTHO"
        cam.data.ortho_scale = ortho_scale
    else:
        cam.data.type = "PERSP"


def render(path, width=1100, height=820):
    scene = bpy.context.scene
    scene.render.resolution_x = width
    scene.render.resolution_y = height
    scene.render.filepath = path
    scene.render.image_settings.file_format = "PNG"
    bpy.ops.render.render(write_still=True)
    log("wrote " + os.path.relpath(path, PROJECT))


def main():
    args = args_after_dashes()
    segment = args[0] if args else "bottom"

    with open(MANIFEST, encoding="utf-8") as fh:
        manifest = json.load(fh)

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.wm.obj_import(filepath=os.path.join(PROJECT, manifest["source_mesh"]),
                          use_split_objects=True, use_split_groups=True)

    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    target_obj = next(o for o in meshes if segment in o.name.lower())

    over, guilty = overlap_mask(target_obj)
    log("%s: %d overlapping texels caused by %d face(s)" % (segment, int(over.sum()), len(guilty)))
    if not guilty:
        return

    grey = flat_material("grey", (0.52, 0.52, 0.55))
    red = flat_material("red", (0.95, 0.12, 0.10))
    for o in meshes:
        o.data.materials.clear()
        o.data.materials.append(grey)
    target_obj.data.materials.append(red)
    for idx in guilty:
        target_obj.data.polygons[idx].material_index = 1

    centre = Vector((0, 0, 0))
    for idx in guilty:
        centre += target_obj.data.polygons[idx].center
    centre /= len(guilty)

    normal = Vector((0, 0, 0))
    for idx in guilty:
        normal += target_obj.data.polygons[idx].normal
    normal.normalize()
    log("faces sit at %s, facing %s"
        % (tuple(round(v, 1) for v in centre), tuple(round(v, 2) for v in normal)))

    lo = Vector((min(min((o.matrix_world @ Vector(c))[i] for c in o.bound_box) for o in meshes)
                 for i in range(3)))
    hi = Vector((max(max((o.matrix_world @ Vector(c))[i] for c in o.bound_box) for o in meshes)
                 for i in range(3)))
    span = (hi - lo).length

    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "MATERIAL"
    scene.render.film_transparent = False
    scene.world = bpy.data.worlds.new("w")
    scene.world.color = (0.08, 0.08, 0.09)

    cam_data = bpy.data.cameras.new("cam")
    cam = bpy.data.objects.new("cam", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam

    aim_camera(cam, (lo + hi) / 2, normal + Vector((0.25, -0.25, 0.1)), span * 0.95)
    render(os.path.join(OUT_DIR, "overlap_%s_onmesh_wide.png" % segment))

    # stand well clear and crop with the lens instead of the distance
    aim_camera(cam, centre, normal + Vector((0.2, -0.2, 0.05)), span * 1.2,
               ortho_scale=span * 0.16)
    render(os.path.join(OUT_DIR, "overlap_%s_onmesh_close.png" % segment))


main()
