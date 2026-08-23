#!/usr/bin/env bash
#
# The Phase 0 gate, executed against the published NativeAOT binary.
#
#     smoke-test.sh <path-to-gatehouse-binary>
#
# Proves that one binary, with no .NET runtime installed alongside it, will:
#   1. start and report ready,
#   2. list its configured models,
#   3. proxy a streamed completion, delivering chunks incrementally,
#   4. proxy a buffered completion and report provider token usage,
#   5. reject an unconfigured model with 404 rather than 500.
#
# Publishing without running the result proves only that the linker exited zero.
# AOT failures characteristically surface at first use — a missing serializer
# context, a trimmed type — which is exactly what steps 2 to 5 exercise.

set -euo pipefail

BINARY="${1:?usage: smoke-test.sh <path-to-gatehouse-binary>}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

GATEWAY_PORT=18080
UPSTREAM_PORT=18081
WORK_DIR="$(mktemp -d)"

# On Windows this script runs under Git Bash, where mktemp yields a POSIX path
# (/tmp/...) that the native binary cannot open. Anything handed to gatehouse.exe
# — its config path, and the SQLite path inside that config — has to be the
# Windows form, while shell file operations keep using the POSIX form.
if command -v cygpath >/dev/null 2>&1; then
    WORK_DIR_NATIVE="$(cygpath -m "$WORK_DIR")"
else
    WORK_DIR_NATIVE="$WORK_DIR"
fi

# Windows runners provide Python as `python`; Linux runners as `python3`.
if command -v python3 >/dev/null 2>&1; then
    PYTHON=python3
elif command -v python >/dev/null 2>&1; then
    PYTHON=python
else
    echo "FAIL: no Python interpreter available to host the stub upstream." >&2
    exit 1
fi

STUB_PID=""
GATEWAY_PID=""

cleanup() {
    local status=$?

    [ -n "$GATEWAY_PID" ] && kill "$GATEWAY_PID" 2>/dev/null || true
    [ -n "$STUB_PID" ] && kill "$STUB_PID" 2>/dev/null || true

    if [ "$status" -ne 0 ] && [ -f "$WORK_DIR/gateway.log" ]; then
        echo "--- gateway log ---" >&2
        cat "$WORK_DIR/gateway.log" >&2
    fi

    # Signals are asynchronous, so the gateway may still hold the SQLite file for a
    # moment after kill returns. Wait for it before deleting, and never let a
    # cleanup failure change the verdict of the test.
    for _ in $(seq 1 20); do
        if [ -z "$GATEWAY_PID" ] || ! kill -0 "$GATEWAY_PID" 2>/dev/null; then
            break
        fi
        sleep 0.25
    done

    rm -rf "$WORK_DIR" 2>/dev/null || true
    return $status
}
trap cleanup EXIT

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

# ------------------------------------------------------------------------------
# Configuration
# ------------------------------------------------------------------------------
cat > "$WORK_DIR/gatehouse.json" <<JSON
{
  "Gatehouse": {
    "Store": {
      "ConnectionString": "Data Source=$WORK_DIR_NATIVE/gatehouse.db",
      "AutoMigrate": true
    },
    "Telemetry": { "ServiceName": "gatehouse-smoke" },
    "Providers": {
      "stub": {
        "Kind": "openai-compatible",
        "BaseUrl": "http://127.0.0.1:$UPSTREAM_PORT/v1",
        "TimeoutSeconds": 30
      }
    },
    "Models": {
      "smoke-model": { "Provider": "stub", "UpstreamModel": "upstream-name" }
    }
  }
}
JSON

# ------------------------------------------------------------------------------
# Start the stub upstream
# ------------------------------------------------------------------------------
"$PYTHON" "$SCRIPT_DIR/stub-upstream.py" "$UPSTREAM_PORT" > "$WORK_DIR/stub.log" 2>&1 &
STUB_PID=$!

for _ in $(seq 1 30); do
    if curl --silent --output /dev/null --max-time 1 \
        --data '{"model":"x"}' "http://127.0.0.1:$UPSTREAM_PORT/v1/chat/completions"; then
        break
    fi
    sleep 0.5
done

# ------------------------------------------------------------------------------
# Issue a virtual key
# ------------------------------------------------------------------------------
# Authentication is required by default, and a gateway with no keys refuses to
# start — so this step is not optional, and running it here also proves the
# `keys` subcommand works in the published NativeAOT binary rather than only
# under the JIT.
echo "Creating a virtual key"
"$BINARY" keys create --name smoke-test --org ci --team ci --app smoke \
    --config "$WORK_DIR_NATIVE/gatehouse.json" > "$WORK_DIR/keys.txt" 2>&1 \
    || { echo "--- keys create output ---" >&2; cat "$WORK_DIR/keys.txt" >&2; fail "keys create failed"; }

SECRET="$(grep -oE 'gh-sk-[A-Za-z0-9_-]+' "$WORK_DIR/keys.txt" | head -n 1)"
[ -n "$SECRET" ] || { cat "$WORK_DIR/keys.txt" >&2; fail "no secret in the keys create output"; }
echo "PASS: virtual key issued"

AUTH_HEADER="Authorization: Bearer $SECRET"

# ------------------------------------------------------------------------------
# Start the gateway
# ------------------------------------------------------------------------------
echo "Starting $BINARY"
"$BINARY" --config "$WORK_DIR_NATIVE/gatehouse.json" --urls "http://127.0.0.1:$GATEWAY_PORT" \
    > "$WORK_DIR/gateway.log" 2>&1 &
GATEWAY_PID=$!

