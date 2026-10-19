"""How big to generate a panel, given the island it has to fill.

Shared by the node and by the graph builder, which is the point: the size has to follow
the part being restyled. Baking a number into the graph works until someone switches the
segment on the canvas, and then it silently squashes one part's island into another
part's proportions.

No torch here, so the builder can import it outside ComfyUI.
"""

LONG_EDGE = 1024
STEP = 64
SMALLEST = 512


def generation_size(width, height, long_edge=LONG_EDGE, step=STEP, smallest=SMALLEST):
    """A size the sampler is comfortable at, as near the island's shape as the step allows.

    SDXL wants multiples of 64 and starts repeating itself well above 1024 on its long
    edge. The few percent of aspect error the rounding leaves does not survive the trip:
    the composite fits the result back to the island's exact rectangle.

    What must not happen is a size chosen for a different part. An island of 1964x1664
    forced into 1024x512 is stretched more than three times vertically on the way back,
    which reads as heavy blur and misplaced detail on the model.
    """
    longest = max(width, height)
    if longest <= 0:
        raise ValueError("the island has no size")

    scale = float(long_edge) / longest
    out = []
    for value in (width, height):
        rounded = int(round(value * scale / step)) * step
        out.append(max(smallest, min(long_edge, rounded)))
    return out[0], out[1]
