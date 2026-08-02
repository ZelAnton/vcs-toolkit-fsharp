# vcs-mcp — the Model Context Protocol server, containerised with every VCS/forge CLI
# it can drive already installed. Built and published to ghcr.io by the `image` job of
# .github/workflows/release.yml (tagged with the release version AND `latest`), which
# smoke-tests the built image over stdio BEFORE pushing it.
#
# Build locally:
#   docker build --build-arg VERSION=0.1.0 -t vcs-mcp:dev .
#   pwsh ./scripts/smoke-mcp-image.ps1 -Image vcs-mcp:dev -ExpectedVersion 0.1.0
#
# Deliberately written for the plain, built-in BuildKit frontend: no `# syntax=` line
# (which would pull an external frontend image at build time) and no heredocs, so it
# builds identically on an older daemon and adds no build-time supply-chain surface.
#
# ---------------------------------------------------------------------------------
# DECISION 1 — published output, not `dotnet tool install`.
# The final stage is the .NET *runtime* image with the framework-dependent publish
# output of src/VcsToolkit.Mcp.Server copied in. Installing the `vcs-mcp` global tool
# instead would drag the whole .NET *SDK* (~1 GB) into the shipped image for no runtime
# benefit — a global tool is just those same assemblies plus a shim. `vcs-mcp` is still
# on PATH inside the image (a two-line shim over `dotnet /opt/vcs-mcp/vcs-mcp.dll`), so
# the ENTRYPOINT and any `docker exec` read exactly like the tool install.
#
# DECISION 2 — which CLIs ship (image size vs. a self-consistent feature set).
# ALL FIVE the server can drive are installed: `git`, `jj`, `gh`, `glab`, `tea`. The
# server exposes repo_* over git/jj and forge_* over GitHub/GitLab/Gitea; leaving glab
# or tea out would ship an image whose documented tool catalogue fails at runtime on two
# of the three supported forges, with a spawn error rather than a clear refusal. Measured
# in the built amd64 image, the three forge CLIs are static Go binaries costing gh ~39 MB
# + glab ~49 MB + tea ~18 MB — ~105 MB of a ~620 MB image, the rest of which is the .NET
# runtime base (~310 MB), git (~85 MB with its dependencies), jj (~26 MB) and the server
# itself (~10 MB). Roughly a sixth of the image buys the difference between "works with
# whichever forge you use" and "works only with GitHub"; a deployment that needs the
# smaller image can trim the download steps below, which is a cheaper change than
# diagnosing a missing binary from inside a running MCP session.
#
# DECISION 3 — pinned versions + upstream checksums where they exist.
# Every CLI version is a build ARG pinned to an exact release, so an image rebuild is
# reproducible and an upgrade is an explicit, reviewable change. `jj` is pinned to the
# same version .github/workflows/ci.yml installs, so the container and CI exercise the
# same jj. Downloads are verified against the checksum file the upstream project
# publishes alongside the release (gh, glab, tea). jj publishes no checksum or signature
# asset for its release binaries (verified against the release's asset list), so its
# tarball is only TLS- and version-verified — the `jj --version` assertion below fails
# the build if the downloaded binary is not the pinned release.
# ---------------------------------------------------------------------------------

ARG DOTNET_SDK_TAG=10.0
ARG DOTNET_RUNTIME_TAG=10.0

# ---------------------------------------------------------------------------------
# Stage 1 — build the solution and publish the server.
# ---------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_TAG} AS build

# The release version, so the assembly (and therefore the `serverInfo.version` the
# server advertises over MCP) matches the tag the image is published under. The default
# is only for local builds.
ARG VERSION=0.0.0-dev

ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

WORKDIR /src
COPY . .

# The whole solution, not just the server project: cross-project references in this repo
# are <Reference> + AssemblySearchPaths (never <ProjectReference>) and the build ORDER
# lives in VcsToolkit.slnx's BuildDependency entries, so building the server project
# alone would not build the VcsToolkit.* libraries it resolves from their bin folders.
# Driving the .slnx keeps that ordering in exactly one place — a library added to the
# server's dependency chain needs no edit here.
#
# SourceLink is switched off for this build on purpose: .dockerignore keeps `.git` out of
# the context (the image is built from a checkout's FILES, not its history), and without a
# repository SourceLink emits an "unable to locate repository"/"source link is empty"
# warning pair per project. Disabling it states that intent instead of tolerating a dozen
# warnings — the shipped assemblies are the container's, never the published .nupkg's,
# whose SourceLink metadata is produced by the `release` job's own repository-aware build.
RUN dotnet build VcsToolkit.slnx --configuration Release \
        -p:Version="${VERSION}" \
        -p:EnableSourceLink=false \
        -p:EnableSourceControlManagerQueries=false