ready=0
for _ in $(seq 1 60); do
    if curl --silent --fail --max-time 2 "http://127.0.0.1:$GATEWAY_PORT/health/ready" >/dev/null; then
        ready=1
        break
    fi

    # A dead process will never become ready; fail immediately rather than after
    # the full timeout, so the log is the first thing the reader sees.
    if ! kill -0 "$GATEWAY_PID" 2>/dev/null; then
        fail "the gateway exited during startup"
    fi

    sleep 0.5
done

[ "$ready" -eq 1 ] || fail "the gateway did not report ready within 30 seconds"
echo "PASS: gateway started and reported ready"

# ------------------------------------------------------------------------------
# 2. Model listing
# ------------------------------------------------------------------------------
models="$(curl --silent --fail --header "$AUTH_HEADER" "http://127.0.0.1:$GATEWAY_PORT/v1/models")"
echo "$models" | grep -q 'smoke-model' \
    || fail "/v1/models did not list the configured alias. Got: $models"
echo "PASS: /v1/models lists the configured alias"

# ------------------------------------------------------------------------------
# 3. Streamed completion
# ------------------------------------------------------------------------------
# --no-buffer keeps curl from hiding the very behaviour under test.
stream_output="$WORK_DIR/stream.txt"
start_ns=$(date +%s%N)

curl --silent --no-buffer --fail \
    --header "$AUTH_HEADER" \
    --header 'Content-Type: application/json' \
    --data '{"model":"smoke-model","stream":true,"messages":[{"role":"user","content":"hello"}]}' \
    "http://127.0.0.1:$GATEWAY_PORT/v1/chat/completions" > "$stream_output" \
    || fail "the streamed request failed"

end_ns=$(date +%s%N)
elapsed_ms=$(( (end_ns - start_ns) / 1000000 ))

grep -q 'data: ' "$stream_output" || fail "no SSE data frames in the streamed response"
grep -q '\[DONE\]' "$stream_output" || fail "the stream did not terminate with [DONE]"

# Reassemble the text the client would have seen.
text="$(grep '^data: {' "$stream_output" \
        | sed 's/^data: //' \
        | "$PYTHON" -c '
import json, sys
out = []
for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    chunk = json.loads(line)
    for choice in chunk.get("choices", []):
        content = choice.get("delta", {}).get("content")
        if content:
            out.append(content)
print("".join(out), end="")
')"

[ "$text" = "The gate is open." ] \
    || fail "reassembled stream text was '$text', expected 'The gate is open.'"
echo "PASS: streamed completion reassembled to '$text'"

# The stub sleeps 150 ms before each of four chunks, so an honest stream takes at
# least ~600 ms. A gateway that buffered the upstream would return sooner only if
# it also skipped the delays, which it cannot — but a *client*-side buffer would
# still show the full elapsed time, so this checks the floor, not the ceiling.
[ "$elapsed_ms" -ge 500 ] \
    || fail "stream completed in ${elapsed_ms}ms, faster than the stub can produce it"
echo "PASS: stream took ${elapsed_ms}ms, consistent with incremental delivery"

# Every chunk must arrive as its own SSE frame. One frame containing everything
# would mean the relay concatenated the stream before flushing.
frame_count="$(grep -c '^data: {' "$stream_output")"
[ "$frame_count" -ge 4 ] \
    || fail "expected at least 4 SSE frames, got $frame_count (the stream was coalesced)"
echo "PASS: $frame_count separate SSE frames"

# ------------------------------------------------------------------------------
# 4. Buffered completion
# ------------------------------------------------------------------------------
buffered="$(curl --silent --fail \
    --header "$AUTH_HEADER" \
    --header 'Content-Type: application/json' \
    --data '{"model":"smoke-model","stream":false,"messages":[{"role":"user","content":"hello"}]}' \
    "http://127.0.0.1:$GATEWAY_PORT/v1/chat/completions")"

echo "$buffered" | grep -q '"total_tokens":18' \
    || fail "buffered response did not carry provider token usage. Got: $buffered"
echo "$buffered" | grep -q '"gatehouse_provider":"stub"' \
    || fail "buffered response did not name the serving provider. Got: $buffered"
echo "PASS: buffered completion reports provider usage and the serving provider"

# ------------------------------------------------------------------------------
# 5. Unknown model
# ------------------------------------------------------------------------------
status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    --header "$AUTH_HEADER" \
    --header 'Content-Type: application/json' \
    --data '{"model":"not-configured","messages":[{"role":"user","content":"hi"}]}' \
    "http://127.0.0.1:$GATEWAY_PORT/v1/chat/completions")"

[ "$status" = "404" ] || fail "unknown model returned HTTP $status, expected 404"
echo "PASS: unknown model rejected with 404"

# ------------------------------------------------------------------------------
# 6. Authentication is actually enforced
# ------------------------------------------------------------------------------
# The same request without a credential. A gateway holding provider credentials
# that serves anonymous callers is the one failure here worth failing the build
# over, so it is asserted rather than assumed from the fact that the authenticated
# calls above worked.
status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    --header 'Content-Type: application/json' \
    --data '{"model":"smoke-model","messages":[{"role":"user","content":"hi"}]}' \
    "http://127.0.0.1:$GATEWAY_PORT/v1/chat/completions")"

[ "$status" = "401" ] || fail "an unauthenticated request returned HTTP $status, expected 401"
echo "PASS: unauthenticated request rejected with 401"

# ------------------------------------------------------------------------------
# The request log must exist. A gateway that serves traffic without recording it
# has failed at its actual job, and under AOT this is where a trimmed SQLite
# provider would show up.
# ------------------------------------------------------------------------------
[ -f "$WORK_DIR/gatehouse.db" ] || fail "no SQLite request log was created"
echo "PASS: SQLite request log created"

echo
echo "Phase 0 gate satisfied: a streamed completion proxied through one binary."
