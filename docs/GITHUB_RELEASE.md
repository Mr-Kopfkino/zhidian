# GitHub 发布指南

本次发布为 **致电 v2.2.0 / Windows 10、11 x64 / MIT**。准备目录将源码与用户下载附件分开，不能把整个开发工作区上传到仓库。

## 1. 上传源码

将准备目录中 `source` 的**内容**放到 GitHub 仓库根目录。它包含 README、LICENSE、第三方说明、源码、测试和构建脚本，不包含用户数据、私人模型或运行环境。也可使用同级 `Zhidian-v2.2.0-source.zip` 解压得到相同源码。

在 GitHub 创建空仓库后，在 `source` 目录打开终端，将下方仓库地址换成你的实际地址：

```powershell
git init -b main
git add .
git commit -m "Release Zhidian v2.2.0"
git remote add origin https://github.com/YOUR_ACCOUNT/YOUR_REPOSITORY.git
git push -u origin main
git tag -a v2.2.0 -m "Zhidian v2.2.0"
git push origin v2.2.0
```

没有执行过这些上传命令；准备目录本身也未创建 Git 历史。`source-manifest.json` 是本次源文件 SHA-256 清单，供本地核对，无需提交。

## 2. 创建 Release

进入仓库 Releases → Draft a new release，选择 `v2.2.0` 标签，标题填 `致电 v2.2.0 — Windows x64 完整便携版`。将同级 `RELEASE_NOTES.md` 全文作为发布说明。将 **release-assets 目录内的全部 9 个文件**拖入附件区域：

- `Zhidian-v2.2.0-win-x64.zip.001` ～ `.004`（4 卷，缺一不可）
- `Extract-Zhidian.cmd`、`Extract-Zhidian.ps1`
- `release-manifest.json`
- `README-DOWNLOAD.txt`
- `SHA256SUMS.txt`

确认附件上传完成后发布。不要上传旧的约 7.3 GB 单个 ZIP，也不要把 release-assets 提交到 Git 仓库。GitHub 要求每个 Release 附件小于 2 GiB，当前每卷最多 1,900,000,000 字节。依据：[GitHub Releases 附件限制](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)。

GitHub 自动生成的 `Source code (zip)` 是源码，不是可直接运行的软件，发布说明已明确区分。

## 3. 用户如何使用

下载全部 9 个附件到同一个短路径可写目录，保留原文件名。预留至少 22 GB 可用空间，双击 `Extract-Zhidian.cmd`，等待校验合并与解压。成功后进入 `Zhidian-v2.2.0-win-x64` 运行 `Zhidian.exe`。脚本不覆盖已有同名程序目录，避免覆盖旧数据；再次解压请换新位置。

程序自带 .NET、Python 和语音运行库；聊天服务与角色声模由用户自行配置。分卷属于完整 ZIP 的连续字节切片，不能逐卷独立解压。无需安装 Python、.NET SDK、CUDA Toolkit 或额外解压软件。

## 4. 从源码再次生成

需要 Windows x64、.NET 9 SDK、Python 3.11+ 及构建时网络。应用运行不要求开发工具。首次构建完整语音环境需要下载大型公共资源；已经有经清单验证的引擎时可复用。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-voice-engine.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -SelfContained -Test -OutputDirectory release/stage-v2.2.0 -VoiceEngineDirectory .build/voice-engine/gpt-sovits
python -I -B scripts/package-release.py --stage release/stage-v2.2.0 --output release/Zhidian-v2.2.0-win-x64.zip
python -I -B scripts/prepare-github-release.py --archive release/Zhidian-v2.2.0-win-x64.zip --output release/GitHub-v2.2.0
```

输出必须使用全新目录。不要在用于打包的 stage 中运行程序；启动测试应使用另外解压的副本。后续版本需同时更新项目版本、发布说明与指南，再执行这些步骤。自动打包会检查应用、引擎清单、常见凭据、私人路径与产物哈希；完整发布仍需保留第三方许可。
