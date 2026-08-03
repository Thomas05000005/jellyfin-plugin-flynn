#!/usr/bin/env python3
"""Generate the plugin's meta.json, including the `assemblies` whitelist.

The whitelist is derived from the files that are actually about to be shipped -- never written
by hand. Two facts from the server make that non-negotiable
(Emby.Server.Implementations/Plugins/PluginManager.cs):

* With no whitelist (field absent, or an empty list) the server loads every ``*.dll`` under the
  plugin folder RECURSIVELY, ``runtimes/<rid>/native/*.dll`` included. A native DLL raises
  BadImageFormatException, and the handler disables the WHOLE plugin, then breaks -- the
  remaining assemblies are not even attempted.
* With a whitelist the check is fail-closed and all-or-nothing: every listed path must resolve
  inside the plugin folder AND be physically present, or the plugin is marked Malfunctioned.
  Listing a file that is not in the zip is therefore worse than listing nothing at all.

So: list what is there, and split managed from native by reading the PE header rather than by
guessing from the file name. Managed assemblies go in the whitelist; native ones stay in the zip
(their managed wrapper P/Invokes them by name) but are never handed to LoadFromAssemblyPath.
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
from pathlib import Path

# Index of the CLI header in the PE optional header's data directory table. An RVA of 0 there
# means "no managed metadata", i.e. a native image.
_CLI_HEADER_DIRECTORY_INDEX = 14
_PE32 = 0x10B
_PE32_PLUS = 0x20B


def is_managed_assembly(path: Path) -> bool:
    """Return True when *path* is a managed .NET assembly (as opposed to a native DLL)."""
    try:
        blob = path.read_bytes()
    except OSError:
        return False

    try:
        # DOS header -> offset of the PE signature.
        if blob[:2] != b"MZ":
            return False
        pe_offset = struct.unpack_from("<I", blob, 0x3C)[0]
        if blob[pe_offset:pe_offset + 4] != b"PE\0\0":
            return False

        # COFF header is 20 bytes; the optional header starts right after it.
        optional_header = pe_offset + 24
        magic = struct.unpack_from("<H", blob, optional_header)[0]
        if magic == _PE32:
            directories = optional_header + 96
        elif magic == _PE32_PLUS:
            directories = optional_header + 112
        else:
            return False

        entry = directories + _CLI_HEADER_DIRECTORY_INDEX * 8
        rva = struct.unpack_from("<I", blob, entry)[0]
        return rva != 0
    except (struct.error, IndexError):
        return False


def collect_assemblies(staging: Path) -> tuple[list[str], list[str]]:
    """Split the DLLs under *staging* into (managed, native), as plugin-relative POSIX paths."""
    managed: list[str] = []
    native: list[str] = []
    for dll in sorted(staging.rglob("*.dll")):
        relative = dll.relative_to(staging).as_posix()
        (managed if is_managed_assembly(dll) else native).append(relative)
    return managed, native


def build_meta(manifest_path: Path, staging: Path) -> dict:
    """Build the meta.json payload from the repository manifest and the staged files."""
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))[0]
    version = manifest["versions"][0]

    managed, native = collect_assemblies(staging)
    if not managed:
        raise SystemExit(
            f"No managed assembly found under {staging}. Refusing to emit a meta.json whose "
            f"whitelist would silently disable the plugin."
        )

    for name in native:
        print(f"  native (kept in the zip, excluded from the whitelist): {name}")
    for name in managed:
        print(f"  managed (whitelisted): {name}")

    return {
        "category": manifest["category"],
        "changelog": version["changelog"],
        "description": manifest["description"],
        "guid": manifest["guid"],
        "name": manifest["name"],
        "overview": manifest["overview"],
        "owner": manifest["owner"],
        "targetAbi": version["targetAbi"],
        "timestamp": version["timestamp"],
        "version": version["version"],
        "status": "Active",
        "autoUpdate": True,
        "assemblies": managed,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=Path("manifest.json"))
    parser.add_argument(
        "--staging",
        type=Path,
        required=True,
        help="Directory holding exactly the files that will be zipped.",
    )
    args = parser.parse_args()

    if not args.staging.is_dir():
        raise SystemExit(f"Staging directory not found: {args.staging}")

    meta = build_meta(args.manifest, args.staging)
    out = args.staging / "meta.json"
    out.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {out} with {len(meta['assemblies'])} whitelisted assembly/assemblies.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
