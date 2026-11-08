# 内置语音真实集成验证

这是手动运行的真实引擎验证工具，引用生产 `Qiban.Core`，通过 `VoiceClient` 启动发布目录内的 Python worker。完整运行组件、基础模型、角色权重及参考音频准备完成后再执行；运行前关闭该发布目录的应用和其他 smoke 进程。

工具仅从指定状态文件提取角色语音所需的权重路径、参考音频、参考文字和语言，不读取聊天内容或密钥字段，不修改状态文件。所有具有现存 GPT/SoVITS 权重的角色都会测试；缺少参考音频会作为真实配置错误失败。强制使用内置 provider，不访问保存的外接 API。

验证内容：

- 预取消的挂载、合成不启动 worker、不产生文件。
- 每个符合条件的角色挂载成功；固定中文、日文测试句分别合成。
- WAV 为有效 PCM16、长度 0.1–120 秒且包含非零样本，输出采样率、时长、峰值及 RMS。
- 每份音频检测后立即删除，仅删除本次 `VoiceClient` 返回的 `app-root/cache/voice-*.wav`。
- `StopEmbedded()` 和重新挂载后的 `Dispose()` 分别终止真实 Python 进程，不残留 session 或新增缓存文件。
- 状态文件前后 SHA256 相同。输出仅角色序号、固定状态码和音频指标，不包含姓名、文本、路径或密钥，也不输出异常原文。

从仓库根目录准备隔离的构建环境；所有构建产物、SDK 状态、临时文件和 NuGet 缓存均位于 `.build`：

```powershell
$smokeBuild = Join-Path (Get-Location) '.build/embedded-smoke'
$env:DOTNET_CLI_HOME = Join-Path $smokeBuild 'dotnet'
$env:NUGET_PACKAGES = Join-Path $smokeBuild 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $smokeBuild 'nuget-http'
$env:APPDATA = Join-Path $smokeBuild 'profile/roaming'
$env:LOCALAPPDATA = Join-Path $smokeBuild 'profile/local'
$env:TEMP = Join-Path $smokeBuild 'temp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,$env:NUGET_HTTP_CACHE_PATH,$env:APPDATA,$env:LOCALAPPDATA,$env:TEMP | Out-Null

dotnet restore tests/Qiban.EmbeddedSmoke/Qiban.EmbeddedSmoke.csproj `
  --artifacts-path "$smokeBuild/artifacts" --configfile NuGet.Config
dotnet build tests/Qiban.EmbeddedSmoke/Qiban.EmbeddedSmoke.csproj -c Release --no-restore `
  --artifacts-path "$smokeBuild/artifacts" `
  --disable-build-servers -m:1 -p:UseSharedCompilation=false
```

仅在完整应用目录就绪后运行。状态文件可以位于现有应用目录，不需要复制用户数据；请将下面的状态路径替换为实际文件：

```powershell
dotnet "$smokeBuild/artifacts/bin/Qiban.EmbeddedSmoke/release/Qiban.EmbeddedSmoke.dll" `
  --app-root "release/.staging/Laidian-Light" `
  --state-file "release/Laidian-Light/data/state.json"
```

进程退出码 `0` 表示全部通过，`1` 表示失败。每条输出为 JSON，最终记录 `check=result`。发生错误时也会调用 `Dispose()` 并清理已返回的音频；遗留 session 会明确判失败，不会通过额外递归删除掩盖问题。已有 session 或同目录引擎进程会在测试开始前被拒绝，工具不会停止其他应用拥有的进程。
