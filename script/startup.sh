#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

echo "YTray 开发模式：编译 Debug 版本并在当前终端前台运行，关闭终端即退出。" >&2
echo "如需安装应用，请改用 ./script/package-macos.sh 生成 dist/YTray.app。" >&2

exec swift run --package-path "$PROJECT_ROOT/darwin" YTray "$@"
