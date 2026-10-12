#!/bin/sh
# ============================================================================
#  NexusShell artifact image - entrypoint
#
#  The image carries a published win-x64 build of a Windows desktop app, so it
#  cannot run that app. It hands the payload out and proves it was not altered:
#
#    extract   copy the build into /out (requires a bind mount or volume)
#    verify    re-hash every shipped file against SHA256SUMS
#    list      print SHA256SUMS
#    version   print the version baked in at build time
#    help      this text
# ============================================================================
set -eu

PAYLOAD="/opt/nexusshell/win-x64"
MANIFEST="/opt/nexusshell/SHA256SUMS"
VERSION_FILE="/opt/nexusshell/VERSION"
OUT="/out"
DOCS="/opt/nexusshell"

version_string() { cat "$VERSION_FILE" 2>/dev/null || echo "unknown"; }

usage() {
  cat <<EOF
NexusShell artifact image $(version_string)

  This image does not run NexusShell. NexusShell is a Windows desktop app
  (WPF + ConPTY + WebView2); it needs Windows 10 1903+ with a desktop session.
  The image carries the published win-x64 build so it can be fetched and
  verified by tag or digest from anywhere.

Usage:
  docker run --rm -v "\$PWD/out:/out" IMAGE [extract]   copy the build to ./out
  docker run --rm IMAGE verify                          re-hash all files
  docker run --rm IMAGE list                            print SHA256SUMS
  docker run --rm IMAGE version                         print the version
  docker run --rm IMAGE help                            this text

After extracting, run NexusShell.App.exe on Windows.
EOF
}

# /out is a plain directory inside the image. A bind mount or volume appears in
# /proc/self/mounts, which is how "no output directory given" is told apart from
# a real mount - otherwise extract would copy 200 MB into a throwaway layer and
# report success.
out_is_mounted() {
  awk '$2 == "/out" { found = 1 } END { exit !found }' /proc/self/mounts
}

cmd_extract() {
  if ! out_is_mounted; then
    echo "error: nothing is mounted at /out, so there is nowhere to extract to." >&2
    echo "       mount a host directory, for example:" >&2
    echo "         docker run --rm -v \"\$PWD/out:/out\" IMAGE" >&2
    echo "       (PowerShell: -v \"\${PWD}\out:/out\"   cmd.exe: -v \"%CD%\out:/out\")" >&2
    return 3
  fi
  if [ ! -w "$OUT" ]; then
    echo "error: /out is mounted read-only; extract needs to write to it." >&2
    return 4
  fi

  echo "NexusShell $(version_string) - extracting win-x64 build to /out ..."
  cp -R "$PAYLOAD/." "$OUT/"
  # Ship the manifest and docs alongside so the copy can be verified later.
  cp "$MANIFEST" "$VERSION_FILE" "$OUT/" 2>/dev/null || true
  for doc in README.md LICENSE THIRD-PARTY-NOTICES.md; do
    [ -f "$DOCS/$doc" ] && cp "$DOCS/$doc" "$OUT/" || true
  done

  echo "verifying the extracted copy ..."
  if ( cd "$OUT" && sha256sum -cs SHA256SUMS ); then
    echo "ok: $(wc -l < "$MANIFEST") files extracted and verified."
    echo "run NexusShell.App.exe on Windows 10 1903+ (x64)."
  else
    echo "error: checksum mismatch after copying - the output directory may be full or the payload damaged." >&2
    return 5
  fi
}

cmd_verify() {
  echo "NexusShell $(version_string) - verifying payload inside the image ..."
  if ( cd "$PAYLOAD" && sha256sum -cs "$MANIFEST" ); then
    echo "ok: $(wc -l < "$MANIFEST") files match SHA256SUMS."
  else
    echo "FAILED: payload does not match SHA256SUMS - do not trust this image." >&2
    ( cd "$PAYLOAD" && sha256sum -cw "$MANIFEST" 2>&1 | grep -v ': OK$' | head -20 ) >&2 || true
    return 6
  fi
}

cmd_list() { cat "$MANIFEST"; }

case "${1:-extract}" in
  extract) cmd_extract ;;
  verify)  cmd_verify ;;
  list)    cmd_list ;;
  version) version_string ;;
  help|-h|--help) usage ;;
  *) echo "error: unknown command '$1'" >&2; echo >&2; usage >&2; exit 2 ;;
esac
