#!/bin/bash
# WorkLog macOS 打包脚本：编译 + 组装 .app bundle
# 用法: ./scripts/build_app.sh [release|debug]
set -e
cd "$(dirname "$0")/.."

APP_NAME="WorkLog"
CONFIG="${1:-release}"
BUNDLE_ID="com.worklog.app"

echo "▶ 编译 ($CONFIG)..."
swift build --disable-sandbox -c "$CONFIG"

BIN=".build/$CONFIG/$APP_NAME"
APP="$APP_NAME.app"

if [ ! -f "$BIN" ]; then
  echo "✗ 未找到可执行文件: $BIN"; exit 1
fi

echo "▶ 组装 $APP ..."
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp "$BIN" "$APP/Contents/MacOS/$APP_NAME"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>WorkLog</string>
    <key>CFBundleDisplayName</key><string>WorkLog</string>
    <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
    <key>CFBundleVersion</key><string>0.1.0</string>
    <key>CFBundleShortVersionString</key><string>0.1.0</string>
    <key>CFBundleExecutable</key><string>$APP_NAME</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>NSPrincipalClass</key><string>NSApplication</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
</dict>
</plist>
PLIST

# 生成占位图标（绿色圆角方块 + WL 缩写），后续可替换真实 icns
echo "▶ 生成占位图标..."
ICONSET="$APP/Contents/Resources/AppIcon.iconset"
mkdir -p "$ICONSET"
python3 - "$ICONSET" <<'PY'
import sys, os
try:
    from PIL import Image, ImageDraw
except ImportError:
    print("PIL 未安装，跳过图标生成"); sys.exit(0)
out = sys.argv[1]
green = (26, 158, 92, 255)
dark = (20, 122, 70, 255)
for size in [16, 32, 64, 128, 256, 512, 1024]:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = int(size * 0.22)
    d.rounded_rectangle([0, 0, size-1, size-1], radius=r, fill=green)
    # 简单白色竖线模拟笔记
    m = size // 2
    w = max(1, size // 12)
    off = size // 5
    for dx in (-1, 0, 1):
        x0 = m + dx * off
        d.line([x0, size//4, x0, size*3//4], fill=(255,255,255,255), width=w)
    img.save(f"{out}/icon_{size}x{size}.png")
print("图标已生成")
PY

ICONUTIL=$(xcrun -f iconutil 2>/dev/null || true)
if [ -n "$ICONUTIL" ] && ls "$ICONSET"/icon_*.png >/dev/null 2>&1; then
  "$ICONUTIL" -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
  /usr/libexec/PlistBuddy -c "Add :CFBundleIconFile string AppIcon.icns" "$APP/Contents/Info.plist" 2>/dev/null || \
    /usr/libexec/PlistBuddy -c "Set :CFBundleIconFile AppIcon.icns" "$APP/Contents/Info.plist"
  rm -rf "$ICONSET"
fi

# 临时签名，避免 Gatekeeper 拦截（自签名）
codesign --force --deep -s - "$APP" 2>/dev/null || echo "（签名跳过）"

echo ""
echo "✅ 打包完成: $(pwd)/$APP"
echo "   运行: open \"$(pwd)/$APP\""
