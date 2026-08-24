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
#   5. reject an unconfigured model with 404 rather than 500,
#   6. reject an unauthenticated request with 401,
#   7. start from the sample configuration the README points new users at,
#   8. report recorded usage and reconcile it against a provider statement,
#   9. serve a repeated request from the exact-match response cache.
#
# Publishing without running the result proves only that the linker exited zero.
# AOT failures characteristically surface at first use — a missing serializer
# context, a trimmed type — which is exactly what steps 2 to 9 exercise.

set -euo pipefail

BINARY="${1:?usage: smoke-test.sh <path-to-gatehouse-binary>}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

GATEWAY_PORT=18080
UPSTREAM_PORT=18081

# A second gateway port, for the step that checks the shipped sample config
# starts. It runs after the main gateway has been asserted against, but that one
# is still bound, so it needs a port of its own.
SAMPLE_PORT=18082

# A third gateway port, for the cache check, which runs with caching enabled while
# the other two are still bound.
CACHE_PORT=18083

# samples/ lives two levels above .github/scripts.
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
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
SAMPLE_PID=""
CACHE_PID=""

cleanup() {
    local status=$?

    [ -n "$GATEWAY_PID" ] && kill "$GATEWAY_PID" 2>/dev/null || true
    [ -n "$STUB_PID" ] && kill "$STUB_PID" 2>/dev/null || true
    [ -n "$SAMPLE_PID" ] && kill "$SAMPLE_PID" 2>/dev/null || true
    [ -n "$CACHE_PID" ] && kill "$CACHE_PID" 2>/dev/null || true

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
# ------------------------------------------------------------------------------

# ------------------------------------------------------------------------------
# 7. The shipped sample configuration must actually load
# ------------------------------------------------------------------------------
# samples/gatehouse.json is what the README's quick start points people at, and
# nothing else in CI reads it. It shipped for two phases with a '//' comment key
# inside the Models dictionary, which binds as a model literally named '//' and
# makes startup validation reject the file — so the very first thing a new user
# was told to run could not start. Asserting on it here is cheap and permanent.
#
# Startup contacts no provider, so the real endpoints in the sample are never
# called; this exercises configuration binding and validation only.
echo "Validating the shipped sample configuration"

SAMPLE_DIR="$WORK_DIR/sample"
mkdir -p "$SAMPLE_DIR"
cp "$REPO_ROOT/samples/gatehouse.json" "$SAMPLE_DIR/gatehouse.json"

SAMPLE_DIR_NATIVE="$SAMPLE_DIR"
if command -v cygpath >/dev/null 2>&1; then
    SAMPLE_DIR_NATIVE="$(cygpath -m "$SAMPLE_DIR")"
fi

( cd "$SAMPLE_DIR" && "$BINARY" keys create --name sample-check \
      --config "$SAMPLE_DIR_NATIVE/gatehouse.json" ) > "$SAMPLE_DIR/keys.txt" 2>&1 \
    || { cat "$SAMPLE_DIR/keys.txt" >&2; fail "the sample configuration was rejected by 'keys create'"; }

( cd "$SAMPLE_DIR" && "$BINARY" --config "$SAMPLE_DIR_NATIVE/gatehouse.json" \
      --urls "http://127.0.0.1:$SAMPLE_PORT" ) > "$SAMPLE_DIR/gateway.log" 2>&1 &
SAMPLE_PID=$!

sample_ready=0
for _ in $(seq 1 60); do
    if curl --silent --fail --max-time 2 "http://127.0.0.1:$SAMPLE_PORT/health/ready" >/dev/null; then
        sample_ready=1
        break
    fi

    if ! kill -0 "$SAMPLE_PID" 2>/dev/null; then
        break
    fi

    sleep 0.5
done

if [ "$sample_ready" -ne 1 ]; then
    echo "--- sample gateway log ---" >&2
    cat "$SAMPLE_DIR/gateway.log" >&2
    kill "$SAMPLE_PID" 2>/dev/null || true
    fail "samples/gatehouse.json did not produce a gateway that starts"
fi

kill "$SAMPLE_PID" 2>/dev/null || true
wait "$SAMPLE_PID" 2>/dev/null || true
echo "PASS: samples/gatehouse.json loads, validates and starts"

# ------------------------------------------------------------------------------
# 8. Usage reporting works in the published binary
# ------------------------------------------------------------------------------
# The completions above are already in the request log, so this asserts the whole
# metering path end to end: the aggregation SQL, the v3 cache and metered columns,
# and the CLI's own configuration binding — which is a separate code path from the
# server's, and therefore a separate chance for NativeAOT to break it.
echo "Reporting usage"
usage_out="$("$BINARY" usage summary --from '1970-01-01T00:00:00Z' --to '2999-01-01T00:00:00Z' \
    --config "$WORK_DIR_NATIVE/gatehouse.json" 2>&1)" \
    || { echo "$usage_out" >&2; fail "usage summary failed"; }

# 'upstream-name' is what the route sends upstream, so finding it here also proves
# the aggregation groups by the upstream model rather than the caller's alias.
echo "$usage_out" | grep -q 'upstream-name' \
    || { echo "$usage_out" >&2; fail "usage summary did not report the model that served traffic"; }
echo "PASS: usage summary reports recorded traffic"

# A statement naming a model nobody called must produce a finding and exit 1.
# The exit code is the contract a scheduled month-end job depends on, so it is
# asserted rather than inferred from the text.
cat > "$WORK_DIR/statement.csv" <<'CSV'
provider,model,prompt_tokens,completion_tokens
stub,a-model-nobody-called,50000000,10000000
CSV

set +e
"$BINARY" usage reconcile --statement "$WORK_DIR_NATIVE/statement.csv" \
    --from '1970-01-01T00:00:00Z' --to '2999-01-01T00:00:00Z' \
    --config "$WORK_DIR_NATIVE/gatehouse.json" > "$WORK_DIR/reconcile.txt" 2>&1
reconcile_status=$?
set -e

[ "$reconcile_status" = "1" ] \
    || { cat "$WORK_DIR/reconcile.txt" >&2; fail "usage reconcile exited $reconcile_status, expected 1"; }

grep -q 'NOT RECORDED BY GATEHOUSE' "$WORK_DIR/reconcile.txt" \
    || { cat "$WORK_DIR/reconcile.txt" >&2; fail "reconcile did not flag a model Gatehouse never saw"; }
echo "PASS: usage reconcile flags unrecorded spend and exits 1"

# ------------------------------------------------------------------------------
# 9. Exact-match caching works in the published binary
# ------------------------------------------------------------------------------
# Caching is off by default, so it is enabled here through an environment
# variable rather than by changing the config the earlier steps asserted against.
#
# Worth a step of its own under NativeAOT specifically: the cache key path uses
# Utf8JsonWriter, IncrementalHash and ArrayPool, and cryptography plus pooling is
# exactly the combination that works under the JIT and fails once trimmed.
echo "Checking the response cache"

CACHE_DIR="$WORK_DIR/cache"
mkdir -p "$CACHE_DIR"
sed "s|$WORK_DIR_NATIVE/gatehouse.db|$WORK_DIR_NATIVE/cache/gatehouse.db|" \
    "$WORK_DIR/gatehouse.json" > "$CACHE_DIR/gatehouse.json"

CACHE_DIR_NATIVE="$CACHE_DIR"
if command -v cygpath >/dev/null 2>&1; then
    CACHE_DIR_NATIVE="$(cygpath -m "$CACHE_DIR")"
fi

"$BINARY" keys create --name cache-check --config "$CACHE_DIR_NATIVE/gatehouse.json" \
    > "$CACHE_DIR/keys.txt" 2>&1 \
    || { cat "$CACHE_DIR/keys.txt" >&2; fail "keys create failed for the cache check"; }

CACHE_SECRET="$(grep -oE 'gh-sk-[A-Za-z0-9_-]+' "$CACHE_DIR/keys.txt" | head -n 1)"
[ -n "$CACHE_SECRET" ] || fail "no secret for the cache check"

Gatehouse__Cache__Enabled=true "$BINARY" --config "$CACHE_DIR_NATIVE/gatehouse.json" \
    --urls "http://127.0.0.1:$CACHE_PORT" > "$CACHE_DIR/gateway.log" 2>&1 &
CACHE_PID=$!

cache_ready=0
for _ in $(seq 1 60); do
    if curl --silent --fail --max-time 2 "http://127.0.0.1:$CACHE_PORT/health/ready" >/dev/null; then
        cache_ready=1
        break
    fi
    if ! kill -0 "$CACHE_PID" 2>/dev/null; then
        break
    fi
    sleep 0.5
done

if [ "$cache_ready" -ne 1 ]; then
    cat "$CACHE_DIR/gateway.log" >&2
    fail "the cache-enabled gateway did not start"
fi

cache_body='{"model":"smoke-model","stream":false,"messages":[{"role":"user","content":"cache me"}]}'

# First call populates, second must be served from memory.
curl --silent --fail --header "Authorization: Bearer $CACHE_SECRET" \
    --header 'Content-Type: application/json' --data "$cache_body" \
    "http://127.0.0.1:$CACHE_PORT/v1/chat/completions" > /dev/null \
    || fail "the first cacheable request failed"

cache_header="$(curl --silent --dump-header - --output /dev/null \
    --header "Authorization: Bearer $CACHE_SECRET" \
    --header 'Content-Type: application/json' --data "$cache_body" \
    "http://127.0.0.1:$CACHE_PORT/v1/chat/completions" | grep -i '^x-gatehouse-cache:' || true)"

echo "$cache_header" | grep -qi 'hit' \
    || { cat "$CACHE_DIR/gateway.log" >&2; fail "the repeated request was not served from cache (header: '$cache_header')"; }

kill "$CACHE_PID" 2>/dev/null || true
wait "$CACHE_PID" 2>/dev/null || true
echo "PASS: a repeated request is served from the response cache"

# ------------------------------------------------------------------------------
# The request log must exist. A gateway that serves traffic without recording it
# has failed at its actual job, and under AOT this is where a trimmed SQLite
# provider would show up.
# ------------------------------------------------------------------------------
[ -f "$WORK_DIR/gatehouse.db" ] || fail "no SQLite request log was created"
echo "PASS: SQLite request log created"

echo
echo "Phase 0 gate satisfied: a streamed completion proxied through one binary."
