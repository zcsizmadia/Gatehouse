# Deploying Gatehouse with Docker

The image is a NativeAOT binary on a chiselled base: no .NET runtime, no shell, no
package manager. It runs as an unprivileged user and contains almost nothing for a
scanner to find.

## Build

From the repository root — not from `deploy/docker`, because the build needs the
whole source tree:

```bash
docker build -f deploy/docker/Dockerfile -t gatehouse:dev .
```

Multi-architecture, if you need arm64:

```bash
docker buildx build -f deploy/docker/Dockerfile \
  --platform linux/amd64,linux/arm64 \
  -t gatehouse:dev .
```

## Run

```bash
docker run --rm -p 8080:8080 \
  -v gatehouse-data:/var/lib/gatehouse \
  -v "$PWD/samples/gatehouse.json:/etc/gatehouse/gatehouse.json:ro" \
  -e OPENAI_API_KEY \
  gatehouse:dev --config /etc/gatehouse/gatehouse.json
```

Then:

```bash
curl -H "Authorization: Bearer gh-sk-..." http://localhost:8080/v1/models
```

### Issue a key first

Authentication is required by default and the container will exit on startup without
a key. The image has no shell, but the entrypoint is the binary, so the CLI is
reachable directly:

```bash
docker volume create gatehouse-data

docker run --rm -v gatehouse-data:/var/lib/gatehouse \
  gatehouse:dev keys create --name my-app --org acme
```

The secret is printed once. Use the same volume when starting the gateway, or it
will not find the key.

### The volume is not optional

SQLite is the default store, and `/var/lib/gatehouse` is where it lives. Without a
mounted volume the request log dies with the container — which means your usage and
audit history dies with it. The image declares the path as a `VOLUME` so a forgotten
mount produces an anonymous volume rather than silent data loss, but name it
yourself.

## Configuration

Either mount a file and pass `--config`, or use environment variables. The
environment form uses `__` as the separator:

```bash
docker run --rm -p 8080:8080 \
  -v gatehouse-data:/var/lib/gatehouse \
  -e Gatehouse__Providers__openai__Kind=openai-compatible \
  -e Gatehouse__Providers__openai__BaseUrl=https://api.openai.com/v1 \
  -e Gatehouse__Providers__openai__ApiKeyEnvironmentVariable=OPENAI_API_KEY \
  -e Gatehouse__Models__gpt-4o-mini__Provider=openai \
  -e OPENAI_API_KEY \
  gatehouse:dev
```

Note `-e OPENAI_API_KEY` with no value: that passes the variable through from your
shell rather than writing the secret into the command, your shell history, and
`docker inspect`.

For anything beyond a laptop, use your orchestrator's secret mechanism — Kubernetes
secrets mounted as environment variables, ECS task-definition secrets, Docker
Swarm secrets. On Azure, prefer managed identity and store no credential at all.

## Health checks

The image intentionally has **no `HEALTHCHECK` instruction**. Implementing one
would require a shell or `curl` inside the image, which is exactly what the
chiselled base exists to avoid.

Point your orchestrator at the HTTP endpoints instead. Kubernetes, ECS and Nomad
all probe over HTTP without help from inside the container:

| Endpoint         | Use as       |
| ---------------- | ------------ |
| `/health/live`   | liveness     |
| `/health/ready`  | readiness    |

`/health/ready` returns 200 only after configuration has validated and the request
log schema is in place, so a misconfigured deployment fails its readiness probe
rather than serving errors.

## Kubernetes

A Helm chart arrives in Phase 3. Until then, Gatehouse is an ordinary stateless
Deployment plus a volume, with one caveat worth knowing:

**SQLite does not support multiple writers across pods.** For a single replica,
mount a `PersistentVolumeClaim` and you are done. For more than one replica you
need either one PVC per pod (a StatefulSet, each pod keeping its own request log,
aggregated downstream) or a shared store — which is what the pluggable
`IRequestLogStore` exists for, and what Phase 3 addresses properly.

Kubernetes remains **optional**, not assumed. A single container on a single host
is a supported production topology.

## Security notes

- The container runs as the non-root `app` user. Do not override it with
  `--user root`; Gatehouse never needs it and holds provider credentials.
- Terminate TLS in front of the gateway. See
  [SECURITY.md](../../SECURITY.md#hardening-guidance).
- Bind the inference port to application networks only. Do not expose the gateway
  to the public internet without an authenticating layer in front — virtual keys
  arrive in Phase 1.

## Verifying the image

Released images carry Sigstore signatures and SLSA provenance. See
[SECURITY.md](../../SECURITY.md#verifying-a-release). Locally built images carry
neither, which is as it should be.
