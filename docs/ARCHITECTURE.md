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
    Mutations/     dry-run -> apply -> undo manifest. Mandatory for every write.
    Validation/    URL / colour / SSRF helpers, shared, mutation-tested
    Web/           script injection middleware
  Modules/         one folder per feature area, self-contained
  Client/
    runtime/       shared client runtime (SPA hooks, config hot-reload, BroadcastChannel)
    modules/       one bundle per module
tests/Jellyfin.Plugin.Flynn.Tests/
build/             meta.json + assemblies generation, packaging
```

A module owns its C#, its client bundle, its tests and its own `CLAUDE.md`. Adding one must
never mean editing the admin shell: the page renders from the registry.

---

## 3. The module contract

```csharp
public interface IFlynnModule
{
    string Id { get; }            // lowercase kebab, config key AND route segment, never renamed
    string DisplayName { get; }
    string Summary { get; }
    Task<ModuleCard> BuildCardAsync(CancellationToken ct);
}
```

`BuildCardAsync` is called on page load, so it reads pre-computed values — the scheduled task does
the work, the page reads the result. Nothing expensive at render time; at 400 000 tracks that rule
is the difference between a dashboard and an outage.

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
2. `Core/Config` — ConfigStore, the single writer
3. `Core/Data` — SQLite, additive-only migrations, tested against a **pre-existing** database
4. `Core/Issues` — the inbox
5. `Core/Mutations` — dry-run / apply / undo. **Nothing writes before this exists.**
6. `Core/Web` — script injection (port from MaintenanceDeluxe, it is proven)
7. Admin shell rendered from the registry
8. Then wave 1: Storage → Forecast → Resources → Prometheus

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

## 6. Still open

- **Sidecar: load-bearing or optional?** Roughly half the planned features need capabilities a
  plugin does not have. If it is load-bearing, the socle must carry the shared contract and
  `IsSidecarHostSafe` from the start.
- **Write level.** Level 0 (read-only) / 1 (Jellyfin API) / 2 (files on disk). Level 2 is where
  the value is and where the risk is; it is what makes `Core/Mutations` non-negotiable.
- **Client runtime shape** — one bundle or per-module lazy loading.
