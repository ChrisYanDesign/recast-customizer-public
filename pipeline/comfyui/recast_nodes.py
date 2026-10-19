"""Two ComfyUI nodes that let a restyle graph work on one segment of the glove.

Everything else in the graph is stock ComfyUI. These two exist because a graph cannot
know what only the asset knows: where a part's UV island sits on its 2048 sheet, which
texture is that part's base, and -- on the way out -- how to put a generated panel back
inside the island without disturbing a pixel outside it.

The manifest is the single source of truth. Nothing here is hand-entered: every number
comes from pipeline/blender/measure_and_mask.py, which measures the mesh.

Two conventions carry through:

- uv_bbox is [x, y, w, h] in IMAGE coordinates -- origin top-left, y increasing
  downwards. The rasteriser flips once on the way out, so everything downstream can
  assume image coordinates.
- ComfyUI's IMAGE is a float tensor shaped [batch, height, width, channels] in 0..1,
  and MASK is [batch, height, width].
"""

import json
import os

import numpy as np
import torch
import torch.nn.functional as F
from PIL import Image

try:
    from .recast_sizing import LONG_EDGE, generation_size
except ImportError:                      # when the file is loaded on its own, as in tests
    from recast_sizing import LONG_EDGE, generation_size

# Where the project lives. The nodes are installed into ComfyUI by a junction, so this
# file's own path points inside custom_nodes rather than the repo.
def _default_project():
    """The repository these nodes belong to.

    realpath follows the junction back out of custom_nodes, and the repo is the folder two
    levels above this one -- the one holding Assets. Falls back to empty rather than to a
    path from whoever wrote this, so a wrong guess says so instead of quietly reading
    somebody else's files.
    """
    from_env = os.environ.get("RECAST_PROJECT")
    if from_env:
        return from_env
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.realpath(__file__))))
    return root if os.path.isdir(os.path.join(root, "Assets")) else ""


DEFAULT_PROJECT = _default_project()
DEFAULT_MANIFEST = "Assets/RecastCustomizer/glove_manifest.json"
FALLBACK_SEGMENTS = ["top", "bottom", "thumb", "tassel"]


def _resolve(project, path):
    """A manifest path, which is always project-relative, as an absolute path."""
    return path if os.path.isabs(path) else os.path.join(project, path)


def _load_manifest(project, manifest):
    with open(_resolve(project, manifest), encoding="utf-8") as fh:
        return json.load(fh)


def _segment(data, segment_id):
    for seg in data["segments"]:
        if seg["id"] == segment_id:
            return seg
    raise ValueError(
        "no segment '%s' in the manifest; it has %s"
        % (segment_id, ", ".join(s["id"] for s in data["segments"]))
    )


def _choices(key, fallback):
    """Widget options read from the default manifest, so the node offers real names.

    Read once, when ComfyUI builds its node list. A manifest that cannot be read must
    not stop the node from loading -- the path is editable on the node itself.
    """
    try:
        data = _load_manifest(DEFAULT_PROJECT, DEFAULT_MANIFEST)
        if key == "segment":
            found = [s["id"] for s in data["segments"]]
        else:
            found = sorted({v for s in data["segments"] for v in s["variants"]})
        return found or fallback
    except Exception:
        return fallback


WHOLE_ISLAND = "whole island"


def _region_choices():
    """Every region name declared on any segment, plus the whole-island default.

    One flat list, because ComfyUI settles a widget's options when it builds its node
    list and cannot narrow them once a segment is picked. Choosing a region a segment
    does not have is rejected at run time, by name.
    """
    names = [WHOLE_ISLAND]
    try:
        data = _load_manifest(DEFAULT_PROJECT, DEFAULT_MANIFEST)
        for seg in data["segments"]:
            for region in seg.get("art_regions", []):
                if region["name"] not in names:
                    names.append(region["name"])
    except Exception:
        pass
    return names


def _read_image(path):
    """A PNG as an IMAGE tensor."""
    rgb = np.asarray(Image.open(path).convert("RGB"), dtype=np.float32) / 255.0
    return torch.from_numpy(rgb)[None, ...]


def _read_mask(path):
    """A mask PNG as a MASK tensor."""
    grey = np.asarray(Image.open(path).convert("L"), dtype=np.float32) / 255.0
    return torch.from_numpy(grey)[None, ...]


def _dilate(mask, texels):
    """Grow a mask outwards by whole texels, with a max filter."""
    if texels <= 0:
        return mask
    k = texels * 2 + 1
    return F.max_pool2d(mask[:, None], kernel_size=k, stride=1, padding=texels)[:, 0]


def _blur(mask, texels):
    """Soften a mask's edge with a box filter of the same reach."""
    if texels <= 0:
        return mask
    k = texels * 2 + 1
    return F.avg_pool2d(mask[:, None], kernel_size=k, stride=1, padding=texels)[:, 0]


