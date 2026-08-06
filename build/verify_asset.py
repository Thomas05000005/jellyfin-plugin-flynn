#!/usr/bin/env python3
"""Prove the published release asset is really there and really matches, before announcing it.

The manifest is a promise: it tells every Jellyfin server in the world "download this URL, and its
md5 will be exactly this". Writing that promise from a checksum computed on a local file proves
nothing about what GitHub actually serves.

The failure this exists to stop has already happened once here. A ``curl -L`` against a release
that did not exist returned a nine-byte "Not Found" page, ``md5sum`` hashed it without complaint,
and the result was reported as the release checksum. Every step was green. So the check is not
"did the download succeed" -- it is "is what came back a real zip archive, and does it hash to
what we are about to promise".
"""

from __future__ import annotations

import argparse
import hashlib
import io
import sys
import time
import urllib.error
import urllib.request
import zipfile

# GitHub serves a freshly created release asset through a CDN, and the first request can arrive
# before it has propagated. Retrying is correct; sleeping longer and hoping is not.
_ATTEMPTS = 6
_PAUSE_SECONDS = 5


def fetch(url: str) -> bytes:
    """Download *url*, retrying while the asset propagates."""
    last = ""
    for attempt in range(1, _ATTEMPTS + 1):
        try:
            with urllib.request.urlopen(url, timeout=60) as response:
                return response.read()
        except (urllib.error.URLError, TimeoutError) as error:
            last = str(error)
            print(f"  attempt {attempt}/{_ATTEMPTS}: {last}")
            if attempt < _ATTEMPTS:
                time.sleep(_PAUSE_SECONDS)

    raise SystemExit(f"Could not download {url} after {_ATTEMPTS} attempts: {last}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", required=True, help="Public download URL of the published asset.")
    parser.add_argument("--md5", required=True, help="Checksum we are about to publish.")
    args = parser.parse_args()

    blob = fetch(args.url)
    print(f"  fetched {len(blob)} bytes")

    # A 404 page is bytes too, and it hashes perfectly happily. Only a real archive counts.
    if not zipfile.is_zipfile(io.BytesIO(blob)):
        head = blob[:120].decode("utf-8", "replace")
        raise SystemExit(f"What came back is not a zip archive. First bytes:\n  {head!r}")

    served = hashlib.md5(blob).hexdigest()  # noqa: S324 - a transfer check, not a security property
    if served != args.md5:
        raise SystemExit(f"Checksum mismatch.\n  served:    {served}\n  publishing: {args.md5}")

    with zipfile.ZipFile(io.BytesIO(blob)) as archive:
        names = sorted(archive.namelist())
    if "meta.json" not in names:
        raise SystemExit(f"The archive has no meta.json, so the plugin cannot load. Holds: {names}")

    print(f"  a real zip, md5 {served} as promised, holding {len(names)} files")
    return 0


if __name__ == "__main__":
    sys.exit(main())
