"""Finds the painted emblems on a part's sheet, and records them in the manifest.

Everything else measured in this pipeline comes from the mesh: UV islands, bounding
boxes, triangle counts. An emblem is different. Where a motif sits on a panel is an art
decision, not a property of the geometry, so it cannot be measured -- only found, checked
by eye, and then written down.

So this is the one step whose output is meant to be reviewed rather than trusted. It
proposes regions, draws them over the texture for inspection, and writes them into the
manifest where a wrong number can be corrected by hand and will stay corrected.

What it keys on: the figure is the only strongly cool-toned thing on a warm leather
sheet. That holds on variant 01 and not on the others, whose leather is itself cool --
which does not matter, because the three variants share one unwrap, so a region found on
one is in the same place on all three.

    python pipeline/find_art_regions.py                    # propose, draw, do not write
    python pipeline/find_art_regions.py --write            # write them into the manifest

The mask it writes is the figure's own silhouette, generously grown -- not the rectangle.
Compositing into a rectangle leaves a visible square edge where new art meets old; an
organic blob inside the existing field does not.
"""

import argparse
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

HERE = os.path.dirname(os.path.realpath(__file__))
PROJECT = os.path.dirname(HERE)
MANIFEST = os.path.join(PROJECT, "Assets", "RecastCustomizer", "glove_manifest.json")
MASK_DIR = os.path.join(HERE, "masks", "regions")
OUT_DIR = os.path.join(HERE, "out")

REFERENCE_VARIANT = "01"
MIN_TEXELS = 20000
GROW_RECT = 0.70        # the panel runs about this much wider than the figure in it
GROW_MASK = 55          # texels the figure's silhouette is grown by, for the blend
SMOOTH = 21             # closes the gaps between a figure's separate strokes


def figures(rgb):
    """Bounding boxes and a combined silhouette of the cool-toned figures."""
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    cool = (b > 90) & (g > 80) & (r < b - 35) & (g < b + 60)

    labelled, count = ndimage.label(cool, structure=np.ones((3, 3)))
    sizes = ndimage.sum(cool, labelled, range(1, count + 1))

    found = []
    for index, size in enumerate(sizes):
        if size < MIN_TEXELS:
            continue
        ys, xs = np.where(labelled == index + 1)
        found.append({
            "bbox": [int(xs.min()), int(ys.min()),
                     int(xs.max() - xs.min() + 1), int(ys.max() - ys.min() + 1)],
            "texels": int(size),
            "label": index + 1,
        })
    found.sort(key=lambda f: f["bbox"][1])
    return found, labelled


def grow(box, sheet, factor=GROW_RECT):
    x, y, w, h = box
    cx, cy = x + w / 2.0, y + h / 2.0
    pw, ph = w * (1 + factor), h * (1 + factor)
    x0 = max(0, int(cx - pw / 2))
    y0 = max(0, int(cy - ph / 2))
    x1 = min(sheet, int(cx + pw / 2))
    y1 = min(sheet, int(cy + ph / 2))
    return [x0, y0, x1 - x0, y1 - y0]


def silhouette(labelled, label, sheet):
    """The figure, closed up and grown, as a soft mask the composite can blend with."""
    solid = labelled == label
    solid = ndimage.binary_closing(solid, structure=np.ones((SMOOTH, SMOOTH)),
                                   iterations=3)
    solid = ndimage.binary_fill_holes(solid)
    solid = ndimage.binary_dilation(solid, iterations=GROW_MASK)
    soft = ndimage.gaussian_filter(solid.astype(np.float32), sigma=GROW_MASK / 4.0)
    return np.clip(soft, 0.0, 1.0)


def main():
    p = argparse.ArgumentParser(description="Find the painted emblems on a part")
    p.add_argument("--segment", default="bottom")
    p.add_argument("--variant", default=REFERENCE_VARIANT)
    p.add_argument("--write", action="store_true", help="record them in the manifest")
    args = p.parse_args()

    with open(MANIFEST, encoding="utf-8") as fh:
        manifest = json.load(fh)
    segment = next((s for s in manifest["segments"] if s["id"] == args.segment), None)
    if segment is None:
        raise SystemExit("no segment '%s' in the manifest" % args.segment)

    path = os.path.join(PROJECT, segment["maps"][args.variant]["diffuse"])
    image = Image.open(path).convert("RGB")
    sheet = image.size[0]
    rgb = np.asarray(image, dtype=np.float32)

    found, labelled = figures(rgb)
    if not found:
        raise SystemExit("found no figure on %s %s" % (args.segment, args.variant))

    os.makedirs(MASK_DIR, exist_ok=True)
    os.makedirs(OUT_DIR, exist_ok=True)

    preview = image.copy()
    draw = ImageDraw.Draw(preview)
    regions = []
    for index, figure in enumerate(found, start=1):
        name = "emblem_%02d" % index
        rect = grow(figure["bbox"], sheet)
        mask = silhouette(labelled, figure["label"], sheet)

        rel = "pipeline/masks/regions/%s_%s.png" % (args.segment, name)
        Image.fromarray((mask * 255).astype(np.uint8)).save(os.path.join(PROJECT, rel))

        regions.append({"name": name, "rect": rect, "mask": rel,
                        "found_from_variant": args.variant})

        fx, fy, fw, fh = figure["bbox"]
        draw.rectangle([fx, fy, fx + fw, fy + fh], outline=(90, 160, 255), width=6)
        draw.rectangle([rect[0], rect[1], rect[0] + rect[2], rect[1] + rect[3]],
                       outline=(0, 255, 0), width=10)
        print("%s  figure (%4d,%4d) %4dx%-4d   region (%4d,%4d) %4dx%-4d   mask %s"
              % (name, fx, fy, fw, fh, rect[0], rect[1], rect[2], rect[3],
                 os.path.basename(rel)))

    preview_path = os.path.join(OUT_DIR, "art_regions_%s.png" % args.segment)
    preview.resize((1024, 1024), Image.LANCZOS).save(preview_path)
    print("\npreview: %s" % os.path.relpath(preview_path, PROJECT))

    if not args.write:
        print("not written. Check the preview, then add --write.")
        return 0

    segment["art_regions"] = regions
    # Deliberately not listed among the generated fields. Those are measurements and a
    # hand edit to one is a mistake; this is a proposal, and correcting a number here is
    # the intended way to use it.
    manifest["art_regions_note"] = (
        "Proposed by pipeline/find_art_regions.py and meant to be checked by eye. Where "
        "a motif sits on a panel is an art decision, not something the mesh knows, so "
        "these may be edited by hand and the edit will stand.")
    with open(MANIFEST, "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2)
        fh.write("\n")
    print("wrote %d region(s) into the manifest for '%s'" % (len(regions), args.segment))
    return 0


if __name__ == "__main__":
    sys.exit(main())
