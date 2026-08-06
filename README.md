# Flynn

An administration suite for **Jellyfin 12.0 and later**. One plugin, many modules you switch on
individually.

> *He fights for the Users.*

## Install

Add this repository to Jellyfin, then install Flynn from the catalogue:

**Dashboard → Plugins → Repositories → `+`**

| Field | Value |
|---|---|
| Repository name | `Flynn` |
| Repository URL | `https://raw.githubusercontent.com/Thomas05000005/jellyfin-plugin-flynn/main/manifest.json` |

Then **Dashboard → Plugins → Catalogue → Flynn → Install**, and restart the server.

Nothing else is needed. Flynn delivers its own client script by rewriting the web UI response in
flight, so unlike most plugins in this space it does not require JavaScript Injector or File
Transformation — neither of which has a Jellyfin 12 build.

### Requirements

- Jellyfin **12.0** or later. There is no build for 10.11.x and there will not be one.
- Nothing else. No companion container, no external service.

## What it does today

**Storage** — how much each library holds and how much room is left on each device, measured
nightly. Jellyfin's own dashboard shows paths but neither sizes nor free space.

Since then: **Capacity**, which says when a disk runs out and refuses to answer before it has fourteen nightly readings, and **Resources**, which measures what Jellyfin costs including the ffmpeg children a process-level reading cannot see.

That is the whole list. Flynn is early; the socle it is built on is further along than the
features are.

## What it is careful about

**It changes nothing by default.** Every module starts read-only, and the write level is a
setting. Anything that would touch files has to say how it would be undone *before* it runs, or
it does not run.

**It never renames or moves a media file.** Folders and sidecar files only.

**A broken module cannot take the rest down.** Each one is isolated; a failure shows an error card
and never plausible-looking numbers, because wrong numbers are what get libraries deleted.

**Its database lives outside the plugin folder**, so removing and reinstalling the plugin — which
Jellyfin's own upgrade guidance tells you to do — does not throw away months of history.

## Building it yourself

```bash
dotnet build -c Release
dotnet test  -c Release
```

Development notes are in [CLAUDE.md](CLAUDE.md); the design and the decisions behind it are in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Licence

MIT.
