#!/usr/bin/env bash
# Assert that a running Jellyfin container loaded Flynn cleanly and that its socle came up.
#
# Every match below is done with a here-string rather than `echo "$LOG" | grep`. That pipeline is
# a trap under `pipefail`: `grep -q` exits at the first match and closes the pipe, `echo` then dies
# of EPIPE, and pipefail reports the pipeline as failed *because* the pattern was found. It only
# shows up once the log is long enough for echo not to finish first, so it reads as flakiness.
set -euo pipefail

CONTAINER="${1:-jf}"
LOG="$(docker logs "$CONTAINER" 2>&1)"

echo "--- Flynn lines in the server log ---"
grep -i "flynn" <<< "$LOG" || echo "(none)"
echo "-------------------------------------"

# Positive: the assembly was actually loaded on this server version. A targetAbi mismatch or a
# disabled plugin shows up here as silence.
if ! grep -qiE "Loaded (assembly )?Jellyfin\.Plugin\.Flynn|Loaded plugin: .?Flynn" <<< "$LOG"; then
  echo "::error::Flynn was never loaded. Check targetAbi against this server, and whether the plugin was disabled."
  exit 1
fi

# Negative: none of the known ways a plugin dies may appear. Malfunctioned is what an unlisted
# native DLL produces; MissingMethodException is what an ABI drift produces.
FAILURES="BadImageFormatException|Malfunctioned|Failed to load plugin|Failed to load assembly|MissingMethodException|MethodAccessException|Could not load file or assembly|Skipping disabled plugin.*Flynn"
if grep -iE "$FAILURES" <<< "$LOG"; then
  echo "::error::A plugin load failure signature appeared in the server log."
  exit 1
fi

# Loading is not the same as working. This is the end-to-end proof that the socle came up on a
# real server: DI resolved, the startup service ran, and -- the assumption most worth checking --
# SQLite resolved to the SERVER's Microsoft.Data.Sqlite and its native e_sqlite3, since the plugin
# deliberately ships neither. If that fallback broke, this line is what goes missing.
if ! grep -qE "Flynn database ready at schema version [0-9]+" <<< "$LOG"; then
  echo "::error::Flynn loaded but its database never came up. If the plugin ships no SQLite binary,"
  echo "::error::this is the load-context fallback to the server's copy failing."
  exit 1
fi

grep -E "Flynn database ready at schema version [0-9]+" <<< "$LOG"
echo "Flynn loaded cleanly on $CONTAINER and its database is up."
