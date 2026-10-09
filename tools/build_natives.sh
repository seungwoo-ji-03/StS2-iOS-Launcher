#!/bin/bash
# Build the two native GDExtensions the game needs, for iOS arm64, into gameapp/addons/:
#   * utopia-rise/fmod-gdextension (pinned) + patches/fmod-gdextension/  -> addons/fmod/*.xcframework
#   * EsotericSoftware spine-godot 4.2 (the game's skeletons are 4.2.x)   -> addons/spine/*.framework
#
# usage: tools/build_natives.sh <FMOD_IOS_SDK> <work dir>
#   FMOD_IOS_SDK: the unpacked "FMOD Engine" iOS package from fmod.com (free account; FMOD's license
#                 applies — it is not redistributed here). Must contain api/core/{inc,lib} and
#                 api/studio/{inc,lib}.
set -euo pipefail
# iOS SDK comes from Xcode (the Command Line Tools have none)
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"
xcrun --sdk iphoneos --show-sdk-path >/dev/null 2>&1 || { echo "iOS SDK not found: install Xcode (DEVELOPER_DIR=$DEVELOPER_DIR)"; exit 1; }

FMOD_GDEXT_REPO=https://github.com/utopia-rise/fmod-gdextension.git
FMOD_GDEXT_REV=b74dd11
SPINE_REPO=https://github.com/EsotericSoftware/spine-runtimes.git
SPINE_BRANCH=4.2
GODOT_CPP_BRANCH=4.5-stable

SDK="${1:?usage: build_natives.sh <FMOD_IOS_SDK> <work dir>}"
WORK="${2:?usage: build_natives.sh <FMOD_IOS_SDK> <work dir>}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
ADDONS="$REPO/gameapp/addons"
mkdir -p "$WORK"; WORK="$(cd "$WORK" && pwd)"
log(){ echo "[natives] $*"; }

command -v scons >/dev/null || { echo "scons not found (brew install scons)"; exit 1; }
for d in api/core/inc api/core/lib api/studio/inc api/studio/lib; do
  [ -d "$SDK/$d" ] || { echo "$SDK/$d missing: point FMOD_IOS_SDK at the unpacked FMOD Engine iOS package"; exit 1; }
done

# FMOD
log "fmod-gdextension @ $FMOD_GDEXT_REV"
F="$WORK/fmod-gdextension"
[ -d "$F/.git" ] || git clone --quiet "$FMOD_GDEXT_REPO" "$F"
git -C "$F" fetch --quiet origin
git -C "$F" checkout --quiet --force "$FMOD_GDEXT_REV"
git -C "$F" clean -qfdx -e demo/addons/fmod/libs
git -C "$F" submodule update --quiet --init --recursive
for p in "$REPO"/patches/fmod-gdextension/*.patch; do git -C "$F" apply "$p"; log "applied $(basename "$p")"; done
cp "$REPO/patches/fmod-gdextension/ios_plugins_loader_stub.cpp" "$F/src/plugins/"

# the SConstruct expects <dir>/ios/{core,studio}/{inc,lib}
L="$WORK/fmodlibs"; rm -rf "$L"; mkdir -p "$L/ios/core" "$L/ios/studio"
for part in core studio; do for x in inc lib; do ln -s "$SDK/api/$part/$x" "$L/ios/$part/$x"; done; done

rm -rf "$F/demo/addons/fmod/libs/ios" "$F/.sconsign.dblite"
(cd "$F" && scons -j"$(sysctl -n hw.ncpu)" platform=ios arch=arm64 ios_simulator=no target=template_release fmod_lib_dir="$L/" >"$WORK/fmod-build.log" 2>&1) \
  || { tail -20 "$WORK/fmod-build.log"; exit 1; }
XC=$(find "$F/demo/addons/fmod/libs/ios" -maxdepth 1 -name '*.xcframework' | head -1)
[ -n "$XC" ] || { echo "fmod xcframework not produced (see $WORK/fmod-build.log)"; exit 1; }
mkdir -p "$ADDONS/fmod"; rm -rf "$ADDONS/fmod/libGodotFmod.ios.template_release.xcframework"
cp -R "$XC" "$ADDONS/fmod/libGodotFmod.ios.template_release.xcframework"
log "fmod -> addons/fmod/$(basename "$XC")"

# spine
log "spine-godot branch $SPINE_BRANCH"
S="$WORK/spine-runtimes"
[ -d "$S/.git" ] || git clone --quiet --depth 1 --branch "$SPINE_BRANCH" "$SPINE_REPO" "$S"
git -C "$S" fetch --quiet --depth 1 origin "$SPINE_BRANCH" && git -C "$S" checkout --quiet --force FETCH_HEAD
log "spine-runtimes $(git -C "$S" rev-parse --short HEAD)"
(cd "$S/spine-godot/build" && ./setup-extension.sh "$GODOT_CPP_BRANCH" false >"$WORK/spine-setup.log" 2>&1) \
  || { tail -20 "$WORK/spine-setup.log"; exit 1; }
(cd "$S/spine-godot/build" && ./build-extension.sh ios >"$WORK/spine-build.log" 2>&1) \
  || { tail -20 "$WORK/spine-build.log"; exit 1; }
FW=$(find "$S/spine-godot" -type d -name 'libspine_godot.ios.template_release.framework' | head -1)
[ -n "$FW" ] || { echo "spine framework not produced (see $WORK/spine-build.log)"; exit 1; }
mkdir -p "$ADDONS/spine"; rm -rf "$ADDONS/spine/libspine_godot.ios.template_release.framework"
cp -R "$FW" "$ADDONS/spine/"
log "spine -> addons/spine/$(basename "$FW")"

# gameapp/addons/*/*.gdextension descriptors are committed in the repo.
log "done"
