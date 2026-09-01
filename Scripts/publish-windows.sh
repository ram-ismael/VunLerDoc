#!/usr/bin/env bash
set -euo pipefail
dotnet publish VunLerDoc.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
