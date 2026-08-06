# Flynn — architecture

> *He fights for the Users.*

Jellyfin 12.0+ administration suite. One plugin, many modules, each one switchable.

---

## 1. Decisions taken

| # | Decision | Why |
|---|---|---|
| 1 | **One plugin, toggleable modules** | A family of plugins forces the admin through a chain of manual installs. One install, feature cards inside. |
| 2 | **New GUID `3c456481-2879-4378-82fd-96bc084d5668`** | Clean break from MaintenanceDeluxe. The two can coexist on the same server; nothing is inherited, nothing is migrated. |
| 3 | **Jellyfin 12+ only, `net10.0`** | 12.0 ships `net10.0`-only SDK packages. Servers on 10.11.x keep the released MaintenanceDeluxe `v0.9.0`. |
| 4 | **Own script injection** | An `IStartupFilter` + middleware rewrites the SPA shell response body in flight. No disk writes, no dependency on JavaScript Injector / File Transformation — neither has a Jellyfin 12 build, which would have made the plugin a silent no-op. |
| 5 | **`ILibraryManager` only for library reads** | The core database layer churns between server versions. Our own history lives in our own SQLite. |
| 6 | **Degradation per module** | A module that throws switches off its own card. The suite survives. Enforced by `ModuleRegistry`, proven by tests. |
| 7 | **`assemblies` generated from the built zip** | The whitelist is fail-closed and all-or-nothing: a hand-maintained list becomes a time bomb at the first new dependency. |

### Reversed from the earlier plan

The pivot plan said *keep the GUID so the rename is just an update*. That is no longer the
intent: Flynn is a rewrite, not a rename. New GUID, new repository, no upgrade path.

---

## 2. Layout

```
src/Jellyfin.Plugin.Flynn/
  Core/            the socle every module leans on
    Config/        ConfigStore: the single writer, holds the lock
    Data/          SQLite connection + additive-only migrations
    Modules/       IFlynnModule, ModuleCard, ModuleRegistry      [done]
    Issues/        the one inbox: everything actionable lands here
    Mutations/     dry-run -> apply -> undo manifest. Mandatory for every write.  [inert]
    Localization/  string catalogues, resolved for the reader's language
    Web/           script injection middleware
  Modules/         one folder per feature area
    Storage/  Forecast/  Resources/
  Client/
    runtime/       injected on every page. Draws nothing yet.
    admin/         the admin page: one bundle, rendered from the module registry
tests/Jellyfin.Plugin.Flynn.Tests/
build/             meta.json + assemblies generation, packaging
```

A module owns its C#, its tests and, once it has anything non-obvious in it, its own
`CLAUDE.md`. Adding one must never mean editing the admin shell: the page renders from the registry.

**Not yet true**: `Core/Validation/` does not exist -- nothing needs a URL or SSRF check until
something calls outward, and inventing the helpers before their first caller would mean guessing at
their shape. `.claude/rules/client-js.md` states a rule about them, which is a rule waiting for its
code. `Client/modules/` does not exist either: one admin bundle has been enough, and splitting it
before there is a second surface would be structure for its own sake. Neither module folder carries
a `CLAUDE.md` yet, though `Modules/Storage/` has earned one -- it holds the mountinfo parsing and
the ZFS reasoning.

---

## 3. The module contract

```csharp
public interface IFlynnModule
{
    string Id { get; }              // lowercase kebab, config key AND route segment, never renamed
    string NameKey { get; }         // a catalogue key, not text: the reader's language decides
    string SummaryKey { get; }
    ModuleCategory Category { get; }// which shelf the dashboard groups it under
    bool EnabledByDefault { get; }  // absence of a saved preference is "not asked yet", not "no"
    Task<ModuleCard> BuildCardAsync(CancellationToken ct);
}
```

`BuildCardAsync` is called on page load, so it reads pre-computed values — the scheduled task does
the work, the page reads the result. Nothing expensive at render time; at 400 000 tracks that rule
is the difference between a dashboard and an outage.

`ResourcesModule` is the one exception, and it is a deliberate one: a CPU rate needs two readings,
and there is nothing precomputed to read when the page is opened cold. It reuses a stored sample
when one is recent enough and only falls back to taking a fresh pair 300 ms apart. Any module that
wants to do the same needs a better reason than convenience.

`ModuleRegistry` is the only caller. It runs modules concurrently, gives each one a 5 s deadline,
and turns a throw, a hang or a disabled switch into a card instead of an exception. Caller
cancellation is re-thrown, never disguised as a module failure.

**States**: `Disabled` · `Healthy` · `Degraded` · `Failed`. A `Failed` module shows an error — it
never shows plausible numbers, because plausible numbers feed deletion decisions.

