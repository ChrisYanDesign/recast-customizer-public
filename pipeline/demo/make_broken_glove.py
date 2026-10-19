"""Derive a deliberately broken copy of the glove, for demonstrating the validator.

    blender --background --python pipeline/demo/make_broken_glove.py

Writes pipeline/demo/broken_glove/, a complete kit with seven faults injected. Nothing is
hand-made: it is derived from the real asset every time, so it cannot drift away from it,
and it lives outside Assets/ so Unity never imports it.

The faults are the ones that actually happen on the way out of a DCC tool, not inventions.
One of them -- a mesh group carrying a stray selection-set name -- is a bug this project
hit for real, which cost an afternoon before the check existed to catch it.
"""

import hashlib
import json
import os
import shutil
import sys

import bpy
import numpy as np

HERE = os.path.dirname(os.path.realpath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
SRC_MANIFEST = os.path.join(PROJECT, "Assets", "RecastCustomizer", "glove_manifest.json")
DEST = os.path.join(PROJECT, "pipeline", "demo", "broken_glove")
DEST_ASSET = os.path.join(DEST, "GloveAsset")

FAULTS = []


def log(msg):
    print("[broken] " + msg, flush=True)


def fault(n, what):
    FAULTS.append("%d. %s" % (n, what))
    log("fault %d: %s" % (n, what))


# --- images ------------------------------------------------------------------


def load(path):
    img = bpy.data.images.load(path)
    img.colorspace_settings.name = "Non-Color"
    w, h = img.size
    px = np.zeros(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    return px.reshape(h, w, 4)


def save(path, rgba):
    h, w = rgba.shape[:2]
    img = bpy.data.images.new("o", width=w, height=h, alpha=False, float_buffer=False)
    img.colorspace_settings.name = "Non-Color"
    img.pixels.foreach_set(np.ascontiguousarray(rgba, dtype=np.float32).ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)


def resample(rgba, width, height):
    """Nearest-neighbour, which is all a broken export deserves."""
    h, w = rgba.shape[:2]
    ys = np.clip((np.arange(height) * h / height).astype(int), 0, h - 1)
    xs = np.clip((np.arange(width) * w / width).astype(int), 0, w - 1)
    return rgba[ys][:, xs]


# --- unity bookkeeping -------------------------------------------------------


def fake_guid(name):
    return hashlib.md5(("broken/" + name).encode()).hexdigest()


def write_meta(path):
    with open(path + ".meta", "w", encoding="utf-8") as fh:
        fh.write("fileFormatVersion: 2\nguid: %s\n" % fake_guid(os.path.basename(path)))


def write_material(folder, referenced):
    with open(os.path.join(folder, "falcon_glove_broken_mat.mat"), "w", encoding="utf-8") as fh:
        fh.write("Material:\n  m_Name: falcon_glove_broken_mat\n")
        for name in sorted(referenced):
            fh.write("  - m_Texture: {fileID: 2800000, guid: %s, type: 3}\n" % fake_guid(name))


# --- the mesh ----------------------------------------------------------------


def write_mesh(src_obj, dest_obj):
    """Copy the OBJ, renaming one group and pushing one part's UVs out of bounds.

    Done as a text edit so everything else in the file is byte-identical to the real
    mesh -- the demo differs only by the faults, not by an export round trip.
    """
    with open(src_obj, encoding="utf-8", errors="replace") as fh:
        lines = fh.read().split("\n")

    # which vt entries belong to the tassel, so only its island moves
    group = None
    tassel_vt = set()
    vt_index = 0
    vt_line_of = {}
    for i, line in enumerate(lines):
        if line.startswith("vt "):
            vt_index += 1
            vt_line_of[vt_index] = i
        elif line.startswith("g "):
            group = line[2:].strip()
        elif line.startswith("f ") and group and "tassel" in group:
            for token in line[2:].split():
                bits = token.split("/")
                if len(bits) > 1 and bits[1]:
                    tassel_vt.add(int(bits[1]))

    for idx in tassel_vt:
        i = vt_line_of.get(idx)
        if i is None:
            continue
        parts = lines[i].split()
        u, v = float(parts[1]), float(parts[2])
        lines[i] = "vt %.6f %.6f" % (u, v + 0.75)      # pushed off the top of the map
    fault(7, "the tassel's UVs are pushed outside the 0-1 square (%d coordinates moved)"
          % len(tassel_vt))

    # One small gap at a single edge of the bottom, not damage scattered over the whole
    # part. A validator earns its keep on things that slip past a review; a glove that
    # looks shredded would be sent back before anyone ran a check on it.
    verts = []
    group = None
    bottom_faces = []
    for i, line in enumerate(lines):
        if line.startswith("v "):
            bits = line.split()
            verts.append((float(bits[1]), float(bits[2]), float(bits[3])))
        elif line.startswith("g "):
            group = line[2:].strip()
        elif line.startswith("f ") and group and "bottom" in group:
            idx = []
            for token in line[2:].split():
                first = token.split("/")[0]
                if first:
                    idx.append(int(first))
            if idx:
                bottom_faces.append((i, idx))

    holes = set()
    if bottom_faces:
        def centre(idx):
            pts = [verts[j - 1] for j in idx if 0 < j <= len(verts)]
            if not pts:
                return None
            n = len(pts)
            return (sum(p[0] for p in pts) / n, sum(p[1] for p in pts) / n)

        centres = [(i, centre(idx)) for i, idx in bottom_faces]
        centres = [(i, c) for i, c in centres if c is not None]
        if centres:
            # the lower-left corner of the part, in model space
            xs = [c[0] for _, c in centres]
            ys = [c[1] for _, c in centres]
            anchor = (min(xs), min(ys))
            centres.sort(key=lambda e: (e[1][0] - anchor[0]) ** 2 + (e[1][1] - anchor[1]) ** 2)
            holes = {i for i, _ in centres[:9]}

    for i in holes:
        lines[i] = None
    fault(8, "%d faces are missing from one edge of the bottom, leaving a small gap"
          % len(holes))

    out = []
    for line in lines:
        if line is None:
            continue
        if line.strip() == "g falcon_glove_bottom":
            out.append("g textureEditorIsolateSelectSet falcon_glove_bottom")
        else:
            out.append(line)
    fault(6, "the bottom mesh group carries a stray selection-set name, so the engine "
             "cannot bind it")

    with open(dest_obj, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(out))


# --- main --------------------------------------------------------------------


def main():
    with open(SRC_MANIFEST, encoding="utf-8") as fh:
        src = json.load(fh)

    shutil.rmtree(DEST, ignore_errors=True)
    os.makedirs(DEST_ASSET)

    src_glove = os.path.dirname(os.path.join(PROJECT, src["source_mesh"]))
    write_mesh(os.path.join(PROJECT, src["source_mesh"]),
               os.path.join(DEST_ASSET, "falcon_glove.obj"))

    asset = src["asset"]
    referenced = []

    for seg in src["segments"]:
        sid = seg["id"]
        for variant in seg["variants"]:
            for kind, suffix in (("diffuse", "diff"), ("normal", "norm"), ("specular", "spec")):
                name = "%s_%s_%s_%s.png" % (asset, sid, variant, suffix)
                source = os.path.join(src_glove, name)
                if not os.path.exists(source):
                    continue

                if sid == "thumb" and variant == "02" and kind == "normal":
                    fault(4, "thumb variant 02 has no normal map")
                    continue

                dest_name = name
                if sid == "bottom" and variant == "01" and kind == "diffuse":
                    dest_name = "%s_%s_%s_%s.png" % (asset, sid, "1", suffix)
                    fault(3, "a diffuse map is named %s -- one digit, not two" % dest_name)

                dest = os.path.join(DEST_ASSET, dest_name)

                if sid == "bottom" and variant == "03" and kind == "diffuse":
                    px = load(source)
                    save(dest, np.kron(px, np.ones((2, 2, 1), dtype=np.float32)))
                    fault(1, "a diffuse map was exported at 4096, over the 2048 ceiling")
                elif sid == "thumb" and variant == "01" and kind == "diffuse":
                    # the normal map saved into the colour slot: right name, right size,
                    # wrong picture
                    shutil.copyfile(
                        os.path.join(src_glove, "%s_thumb_01_norm.png" % asset), dest)
                    fault(9, "a normal map has been saved as a diffuse map -- the file is "
                             "named and sized correctly, it is simply the wrong picture")
                elif sid == "tassel" and variant == "02" and kind == "diffuse":
                    save(dest, resample(load(source), 1000, 1000))
                    fault(2, "a diffuse map is 1000x1000 -- not a power of two")
                else:
                    shutil.copyfile(source, dest)

                write_meta(dest)
                referenced.append(dest_name)

        if seg.get("occlusion"):
            ao = os.path.basename(seg["occlusion"])
            shutil.copyfile(os.path.join(src_glove, ao), os.path.join(DEST_ASSET, ao))
            write_meta(os.path.join(DEST_ASSET, ao))
            referenced.append(ao)

    orphan = "%s_tassel_01_spec.png" % asset
    if orphan in referenced:
        referenced.remove(orphan)
        fault(5, "a specular map sits in the folder that no material references")

    write_material(DEST_ASSET, referenced)

    manifest = {
        "asset": asset,
        "displayName": "Falconer's Glove (broken export)",
        "sourceMesh": "GloveAsset/falcon_glove.obj",
        "source_mesh": os.path.relpath(os.path.join(DEST_ASSET, "falcon_glove.obj"),
                                       PROJECT).replace("\\", "/"),
        "materialNamePattern": src.get("materialNamePattern"),
        "segments": [{k: v for k, v in seg.items()
                      if k in ("id", "label", "childRenderer", "variants")}
                     for seg in src["segments"]],
    }
    with open(os.path.join(DEST, "manifest.json"), "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2)
        fh.write("\n")

    with open(os.path.join(DEST, "FAULTS.md"), "w", encoding="utf-8") as fh:
        fh.write("# Injected faults\n\nGenerated by `pipeline/demo/make_broken_glove.py` "
                 "from the real glove. Each one is a mistake that happens on the way out "
                 "of a DCC tool.\n\n")
        for f in sorted(set(FAULTS)):
            fh.write("- %s\n" % f[3:])

    log("wrote %s with %d faults" % (os.path.relpath(DEST, PROJECT), len(set(FAULTS))))


main()