# --no-build: reuse the artifacts of the step above rather than rebuilding the project
# with a second, possibly divergent set of properties.
RUN dotnet publish src/VcsToolkit.Mcp.Server/VcsToolkit.Mcp.Server.fsproj \
        --configuration Release \
        --no-build \
        --output /app

# ---------------------------------------------------------------------------------
# Stage 2 — fetch the VCS/forge CLIs that are not distributed as OS packages.
# A throwaway stage (only /out/bin is copied out), so curl/tar and the downloaded
# archives never reach the published image. It reuses the SDK image, already pulled by
# stage 1 and carrying curl/tar/sha256sum, instead of pulling another base image.
# ---------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_TAG} AS clis

# Set by BuildKit to the target platform's architecture; the fallback keeps the build
# correct under a classic (non-BuildKit) builder, which leaves it unset.
ARG TARGETARCH

ARG JJ_VERSION=0.42.0
ARG GH_VERSION=2.97.0
ARG GLAB_VERSION=1.111.0
ARG TEA_VERSION=0.9.2

WORKDIR /tmp/dl

# Resolve the per-project architecture spellings once; every step below sources this.
# An unsupported architecture fails the build loudly instead of downloading an
# amd64 binary onto some other platform.
RUN set -eu; \
    arch="${TARGETARCH:-$(dpkg --print-architecture)}"; \
    case "$arch" in \
      amd64) jj_target='x86_64-unknown-linux-musl'; go_arch='amd64' ;; \
      arm64) jj_target='aarch64-unknown-linux-musl'; go_arch='arm64' ;; \
      *) echo "vcs-mcp image: unsupported target architecture '$arch' (amd64 and arm64 only)" >&2; exit 1 ;; \
    esac; \
    mkdir -p /out/bin; \
    printf 'JJ_TARGET=%s\nGO_ARCH=%s\n' "$jj_target" "$go_arch" > /tmp/arch.env; \
    cat /tmp/arch.env

# jj — statically linked musl build, so it carries no glibc/OpenSSL expectations into
# the runtime image. No upstream checksum/signature asset exists for these tarballs
# (see DECISION 3), so integrity rests on TLS plus the version assertion in stage 3.
RUN set -eu; . /tmp/arch.env; \
    curl --fail --location --proto '=https' --tlsv1.2 --retry 3 --retry-delay 2 --silent --show-error \
         --output jj.tar.gz \
         "https://github.com/jj-vcs/jj/releases/download/v${JJ_VERSION}/jj-v${JJ_VERSION}-${JJ_TARGET}.tar.gz"; \
    tar -xzf jj.tar.gz -C /out/bin ./jj; \
    rm -f jj.tar.gz

# gh — verified against the release's own gh_<version>_checksums.txt.
RUN set -eu; . /tmp/arch.env; \
    asset="gh_${GH_VERSION}_linux_${GO_ARCH}.tar.gz"; \
    base="https://github.com/cli/cli/releases/download/v${GH_VERSION}"; \
    curl --fail --location --proto '=https' --tlsv1.2 --retry 3 --retry-delay 2 --silent --show-error \
         --output "$asset" "${base}/${asset}"; \
    curl --fail --location --proto '=https' --tlsv1.2 --retry 3 --retry-delay 2 --silent --show-error \
         --output checksums.txt "${base}/gh_${GH_VERSION}_checksums.txt"; \
    sha256sum --ignore-missing --check checksums.txt; \
    tar -xzf "$asset" --strip-components=2 -C /out/bin "gh_${GH_VERSION}_linux_${GO_ARCH}/bin/gh"; \
    rm -f "$asset" checksums.txt

# glab — verified against the release's own checksums.txt.
RUN set -eu; . /tmp/arch.env; \
    asset="glab_${GLAB_VERSION}_linux_${GO_ARCH}.tar.gz"; \
    base="https://gitlab.com/gitlab-org/cli/-/releases/v${GLAB_VERSION}/downloads"; \
    curl --fail --location --proto '=https' --tlsv1.2 --retry 3 --retry-delay 2 --silent --show-error \
         --output "$asset" "${base}/${asset}"; \
    curl --fail --location --proto '=https' --tlsv1.2 --retry 3 --retry-delay 2 --silent --show-error \
         --output checksums.txt "${base}/checksums.txt"; \
    sha256sum --ignore-missing --check checksums.txt; \
    tar -xzf "$asset" --strip-components=1 -C /out/bin bin/glab; \
    rm -f "$asset" checksums.txt

