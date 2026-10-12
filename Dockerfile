# syntax=docker/dockerfile:1.7
# ============================================================================
#  NexusShell - artifact image  (Linux container, the one published to Docker Hub)
#
#  WHY THIS IMAGE DOES NOT RUN NEXUSSHELL
#  NexusShell is a WPF desktop application (TFM net10.0-windows10.0.19041.0).
#  It spawns real shells through Win32 ConPTY, renders them in a WebView2
#  Chromium host, encrypts the AI key with Windows DPAPI, and speaks through the
#  NVDA and JAWS APIs. None of that exists in a container - there is no Win32
#  subsystem, no desktop session, no WebView2 runtime and no screen reader. So
#  this image does not pretend to run the app. It *carries* a published
#  self-contained win-x64 build, with checksums, so a release can be fetched and
#  verified by tag from any machine that can pull an image:
#
#      docker run --rm -v "%CD%\out:/out" mohammedkhaledmahmoud/nexusshell:4.1.1
#
#  That gives CI runners, air-gapped mirrors and scripted installs one immutable,
#  digest-addressable source for a build - the same job a GitHub release asset
#  does, but pinnable by digest and pullable through a registry.
#
#  The binaries come from the host, because WPF cannot be compiled on Linux: the
#  Windows Desktop SDK is Windows-only. docker/publish.ps1 runs `dotnet publish`
#  on the host first. To compile inside a container instead, switch Docker
#  Desktop to Windows containers and use docker/Dockerfile.build.
# ============================================================================

# Alpine, not scratch: the entrypoint needs a shell to copy the payload out and
# `sha256sum` to verify it. Pinned by digest so the published image is
# reproducible even if the alpine:3.22 tag is moved later.
FROM alpine:3.22@sha256:4bcff63911fcb4448bd4fdacec207030997caf25e9bea4045fa6c8c44de311d1

# Supplied by docker/publish.ps1, which reads VERSION out of NexusShell.App.csproj
# so the tag can never disagree with the assembly it contains.
ARG NEXUSSHELL_VERSION=4.1.1
ARG BUILD_DATE
ARG VCS_REF

LABEL org.opencontainers.image.title="NexusShell" \
      org.opencontainers.image.description="Accessible, AI-powered Windows terminal (WPF/ConPTY/WebView2). This image carries the published win-x64 build; the app itself runs on Windows 10 1903+, not in a container." \
      org.opencontainers.image.version="${NEXUSSHELL_VERSION}" \
      org.opencontainers.image.created="${BUILD_DATE}" \
      org.opencontainers.image.revision="${VCS_REF}" \
      org.opencontainers.image.source="https://github.com/Mohammedkhaled96/NexusShell" \
      org.opencontainers.image.url="https://github.com/Mohammedkhaled96/NexusShell" \
      org.opencontainers.image.documentation="https://github.com/Mohammedkhaled96/NexusShell/blob/NexusShell_master/README.md" \
      org.opencontainers.image.licenses="BSL-1.0" \
      org.opencontainers.image.vendor="Farid Mohammed, Mohammed Khaled" \
      org.opencontainers.image.base.name="docker.io/library/alpine:3.22" \
      io.nexusshell.payload.os="windows" \
      io.nexusshell.payload.arch="x64" \
      io.nexusshell.payload.kind="self-contained-publish"

# --- payload -----------------------------------------------------------------
# Owned by root, and the container runs as uid 65534, so the payload is already
# unwritable from inside: the entrypoint only ever copies out of it. No chmod
# afterwards - changing a mode copies the file up into the new layer, so one
# `chmod -R` over 220 MB of payload would double the size of the image to
# re-state permissions that COPY --chown has already given it.
COPY --chown=0:0 artifacts/publish/win-x64/ /opt/nexusshell/win-x64/
COPY --chown=0:0 README.md LICENSE THIRD-PARTY-NOTICES.md /opt/nexusshell/

# --- manifest ----------------------------------------------------------------
# Checksums are computed at build time over the payload actually baked in, so
# `verify` later proves the layer was not altered in transit or in a mirror.
RUN set -eu; \
    test -f /opt/nexusshell/win-x64/NexusShell.App.exe \
      || { echo "FATAL: NexusShell.App.exe missing - build context has no published output" >&2; exit 1; }; \
    cd /opt/nexusshell/win-x64; \
    find . -type f ! -name SHA256SUMS -print0 \
      | sort -z \
      | xargs -0 sha256sum > /opt/nexusshell/SHA256SUMS; \
    printf '%s\n' "${NEXUSSHELL_VERSION}" > /opt/nexusshell/VERSION

COPY --chmod=0555 docker/entrypoint.sh /usr/local/bin/nexusshell-artifact

# Unprivileged: copying files into a mounted directory needs no root, and the
# image is published publicly. 65534 is nobody, present in every Alpine image.
USER 65534:65534

# The extraction target: mount a host directory here. Deliberately NOT declared
# as a VOLUME. A declared volume would make every `docker run` without -v create
# an anonymous 200 MB volume nobody asked for, and because that volume is
# writable the entrypoint could not tell "no output directory given" from a real
# bind mount - it would copy the payload into a throwaway and report success.
# As a plain directory, an unmounted /out is detectable, and is refused.
WORKDIR /out

# No HEALTHCHECK and no long-running process: this is a one-shot command image,
# so a health check would only ever report an exited container.
ENTRYPOINT ["/usr/local/bin/nexusshell-artifact"]
CMD ["extract"]
