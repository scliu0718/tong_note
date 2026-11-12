#!/bin/bash
# 在 macOS 或 Windows 上打出 Windows 可执行文件。
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet publish src/Tonote.App/Tonote.App.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -o dist/win-x64
echo "输出：dist/win-x64/Tonote.exe"
