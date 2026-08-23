# syntax=docker/dockerfile:1.7

# ==============================================================================
# Gatehouse container image.
#
# Produces a NativeAOT single binary on a chiselled base: no .NET runtime, no
# shell, no package manager. The result is a few tens of megabytes with almost
# nothing in it for a scanner to find, which is most of the reason a .NET gateway
# is worth building at all.
#
# Build from the repository root, not from this directory:
#     docker build -f deploy/docker/Dockerfile -t gatehouse:dev .
# ==============================================================================

# ------------------------------------------------------------------------------
# Build stage
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# NativeAOT links with clang and needs the zlib headers. The SDK image does not
# ship them, and the failure without them is a linker error late in the build.
RUN apt-get update \
 && apt-get install --yes --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Restore before copying sources so a source-only change does not re-download the
# package graph. Central Package Management means one file holds every version,
# which makes this layer stable.
# .editorconfig is not optional here. It carries the analyzer severity settings, and
# TreatWarningsAsErrors is on repository-wide — without it the container build fails on
# diagnostics that the local and CI builds correctly suppress, which is a confusing way to
# discover that a file was missing from the build context.
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.config .editorconfig ./
COPY src/Gatehouse.Core/Gatehouse.Core.csproj                             src/Gatehouse.Core/
COPY src/Gatehouse.Providers.OpenAI/Gatehouse.Providers.OpenAI.csproj     src/Gatehouse.Providers.OpenAI/
COPY src/Gatehouse.Storage.Sqlite/Gatehouse.Storage.Sqlite.csproj         src/Gatehouse.Storage.Sqlite/
COPY src/Gatehouse.Server/Gatehouse.Server.csproj                         src/Gatehouse.Server/

ARG TARGETARCH
RUN RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
 && dotnet restore src/Gatehouse.Server/Gatehouse.Server.csproj --runtime "$RID"

COPY src/ src/
COPY README.md ./

# VersionSuffix is cleared for a tagged release build so the artifact is not
# labelled "alpha" by the default in Directory.Build.props.
ARG VERSION=0.1.0
ARG VERSION_SUFFIX=alpha

RUN RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
 && dotnet publish src/Gatehouse.Server/Gatehouse.Server.csproj \
      --configuration Release \
      --runtime "$RID" \
      --self-contained true \
      --no-restore \
      -p:PublishAot=true \
      -p:VersionPrefix="$VERSION" \
      -p:VersionSuffix="$VERSION_SUFFIX" \
      --output /app

# ------------------------------------------------------------------------------
# Runtime stage
# ------------------------------------------------------------------------------
# runtime-deps rather than aspnet: the binary is self-contained, so all this base
# provides is libc and the TLS stack. "chiselled" strips the shell and package
# manager; InvariantGlobalization=true in the project means ICU is not needed.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS runtime

# OCI labels, so `docker inspect` answers the provenance questions an auditor asks.
LABEL org.opencontainers.image.title="Gatehouse" \
      org.opencontainers.image.description="The open AI control plane for the enterprise." \
      org.opencontainers.image.source="https://github.com/zcsizmadia/Gatehouse" \
      org.opencontainers.image.documentation="https://github.com/zcsizmadia/Gatehouse#readme" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.vendor="The Gatehouse Project"

# The chiselled images define a non-root "app" user (uid 64198). Gatehouse holds
# provider credentials, so it never runs as root.
USER app
WORKDIR /app

COPY --from=build --chown=app:app /app/gatehouse /app/gatehouse

# SQLite is the default store and needs somewhere writable that survives a
# restart. Declaring it a volume means an operator who forgets to mount one gets
# an anonymous volume rather than a container that cannot record usage.
VOLUME ["/var/lib/gatehouse"]

ENV Gatehouse__Store__ConnectionString="Data Source=/var/lib/gatehouse/gatehouse.db" \
    ASPNETCORE_URLS="http://+:8080" \
    DOTNET_gcServer=1

EXPOSE 8080

# No HEALTHCHECK instruction: it would need a shell or curl, and this image has
# neither by design. Point your orchestrator at /health/ready instead — Kubernetes,
# ECS and Nomad all probe over HTTP without help from inside the container.

ENTRYPOINT ["/app/gatehouse"]
