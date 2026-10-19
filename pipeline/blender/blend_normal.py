"""Combine a baked geometry normal map with an authored detail normal map.

    blender --background --python pipeline/blender/blend_normal.py -- <baked.png> <authored.png> <out.png>

Uses the whiteout blend, which keeps both contributions readable instead of letting the
stronger one flatten the other:

    n = normalize( xy(a) + xy(b), z(a) * z(b) )

Where the baked map is flat -- everywhere outside the part's UV island -- the result is
the authored map unchanged, so the padding the artist baked is preserved exactly.

Both images are read as Non-Color. A normal map holds directions, not colour, and reading
it through sRGB would bend every vector.
"""

import os
import sys

import bpy
import numpy as np

HERE = os.path.dirname(os.path.realpath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))

FLAT = np.array([0.5, 0.5, 1.0], dtype=np.float32)


def log(msg):
    print("[blend] " + msg, flush=True)


def load_raw(path):
    img = bpy.data.images.load(path)
    img.colorspace_settings.name = "Non-Color"
    w, h = img.size
    px = np.zeros(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    return px.reshape(h, w, 4), w, h


def save_raw(path, rgba, w, h):
    img = bpy.data.images.new("out", width=w, height=h, alpha=False, float_buffer=False)
    img.colorspace_settings.name = "Non-Color"
    img.pixels.foreach_set(rgba.ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)


def despeckle(rgb, size=3):
    """Median filter over the baked map.

    A selected-to-active bake leaves isolated wrong texels where a ray caught a
    neighbouring shell -- single-texel specks surrounded by correct values. The baked
    detail here is geometric and low-frequency, so a median removes the specks without
    costing anything that matters.
    """
    r = size // 2
    pad = np.pad(rgb, ((r, r), (r, r), (0, 0)), mode="edge")
    stack = np.stack([pad[dy:dy + rgb.shape[0], dx:dx + rgb.shape[1]]
                      for dy in range(size) for dx in range(size)], axis=0)
    return np.median(stack, axis=0).astype(np.float32)


def main():
    args = sys.argv[sys.argv.index("--") + 1:]
    if len(args) < 3:
        raise SystemExit("need: <baked.png> <authored.png> <out.png>")
    baked_path, authored_path, out_path = [os.path.join(PROJECT, a) if not os.path.isabs(a)
                                           else a for a in args[:3]]

    baked, bw, bh = load_raw(baked_path)
    authored, aw, ah = load_raw(authored_path)
    if (bw, bh) != (aw, ah):
        raise SystemExit("sizes differ: baked %dx%d, authored %dx%d" % (bw, bh, aw, ah))

    size = int(args[3]) if len(args) > 3 else 3
    clean = despeckle(baked[..., :3], size)
    specks = int((np.linalg.norm(clean - baked[..., :3], axis=-1) > 0.05).sum())
    log("despeckled the bake with a %dx%d median: %s texels replaced"
        % (size, size, "{:,}".format(specks)))

    a = clean * 2.0 - 1.0                 # geometry, from the bake
    b = authored[..., :3] * 2.0 - 1.0     # painted surface detail

    xy = a[..., :2] + b[..., :2]
    z = a[..., 2:3] * b[..., 2:3]
    n = np.concatenate([xy, z], axis=-1)
    n /= np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-8)

    out = np.ones((bh, bw, 4), dtype=np.float32)
    out[..., :3] = n * 0.5 + 0.5

    # how much the authored map actually changed, and where
    moved = np.linalg.norm(out[..., :3] - authored[..., :3], axis=-1)
    touched = moved > 1.0 / 255.0
    flat_in_bake = np.linalg.norm(baked[..., :3] - FLAT, axis=-1) < 0.01
    log("authored map changed on %.2f%% of texels; strongest change %.3f"
        % (100.0 * touched.mean(), float(moved.max())))
    log("texels where the bake was flat and the authored map survived untouched: %.2f%%"
        % (100.0 * (flat_in_bake & ~touched).mean()))

    save_raw(out_path, out, bw, bh)
    log("wrote " + os.path.relpath(out_path, PROJECT))


main()
