#!/usr/bin/env python3
"""The single definition of what a released version looks like.

Both the in-zip ``meta.json`` and the repository ``manifest.json`` describe the same release, and
they used to be written independently: the manifest by hand before the build, meta.json by reading
that manifest back. That arrangement produced the failure Thomas hit -- the manifest advertised a
version whose zip did not exist yet, carrying a placeholder checksum, and Jellyfin refused the
install. It also made ``manifest.json`` a file with two authors, which meant every release raced a
local push against the workflow's commit.

So the entry is computed here, once, from things that are already true:

* the version comes from the git tag,
* the changelog comes from a file the release author wrote deliberately,
* the download URL is derived, never typed,
* the checksum is supplied only after the artefact exists and has been fetched back.

``targetAbi`` is a MINIMUM server version, not an exact match: a plugin built against 12.0 keeps
loading on later servers. It is not read from the csproj because the SDK version we compile against
and the oldest server we promise to run on are two different decisions.
"""

from __future__ import annotations

import re
from datetime import datetime, timezone
from pathlib import Path

TARGET_ABI = "12.0.0.0"

_TAG = re.compile(r"^v(\d+)\.(\d+)\.(\d+)$")


def four_part(tag: str) -> str:
    """Turn ``v0.5.0`` into ``0.5.0.0``, which is the only shape Jellyfin accepts."""
    if not _TAG.match(tag):
        raise SystemExit(f"Tag {tag!r} is not of the form vMAJOR.MINOR.PATCH")
    return f"{tag[1:]}.0"


def changelog_path(tag: str, root: Path) -> Path:
    """Where the one-paragraph catalogue blurb for *tag* lives."""
    return root / "docs" / "release-notes" / f"{tag}.manifest.txt"


def changelog_for(tag: str, root: Path) -> str:
    """Read the catalogue blurb, refusing to publish a release that has none.

    This is the text a stranger reads in the plugin catalogue before deciding to install, and it is
    the only description Jellyfin ever shows for the version. An empty one is not a small omission,
    so it fails the release rather than shipping a blank.
    """
    path = changelog_path(tag, root)
    if not path.is_file():
        raise SystemExit(
            f"No catalogue blurb at {path}.\n"
            f"Write one paragraph describing what this release changes, for the plugin catalogue."
        )

    text = " ".join(path.read_text(encoding="utf-8").split())
    if not text:
        raise SystemExit(f"{path} is empty.")
    return text


def source_url(tag: str, owner: str, repo: str) -> str:
    """The download URL, derived rather than typed.

    GitHub's asset paths are case sensitive, and a hand-typed URL that differs by one letter
    publishes a release which installs as a 404 while every other check stays green. That happened
    on the predecessor plugin, which is why nothing here accepts a URL as input.
    """
    return f"https://github.com/{owner}/{repo}/releases/download/{tag}/Flynn-{tag}.zip"


def utc_stamp() -> str:
    """Now, in the seven-decimal shape Jellyfin's manifests use."""
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.0000000Z")


def build_entry(
    tag: str,
    root: Path,
    owner: str,
    repo: str,
    checksum: str,
    timestamp: str,
) -> dict:
    """Assemble the complete manifest entry for *tag*."""
    if not re.fullmatch(r"[0-9a-f]{32}", checksum):
        raise SystemExit(f"Checksum {checksum!r} is not a 32 character hex md5")

    return {
        "version": four_part(tag),
        "changelog": changelog_for(tag, root),
        "targetAbi": TARGET_ABI,
        "sourceUrl": source_url(tag, owner, repo),
        "checksum": checksum,
        "timestamp": timestamp,
    }
