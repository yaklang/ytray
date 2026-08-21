#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEV_BUNDLED_EXTENSION_DIR="$PROJECT_ROOT/darwin/.build/ytray-dev/BundledExtension"

echo "YTray 开发模式：编译 Debug 版本并在当前终端前台运行，关闭终端即退出。" >&2
echo "如需安装应用，请改用 ./script/package-macos.sh 生成 dist/YTray.app。" >&2

# swift run does not create an .app resource directory. Prepare the same validated
# archive used by Release builds in an ignored build directory and expose it only to
# Debug builds through an explicit resource path.
"$SCRIPT_DIR/prepare-yakit-browser-agent.sh" "$DEV_BUNDLED_EXTENSION_DIR" --use-cache
export YTRAY_BUNDLED_EXTENSION_DIR="$DEV_BUNDLED_EXTENSION_DIR"

exec swift run --package-path "$PROJECT_ROOT/darwin" YTray "$@"
