"""ComfyUI entry point for the Recast nodes.

ComfyUI imports every folder under custom_nodes and looks for these two mappings.
"""

from . import comfy_workaround
from .recast_nodes import NODE_CLASS_MAPPINGS, NODE_DISPLAY_NAME_MAPPINGS

# Announced on every start, because it changes how the whole ComfyUI process loads
# models, not only how the Recast nodes behave. See comfy_workaround.py.
comfy_workaround.announce()

__all__ = ["NODE_CLASS_MAPPINGS", "NODE_DISPLAY_NAME_MAPPINGS"]
