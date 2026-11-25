#!/bin/zsh
# 打包通用版（Apple 芯片 + Intel），产物为 dist/InkCat-<版本>.dmg
set -euo pipefail
cd "${0:A:h}"
ARCHS="arm64 x86_64" ./build.sh
version=$(plutil -extract CFBundleShortVersionString raw Info.plist)
stage=$(mktemp -d) && chmod 755 "$stage"
cp -R InkCat.app LICENSE LICENSE-ART.md "$stage"
ln -s /Applications "$stage/Applications"
mkdir -p dist
hdiutil create -quiet -ov -volname "InkCat $version" -srcfolder "$stage" -fs HFS+ -format UDZO "dist/InkCat-$version.dmg"
shasum -a 256 "dist/InkCat-$version.dmg"