# tea — a bare binary plus its sibling .sha256 file on dl.gitea.com.
RUN set -eu; . /tmp/arch.env; \
    asset="tea-${TEA_VERSION}-linux-${GO_ARCH}"; \
    base="https://dl.gitea.com/tea/${TEA_VERSION}"; \
    curl --fail --location --proto '=https' --tlsv1.2 --retry 3 --retry-delay 2 --silent --show-error \
         --output "$asset" "${base}/${asset}"; \
    curl --fail --location --proto '=https' --tlsv1.2 --retry 3 --retry-delay 2 --silent --show-error \
         --output "${asset}.sha256" "${base}/${asset}.sha256"; \
    sha256sum --check "${asset}.sha256"; \
    mv "$asset" /out/bin/tea; \
    rm -f "${asset}.sha256"

# The archives preserve their own modes; normalise anyway so a mode change upstream
# cannot ship a non-executable binary.
RUN set -eu; chmod 0755 /out/bin/jj /out/bin/gh /out/bin/glab /out/bin/tea

# ---------------------------------------------------------------------------------
# Stage 3 — the shipped image: .NET runtime + the CLIs + the published server.
# ---------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_RUNTIME_TAG} AS runtime

ARG VERSION=0.0.0-dev

LABEL org.opencontainers.image.title="vcs-mcp" \
      org.opencontainers.image.description="Model Context Protocol server driving a git/jj repository and its GitHub/GitLab/Gitea forge, with git, jj, gh, glab and tea preinstalled." \
      org.opencontainers.image.source="https://github.com/ZelAnton/vcs-toolkit-fsharp" \
      org.opencontainers.image.documentation="https://github.com/ZelAnton/vcs-toolkit-fsharp/blob/main/docs/mcp-server.md" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}"

# git is the only CLI available as an OS package (the runtime image ships neither it nor
# curl). ca-certificates is what lets git/gh/glab/tea verify TLS at all; openssh-client
# is what makes an `ssh://`/`git@` remote work for repo_fetch/repo_push.
RUN set -eu; \
    apt-get update; \
    DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends \
        git \
        ca-certificates \
        openssh-client; \
    rm -rf /var/lib/apt/lists/*

COPY --from=clis /out/bin/ /usr/local/bin/
COPY --from=build /app/ /opt/vcs-mcp/

# Put `vcs-mcp` on PATH so the container's command line reads like the global tool's.
# `exec` replaces the shim, so the server is PID 1's own process and keeps stdio and
# signal handling intact — the MCP stdio transport depends on both.
RUN set -eu; \
    printf '%s\n' '#!/bin/sh' 'exec dotnet /opt/vcs-mcp/vcs-mcp.dll "$@"' > /usr/local/bin/vcs-mcp; \
    chmod 0755 /usr/local/bin/vcs-mcp

# A bind-mounted repository is owned by the HOST user, never by the container's user, so
# git's ownership check ("detected dubious ownership") would refuse every single repo_*
# tool call — including when the container runs as root. The check defends a *shared*
# machine against a repo planted by another user; this image is a single-purpose,
# single-tenant sidecar whose only repository is the one its operator chose to mount, so
# the check protects nothing here and is disabled image-wide.
RUN git config --system --add safe.directory '*'

# Fail the BUILD, not some later MCP session, if a CLI did not land or is not the pinned
# release: each binary must run, and `vcs-mcp --help` must exit 0 without a repository.
RUN set -eux; \
    git --version; \
    jj --version; \
    gh --version; \
    glab --version; \
    tea --version; \
    vcs-mcp --help > /dev/null

ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# The conventional mount point for the served repository; `--repo /repo` is spelled out
# in CMD rather than left to the server's "current directory" default so that `docker
# inspect` shows what the container serves.
#
# The image deliberately does NOT bake in a non-root user: it exists to read and write a
# bind-mounted working copy owned by whoever runs it, and a fixed uid would fail on
# every write. Run as yourself with `--user "$(id -u):$(id -g)"` (add `-e HOME=/tmp` if
# a forge CLI needs a writable home) — see docs/mcp-server.md, "Docker".
WORKDIR /repo
ENTRYPOINT ["vcs-mcp"]
CMD ["--repo", "/repo"]
