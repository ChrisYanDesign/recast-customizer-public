"""Builds the restyle graph, and optionally runs it.

The graph is written, not clicked. A hand-wired graph is a screenshot; a generated one is
reproducible -- the same segment restyled at three strengths is three runs of this script
rather than three careful passes over a canvas, and the numbers in the write-up can be
traced to a command.

What it builds, in order:

    Recast Segment Loader   the part's base texture, its UV mask, its island
    ImageScale              the island fitted to a size SDXL is comfortable at
    Canny                   the panel's own edges: seams, stitching, panel breaks
    ControlNetApplyAdvanced those edges, holding the structure during generation
    VAEEncode + KSampler    image-to-image; denoise IS the restyle strength
    VAEDecode
    Recast Composite        the result back inside the island, nothing else touched

Usage:

    python pipeline/comfyui/build_graph.py --segment top --variant 01 --run
    python pipeline/comfyui/build_graph.py --segment top --strength 0.35 --run
"""

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.realpath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
MANIFEST = "Assets/RecastCustomizer/glove_manifest.json"
WORKFLOWS = os.path.join(HERE, "workflows")

CHECKPOINT = "sd_xl_base_1.0.safetensors"
CONTROLNET = "control-lora-canny-rank256.safetensors"

# The style the restyle aims at, in the asset's own terms. Not a description of a
# photograph: these are hand-painted game textures, read under the engine's own lighting,
# so baked-in shadow and highlight are faults rather than detail.
POSITIVE = ("ornate falcon-themed leather gauntlet panel, tooled dark leather, "
            "brass filigree and rivets, stitched seams, hand-painted stylised game "
            "texture, flat even lighting, crisp detail")
# The asset's own idiom, for restyling an emblem rather than a whole part. Named colours
# are left out on purpose: the drawing style is what has to match, and pinning the palette
# too would stop a new motif from being its own colour.
EMBLEM_STYLE = ("flat ancient Egyptian tomb painting, bold dark outlines, solid flat "
                "colour fills, no shading, no gradients, no specular highlights, "
                "stylised decorative profile, painted on a plain field")
EMBLEM_NEGATIVE = ("photograph, 3d render, realistic, volumetric shading, soft shadows, "
                   "gradients, depth of field, text, watermark, frame, border")

NEGATIVE = ("photograph, baked shadows, strong highlights, text, watermark, signature, "
            "blurry, border, frame, empty background")


sys.path.insert(0, HERE)
from recast_sizing import LONG_EDGE, generation_size  # noqa: E402


def island(segment_id, manifest=MANIFEST):
    with open(os.path.join(PROJECT, manifest), encoding="utf-8") as fh:
        data = json.load(fh)
    for seg in data["segments"]:
        if seg["id"] == segment_id:
            return seg
    raise SystemExit("no segment '%s' in the manifest; it has %s"
                     % (segment_id, ", ".join(s["id"] for s in data["segments"])))


