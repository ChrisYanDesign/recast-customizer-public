"""Minimal reproduction of the bug comfy_workaround.py works around.

Builds two safetensors files and reads each back. The small one reads; the large one
kills the process with a Windows access violation inside torch's storage slicing -- not
an exception, so there is no traceback and nothing to catch.

Measured on ComfyUI 0.39.2, torch 2.12.1+cu130, safetensors 0.8.0, Windows 11:

    3.0 GiB   ok
    5.0 GiB   process dies

Kept in the repo because the workaround is only defensible with the evidence beside it,
and because this is the form the bug should be reported upstream in.

Run each size in its own process, since a crash takes the interpreter with it:

    <ComfyUI>\\.venv\\Scripts\\python.exe pipeline/comfyui/large_file_repro.py 3.0 <dir>
    <ComfyUI>\\.venv\\Scripts\\python.exe pipeline/comfyui/large_file_repro.py 5.0 <dir>

Each run writes a file of that size, so point <dir> at a disk with room to spare and
delete them afterwards.
"""

import faulthandler
import gc
import os
import sys

faulthandler.enable()

import safetensors
import safetensors.torch
import torch

GIB = 1 << 30


def build(path, gib):
    count = int(gib * GIB) // 2          # float16
    tensor = torch.zeros(count, dtype=torch.float16)
    safetensors.torch.save_file({"w": tensor}, path)
    del tensor
    gc.collect()
    print("built %s (%.2f GiB)" % (os.path.basename(path), os.path.getsize(path) / GIB),
          flush=True)


def read_back(path):
    print("reading %s ..." % os.path.basename(path), end=" ", flush=True)
    with safetensors.safe_open(path, framework="pt", device="cpu") as handle:
        tensor = handle.get_tensor("w")
    print("ok, %s elements" % "{:,}".format(tensor.numel()), flush=True)


def main():
    if len(sys.argv) < 3:
        raise SystemExit("need: <size in GiB> <directory to write into>")
    size, folder = sys.argv[1], sys.argv[2]

    print("torch %s, safetensors %s" % (torch.__version__, safetensors.__version__))
    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, "repro_%s.safetensors" % size)
    if not os.path.exists(path):
        build(path, float(size))
    read_back(path)
    print("survived at %s GiB" % size)
    return 0


if __name__ == "__main__":
    sys.exit(main())
