# syntax=docker/dockerfile:1.7

# ==============================================================================
# Gatehouse container image.
#
# Produces a self-contained NativeAOT executable on a chiselled base: no .NET runtime, no
# shell, no package manager. The result is a few tens of megabytes with almost
# nothing in it for a scanner to find, which is most of the reason a .NET gateway
# is worth building at all.
#
# Build from the repository root, not from this directory:
#     docker build -f deploy/docker/Dockerfile -t gatehouse:dev .
#
# Base images are pinned by digest rather than by tag. A tag is a mutable pointer, so
# "the image we tested" and "the image we shipped" can differ silently — which is the
# gap the SBOM and provenance in SECURITY.md exist to close. Both digests are for the
# multi-arch manifest list, so per-platform resolution still works.
#
# Pinning does not mean falling behind on base-image patches: Dependabot is configured
# for the docker ecosystem, so digest bumps arrive as reviewable pull requests instead
# of arriving invisibly.
# ==============================================================================

# ------------------------------------------------------------------------------
# Build stage
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build

# NativeAOT links with clang and needs the zlib headers. The SDK image does not
# ship them, and the failure without them is a linker error late in the build.
RUN apt-get update \
 && apt-get install --yes --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Restore before copying sources so a source-only change does not re-download the
# package graph. Central Package Management means one file holds every version,
# which makes this layer stable.
#
# .editorconfig belongs in this list and is easy to leave out. It carries the analyzer
# severity settings, and TreatWarningsAsErrors is on repository-wide, so without it the
# container build fails on diagnostics the local and CI builds deliberately suppress —
# a confusing way to discover that a file was missing from the build context.
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.config .editorconfig ./
# Every project file, listed individually so that the restore layer is cached against the
# dependency graph rather than against the source. A new project must be added here or restore
# fails inside the image while succeeding everywhere else.
COPY src/Gatehouse.Core/Gatehouse.Core.csproj                                     src/Gatehouse.Core/
COPY src/Gatehouse.Providers.Anthropic/Gatehouse.Providers.Anthropic.csproj       src/Gatehouse.Providers.Anthropic/
COPY src/Gatehouse.Providers.AzureOpenAI/Gatehouse.Providers.AzureOpenAI.csproj   src/Gatehouse.Providers.AzureOpenAI/
COPY src/Gatehouse.Providers.Bedrock/Gatehouse.Providers.Bedrock.csproj           src/Gatehouse.Providers.Bedrock/
COPY src/Gatehouse.Providers.Google/Gatehouse.Providers.Google.csproj             src/Gatehouse.Providers.Google/
COPY src/Gatehouse.Providers.OpenAI/Gatehouse.Providers.OpenAI.csproj             src/Gatehouse.Providers.OpenAI/
COPY src/Gatehouse.Storage.Sqlite/Gatehouse.Storage.Sqlite.csproj                 src/Gatehouse.Storage.Sqlite/
COPY src/Gatehouse.Server/Gatehouse.Server.csproj                                 src/Gatehouse.Server/

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

# Staged here so the runtime stage can COPY it with the right ownership. The chiselled
# runtime base has no shell, so `RUN mkdir` is not available there — and the directory has to
# exist in the image, because Docker creates a missing volume mount point owned by root. The
# gateway runs as an unprivileged user, so a root-owned data directory means SQLite cannot
# open its file and the container fails its readiness probe on first start.
RUN mkdir -p /staging/var/lib/gatehouse

# Drop debug artifacts, then keep everything else.
#
# "Single binary" is very nearly true and not quite: NativeAOT does not statically link the
# SQLite native library, so libe_sqlite3.so sits beside the executable and is loaded by
# P/Invoke at first use. Copying only the executable produces an image that starts, passes a
# port check, and then dies with DllNotFoundException the moment anything touches the request
# log. Copy the publish directory as a whole so a future native dependency cannot reintroduce
# the same failure.
RUN rm -f /app/*.dbg /app/*.pdb


# ------------------------------------------------------------------------------
# Runtime stage
# ------------------------------------------------------------------------------
# runtime-deps rather than aspnet: the binary is self-contained, so all this base
# provides is libc and the TLS stack. "chiselled" strips the shell and package
# manager; InvariantGlobalization=true in the project means ICU is not needed.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled@sha256:afd8faf9aca00ee69b44b33631665e654e2423e143b6362dffe52c006cd7c4e6 AS runtime

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

COPY --from=build --chown=app:app /app/ /app/

# SQLite is the default store and needs somewhere writable that survives a
# restart. The directory is copied in — rather than only declared as a volume —
# so that it exists in the image owned by the unprivileged runtime user. Docker
# propagates an existing mount point's ownership to a fresh volume; a mount point
# that does not exist is created owned by root, which the gateway cannot write.
COPY --from=build --chown=app:app /staging/var/lib/gatehouse /var/lib/gatehouse

# Declaring the volume means an operator who forgets to mount one gets an
# anonymous volume rather than a container that silently cannot record usage.
VOLUME ["/var/lib/gatehouse"]

ENV Gatehouse__Store__ConnectionString="Data Source=/var/lib/gatehouse/gatehouse.db" \
    ASPNETCORE_URLS="http://+:8080" \
    DOTNET_gcServer=1

EXPOSE 8080

# No HEALTHCHECK instruction: it would need a shell or curl, and this image has
# neither by design. Point your orchestrator at /health/ready instead — Kubernetes,
# ECS and Nomad all probe over HTTP without help from inside the container.

ENTRYPOINT ["/app/gatehouse"]
