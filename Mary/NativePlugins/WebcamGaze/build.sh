#!/bin/sh
# Builds the macOS plugin into the Unity project. Run from this folder:  ./build.sh   (or ./build.sh test)
set -e
cd "$(dirname "$0")"
FRAMEWORKS="-framework Foundation -framework AVFoundation -framework Vision -framework CoreMedia -framework CoreVideo -framework CoreGraphics -framework ImageIO"
OUT=../../Assets/Plugins/macOS
mkdir -p "$OUT"
clang -dynamiclib -fobjc-arc -arch arm64 -arch x86_64 -mmacosx-version-min=12.0 $FRAMEWORKS \
      -install_name @rpath/libWebcamGaze.dylib -o "$OUT/libWebcamGaze.dylib" WebcamGaze.m
echo "built $OUT/libWebcamGaze.dylib"
if [ "$1" = "test" ]; then
  clang -fobjc-arc -arch arm64 -mmacosx-version-min=12.0 $FRAMEWORKS -o /tmp/wgtest WebcamGaze.m test_main.m
  echo "built /tmp/wgtest (run it:  /tmp/wgtest 15)"
fi