def build(args):
    seg = island(args.segment)
    rect = seg["uv_bbox"]
    if args.region and args.region != "whole island":
        declared = {r["name"]: r for r in seg.get("art_regions", [])}
        if args.region not in declared:
            raise SystemExit("segment '%s' has no region '%s'; it has %s"
                             % (args.segment, args.region,
                                ", ".join(declared) if declared else "none"))
        rect = declared[args.region]["rect"]
    _, _, w, h = (int(v) for v in rect)
    gen_w, gen_h = generation_size(w, h, args.long_edge)

    graph = {
        "1": {"class_type": "CheckpointLoaderSimple",
              "inputs": {"ckpt_name": args.checkpoint}},

        "2": {"class_type": "RecastSegmentLoader",
              "inputs": {"project": PROJECT, "manifest": MANIFEST,
                         "segment": args.segment, "variant": args.variant,
                         "map_name": args.map_name, "region": args.region,
                         "long_edge": args.long_edge}},

        # The island's rectangle, fitted to a size the sampler handles well. The whole
        # 2048 sheet is mostly empty space, and generating over it would spend most of
        # the resolution on nothing.
        #
        # The size is taken from the loader rather than written in, so switching the
        # segment on the canvas resizes with it. The four islands are different shapes
        # -- the top is 1260x661, the bottom 1964x1664 -- and one part's numbers used on
        # another stretch it out of proportion on the way back.
        "3": {"class_type": "ImageScale",
              "inputs": {"image": ["2", 1], "upscale_method": "lanczos",
                         "width": ["2", 7], "height": ["2", 8], "crop": "disabled"}},

        # The structure that has to survive the restyle: seams, stitching, panel edges.
        "4": {"class_type": "Canny",
              "inputs": {"image": ["3", 0],
                         "low_threshold": args.canny_low,
                         "high_threshold": args.canny_high}},

        "5": {"class_type": "ControlNetLoader",
              "inputs": {"control_net_name": args.controlnet}},

        "6": {"class_type": "CLIPTextEncode",
              "inputs": {"text": args.prompt, "clip": ["1", 1]}},
        "7": {"class_type": "CLIPTextEncode",
              "inputs": {"text": args.negative, "clip": ["1", 1]}},

        "8": {"class_type": "ControlNetApplyAdvanced",
              "inputs": {"positive": ["6", 0], "negative": ["7", 0],
                         "control_net": ["5", 0], "image": ["4", 0],
                         "strength": args.control_strength,
                         "start_percent": 0.0, "end_percent": args.control_end}},

        # Image-to-image rather than generating from nothing, so the existing paint is
        # the starting point and denoise is the restyle strength: low keeps the original
        # read, high reinvents it.
        "9": {"class_type": "VAEEncode",
              "inputs": {"pixels": ["3", 0], "vae": ["1", 2]}},

        "10": {"class_type": "KSampler",
               "inputs": {"model": ["1", 0], "positive": ["8", 0], "negative": ["8", 1],
                          "latent_image": ["9", 0], "seed": args.seed,
                          "steps": args.steps, "cfg": args.cfg,
                          "sampler_name": args.sampler, "scheduler": args.scheduler,
                          "denoise": args.strength}},

        "11": {"class_type": "VAEDecode",
               "inputs": {"samples": ["10", 0], "vae": ["1", 2]}},

        "12": {"class_type": "RecastCompositeExport",
               "inputs": {"base": ["2", 0], "generated": ["11", 0], "mask": ["2", 2],
                          "segment_info": ["2", 4],
                          "padding": args.padding, "feather": args.feather,
                          "strength": 1.0, "output_path": args.output_path,
                          "save": True}},

        # For running the graph by hand on the canvas. They cost nothing when the graph
        # is submitted over the API, and without them someone driving it in ComfyUI sees
        # the texture only as a file appearing in a folder.
        "13": {"class_type": "PreviewImage", "inputs": {"images": ["12", 0]}},
        "14": {"class_type": "PreviewImage", "inputs": {"images": ["4", 0]}},
    }
    return graph, (w, h, gen_w, gen_h)


