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
#
# Polled, not asserted once. The server answers /System/Info/Public before the plugin's hosted
# services have finished, so a single check races them: it passed with 0.6s of slack on one run
# and failed with 0.2s on the next. Waiting for the condition is the fix; widening a sleep is not.
DB_READY="Flynn database ready at schema version [0-9]+"
for _ in $(seq 1 60); do
  if grep -qE "$DB_READY" <<< "$LOG"; then
    grep -E "$DB_READY" <<< "$LOG"
    echo "Flynn loaded cleanly on $CONTAINER and its database is up."
    exit 0
  fi
  sleep 1
  LOG="$(docker logs "$CONTAINER" 2>&1)"
done

echo "::error::Flynn loaded but its database never came up within 60s. If the plugin ships no"
echo "::error::SQLite binary, this is the load-context fallback to the server's copy failing."
exit 1