class RecastSegmentLoader:
    """Everything the graph needs about one segment-variant, read from the manifest.

    The panel outputs matter as much as the full sheet. A 2048 texture is mostly empty
    space around one irregular island, so a preprocessor or a sampler run over the whole
    sheet spends most of its resolution on nothing. The panel is the island's rectangle,
    which is what gets generated and then placed back.
    """

    CATEGORY = "Recast"
    FUNCTION = "load"
    RETURN_TYPES = ("IMAGE", "IMAGE", "MASK", "MASK", "RECAST_SEGMENT",
                    "INT", "INT", "INT", "INT", "STRING")
    RETURN_NAMES = ("base", "panel", "mask", "panel_mask", "segment_info",
                    "panel_width", "panel_height", "gen_width", "gen_height",
                    "summary")

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "project": ("STRING", {"default": DEFAULT_PROJECT}),
                "manifest": ("STRING", {"default": DEFAULT_MANIFEST}),
                "segment": (_choices("segment", FALLBACK_SEGMENTS),),
                "variant": (_choices("variant", ["01", "02", "03"]),),
                "map_name": (["diffuse", "normal"],),
                # A region restricts the work to one painted emblem inside the island,
                # leaving the leather, the border and the stitching alone. WHOLE_ISLAND
                # is the old behaviour: repaint the entire part.
                "region": (_region_choices(),),
                # Wire gen_width/gen_height into the resize rather than typing numbers
                # in: the four islands are different shapes, and a size left over from
                # another part stretches this one out of proportion on the way back.
                "long_edge": ("INT", {"default": LONG_EDGE, "min": 512, "max": 2048,
                                      "step": 64}),
            }
        }

    def load(self, project, manifest, segment, variant, map_name,
             region=WHOLE_ISLAND, long_edge=LONG_EDGE):
        data = _load_manifest(project, manifest)
        seg = _segment(data, segment)

        if variant not in seg["variants"]:
            raise ValueError(
                "segment '%s' has no variant '%s'; it has %s"
                % (segment, variant, ", ".join(seg["variants"]))
            )

        base_path = _resolve(project, seg["maps"][variant][map_name])
        mask_path = _resolve(project, seg["uv_mask"])
        base = _read_image(base_path)
        mask = _read_mask(mask_path)

        if base.shape[1:3] != mask.shape[1:3]:
            raise ValueError(
                "the mask is %dx%d but the texture is %dx%d; re-run the measure step"
                % (mask.shape[2], mask.shape[1], base.shape[2], base.shape[1])
            )

        x, y, w, h = (int(v) for v in seg["uv_bbox"])
        if w <= 0 or h <= 0:
            raise ValueError("segment '%s' has an empty UV bounding box" % segment)
        scope = "island"

        # A region swaps in a smaller rectangle and its own mask. Nothing downstream
        # changes: the composite places into whatever rectangle it is given and blends
        # with whatever mask, so its guarantee holds against the emblem's edge exactly
        # as it held against the island's.
        if region and region != WHOLE_ISLAND:
            declared = {r["name"]: r for r in seg.get("art_regions", [])}
            if region not in declared:
                raise ValueError(
                    "segment '%s' has no region '%s'; it has %s. Run "
                    "pipeline/find_art_regions.py to propose some."
                    % (segment, region,
                       ", ".join(declared) if declared else "none"))
            chosen = declared[region]
            x, y, w, h = (int(v) for v in chosen["rect"])
            mask = _read_mask(_resolve(project, chosen["mask"]))
            if mask.shape[1:3] != base.shape[1:3]:
                raise ValueError("the region mask is not the size of the sheet")
            scope = region

        panel = base[:, y:y + h, x:x + w, :]
        panel_mask = mask[:, y:y + h, x:x + w]

        info = {
            "asset": data.get("asset", "asset"),
            "segment": segment,
            "variant": variant,
            "map_name": map_name,
            "region": scope,
            "bbox": [x, y, w, h],
            "project": project,
            "base_path": base_path,
            "mask_path": mask_path,
            "texture_size": int(base.shape[1]),
        }

        gen_w, gen_h = generation_size(w, h, long_edge)

        covered = float(panel_mask.mean()) * 100.0
        summary = (
            "%s %s %s / %s\n"
            "working on: %s\n"
            "sheet %dx%d, area %dx%d at (%d, %d)\n"
            "generate at %dx%d\n"
            "the mask fills %.1f%% of that rectangle"
            % (info["asset"], segment, variant, map_name, scope,
               base.shape[2], base.shape[1], w, h, x, y, gen_w, gen_h, covered)
        )

        return (base, panel, mask, panel_mask, info, w, h, gen_w, gen_h, summary)


