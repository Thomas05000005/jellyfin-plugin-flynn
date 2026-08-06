# Cutting a release

## What you do

1. Bump `AssemblyVersion` **and** `FileVersion` in
   `src/Jellyfin.Plugin.Flynn/Jellyfin.Plugin.Flynn.csproj` to the four-part form (`0.6.0.0`).
2. Write `docs/release-notes/v0.6.0.md` — the prose humans read on the GitHub release page.
3. Write `docs/release-notes/v0.6.0.manifest.txt` — **one paragraph**, the blurb a stranger sees in
   the plugin catalogue before deciding to install. This is the only description Jellyfin ever
   shows for the version.
4. Commit, tag `v0.6.0`, push both.

**Do not touch `manifest.json`.** It has exactly one author: the release workflow.

## What the workflow does

Preflight refuses the release, before building anything, if the tag and the two csproj versions
disagree, if either release-notes file is missing or empty, or if that version is already
published. A release that fails at the end leaves a published tag and a half-written manifest
behind, so it is much cheaper to refuse up front.

Then it builds, tests, stages, zips — and publishes. Only afterwards does it **download the
published asset back from GitHub** and prove three things about it: that it is a real zip archive,
that it contains `meta.json`, and that it hashes to the checksum about to be promised. Then, and
only then, the entry is written into the manifest and committed.

## Why in that order

`manifest.json` is a promise made to every Jellyfin server that has this repository configured:
*download this URL, and its md5 will be exactly this.*

Writing that promise from a checksum computed on a local file proves nothing about what GitHub
actually serves. That mistake has been made here: a `curl -L` against a release that did not exist
yet returned a nine-byte "Not Found" page, `md5sum` hashed it without complaint, and the result was
reported as the release checksum with every step green. The verification step exists for exactly
that, which is why it checks *is this a zip* before it checks *does it hash right*.

The old arrangement also gave `manifest.json` two authors — a human adding the entry before the
build, the workflow patching the checksum in afterwards. Both of that arrangement's problems came
from the sharing:

- between the human's push and the workflow's patch, the manifest advertised a version whose zip
  did not exist and whose checksum was a placeholder, so anyone who fetched the repository in that
  window got a failed install — which happened;
- every release raced a local push against the workflow's commit to the same file.

With a single author, the entry appears only after the artefact is known to be good, and a local
commit can never conflict with it.

## The pieces

| File | Does |
|---|---|
| `build/release_entry.py` | Defines what a released version looks like. Derives the download URL rather than accepting one — GitHub asset paths are case sensitive, and a one-letter typo publishes a release that installs as a 404 while every check stays green. |
| `build/verify_asset.py` | Fetches the published asset back and proves it. |
| `build/add_manifest_entry.py` | Writes the entry. Refuses duplicates and anything that is not a 32-character hex md5. |
| `build/make_meta.py` | Builds the in-zip `meta.json`, including the `assemblies` whitelist read off the staged files. |
