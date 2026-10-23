#!/bin/bash
# 同笔记 macOS 打包脚本：编译 + 组装 .app bundle
# 用法: ./scripts/build_app.sh [release|debug]
set -e
cd "$(dirname "$0")/.."

BIN_NAME="WorkLog"          # SwiftPM 可执行目标名（保持不变，避免破坏模块/存储身份）
APP_NAME="同笔记"           # 应用显示名 / .app 包名
CONFIG="${1:-release}"
BUNDLE_ID="com.tong.log.app"

echo "▶ 编译 ($CONFIG)..."
swift build --disable-sandbox -c "$CONFIG"

BIN=".build/$CONFIG/$BIN_NAME"
APP="$APP_NAME.app"

if [ ! -f "$BIN" ]; then
  echo "✗ 未找到可执行文件: $BIN"; exit 1
fi

echo "▶ 组装 $APP ..."
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp "$BIN" "$APP/Contents/MacOS/$BIN_NAME"

# 复制百度网盘凭证配置（已 .gitignore，不入库）
if [ -f "baidupan.secret.json" ]; then
  cp "baidupan.secret.json" "$APP/Contents/Resources/baidupan.secret.json"
else
  echo "⚠ 未找到 baidupan.secret.json，登录授权将不可用（见 百度网盘开放平台接入指南.md）"
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$APP_NAME</string>
    <key>CFBundleDisplayName</key><string>$APP_NAME</string>
    <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
    <key>CFBundleVersion</key><string>0.1.0</string>
    <key>CFBundleShortVersionString</key><string>0.1.0</string>
    <key>CFBundleExecutable</key><string>$BIN_NAME</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>NSPrincipalClass</key><string>NSApplication</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>CFBundleURLTypes</key>
    <array>
        <dict>
            <key>CFBundleURLName</key><string>com.tong.log.app.oauth</string>
            <key>CFBundleURLSchemes</key>
            <array>
                <string>tongnote</string>
            </array>
        </dict>
    </array>
</dict>
</plist>
PLIST

# 生成应用图标（绿色渐变 + 白色「同」字）
echo "▶ 生成应用图标..."
PY=""
if command -v python3 >/dev/null 2>&1 && python3 -c "import PIL" 2>/dev/null; then
  PY=python3
elif [ -x "$HOME/.workbuddy/binaries/python/envs/default/bin/python" ]; then
  PY="$HOME/.workbuddy/binaries/python/envs/default/bin/python"
fi

if [ -n "$PY" ] && "$PY" -c "import PIL" 2>/dev/null; then
  "$PY" scripts/make_icon.py "$APP/Contents/Resources"
  /usr/libexec/PlistBuddy -c "Add :CFBundleIconFile string AppIcon.icns" "$APP/Contents/Info.plist" 2>/dev/null || \
    /usr/libexec/PlistBuddy -c "Set :CFBundleIconFile AppIcon.icns" "$APP/Contents/Info.plist"
else
  echo "⚠ 未找到 Pillow，跳过图标生成"
fi

# 临时签名，避免 Gatekeeper 拦截（自签名）
codesign --force --deep -s - "$APP" 2>/dev/null || echo "（签名跳过）"

echo ""
echo "✅ 打包完成: $(pwd)/$APP"
echo "   运行: open \"$(pwd)/$APP\""