def submit(graph, server, poll_seconds):
    body = json.dumps({"prompt": graph}).encode("utf-8")
    request = urllib.request.Request(server + "/prompt", data=body,
                                     headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            queued = json.load(response)
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")
        raise SystemExit("ComfyUI rejected the graph:\n" + detail)
    except urllib.error.URLError as e:
        raise SystemExit("could not reach ComfyUI at %s (%s). Is it running?"
                         % (server, e.reason))

    prompt_id = queued["prompt_id"]
    print("queued as %s; waiting" % prompt_id, flush=True)

    deadline = time.time() + poll_seconds
    while time.time() < deadline:
        time.sleep(2)
        with urllib.request.urlopen(server + "/history/" + prompt_id, timeout=30) as r:
            history = json.load(r)
        entry = history.get(prompt_id)
        if not entry:
            continue
        status = entry.get("status", {})
        if status.get("completed") or status.get("status_str") == "success":
            return entry
        if status.get("status_str") == "error":
            for kind, data in status.get("messages", []):
                if kind == "execution_error":
                    raise SystemExit("the graph failed in %s (%s): %s"
                                     % (data.get("node_type"), data.get("node_id"),
                                        data.get("exception_message")))
            raise SystemExit("the graph failed; see ComfyUI's log")
    raise SystemExit("gave up after %d seconds" % poll_seconds)


def main():
    p = argparse.ArgumentParser(description="Build, and optionally run, the restyle graph")
    p.add_argument("--segment", default="top")
    p.add_argument("--variant", default="01")
    p.add_argument("--map-name", default="diffuse", choices=["diffuse", "normal"])
    p.add_argument("--region", default="whole island",
                   help="an emblem declared in the manifest, or 'whole island'")
    p.add_argument("--strength", type=float, default=0.55,
                   help="the restyle strength: how far from the original paint (0-1)")
    p.add_argument("--long-edge", type=int, default=LONG_EDGE,
                   help="longest edge to generate at; higher is sharper and needs more VRAM")
    p.add_argument("--match-style", action="store_true",
                   help="append the asset's own drawing style to the prompt, so a new "
                        "motif is painted the way the existing art is")
    p.add_argument("--replace-art", action="store_true",
                   help="change the pattern, not only the material: high denoise and a "
                        "loose grip on the existing drawing")
    p.add_argument("--steps", type=int, default=28)
    p.add_argument("--cfg", type=float, default=6.5)
    p.add_argument("--seed", type=int, default=12345)
    p.add_argument("--sampler", default="dpmpp_2m")
    p.add_argument("--scheduler", default="karras")
    p.add_argument("--control-strength", type=float, default=0.8)
    p.add_argument("--control-end", type=float, default=0.8)
    p.add_argument("--canny-low", type=float, default=0.2)
    p.add_argument("--canny-high", type=float, default=0.5)
    p.add_argument("--padding", type=int, default=8)
    p.add_argument("--feather", type=int, default=2)
    p.add_argument("--prompt", default=POSITIVE)
    p.add_argument("--negative", default=NEGATIVE)
    p.add_argument("--checkpoint", default=CHECKPOINT)
    p.add_argument("--controlnet", default=CONTROLNET)
    p.add_argument("--output-path",
                   default="pipeline/out/restyle/"
                           "{asset}_{segment}_{variant}_{map_name}_restyled.png")
    p.add_argument("--server", default="http://127.0.0.1:8188")
    p.add_argument("--timeout", type=int, default=900)
    p.add_argument("--run", action="store_true", help="submit it, not just write it")
    args = p.parse_args()

    # Two jobs pull in opposite directions. Keeping the art and changing the material
    # wants the existing drawing held hard; drawing something new wants it let go. The
    # defaults do the first, because it is the one that keeps an asset usable; this
    # switch does the second, and gives up alignment with the old seams to do it.
    # An emblem swap is a style-match job: the motif changes, the drawing style must not.
    if args.match_style:
        if args.prompt == p.get_default("prompt"):
            raise SystemExit("--match-style needs --prompt describing the new motif, "
                             "e.g. --prompt \"a dragon head in profile\"")
        args.prompt = args.prompt.rstrip(", ") + ", " + EMBLEM_STYLE
        if args.negative == p.get_default("negative"):
            args.negative = EMBLEM_NEGATIVE

    if args.replace_art:
        if args.strength == p.get_default("strength"):
            args.strength = 0.95
        if args.control_strength == p.get_default("control_strength"):
            args.control_strength = 0.30
        if args.control_end == p.get_default("control_end"):
            args.control_end = 0.40

    graph, (w, h, gen_w, gen_h) = build(args)

    os.makedirs(WORKFLOWS, exist_ok=True)
    tag = args.region.replace(" ", "_") if args.region != "whole island" else "island"
    name = "recast_restyle_%s_%s_%s.json" % (args.segment, args.variant, tag)
    path = os.path.join(WORKFLOWS, name)
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(graph, fh, indent=2)
        fh.write("\n")

    print("segment  : %s %s (%s)" % (args.segment, args.variant, args.map_name))
    print("island   : %dx%d, generated at %dx%d" % (w, h, gen_w, gen_h))
    print("strength : %.2f restyle, %.2f control" % (args.strength, args.control_strength))
    print("graph    : %s" % os.path.relpath(path, PROJECT))

    if not args.run:
        print("\nnot submitted. Add --run, or open that file in ComfyUI.")
        return 0

    entry = submit(graph, args.server, args.timeout)

    print()
    reported = entry.get("outputs", {}).get("12", {}).get("text")
    if reported:
        print("\n".join(reported))
    else:
        print("the run finished but the composite node reported nothing; "
              "check ComfyUI's log")
    return 0


if __name__ == "__main__":
    sys.exit(main())
