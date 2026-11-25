#!/bin/zsh
# 编译并做本地签名，产物为 InkCat.app
set -euo pipefail
cd "${0:A:h}"
rm -rf InkCat.app
mkdir -p InkCat.app/Contents/{MacOS,Resources}
cp Info.plist InkCat.app/Contents/
cp Assets/*.png InkCat.app/Contents/Resources/
rm InkCat.app/Contents/Resources/icon.png

iconset=$(mktemp -d)/AppIcon.iconset
mkdir "$iconset"
for n in 16 32 128 256 512; do
    sips -z $n $n Assets/icon.png --out "$iconset/icon_${n}x$n.png" >/dev/null
    sips -z $((n * 2)) $((n * 2)) Assets/icon.png --out "$iconset/icon_${n}x$n@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o InkCat.app/Contents/Resources/AppIcon.icns

swiftc -O -swift-version 5 -target "$(uname -m)-apple-macos13.0" Sources/*.swift -o InkCat.app/Contents/MacOS/InkCat
codesign --force --sign - --options runtime --entitlements InkCat.entitlements InkCat.app
echo "完成：$PWD/InkCat.app"
