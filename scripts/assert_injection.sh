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

# Retried, for the same reason the database check is: the server answers the SPA shell before its
# API pipeline is fully up, and a single probe races that. If it never comes good, the response is
# dumped rather than reduced to a status code, because 503 from a warming server and 503 from a
# route that does not exist need different fixes.
status=""
for _ in $(seq 1 30); do
  status="$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 "${BASE}${src}")"
  [ "$status" = "200" ] && break
  sleep 2
done

if [ "$status" != "200" ]; then
  echo "--- response from ${src} ---"
  curl -s -i --max-time 30 "${BASE}${src}" | head -30 || true
  echo "--- routes the server knows about (first 40) ---"
  curl -s --max-time 30 "${BASE}/System/Info/Public" | head -5 || true
  fail "${src} answered ${status}; the injected tag points at nothing."
fi
echo "  ${src}: 200"

# An API response must never be touched. If the path filter is too broad, this is where it shows.
json="$(curl -fsSL --max-time 30 "${BASE}/System/Info/Public")"
if grep -q "$MARKER" <<< "$json"; then
  fail "A JSON API response was rewritten. The path filter is too broad."
fi
echo "  /System/Info/Public: untouched"

# The admin page pulls these two by URL. A 404 here is a config page that loads and then does
# nothing, which looks like a broken plugin rather than a missing file.
for asset in "/Flynn/admin.js" "/Flynn/admin.css"; do
  code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 "${BASE}${asset}")"
  [ "$code" = "200" ] || fail "${asset} answered ${code}; the admin page would load without it."
  echo "  ${asset}: 200"
done

# Security, not delivery. The asset endpoints are anonymous by necessity because a browser sends
# no authorization header for a script tag, and it would be easy to let that spread to the
# endpoints that actually return data. Anything but 200 is fine here; 200 is not.
for guarded in "/Flynn/modules" "/Flynn/issues"; do
  code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 "${BASE}${guarded}")"
  [ "$code" != "200" ] || fail "${guarded} answered 200 without authentication."
  echo "  ${guarded}: ${code} without auth, as it should be"
done

echo "Flynn client delivery verified on ${BASE}."
