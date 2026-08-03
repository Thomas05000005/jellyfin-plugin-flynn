#!/usr/bin/env python3
"""Fail if any client-side script contains a non-ASCII byte.

Some Jellyfin client paths decode the injected script as latin1 rather than UTF-8. One non-ASCII
byte breaks the whole file silently: no console error, no failed request, the feature simply never
runs. It is the kind of bug that survives several release cycles because nothing reports it.

The rule cannot live in a comment, since a comment is advice and this needs to be a gate. Anything
outside ASCII goes in as a \\uXXXX escape instead.
"""

from __future__ import annotations

import sys
from pathlib import Path

CLIENT_ROOT = Path("src/Jellyfin.Plugin.Flynn/Client")


def offences(path: Path) -> list[str]:
    """Return one message per non-ASCII character found in *path*."""
    found: list[str] = []
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        for column, character in enumerate(line, start=1):
            if ord(character) > 127:
                found.append(
                    f"{path.as_posix()}:{line_number}:{column}: "
                    f"U+{ord(character):04X} {character!r} -- use a \\u{ord(character):04X} escape"
                )
    return found


def main() -> int:
    if not CLIENT_ROOT.is_dir():
        print(f"No client directory at {CLIENT_ROOT}; nothing to check.")
        return 0

    scripts = sorted(CLIENT_ROOT.rglob("*.js"))
    if not scripts:
        print(f"No .js under {CLIENT_ROOT}; nothing to check.")
        return 0

    all_offences = [message for script in scripts for message in offences(script)]

    if all_offences:
        print("Non-ASCII characters in client scripts:", file=sys.stderr)
        for message in all_offences:
            print(f"  {message}", file=sys.stderr)
        print(
            "\nThese break the script silently on clients that decode it as latin1.",
            file=sys.stderr,
        )
        return 1

    print(f"{len(scripts)} client script(s) are pure ASCII.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
