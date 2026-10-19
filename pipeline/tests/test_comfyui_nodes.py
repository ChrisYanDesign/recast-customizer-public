"""Proves the two ComfyUI nodes do what they claim, without ComfyUI running.

The nodes are plain Python -- they take tensors and return tensors -- so they can be
driven directly. What is worth proving is the guarantee the composite node makes:

    every pixel outside the island, beyond the declared padding, is unchanged

and its opposite, which is easy to pass by accident: that pixels *inside* the island
actually changed. A node that returns its input untouched satisfies the first claim
perfectly and is useless.

Run it with ComfyUI's own interpreter, which is where torch lives:

    <ComfyUI>/.venv/Scripts/python.exe pipeline/tests/test_comfyui_nodes.py
"""

import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.realpath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
MANIFEST = "Assets/RecastCustomizer/glove_manifest.json"


def _load_module():
    folder = os.path.join(PROJECT, "pipeline", "comfyui")
    if folder not in sys.path:
        sys.path.insert(0, folder)      # recast_nodes imports recast_sizing beside it
    path = os.path.join(folder, "recast_nodes.py")
    spec = importlib.util.spec_from_file_location("recast_nodes", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


FAILURES = []


def result_of(returned):
    """The composite node returns ComfyUI's dict form, so its report shows on the node."""
    return returned["result"] if isinstance(returned, dict) else returned


def check(label, condition, detail=""):
    mark = "ok  " if condition else "FAIL"
    print("%s %s%s" % (mark, label, ("  -- " + detail) if detail else ""))
    if not condition:
        FAILURES.append(label)


def main():
    import torch

    nodes = _load_module()
    loader = nodes.RecastSegmentLoader()
    composite = nodes.RecastCompositeExport()

    data = nodes._load_manifest(PROJECT, MANIFEST)
    segments = [s["id"] for s in data["segments"]]
    print("segments in the manifest: %s\n" % ", ".join(segments))

    for segment in segments:
        variant = _segment_variants(data, segment)[0]
        (base, panel, mask, panel_mask, info, width, height,
         gen_width, gen_height, summary) = loader.load(PROJECT, MANIFEST, segment,
                                                       variant, "diffuse")

        x, y, w, h = info["bbox"]
        # The generated size has to follow the island's shape, or the result is squashed
        # on the way back. Allowing one 64-pixel step of rounding error either way.
        ratio = (gen_width / float(gen_height)) / (w / float(h))
        check("%-6s generated size keeps the island's shape" % segment,
              0.85 < ratio < 1.18,
              "island %dx%d, generating %dx%d" % (w, h, gen_width, gen_height))
        check("%-6s panel matches the bounding box" % segment,
              panel.shape[1] == h and panel.shape[2] == w,
              "panel %dx%d, bbox %dx%d" % (panel.shape[2], panel.shape[1], w, h))
        check("%-6s the island is not empty" % segment,
              float(panel_mask.max()) > 0.5,
              "island fills %.1f%% of its rectangle" % (float(panel_mask.mean()) * 100))
        check("%-6s reported size matches the panel" % segment,
              (width, height) == (w, h))

        # A flat colour nothing in the texture could coincidentally equal, at a size the
        # sampler would really produce, so the resize path is exercised too.
        generated = torch.zeros((1, 1024, 1024, 3))
        generated[..., 0] = 1.0
        generated[..., 2] = 1.0

        texture, saved, report = result_of(composite.composite(
            base, generated, mask, info, padding=8, feather=2, strength=1.0,
            output_path="", save=False))

        check("%-6s the sheet size is preserved" % segment,
              texture.shape == base.shape)

        # padding 8 plus feather 2: softening the padded mask spreads it by the feather
        # again, so ten texels is the whole reach the node promises to stay inside.
        grown = nodes._dilate(mask[:1], 8 + 2)
        outside = (grown <= 0.0)[..., None].expand_as(texture)
        moved_outside = int((((texture - base[:1]).abs() > (0.5 / 255.0)) & outside).sum())
        check("%-6s nothing changed outside the padded island" % segment,
              moved_outside == 0, "%d pixels" % moved_outside)

        inside = (mask[:1] >= 1.0)[..., None].expand_as(texture)
        moved_inside = int((((texture - base[:1]).abs() > (0.5 / 255.0)) & inside).sum())
        check("%-6s the island itself was restyled" % segment,
              moved_inside > 0, "{:,} pixels".format(moved_inside))

        # strength 0 is the honest no-op, and a useful control in the graph: it proves
        # the blend is a blend rather than a paste.
        untouched, _, _ = result_of(composite.composite(
            base, generated, mask, info, padding=8, feather=2, strength=0.0,
            output_path="", save=False))
        check("%-6s strength 0 changes nothing at all" % segment,
              int(((untouched - base[:1]).abs() > (0.5 / 255.0)).sum()) == 0)

        print()

    # A bad segment or variant must say which names exist rather than fail obscurely.
    for bad, kind in (("wrist", "segment"), ("09", "variant")):
        try:
            loader.load(PROJECT, MANIFEST,
                        "wrist" if kind == "segment" else segments[0],
                        "09" if kind == "variant" else "01", "diffuse")
            check("a bad %s is rejected" % kind, False, "it was accepted")
        except ValueError as e:
            check("a bad %s is rejected" % kind, bad in str(e), str(e).split(";")[0])

    print()
    if FAILURES:
        print("%d check(s) failed: %s" % (len(FAILURES), ", ".join(FAILURES)))
        return 1
    print("every check passed")
    return 0


def _segment_variants(data, segment):
    for seg in data["segments"]:
        if seg["id"] == segment:
            return seg["variants"]
    raise KeyError(segment)


if __name__ == "__main__":
    sys.exit(main())
