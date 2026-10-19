"""Turns a generated graph into one ComfyUI can open on its canvas.

build_graph.py writes the API form -- node ids, class names, and what feeds what. That
is all the server needs, and it is what the server is sent. The canvas needs more: which
inputs are wires and which are widgets, what order the widgets sit in, where each node
is on screen, and a link table.

Rather than keep a second hand-drawn copy of the graph in step with the first, this
derives the canvas form from the API form, asking the running ComfyUI what each node's
inputs actually are. One graph, two renderings of it.

    python pipeline/comfyui/to_ui_workflow.py pipeline/comfyui/workflows/<file>.json --install

--install also drops it in ComfyUI's own workflows folder, where the Workflows panel
lists it.
"""

import argparse
import json
import os
import sys
import urllib.request

HERE = os.path.dirname(os.path.realpath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))

# Wide enough for a long file path on the loader, and for the prompt boxes.
WIDTH = 400
COLUMN = 460
ROW = 60
TITLE = 30
WIDGET_ROW = 24

# The frontend inserts a control for these next to the value itself, and expects the
# extra entry in widgets_values. Leaving it out shifts every later widget by one.
SEEDS = {"seed", "noise_seed"}


def object_info(server):
    with urllib.request.urlopen(server + "/object_info", timeout=60) as response:
        return json.load(response)


def slots(spec):
    """A node class's inputs, in the order the canvas shows them."""
    out = []
    for section in ("required", "optional"):
        for name, definition in (spec["input"].get(section) or {}).items():
            out.append((name, definition[0]))
    return out


def depth_of(graph, node_id, seen=None):
    seen = seen or set()
    if node_id in seen:
        return 0
    seen = seen | {node_id}
    feeders = [v[0] for v in graph[node_id]["inputs"].values()
               if isinstance(v, list) and len(v) == 2 and str(v[0]) in graph]
    if not feeders:
        return 0
    return 1 + max(depth_of(graph, str(f), seen) for f in feeders)


def convert(graph, info):
    depths = {nid: depth_of(graph, nid) for nid in graph}
    order = sorted(graph, key=lambda n: (depths[n], int(n)))
    # Each column stacks downwards from its own running offset. Spacing a row by the
    # height of the node being placed, rather than by what is already in the column,
    # overlaps a tall node onto the short one above it.
    column_y = {}

    nodes = []
    links = []
    link_id = 0
    outputs_used = {}

    # Links first: a node's input slot needs its link id, and the feeding node needs the
    # same id listed on its output.
    for node_id in order:
        node = graph[node_id]
        spec = info[node["class_type"]]
        names = [name for name, _ in slots(spec)]
        for name, value in node["inputs"].items():
            if not (isinstance(value, list) and len(value) == 2 and str(value[0]) in graph):
                continue
            link_id += 1
            source, source_slot = str(value[0]), int(value[1])
            kind = info[graph[source]["class_type"]]["output"][source_slot]
            links.append([link_id, int(source), source_slot, int(node_id),
                          names.index(name), kind])
            node.setdefault("_links", {})[name] = link_id
            outputs_used.setdefault(source, {}).setdefault(source_slot, []).append(link_id)

    for index, node_id in enumerate(order):
        node = graph[node_id]
        spec = info[node["class_type"]]
        column = depths[node_id]

        inputs, widgets = [], []
        for name, kind in slots(spec):
            if name in node.get("_links", {}):
                inputs.append({"name": name, "type": kind,
                               "link": node["_links"][name]})
                continue
            if name not in node["inputs"]:
                continue
            widgets.append(node["inputs"][name])
            if name in SEEDS:
                widgets.append("fixed")

        produced = []
        for slot, kind in enumerate(spec["output"]):
            used = outputs_used.get(node_id, {}).get(slot)
            produced.append({
                "name": spec.get("output_name", spec["output"])[slot],
                "type": kind,
                "links": used,
                "slot_index": slot,
            })

        # A text box is taller than its one widget row suggests, and a long prompt is
        # the usual cause of an overlap.
        long_text = sum(1 for w in widgets if isinstance(w, str) and len(w) > 60)
        height = (TITLE + max(len(inputs), 1) * WIDGET_ROW
                  + len(widgets) * WIDGET_ROW + long_text * 70 + 20)

        y = column_y.get(column, 60)
        column_y[column] = y + height + ROW

        nodes.append({
            "id": int(node_id),
            "type": node["class_type"],
            "pos": [60 + column * COLUMN, y],
            "size": [WIDTH, height],
            "flags": {},
            "order": index,
            "mode": 0,
            "inputs": inputs,
            "outputs": produced,
            "properties": {"Node name for S&R": node["class_type"]},
            "widgets_values": widgets,
        })

    return {
        "last_node_id": max(int(n) for n in graph),
        "last_link_id": link_id,
        "nodes": nodes,
        "links": links,
        "groups": [],
        "config": {},
        "extra": {},
        "version": 0.4,
    }


def main():
    p = argparse.ArgumentParser(description="API graph -> canvas workflow")
    p.add_argument("graph", help="a file written by build_graph.py")
    p.add_argument("--server", default="http://127.0.0.1:8188")
    p.add_argument("--info", help="a saved copy of ComfyUI's /object_info, for when it "
                                  "is not running")
    p.add_argument("--install", action="store_true",
                   help="also copy it into ComfyUI's workflows folder")
    p.add_argument("--comfy-user", default=os.environ.get("COMFYUI_WORKFLOWS", ""),
                   help="ComfyUI's own workflows folder, for --install. Defaults to "
                        "COMFYUI_WORKFLOWS in the environment.")
    args = p.parse_args()

    path = args.graph if os.path.isabs(args.graph) else os.path.join(PROJECT, args.graph)
    with open(path, encoding="utf-8") as fh:
        graph = json.load(fh)

    if args.info:
        with open(args.info, encoding="utf-8") as fh:
            info = json.load(fh)
    else:
        try:
            info = object_info(args.server)
        except Exception as e:
            raise SystemExit("could not reach ComfyUI at %s (%s). It has to be running, "
                             "because the node definitions come from it -- or pass "
                             "--info with a saved copy." % (args.server, e))

    missing = sorted({n["class_type"] for n in graph.values()} - set(info))
    if missing:
        raise SystemExit("ComfyUI does not know these nodes: " + ", ".join(missing))

    workflow = convert(graph, info)
    out = path.replace(".json", "_canvas.json")
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(workflow, fh, indent=2)
        fh.write("\n")
    print("wrote %s" % os.path.relpath(out, PROJECT))
    print("  %d nodes, %d links" % (len(workflow["nodes"]), len(workflow["links"])))

    if args.install:
        if not args.comfy_user:
            raise SystemExit("--install needs --comfy-user, or COMFYUI_WORKFLOWS in the "
                             "environment: the folder ComfyUI lists its workflows from.")
        os.makedirs(args.comfy_user, exist_ok=True)
        target = os.path.join(args.comfy_user, os.path.basename(out))
        with open(target, "w", encoding="utf-8") as fh:
            json.dump(workflow, fh, indent=2)
            fh.write("\n")
        print("installed as %s" % target)
        print("  it appears in ComfyUI's Workflows panel; refresh the browser if not.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
