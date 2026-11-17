#!/usr/bin/env bash
# Builds the SPT Prometheus Exporter mod against a sibling checkout of the server repo
# and copies the resulting DLL into the server's user/mods folder.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVER_REPO="${SERVER_REPO:-$SCRIPT_DIR/../server-csharp}"
CONFIGURATION="${CONFIGURATION:-Debug}"

if [ ! -d "$SERVER_REPO/Libraries" ]; then
    echo "Server repository not found at $SERVER_REPO, cloning..."
    git clone --depth 1 https://github.com/SP-Tushonka/server-csharp.git "$SERVER_REPO"
    if command -v git-lfs >/dev/null 2>&1; then
        (cd "$SERVER_REPO" && git lfs install && git lfs pull) || echo "warning: git lfs pull failed, LFS data may be missing"
    else
        echo "warning: git-lfs not installed, LFS data may be missing"
    fi
fi

echo "Building mod (configuration: $CONFIGURATION)..."
dotnet build "$SCRIPT_DIR/SptPrometheusExporter/SptPrometheusExporter.csproj" -c "$CONFIGURATION"

MOD_DLL="$SCRIPT_DIR/SptPrometheusExporter/bin/$CONFIGURATION/net10.0/SptPrometheusExporter.dll"
DEST_DIR="$SERVER_REPO/SPTushonka.Server/bin/$CONFIGURATION/net10.0/user/mods/SptPrometheusExporter"

mkdir -p "$DEST_DIR"
cp "$MOD_DLL" "$DEST_DIR/"
echo "Copied mod to: $DEST_DIR"
echo "Done. Start the server normally; look for 'Prometheus exporter listening' in the log."