---

## 4. Build order

**Socle first.** Storage, Forecast and Resources all write through `ConfigStore`, `Data` and
`Mutations`; building them before the socle means building them twice.

1. `Core/Modules` — contract + registry ✅
2. `Core/Config` — ConfigStore, the single writer ✅
3. `Core/Data` — SQLite, additive-only migrations, tested against a **pre-existing** database ✅
4. `Core/Issues` — the inbox ✅ (dismiss, snooze, restore, and the withheld list, since v0.6.0)
5. `Core/Mutations` — dry-run / apply / undo. **Nothing writes before this exists.**
   Built and tested, but **inert**: no `IMutation` exists, nothing resolves the kernel, and
   `MaxWriteLevel` is read by nothing. Wiring it is the step before Music, which is the first
   module that will write.
6. `Core/Web` — script injection ✅ (proven on real rc3 and rc4 containers in CI)
7. Admin shell rendered from the registry ✅
8. Wave 1: Storage ✅ → Forecast ✅ → Resources ✅ → Prometheus (not started)

---

## 5. What gets ported, and what gets rewritten

Ported after reading — proven code, still correct:

- script injection middleware + startup filter (with its Content-Encoding guard)
- validation helpers: URL safety, SSRF host checks, colour normalisation
- webhook delivery: retry, log sanitisation, Discord/Slack payloads
- maintenance overlay, banners, announcements — as three ordinary modules

Rewritten, because the old shape does not scale to five pillars:

- `BannerController` (~1300 lines, one controller for everything) → one controller per module
- `admin.js` (~4300 lines) → shared runtime + one bundle per module
- configuration model → settings in XML, everything that grows in SQLite

---

## 6. The companion is optional

The plugin runs *inside* the Jellyfin container, so it can read its own cgroup
(`/sys/fs/cgroup/cpu.stat`, `memory.current`) — and ffmpeg children live in the **same** cgroup,
which is exactly what `Process.GetCurrentProcess()` misses. Storage needs `DriveInfo` and
`ILibraryManager`. Forecast is arithmetic over our own SQLite.

**All of wave 1 therefore ships without a companion container.** That settles it: a plugin is
installed from the catalogue, and requiring a `docker compose` edit to see a dashboard would cost
more adoption than the extra panels are worth.

Genuinely out of reach for a plugin, and therefore the companion's remit: other containers, the
host, GPU metrics, the hardware-transcode self-test, the restore drill, audio fingerprinting.

That last one is not a preference. **A single native DLL in the plugin folder marks the whole
plugin `Malfunctioned`**, so anything native — Chromaprint first — *must* live outside. The
companion is inevitable eventually; it is just never a prerequisite.

### Capability model — build it into the socle now

Each module declares the capabilities it needs. A missing capability produces a card that **names
what is missing** ("GPU panel unavailable: `/dev/dri` is not mounted"), never an empty graph. An
absent companion must be visible, because the alternative failure mode — stale numbers presented
as current — is the one that gets libraries deleted.

The companion's SSRF validator is **separate** from the webhook one. Webhooks must keep rejecting
RFC1918; the companion lives on a Docker network and is reached by a config-pinned host, fixed
scheme and port, no redirects, bearer required, never fed from a free-text field.

## 7. Write level: level-2 architecture, level-0 default

Picking a single level is a trap in both directions. Level 0 forever builds a dashboard that
reports four hundred problems and fixes none — the tool people uninstall after a month. Level 2
immediately means writing to files before the undo kernel is proven, which is how someone's
library gets lost.

- `Core/Mutations` is **designed for level 2 from the start**: preview, apply, undo manifest.
- Every module **starts at level 0**. Writing is opt-in per operation.
- **Nothing writes until `Core/Mutations` exists.**
- **Hard invariant: Flynn never renames or moves a media file.** Folders and sidecar files only.

### Known constraint: in-place tag writes break torrent seeding

The invariant above is not enough for one case, and it is the flagship one. Artist identity merge
has to write **tags** or the next scan re-fragments the artist. ID3 and Vorbis tags live *inside*
the audio file, so rewriting them changes the file's bytes — the torrent piece hashes stop
matching and the file silently stops seeding. The structure is untouched; only the seeding dies.

Planned mitigation, to settle when `Core/Mutations` is designed: a per-library **"this library is
seeded"** flag that downgrades those operations to read-only, plus an explicit warning before any
tag write. Sidecar files are a poor third option — Jellyfin's NFO support is much weaker for music
tracks than for movies.

## 8. Still open

- **Client runtime shape** — one bundle or per-module lazy loading.
