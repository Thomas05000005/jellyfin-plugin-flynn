---
description: Rules for the client-side scripts Flynn injects into the Jellyfin web UI
paths:
  - src/Jellyfin.Plugin.Flynn/Client/**
---

# Injected client scripts

These files are served to browsers and, on some Jellyfin client paths, **decoded as latin1 rather
than UTF-8**. A single non-ASCII byte silently breaks the whole script — no error, the feature
just stops existing.

- Every string literal must be **pure ASCII**. Use `\uXXXX` escapes for chevrons, dashes, accented
  French text and emoji.
- Run `node --check` on any file you touch before committing.
- URL fields need the shared safety check on **both** sides (server normalisation and client
  rendering). Protocol-relative `//evil.com` and backslash-prefixed `/\evil.com` must both be
  rejected — browsers normalise the latter to the former.
- The injection path rewrites the SPA shell response body in flight. It must only ever touch a
  `200 text/html` response with no `Content-Encoding`, and must fall back to the original bytes on
  any failure.
