#!/usr/bin/env bash
# Assert that Flynn's client script is actually delivered by a running server.
#
# Everything client-side depends on this one thing working, and when it does not the failure is
# silent: the page renders normally, minus every feature. Worth checking against a real server
# rather than trusting the unit tests on the injection helpers.
set -euo pipefail

BASE="${1:-http://localhost:8096}"
MARKER='id="flynn-client"'

fail() { echo "::error::$1"; exit 1; }

for path in "/web/index.html" "/"; do
  body="$(curl -fsSL --max-time 30 "${BASE}${path}")" || fail "GET ${path} failed"

  count="$(grep -c "$MARKER" <<< "$body" || true)"
  if [ "$count" -eq 0 ]; then
    fail "No Flynn script tag on ${path}. The middleware did not rewrite the SPA shell."
  fi
  # Loading the client twice means duplicated listeners and duplicated network calls, so "present"
  # is not enough on its own.
  if [ "$count" -ne 1 ]; then
    fail "Flynn script tag appears ${count} times on ${path}; it must appear exactly once."
  fi

  # A rewritten body with a stale Content-Length gets truncated by the browser, and a truncated
  # document usually loses its own closing tag first.
  grep -qi "</html>" <<< "$body" || fail "Document from ${path} is not well formed after rewriting."

  echo "  ${path}: tag present exactly once, document intact"
done

# The tag is worthless if what it points at is a 404.
src="$(grep -o 'src="/Flynn/client\.js?v=[^"]*"' <<< "$(curl -fsSL --max-time 30 "${BASE}/web/index.html")" | head -1 | sed 's/^src="//; s/"$//')"
[ -n "$src" ] || fail "Could not read the script src out of the rewritten document."

status="$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 "${BASE}${src}")"
[ "$status" = "200" ] || fail "${src} answered ${status}; the injected tag points at nothing."
echo "  ${src}: 200"

# An API response must never be touched. If the path filter is too broad, this is where it shows.
json="$(curl -fsSL --max-time 30 "${BASE}/System/Info/Public")"
if grep -q "$MARKER" <<< "$json"; then
  fail "A JSON API response was rewritten. The path filter is too broad."
fi
echo "  /System/Info/Public: untouched"

echo "Flynn client delivery verified on ${BASE}."
