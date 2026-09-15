# YTray 应用内更新

## 用户路径

v0.2.0 移植自 YConnect 主分支 `e42005a` 的 Sparkle／WinSparkle 更新流程。Windows 64／32 位和 macOS Apple Silicon／Intel 均可在“设置 → YTray 更新”点击“更新”，进入原生更新窗口，下载、校验、安装并重新打开。托盘菜单提供更新入口。

正式版启动 15 秒后检查版本，之后每 6 小时检查。后台只更新提示，不下载、不安装、不弹窗；设置里可以关闭自动检查，并查看版本说明和上次检查时间。手动下载使用对应架构的 OSS 安装包。

安装包更新 YTray 与内置 Yakit Browser Agent；新程序启动时沿用既有的内置插件升级逻辑。实例、Profile、代理配置、已安装浏览器运行时和用户安装的插件保留。浏览器运行时与实例绑定，不会被应用更新强制换版。正在运行的浏览器不会被更新器关闭。浏览器启动、运行时安装、插件安装和模态弹窗期间不开始更新；macOS 在最终重启前再次等待忙碌操作完成。

Windows 安装版沿用原有 Inno Setup AppId、安装目录与权限模型，显示安装进度和错误，完成后以原用户身份重新打开；禁止强制关闭其他进程或重启系统。便携版明确确认后迁移到安装版，保留原便携目录与数据。macOS 权限与应用替换由 Sparkle 处理，运行于只读 DMG 等无法更新的位置时，可使用手动下载。

下载失败、签名失败或用户取消均保留当前可用版本。开发包、未打包的 Swift 程序、Debug、UI 渲染和测试进程不会自动检查或安装正式更新。0.1.x 原有更新器仍能读取兼容的 `latest.json` 并升级到 0.2.0，升级后使用新引擎。

## 更新信任与依赖

- Sparkle 2.9.6：固定 SPM 分发 ZIP SHA-256，保留 Framework 符号链接，签署各个 XPC／辅助进程，再签署应用并完成 Apple 公证。
- WinSparkle 0.9.4：固定分发 ZIP 及 x64／x86 DLL SHA-256。正确架构的 DLL 嵌入单文件程序，使用前验证并加载；单文件便携包的交付形式保持不变。保留 Inno Setup 与 Authenticode 签名。
- 四个最终 DMG／EXE 都使用 YTray 独立 Ed25519 私钥签署。公钥位于 `resources/updates/ed25519-public-key.txt`，私钥仅保存在 CI Secret `YTRAY_UPDATE_PRIVATE_KEY` 与维护者的安全备份中，不能提交、输出或随包发布。普通发版不能重新生成密钥。
- `latest.json` 仅用于提示。它须满足产品、版本、对应架构、文件名、固定 OSS 地址和大小限制；实际安装必须通过原生引擎的签名校验。两端静默检查不附带 Cookie 或账户凭据。

macOS 构建号固定为 `major × 1,000,000 + minor × 1,000 + patch`，每一段小于 1000，避免 CI 重跑导致版本比较错误。

## 发布

1. 修改 `VERSION` 与 `CHANGELOG.md`，运行本地单元、发布元数据、打包和原生更新测试。
2. 推送分支并创建 PR。macOS CI 验证构建、测试、UI 和通用安装包；Windows CI 对 amd64／386 分别运行测试、原生引擎签名验收、从 0.1.16 覆盖升级、自动重启与数据保留检查。
3. 可对发布分支手动运行 Release 工作流，完成四个架构的签名、公证、打包和更新源验收。此时仅生成 `prepared-release` 产物。
4. 将通过检查的代码合入 main，推送匹配 `VERSION` 的 `vX.Y.Z` 标签。标签发布流程上传不可变文件并从 CDN 核对哈希，然后发布四个更新源，最后更新版本索引与 GitHub Release。
5. 核对公开的四个平台包、`manifest.json`、`SHA256SUMS`、四个 `appcast-*.xml`、`latest.json` 和 `releases.json`。清单包含提交 SHA；不能覆盖不同字节的历史版本，也不能降低 latest。中断后应复用已验收的 `prepared-release` 恢复上传。

更新源为 `appcast-macos-arm64.xml`、`appcast-macos-amd64.xml`、`appcast-windows-amd64.xml`、`appcast-windows-386.xml`，根目录与不可变版本目录均保留副本。

## 验收命令

```sh
swift test --package-path darwin
python3 script/test-macos-updater.py
bash script/test-release-index.sh
python3 -m unittest discover -s script/tests -v
./script/package-macos.sh --arch arm64 --dmg
```

Windows 在真实 Windows runner 上运行：

```powershell
./windows/build.ps1 -Release -Test -Package -Installer -Architecture amd64
./windows/test-updater.ps1 -Architecture amd64
./windows/verify-upgrade.ps1 -Architecture amd64
# 386 使用相同命令并切换 Architecture。
```

原生测试采用临时目录、随机测试密钥和本机订阅源。macOS 验证拒绝篡改、替换、重启与数据保留；Windows 使用真实 DLL 验证签名和安装交接，再使用真实 Inno 安装器验证覆盖升级与重新启动。不会通过测试订阅源执行任意下载文件。

参考：[Sparkle 发布文档](https://sparkle-project.org/documentation/publishing/)、[WinSparkle 发布文档](https://winsparkle.org/guides/publishing-updates/)。
