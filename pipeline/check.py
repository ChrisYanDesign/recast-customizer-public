"""Check an asset kit, end to end, in one command.

    python pipeline/check.py                                  # the glove
    python pipeline/check.py pipeline/demo/broken_glove        # the broken demo kit
    python pipeline/check.py <kit> --open                      # and open the report

Runs the three steps in order -- measure the mesh in headless Blender, fold the
measurements into the manifest, then validate -- and prints where the report landed.
Exits non-zero when anything fails, so it can run unattended.

A kit is a folder holding a manifest and the mesh and textures it names. Given no
argument it checks the project's own glove.
"""

import argparse
import os
import shutil
import subprocess
import sys
import webbrowser

HERE = os.path.dirname(os.path.realpath(__file__))
PROJECT = os.path.dirname(HERE)

DEFAULT_MANIFEST = "Assets/RecastCustomizer/glove_manifest.json"
DEFAULT_OUT = "pipeline"

BLENDER_CANDIDATES = [
    os.environ.get("BLENDER"),
    shutil.which("blender"),
    r"C:\Program Files\Blender Foundation\blender.exe",
    r"C:\Program Files\Blender Foundation\Blender\blender.exe",
    "/Applications/Blender.app/Contents/MacOS/Blender",
    "/usr/bin/blender",
]


def find_blender():
    for c in BLENDER_CANDIDATES:
        if c and os.path.exists(c):
            return c
    for root in (r"C:\Program Files\Blender Foundation",):
        if not os.path.isdir(root):
            continue
        for dirpath, _, files in os.walk(root):
            if "blender.exe" in files:
                return os.path.join(dirpath, "blender.exe")
    sys.exit("Could not find Blender. Set the BLENDER environment variable to its path.")


def resolve(kit):
    """A kit may be given as its folder or as its manifest file."""
    if kit is None:
        return DEFAULT_MANIFEST, DEFAULT_OUT
    kit = kit.replace("\\", "/").rstrip("/")
    full = os.path.join(PROJECT, kit)
    if os.path.isdir(full):
        manifest = kit + "/manifest.json"
        if not os.path.exists(os.path.join(PROJECT, manifest)):
            sys.exit("no manifest.json in " + kit)
        return manifest, kit
    if not os.path.exists(full):
        sys.exit("no such kit: " + kit)
    # A manifest given on its own writes to the standard output folder. Deriving it from
    # the manifest's own folder would drop reports and masks inside Assets/, where Unity
    # would import them.
    folder = os.path.dirname(kit)
    if not folder or folder.replace("\\", "/").startswith("Assets"):
        return kit, DEFAULT_OUT
    return kit, folder


def step(label, command):
    print("\n--- %s" % label, flush=True)
    result = subprocess.run(command, cwd=PROJECT)
    return result.returncode


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("kit", nargs="?", help="kit folder or manifest; omit for the glove")
    ap.add_argument("--spec", default=None, help="a different spec file")
    ap.add_argument("--open", action="store_true", help="open the report when it is written")
    ap.add_argument("--quiet", action="store_true", help="print failures only")
    args = ap.parse_args()

    manifest, out = resolve(args.kit)
    blender = find_blender()
    py = sys.executable

    print("kit      : " + manifest)
    print("reports  : " + out + "/out")

    rc = step("measuring the mesh", [
        blender, "--background", "--python", "pipeline/blender/measure_and_mask.py",
        "--", "--manifest", manifest, "--out", out])
    if rc != 0:
        sys.exit("the measure step failed")

    rc = step("sampling the textures", [
        blender, "--background", "--python", "pipeline/blender/texture_stats.py",
        "--", "--manifest", manifest, "--out", out])
    if rc != 0:
        sys.exit("the texture step failed")

    rc = step("updating the manifest", [
        py, "pipeline/extend_manifest.py",
        "--manifest", manifest, "--stats", out + "/out/mesh_stats.json",
        "--texture-stats", out + "/out/texture_stats.json"])
    if rc != 0:
        sys.exit("the manifest step failed")

    cmd = [py, "pipeline/validator/validate.py",
           "--manifest", manifest, "--out", out + "/out"]
    if args.spec:
        cmd += ["--spec", args.spec]
    if args.quiet:
        cmd += ["--quiet"]
    rc = step("validating", cmd)

    report = os.path.join(PROJECT, out, "out", "validation.html")
    print("\nreport: " + report)
    if args.open and os.path.exists(report):
        webbrowser.open("file:///" + report.replace("\\", "/"))

    sys.exit(rc)


if __name__ == "__main__":
    main()
