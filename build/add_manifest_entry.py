#!/usr/bin/env python3
"""Add a released version to the repository manifest, once the artefact is known to be good.

``manifest.json`` has exactly one author: this script, running in the release workflow. That is the
point. It used to have two -- a human adding the entry before the build, and the workflow patching
the checksum in afterwards -- and both of that arrangement's problems followed from the sharing:

* between the human's push and the workflow's patch, the manifest advertised a version whose zip
  did not exist and whose checksum was a placeholder, so anyone who fetched the repository in that
  window got a failed install;
* every release then raced a local push against the workflow's commit to the same file.

With a single author the entry appears only after the release exists and has been fetched back and
verified, and a local commit can never conflict with it.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from release_entry import build_entry, four_part  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True, help="Release tag, e.g. v0.6.0")
    parser.add_argument("--checksum", required=True, help="md5 of the published asset")
    parser.add_argument("--timestamp", required=True, help="Release time, UTC")
    parser.add_argument("--owner", required=True)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--manifest", type=Path, default=Path("manifest.json"))
    parser.add_argument("--root", type=Path, default=Path("."))
    args = parser.parse_args()

    data = json.loads(args.manifest.read_text(encoding="utf-8"))
    package = data[0]
    versions = package["versions"]

    version = four_part(args.tag)
    if any(existing["version"] == version for existing in versions):
        # Re-running a release must not produce two entries for one version: Jellyfin would show
        # the version twice and pick between them by list order.
        raise SystemExit(f"Version {version} is already in the manifest. Nothing to do.")

    entry = build_entry(
        tag=args.tag,
        root=args.root,
        owner=args.owner,
        repo=args.repo,
        checksum=args.checksum,
        timestamp=args.timestamp,
    )

    # Newest first: Jellyfin offers the first compatible entry it finds.
    versions.insert(0, entry)
    args.manifest.write_text(json.dumps(data, indent=4) + "\n", encoding="utf-8")

    print(f"Added {version} to the manifest ({len(versions)} versions now published).")
    print(f"  {entry['sourceUrl']}")
    print(f"  md5 {entry['checksum']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
