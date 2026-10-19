# ComfyUI nodes

Two custom nodes. Everything else in the restyle graph is stock ComfyUI, which is the
point: the only parts worth writing are the two that have to know something about the
asset.

| Node | |
|---|---|
| **Recast Segment Loader** | Manifest + segment + variant → the base texture, its UV mask, the island cropped to its own rectangle, and the numbers the graph needs |
| **Recast Composite & Export** | A generated panel → back inside the island, written out, with a measured statement that nothing outside it moved |

## Install

The repo holds the source; ComfyUI gets a link to it, so there is never a second copy to
keep in step. From an ordinary PowerShell prompt — no administrator needed:

    New-Item -ItemType Junction -Path "<ComfyUI>\custom_nodes\recast" -Target "<repo>\pipeline\comfyui"

Then restart ComfyUI. Both nodes appear under the **Recast** category, or by
double-clicking the canvas and typing `recast`.

The nodes need the project path. It is a widget on the loader, so it is visible on the
node rather than buried; `RECAST_PROJECT` in the environment overrides the default.

## Why the panel outputs exist

A segment's texture is 2048 square and mostly empty: one irregular island with a lot of
nothing around it. Generating over the whole sheet would spend most of the resolution on
the empty part, and an edge preprocessor would find the island's rectangular border
rather than the panel's real shape.

So the loader also hands out the island's own rectangle — `panel`, `panel_mask`,
`panel_width`, `panel_height`. That is what the preprocessor reads and the sampler
produces, and the composite node puts it back where it came from.

## The guarantee, and why it is measured rather than asserted

The composite node promises that every pixel outside the island, beyond a declared
padding, is identical to the texture that went in. It checks that itself on every run and
reports the count, which should be zero.

**Padding is not slack.** A GPU filters across an island's edge, and reads a smaller
version of the texture at distance, so a restyle that stops exactly at the boundary lets
the old colour bleed back along every seam. The restyle is grown a few texels outwards
into the empty space, which costs nothing and removes the seam.

**Feather is not free.** Softening a mask spreads it outwards by its own radius, so the
restyle actually reaches `padding + feather`, not `padding`. The first version of this
node got that wrong in exactly that amount and the test caught it — which is why the
reach is computed rather than assumed.

The report also counts the island reaching past its own rectangle. That cannot happen
from a bad composite, only from a bounding box measured against a different unwrap, and
the two look identical on the model.

## Test it

The nodes take tensors and return tensors, so they run without ComfyUI. Use ComfyUI's own
interpreter, which is where torch lives:

    <ComfyUI>\.venv\Scripts\python.exe pipeline/tests/test_comfyui_nodes.py

Thirty checks over all four segments. The pair that matters is "nothing changed outside
the padded island" together with "the island itself was restyled" — the first alone is
satisfied perfectly by a node that does nothing.
