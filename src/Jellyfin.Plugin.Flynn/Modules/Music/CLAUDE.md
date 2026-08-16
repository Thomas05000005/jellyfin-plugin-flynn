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

## Per-track cover art: the deduplication the server undoes one step later

Three steps, each reasonable alone, whose combination stores one image once per track.

1. `AudioImageProvider.GetAudioImagePath` names the extracted file
   `MD5(Album + "-" + AlbumArtists[0]).jpg` under `CachePath/extracted-audio-images`. Every track of
   one album resolves to a **single** cache file. That is a deliberate deduplication.
2. `ItemImageProvider` (line ~241) hands that path to `ProviderManager.SaveImage`.
3. `ImageSaver.SaveImage` opens with
   `saveLocally = item.SupportsLocalMetadata && item.IsSaveLocalMetadataEnabled() && !item.ExtraType.HasValue && item is not Audio`.
   For a track that is **always false**, so it always falls through to
   `Path.Combine(item.GetInternalMetadataPath(), ...)`.

So the one cache file is copied into every track's own metadata folder. A fifteen-track album stores
the same cover fifteen times. On a 223 000-track library that is tens of gigabytes.

Two consequences worth knowing:

- **"Save artwork into media folders" cannot affect music tracks.** The `item is not Audio` test
  short-circuits before the setting is read. An admin changing it is changing nothing.
- `DeleteCacheFileTask` deletes everything under `CachePath` whose **last write time** is older than
  30 days, every 24 h. The extracted cache file is written once, so it is purged a month later
  whatever its use, and re-extracted by ffmpeg on next display. The copies in the metadata folder are
  not touched by that task.

### What the audit is allowed to count

Only images whose path is under `IServerApplicationPaths.InternalMetadataPath`. A cover the
administrator put next to their music is **their** file; counting it would put bytes into a figure
whose whole purpose is to say what could be deleted. There is a test for this and it is the most
important one in the module.

Two covers are treated as the same image when byte length **and both dimensions** match, grouped per
containing folder rather than per album so two discs with genuinely different art are never merged.
Hashing tens of gigabytes nightly to confirm what step 1 above already guarantees would cost more
than the answer is worth.

## Still open

The duplicate-album detector is not built, and it is blocked on a product decision rather than on
code: MusicBrainz separates the **release group** (the album as a work) from the **release** (the
object published). Which one "duplicate" means has to be chosen up front, because getting it wrong
produces hundreds of false positives on a complete discography and destroys the admin's trust on
the first screen they ever see.

## Measured and abandoned

Three detectors were designed, measured against a real 16 309-artist / 40 364-album / 223 412-track
library, and dropped. Do not rebuild them without new evidence.

- **Fragmented artists.** 117 groups of names that normalise identically, 0.7% of the artists. Every
  one of them returns the *same album ids* from both entities: the content is already unified, the
  duplicate exists only in the artist list. Two mechanisms in the server make such pairs impossible
  to create today -- `GetAllArtistNames` does `GroupBy(CleanValue).Select(g => g.Min(Value))`, and
  `MusicArtist.GetPath` does `.TrimEnd('.')` so `Alice` and `Alice.` share a path and therefore a
  GUID. They are legacy rows from an older version.
- **Inverted duos and "Nom, Prenom".** One case in 16 309 for the first, zero for the second. The
  comma is a **genre** delimiter in Jellyfin and never an artist one (`_genreDelimiters` includes
  it, `_nameDelimiters` does not), so composite artist names are the intended behaviour.
- **Split albums.** 57% of albums hold exactly one track, and a sample of 300 found **none** sharing
  a folder with another: the library really is one release per folder. Nothing is split.
