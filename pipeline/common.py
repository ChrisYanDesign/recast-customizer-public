"""Paths and small file helpers shared by the pipeline scripts."""

import glob
import json
import os
import re
import struct

HERE = os.path.dirname(os.path.realpath(__file__))
PROJECT = os.path.dirname(HERE)

ASSET_DIR = os.path.join(PROJECT, "Assets", "RecastCustomizer")
GLOVE_DIR = os.path.join(ASSET_DIR, "GloveAsset")
MANIFEST = os.path.join(ASSET_DIR, "glove_manifest.json")
OUT_DIR = os.path.join(PROJECT, "pipeline", "out")
MASK_DIR = os.path.join(PROJECT, "pipeline", "masks")

# Suffix on disk for each logical map.
MAP_SUFFIX = {"diffuse": "diff", "normal": "norm", "specular": "spec"}

PNG_COLOUR_TYPES = {
    0: "greyscale",
    2: "RGB",
    3: "indexed",
    4: "greyscale+alpha",
    6: "RGBA",
}


def rel(path):
    """A path relative to the project root, with forward slashes."""
    return os.path.relpath(path, PROJECT).replace("\\", "/")


def png_info(path):
    """Width, height, bit depth and colour type, read from the PNG header.

    Parsed by hand so the pipeline has no image-library dependency; the header is
    fixed-layout and the first chunk is always IHDR.
    """
    with open(path, "rb") as fh:
        head = fh.read(26)
    if head[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG: " + path)
    width, height = struct.unpack(">II", head[16:24])
    return {
        "width": width,
        "height": height,
        "bit_depth": head[24],
        "colour_type": head[25],
        "colour": PNG_COLOUR_TYPES.get(head[25], "unknown"),
    }


def png_size(path):
    info = png_info(path)
    return info["width"], info["height"]


def load_json(path):
    with open(path, encoding="utf-8") as fh:
        return json.load(fh)


def save_json(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(data, fh, indent=2)
        fh.write("\n")


def texture_name(asset, segment, variant, map_name):
    """The filename a map is expected to have, from the project's convention."""
    return "%s_%s_%s_%s.png" % (asset, segment, variant, MAP_SUFFIX[map_name])


# --- Unity asset references --------------------------------------------------
#
# Read straight from the project files rather than through the editor, so the
# validator runs unattended with Unity closed.

_GUID = re.compile(r"guid:\s*([0-9a-f]{32})")


def asset_guid(path):
    """The GUID Unity assigned an asset, from its .meta file, or None."""
    meta = path + ".meta"
    if not os.path.exists(meta):
        return None
    with open(meta, encoding="utf-8", errors="replace") as fh:
        found = _GUID.search(fh.read())
    return found.group(1) if found else None


def referenced_guids(root):
    """Every GUID mentioned by a .mat file under root.

    A material names its textures by GUID, so this is what distinguishes a texture the
    project uses from one that merely sits in it.
    """
    guids = set()
    for mat in glob.glob(os.path.join(root, "**", "*.mat"), recursive=True):
        with open(mat, encoding="utf-8", errors="replace") as fh:
            guids.update(_GUID.findall(fh.read()))
    return guids


def _platform_blocks(text):
    """Each platformSettings entry in a .meta, as (buildTarget, maxTextureSize, overridden)."""
    target = size = overridden = None
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("buildTarget:"):
            if target is not None:
                yield target, size, overridden
            target = stripped.split(":", 1)[1].strip()
            size = overridden = None
        elif target is not None:
            if stripped.startswith("maxTextureSize:"):
                size = int(stripped.split(":", 1)[1].strip())
            elif stripped.startswith("overridden:"):
                overridden = stripped.split(":", 1)[1].strip() == "1"
    if target is not None:
        yield target, size, overridden


def texture_import(path):
    """What Unity will actually build this texture at, from its .meta file.

    A PNG on disk is the source, not the asset: the importer caps it at Max Size, so a
    2048 file set to 1024 ships as 1024 and costs a quarter of the memory.

    The governing value is the DefaultTexturePlatform entry under platformSettings. There
    is a maxTextureSize higher up the file too, in the importer's own settings block, but
    Unity does not keep it in step -- it still read 1024 on textures the editor reported
    as 2048 -- so reading that one gives the wrong answer.

    Returns {"max_size": int or None, "overrides": {platform: size}}.
    """
    meta = path + ".meta"
    if not os.path.exists(meta):
        return {"max_size": None, "overrides": {}}

    with open(meta, encoding="utf-8", errors="replace") as fh:
        text = fh.read()

    default = None
    overrides = {}
    for target, size, overridden in _platform_blocks(text):
        if size is None:
            continue
        if target == "DefaultTexturePlatform":
            default = size
        elif overridden:
            overrides[target] = size

    return {"max_size": default, "overrides": overrides}


def shipped_size(path, width, height, platform=None):
    """The largest edge this texture will have in a build."""
    info = texture_import(path)
    cap = info["overrides"].get(platform, info["max_size"]) if platform else info["max_size"]
    longest = max(width, height)
    return longest if cap is None else min(longest, cap)
