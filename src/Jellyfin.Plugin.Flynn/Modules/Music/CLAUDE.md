# Music

Read-only. This module reports and changes nothing, and that is not a stage to grow out of quickly:
a music library is where a confident wrong answer is most expensive, and the only way to earn the
right to propose a correction is to have been checkable first.

## Facts about the server that decide what can honestly be reported

Each was read out of the Jellyfin 12 source, not inferred.

### The loudness scan is off by default, and almost invisible

`LibraryOptions.EnableLUFSScan` has **no initialiser**, so it is `false`. It appears in exactly one
place in the entire server besides its own declaration: `AudioNormalizationTask` filters the
libraries it visits by it.

So **a library with no measured loudness value has not failed at anything** — that is the normal
state of an untouched install. Reporting it as a fault would make the first thing this module ever
says wrong, and the second thing it says ignored.

### Exactly one loudness tag is ever read

`AudioFileProber` reads `REPLAYGAIN_TRACK_GAIN` into `BaseItem.NormalizationGain`. One occurrence
in the whole codebase. Files carrying only an album gain, or tagged the Opus way with
`R128_TRACK_GAIN`, are read as having nothing at all.

### The two values are alternatives, never a sum

`AudioNormalizationTask` computes only when `!NormalizationGain.HasValue && !LUFS.HasValue`. A track
whose tag was read is therefore never measured. Coverage is the union; counting both for one track
reports more covered tracks than the library holds.

## Never walk the library with StartIndex

`StartIndex` becomes a real SQL `OFFSET`, and it is applied **after** a `DISTINCT` over the full
entity and an `ORDER BY SortName`. The engine produces and discards every row before the offset, so
paging a library costs O(n²). At 400 000 tracks in pages of 1 000 that is tens of billions of
discarded rows — a design that works on a test library and destroys a real one.

Walk by album instead: albums are cheap (`MusicAlbum` carries `[RequiresSourceSerialisation]`, so it
is mapped from columns), and their tracks come back in one query per batch via `AlbumIds`, which
compiles to the same `ParentId IN (...)` predicate. One linear pass, no offset.

`Audio` does **not** carry that attribute, so every track row costs a `JsonSerializer.Deserialize`
of its data blob. Touch tracks once.

## Two other traps in the same API

- `GetItemIds` silently ignores `Recursive`. `ParentId = <music library>, Recursive = true,
  IncludeItemTypes = [Audio]` returns **zero** through it, with no error and no log — the direct
  children of a music library are artists, not tracks. `GetItemList` and `GetCount` do honour it.
- Everything on `ILibraryManager` is synchronous and blocking. A full walk holds a thread-pool
  thread, which is why it belongs in the nightly task and never in `BuildCardAsync`.

## Still open

The duplicate-album detector is not built, and it is blocked on a product decision rather than on
code: MusicBrainz separates the **release group** (the album as a work) from the **release** (the
object published). Which one "duplicate" means has to be chosen up front, because getting it wrong
produces hundreds of false positives on a complete discography and destroys the admin's trust on
the first screen they ever see.
