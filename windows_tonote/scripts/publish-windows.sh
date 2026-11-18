#!/bin/bash
# 在 macOS 或 Windows 上打出 Windows 可执行文件。
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet publish src/Tonote.App/Tonote.App.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:ApplicationManifest=app.manifest \
  -o dist/win-x64

# W-29：随包使用说明（若上一工作线已提供 guide 资源则一起打包）
if [ -d docs/guide ]; then
  cp -R docs/guide dist/win-x64/guide
elif [ -f docs/guide/index.html ]; then
  mkdir -p dist/win-x64/guide && cp docs/guide/index.html dist/win-x64/guide/
fi

# W-23：安装包脚本随产物一起输出（需本机装 Inno Setup 才能编译）
cp scripts/installer.iss dist/win-x64/installer.iss 2>/dev/null || true

echo "输出：dist/win-x64/Tonote.exe"
echo "安装包：用 Inno Setup 编译 dist/win-x64/installer.iss"
echo "WebView2：干净机器需先安装 Evergreen Runtime Bootstrapper（脚本 installer.iss 会静默安装）"
