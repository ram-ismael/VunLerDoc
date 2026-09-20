#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="$ROOT_DIR/publish/win-x64"
ZIP_PATH="$ROOT_DIR/publish/win-x64.zip"

cd "$ROOT_DIR"
rm -rf "$OUT_DIR"
rm -f "$ZIP_PATH"
mkdir -p "$OUT_DIR"

dotnet publish VunLerDoc.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$OUT_DIR"

(
  cd "$ROOT_DIR/publish"
  zip -qr "$(basename "$ZIP_PATH")" "$(basename "$OUT_DIR")"
)

echo "Created $ZIP_PATH"