class RecastCompositeExport:
    """Place a generated panel back inside the island, and prove nothing else moved.

    The guarantee is the point of the node: every pixel outside the island, beyond a
    declared padding, is identical to the texture that went in. The node measures that
    itself and says so, rather than leaving it to be believed.

    Padding exists because a GPU samples across an island's edge when it filters and
    when it reads a lower mip, so a restyle that stops exactly at the edge lets the old
    colour bleed back in along every seam. The restyle is grown a few texels past the
    boundary into the empty space around it, which costs nothing and removes the seam.
    """

    CATEGORY = "Recast"
    FUNCTION = "composite"
    RETURN_TYPES = ("IMAGE", "STRING", "STRING")
    RETURN_NAMES = ("texture", "saved_path", "report")
    OUTPUT_NODE = True

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "base": ("IMAGE",),
                "generated": ("IMAGE",),
                "mask": ("MASK",),
                "segment_info": ("RECAST_SEGMENT",),
                "padding": ("INT", {"default": 8, "min": 0, "max": 64}),
                "feather": ("INT", {"default": 2, "min": 0, "max": 32}),
                "strength": ("FLOAT", {"default": 1.0, "min": 0.0, "max": 1.0,
                                       "step": 0.05}),
                "output_path": ("STRING", {
                    "default": "pipeline/out/restyle/"
                               "{asset}_{segment}_{variant}_{map_name}.png"}),
                "save": ("BOOLEAN", {"default": True}),
            }
        }

    def composite(self, base, generated, mask, segment_info, padding, feather,
                  strength, output_path, save):
        info = segment_info
        x, y, w, h = info["bbox"]

        if base.shape[1:3] != mask.shape[1:3]:
            raise ValueError("the base and the mask are different sizes")

        # The generated panel arrives at whatever the sampler produced -- 1024 square is
        # the comfortable size on an 8 GB card -- so it is fitted to the island's
        # rectangle here rather than constraining the graph upstream.
        panel = generated[:1].permute(0, 3, 1, 2)
        if panel.shape[2] != h or panel.shape[3] != w:
            panel = F.interpolate(panel, size=(h, w), mode="bilinear",
                                  align_corners=False)
        panel = panel.permute(0, 2, 3, 1).clamp(0.0, 1.0)

        # The blend region: the island grown by padding, then softened. Softening a mask
        # spreads it outwards by its own radius, so the restyle reaches padding +
        # feather in total and the guarantee below has to be stated against that, not
        # against the padding alone.
        reach = padding + feather
        hard = _dilate(mask[:1], padding)
        blend = _blur(hard, feather).clamp(0.0, 1.0) * float(strength)
        grown = _dilate(mask[:1], reach)

        full = base[:1].clone()
        placed = full.clone()
        placed[:, y:y + h, x:x + w, :] = panel

        alpha = blend[..., None]
        out = full * (1.0 - alpha) + placed * alpha

        # The claim, measured. Nothing beyond the grown island may differ by as much as
        # half a step of 8-bit colour.
        outside = (grown <= 0.0)[..., None].expand_as(out)
        changed = ((out - full).abs() > (0.5 / 255.0)) & outside
        moved = int(changed.sum())

        # The panel only covers its own rectangle, so a mask reaching past that
        # rectangle means a stale bounding box rather than a bad composite. Worth
        # reporting separately: the two faults look identical in the viewport.
        left, top = max(0, x - reach), max(0, y - reach)
        right, bottom = min(out.shape[2], x + w + reach), min(out.shape[1], y + h + reach)
        spill = int((grown[:, :top, :] > 0).sum() + (grown[:, bottom:, :] > 0).sum()
                    + (grown[:, top:bottom, :left] > 0).sum()
                    + (grown[:, top:bottom, right:] > 0).sum())

        saved = ""
        if save:
            name = output_path.format(
                asset=info["asset"], segment=info["segment"],
                variant=info["variant"], map_name=info["map_name"])
            saved = _resolve(info["project"], name)
            os.makedirs(os.path.dirname(saved), exist_ok=True)
            pixels = (out[0].cpu().numpy() * 255.0 + 0.5).astype(np.uint8)
            Image.fromarray(pixels, mode="RGB").save(saved)

        report = (
            "%s %s %s\n"
            "island %dx%d at (%d, %d), grown %d texels (%d padding + %d feather)\n"
            "pixels changed outside the grown island: %s\n"
            "island reaching past its own rectangle: %s texels\n"
            "%s"
            % (info["asset"], info["segment"], info["variant"],
               w, h, x, y, reach, padding, feather,
               "{:,}".format(moved), "{:,}".format(spill),
               ("written to " + saved) if saved else "not written")
        )

        # The report goes to the node's own face as well as down a wire. The guarantee is
        # the reason this node exists, so it should be readable in the graph without
        # wiring anything up, and it is then kept in the run's history.
        return {"ui": {"text": [report]}, "result": (out, saved, report)}


NODE_CLASS_MAPPINGS = {
    "RecastSegmentLoader": RecastSegmentLoader,
    "RecastCompositeExport": RecastCompositeExport,
}

NODE_DISPLAY_NAME_MAPPINGS = {
    "RecastSegmentLoader": "Recast Segment Loader",
    "RecastCompositeExport": "Recast Composite & Export",
}
