#!/usr/bin/env bash
# Assert that a running Jellyfin container loaded Flynn cleanly.
#
# Both halves matter. The positive assertion alone would pass on a server that logged the load and
# then disabled the plugin; the negative assertion alone would pass on a server that never tried to
# load it at all. Neither is a useful signal by itself.
set -euo pipefail

CONTAINER="${1:-jf}"
LOG="$(docker logs "$CONTAINER" 2>&1)"

echo "--- Flynn lines in the server log ---"
echo "$LOG" | grep -i "flynn" || echo "(none)"
echo "-------------------------------------"

# Positive: the assembly was actually loaded on this server version. A targetAbi mismatch or a
# disabled plugin shows up here as silence.
if ! echo "$LOG" | grep -qiE "Loaded (assembly )?Jellyfin\.Plugin\.Flynn|Loaded plugin: .?Flynn"; then
  echo "::error::Flynn was never loaded. Check targetAbi against this server, and whether the plugin was disabled."
  exit 1
fi

# Negative: none of the known ways a plugin dies may appear. Malfunctioned is what an unlisted
# native DLL produces; MissingMethodException is what an ABI drift produces.
FAILURES="BadImageFormatException|Malfunctioned|Failed to load plugin|Failed to load assembly|MissingMethodException|MethodAccessException|Could not load file or assembly|Skipping disabled plugin.*Flynn"
if echo "$LOG" | grep -iE "$FAILURES"; then
  echo "::error::A plugin load failure signature appeared in the server log."
  exit 1
fi

echo "Flynn loaded cleanly on $CONTAINER, with no load or resolution errors."

# Loading is not the same as working. This is the end-to-end proof that the socle came up on a
# real server: DI resolved, the startup service ran, and -- the assumption most worth checking --
# SQLite resolved to the SERVER's Microsoft.Data.Sqlite and its native e_sqlite3, since the plugin
# deliberately ships neither. If that fallback did not work, the message below is what is missing.
if ! echo "$LOG" | grep -qE "Flynn database ready at schema version [0-9]+"; then
  echo "::error::Flynn loaded but its database never came up. If the plugin ships no SQLite binary,"
  echo "::error::this is the load-context fallback to the server's copy failing."
  exit 1
fi

echo "$LOG" | grep -E "Flynn database ready at schema version [0-9]+"
