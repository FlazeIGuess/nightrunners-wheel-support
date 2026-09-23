#!/usr/bin/env bash
# Builds the mod. Requires libs/ to be populated (tools/gen-interop.sh).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
export DOTNET_ROLL_FORWARD=LatestMajor
if [ ! -f "$ROOT/libs/interop/Assembly-CSharp.dll" ]; then
  echo "libs/ is empty. Generate the interop assemblies first:"
  echo "  tools/gen-interop.sh <GameAssembly.dll> <global-metadata.dat> 2019.4.41"
  exit 1
fi
dotnet build -c Release "$ROOT/src/NightRunners.WheelSupport/NightRunners.WheelSupport.csproj" "$@"
echo ""
echo "Result: dist/plugins/NightRunners.WheelSupport/"
ls -la "$ROOT/dist/plugins/NightRunners.WheelSupport/" 2>/dev/null || true
