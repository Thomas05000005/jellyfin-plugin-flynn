# CLAUDE.md

Flynn is a Jellyfin **12.0+** administration suite plugin (`net10.0`). One plugin, many modules
the admin switches on and off individually.

It is a clean rewrite. It shares no code with its predecessor `MaintenanceDeluxe`
(`C:\Users\blanc\projects\JellyFlare`, targeted 10.11, different architecture) — read anything
before porting it, do not copy on trust.

## Commands

```bash
dotnet build -c Release          # must stay at 0 warnings: TreatWarningsAsErrors is on
dotnet test  -c Release
dotnet test  -c Release --filter "FullyQualifiedName~ModuleRegistryTests"
```

## Invariants

Non-obvious things that break production when violated.

### `meta.json` `assemblies` is fail-closed and all-or-nothing

Listing a DLL that is not physically present marks the **whole plugin** `Malfunctioned`
(`PluginManager.TryGetPluginDlls`, jellyfin PR #9564, unchanged in 12.0). An empty `[]` means *no
whitelist*: the server then loads every `*.dll` under the plugin folder **recursively**, including
`runtimes/*/native/*.dll` — and one native DLL raising `BadImageFormatException` disables the
entire plugin, `break`ing before the remaining assemblies are even tried.

**Generate the list from the built zip. Never hand-edit it.** It can only travel inside the zip;
the repository manifest has no such field.

### Configuration: add freely, never rename

`PluginConfiguration` is serialised by `XmlSerializer`. Renaming a property silently orphans the
value in every config already on disk. Every collection property needs `= new()` — the serialiser
can bypass the constructor on nil-marked XML and hand you a null.

### A broken module must never take the suite down

`ModuleRegistry` is the only thing allowed to call `IFlynnModule.BuildCardAsync`. It isolates
failures, timeouts and cancellation. A failed module renders an error card and **never reports
plausible-looking numbers** — wrong numbers feed deletion decisions, an error does not.

### Where data lives

| Kind | Home |
|---|---|
| Settings | `PluginConfiguration` (XML) |
| Anything that grows without bound | our own SQLite |
| Library queries | `ILibraryManager` **only** |

Never read or write the Jellyfin database directly: it is EF Core and it churns between server
versions.

### Anything that must be guaranteed goes in a hook or a test

Instructions in this file are advisory — Claude reads them, it does not execute them. A rule that
must always hold needs a CI check or a `.claude/hooks/` entry, not a sentence here.

## Conventions

- French is fine in user-facing strings; **code comments use unaccented ASCII**.
- `GenerateDocumentationFile` is on: every public member needs an XML doc comment or the build
  fails.
- One module = one self-contained folder under `Modules/`, with its own `CLAUDE.md` once it has
  anything non-obvious in it.

## Status

Skeleton. Only `Core/Modules` (the module contract and its registry) exists.
Design, decisions and the porting plan: `docs/ARCHITECTURE.md`.
