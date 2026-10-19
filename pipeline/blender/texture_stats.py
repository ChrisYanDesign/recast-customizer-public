"""Sample every texture beside the mesh and record what each one looks like.

    blender --background --python pipeline/blender/texture_stats.py -- [--manifest p] [--out p]

Writes <out>/out/texture_stats.json: one entry per PNG, with its average colour and a
flag for whether it reads as a tangent-space normal map.

The point is the flag. A normal map plugged into a colour slot is one of the commonest
export mistakes there is, and it is invisible to every check that only reads file names
and sizes -- the file is the right size, in the right place, named correctly. It is simply
the wrong picture. Reading a few pixels is the only way to know.

Each image is scaled to a thumbnail before being read, so this costs very little even
across a folder of 2048 maps.
"""

import json
import os
import sys

import bpy
import numpy as np

HERE = os.path.dirname(os.path.realpath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))

SAMPLE = 64          # the thumbnail each texture is read at
FLAT = np.array([0.5, 0.5, 1.0])


def log(msg):
    print("[textures] " + msg, flush=True)


def arg(flag, default):
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    return argv[argv.index(flag) + 1] if flag in argv else default


def unit_vector_fraction(rgb):
    """How much of the image behaves like a direction rather than a colour.

    A normal map does not store colour, it stores unit vectors: decode a texel with
    2*v - 1 and its length comes out at 1, pointing outward. Artwork has no reason to
    satisfy that and almost never does.

    The first version of this test used the average colour instead, and it failed on the
    one map it was pointed at: the thumb's island covers 4.6% of its sheet, so the
    surrounding edge-bleed dragged the average well away from flat violet. Per-pixel
    geometry does not care how the island is packed.
    """
    v = rgb * 2.0 - 1.0
    length = np.sqrt((v * v).sum(axis=1))
    return float(((np.abs(length - 1.0) < 0.12) & (v[:, 2] > 0.0)).mean())


def looks_like_a_normal_map(fraction):
    """Measured on this kit: the twelve normal maps score 0.21 to 1.00, and the
    twenty-two colour, specular and occlusion maps score 0.00 to 0.02. There is no
    overlap at all, so the threshold sits an order of magnitude clear of both.

    The spread among the normal maps is packing, not doubt -- a part whose island covers
    a few percent of its sheet is mostly edge-bleed, which is not unit-length.
    """
    return fraction > 0.10


def sample(path):
    img = bpy.data.images.load(path)
    try:
        img.colorspace_settings.name = "Non-Color"
        width, height = img.size
        img.scale(SAMPLE, SAMPLE)
        px = np.zeros(SAMPLE * SAMPLE * 4, dtype=np.float32)
        img.pixels.foreach_get(px)
        rgb = px.reshape(-1, 4)[:, :3]
        fraction = unit_vector_fraction(rgb)
        return {
            "size": [width, height],
            "mean_rgb": [round(float(v), 4) for v in rgb.mean(axis=0)],
            "unit_vector_fraction": round(fraction, 4),
            "looks_like_normal_map": bool(looks_like_a_normal_map(fraction)),
        }
    finally:
        bpy.data.images.remove(img)


def main():
    manifest_path = os.path.join(PROJECT, arg("--manifest",
                                              "Assets/RecastCustomizer/glove_manifest.json"))
    out_dir = os.path.join(PROJECT, arg("--out", "pipeline"), "out")
    os.makedirs(out_dir, exist_ok=True)

    with open(manifest_path, encoding="utf-8") as fh:
        manifest = json.load(fh)
    folder = os.path.dirname(os.path.join(PROJECT, manifest["source_mesh"]))

    stats = {}
    flagged = 0
    for name in sorted(os.listdir(folder)):
        if not name.lower().endswith(".png"):
            continue
        rel = os.path.relpath(os.path.join(folder, name), PROJECT).replace("\\", "/")
        try:
            stats[rel] = sample(os.path.join(folder, name))
        except Exception as e:                      # a broken file is the validator's job
            log("could not read %s: %s" % (name, e))
            continue
        if stats[rel]["looks_like_normal_map"]:
            flagged += 1

    out = os.path.join(out_dir, "texture_stats.json")
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(stats, fh, indent=2)
        fh.write("\n")

    log("sampled %d texture(s); %d read as normal maps" % (len(stats), flagged))
    log("wrote " + os.path.relpath(out, PROJECT))


main()
