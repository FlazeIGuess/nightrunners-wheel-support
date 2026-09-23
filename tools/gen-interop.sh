#!/usr/bin/env bash
#
# Generates the Il2Cpp interop assemblies + copies the BepInEx core into libs/, so the mod can
# be built strongly typed - without having to launch the game.
#
# Requirements:
#   - dotnet SDK 8 (or 6), internet access (BepInEx BE + Unity base libs)
#   - the game's GameAssembly.dll and global-metadata.dat
#
# Usage:
#   tools/gen-interop.sh <GameAssembly.dll> <global-metadata.dat> [UnityVersion]
# Example:
#   tools/gen-interop.sh /home/dev/nr-game/GameAssembly.dll \
#                        /home/dev/nr-game/global-metadata-dir/global-metadata.dat 2019.4.41
#
# The result lands in libs/core (BepInEx core) and libs/interop (generated interop DLLs).
set -euo pipefail

GAME_ASM="${1:?path to GameAssembly.dll missing}"
METADATA="${2:?path to global-metadata.dat missing}"
UNITY_VERSION="${3:-2019.4.41}"

BE_BUILD="788"
BE_HASH="5b766a3"
BE_URL="https://builds.bepinex.dev/projects/bepinex_be/${BE_BUILD}/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.${BE_BUILD}%2B${BE_HASH}.zip"
UNITY_LIBS_URL="https://unity.bepinex.dev/libraries/${UNITY_VERSION}.zip"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CACHE="$ROOT/.cache"
BEP_DIR="$CACHE/bepinex"
UNITY_LIBS="$CACHE/unity-libs"
CORE="$BEP_DIR/BepInEx/core"
LIBS="$ROOT/libs"

export DOTNET_ROLL_FORWARD=LatestMajor

mkdir -p "$CACHE"

echo "==> Fetching BepInEx 6 IL2CPP BE ${BE_BUILD}"
if [ ! -d "$CORE" ]; then
  curl -sL "$BE_URL" -o "$CACHE/bepinex.zip"
  mkdir -p "$BEP_DIR"
  unzip -oq "$CACHE/bepinex.zip" -d "$BEP_DIR"
fi

echo "==> Fetching Unity base libs ${UNITY_VERSION}"
if [ ! -f "$UNITY_LIBS/.done" ]; then
  mkdir -p "$UNITY_LIBS"
  curl -sL "$UNITY_LIBS_URL" -o "$CACHE/unity-libs.zip"
  unzip -oq "$CACHE/unity-libs.zip" -d "$UNITY_LIBS"
  touch "$UNITY_LIBS/.done"
fi

echo "==> Building InteropGen"
dotnet build -c Release "$ROOT/tools/InteropGen/InteropGen.csproj" -p:CoreDir="$CORE/" -v q

echo "==> Generating interop assemblies"
mkdir -p "$LIBS/interop" "$LIBS/core"
dotnet "$ROOT/tools/InteropGen/bin/Release/net6.0/InteropGen.dll" \
  "$GAME_ASM" "$METADATA" "$UNITY_VERSION" "$UNITY_LIBS" "$LIBS/interop"

echo "==> Copying BepInEx core into libs/core"
cp "$CORE"/*.dll "$LIBS/core/"

echo ""
echo "DONE. libs/core: $(ls "$LIBS"/core/*.dll | wc -l) DLLs | libs/interop: $(ls "$LIBS"/interop/*.dll | wc -l) DLLs"
echo "Now build: dotnet build -c Release src/NightRunners.WheelSupport/NightRunners.WheelSupport.csproj"
