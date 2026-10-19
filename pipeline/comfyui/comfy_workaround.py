"""Works around a bug in this ComfyUI build: it cannot read a model file over 4 GiB.

This is not about the glove, and nothing here is part of the pipeline. It exists because
the pipeline cannot run without it on this machine.

## What the bug is

A safetensors file larger than 4 GiB kills the ComfyUI process outright -- a Windows
access violation inside torch's storage slicing, not a Python exception, so there is no
traceback and nothing to catch.

Reduced to two files, with no model involved:

    a 3 GiB safetensors file   reads fine
    a 5 GiB safetensors file   crashes the process

Measured on ComfyUI 0.39.2 with torch 2.12.1+cu130 and safetensors 0.8.0, and unchanged
on safetensors 0.7.0 and 0.6.2, so it is not the safetensors version. Everything else was
ruled out first: the file's hash matched its publisher's, the whole file read back
sequentially, memory-mapped reads succeeded at fourteen offsets across it, both disks
reported healthy, and the same file failed identically from a different drive.

## Why ComfyUI survives it at all

There are two loaders. ComfyUI's own -- used when DynamicVRAM is on -- maps the file and
builds tensors from the mapping, and that one works. The other hands the file to the
safetensors library, and that one is where the crash lives.

So DynamicVRAM must stay ON. Its weakness is separate: during sampling it re-reads each
weight from the file in slices through a native reader, and that reader fails on the same
large file with "hostbuf_file_reader_read failed".

## What this does

Turns off the slice re-reading, which is the only broken part. ComfyUI then copies from
the already-mapped tensors instead, which is what it did before that optimisation
existed. The cost is more system RAM held during a run; the machine has 32 GB.

The patched function is a fallback by design -- it returns False to mean "I did not
handle this, do it the normal way" -- so forcing False is a supported answer rather than
a hack around a contract.

## Turning it off

Set RECAST_NO_COMFY_WORKAROUND=1 in the environment, or remove the Recast nodes from
custom_nodes. It applies to the whole ComfyUI process, not only to the Recast nodes, so
it is announced in the log on every start. Re-check it after a ComfyUI update: when the
upstream bug is fixed this file should be deleted, not kept.
"""

import logging
import os

ENV_DISABLE = "RECAST_NO_COMFY_WORKAROUND"


def apply():
    """Returns a sentence saying what happened, for the caller to log."""
    if os.environ.get(ENV_DISABLE):
        return "large-model workaround NOT applied (%s is set)" % ENV_DISABLE

    try:
        import comfy.memory_management as memory_management
    except ImportError:
        return "large-model workaround skipped (not running inside ComfyUI)"

    if not hasattr(memory_management, "read_tensor_file_slice_into"):
        return ("large-model workaround skipped: this ComfyUI has no "
                "read_tensor_file_slice_into, so the bug it works around is gone")

    if getattr(memory_management, "_recast_workaround", False):
        return "large-model workaround already applied"

    original = memory_management.read_tensor_file_slice_into

    def read_tensor_file_slice_into(*args, **kwargs):
        # False means "not handled, fall back to a normal copy", which is exactly what
        # the caller does with it.
        return False

    read_tensor_file_slice_into.recast_original = original
    memory_management.read_tensor_file_slice_into = read_tensor_file_slice_into
    memory_management._recast_workaround = True

    return ("large-model workaround applied: streaming weight re-reads are off, because "
            "this build crashes on safetensors files over 4 GiB. Set %s=1 to disable."
            % ENV_DISABLE)


def announce():
    logging.info("[Recast] " + apply())
