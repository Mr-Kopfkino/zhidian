param(
    [switch]$SelfContained,
    [switch]$Test,
    [string]$OutputDirectory,
    [string]$VoiceEngineDirectory,
    [switch]$HardlinkVoiceEngine
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = Join-Path $projectRoot '.build'
function Assert-NoReparsePath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse points are not allowed in build paths: $current"
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
$engineSource = $null
$engineFiles = @()
if ($HardlinkVoiceEngine -and -not $VoiceEngineDirectory) { throw '-HardlinkVoiceEngine requires -VoiceEngineDirectory.' }
if ($VoiceEngineDirectory) {
    $engineSource = (Resolve-Path -LiteralPath $VoiceEngineDirectory).Path.TrimEnd('\')
    Assert-NoReparsePath $engineSource
    foreach ($required in @('runtime/python.exe', 'laidian-engine.json', 'GPT_SoVITS/TTS_infer_pack/TTS.py')) {
        if (-not (Test-Path -LiteralPath (Join-Path $engineSource $required) -PathType Leaf)) {
            throw "Embedded voice engine is incomplete: missing $required"
        }
    }
    $engineManifest = Get-Content -LiteralPath (Join-Path $engineSource 'laidian-engine.json') -Raw | ConvertFrom-Json
    if ($engineManifest.schemaVersion -ne 1) { throw 'Unsupported voice engine manifest schemaVersion.' }
    $models = Join-Path $engineSource 'GPT_SoVITS/pretrained_models'
    if (-not (Test-Path -LiteralPath $models -PathType Container)) { throw 'Embedded voice engine has no pretrained_models directory.' }
    # Walk explicitly: never follow directory junctions while preparing a distribution.
    $directories = New-Object 'System.Collections.Generic.Stack[string]'
    $directories.Push($engineSource)
    $files = New-Object 'System.Collections.Generic.List[object]'
    while ($directories.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $directories.Pop() -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Engine contains a reparse point: $($item.FullName)" }
            if ($item.PSIsContainer) { $directories.Push($item.FullName) }
            else { $files.Add($item) }
        }
    }
    $engineFiles = @($files.ToArray())
    if (-not ($engineFiles | Where-Object { $_.FullName.StartsWith($models.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and $_.Extension -in @('.bin', '.safetensors', '.pt', '.pth', '.ckpt') } | Select-Object -First 1)) {
        throw 'Embedded voice engine has no pretrained model weights.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'docs/VOICE_ENGINE.md') -PathType Leaf)) { throw 'Voice engine distribution documentation is missing: docs/VOICE_ENGINE.md' }
}
$env:DOTNET_CLI_HOME = Join-Path $buildRoot 'dotnet'
$env:NUGET_PACKAGES = Join-Path $buildRoot 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $buildRoot 'nuget-http'
$env:APPDATA = Join-Path $buildRoot 'profile/roaming'
$env:LOCALAPPDATA = Join-Path $buildRoot 'profile/local'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:TEMP = Join-Path $buildRoot 'temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP,$env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,$env:APPDATA,$env:LOCALAPPDATA | Out-Null
if ($Test) {
    dotnet restore (Join-Path $projectRoot 'tests/Qiban.Tests/Qiban.Tests.csproj') --configfile (Join-Path $projectRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'Test restore failed.' }
    dotnet run --project (Join-Path $projectRoot 'tests/Qiban.Tests/Qiban.Tests.csproj') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    dotnet restore (Join-Path $projectRoot 'tests/Qiban.NetworkTests/Qiban.NetworkTests.csproj') --configfile (Join-Path $projectRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'Network test restore failed.' }
    dotnet run --project (Join-Path $projectRoot 'tests/Qiban.NetworkTests/Qiban.NetworkTests.csproj') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Network tests failed.' }
    dotnet restore (Join-Path $projectRoot 'tests/Qiban.DesktopTests/Qiban.DesktopTests.csproj') --configfile (Join-Path $projectRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'Desktop test restore failed.' }
    dotnet run --project (Join-Path $projectRoot 'tests/Qiban.DesktopTests/Qiban.DesktopTests.csproj') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Desktop tests failed.' }
}
$output = Join-Path $projectRoot $(if ($SelfContained) { 'release/Laidian-Portable' } else { 'release/Laidian-Light' })
if ($OutputDirectory) {
    $output = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
    $releaseBoundary = [IO.Path]::GetFullPath((Join-Path $projectRoot 'release')).TrimEnd('\') + '\'
    if (-not $output.StartsWith($releaseBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must stay inside release.' }
}
Assert-NoReparsePath $output
if ($engineSource) {
    $engineDestination = [IO.Path]::GetFullPath((Join-Path $output 'engines/gpt-sovits'))
    $destinationBoundary = $engineDestination.TrimEnd('\') + '\'
    if ($engineSource.Equals($engineDestination, [StringComparison]::OrdinalIgnoreCase) -or $engineSource.StartsWith($destinationBoundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Voice engine source must be separate from the publish destination.'
    }
    if ($HardlinkVoiceEngine -and [IO.Path]::GetPathRoot($engineSource) -ne [IO.Path]::GetPathRoot($engineDestination)) {
        throw 'Hardlinked voice engine publishing requires source and destination on the same volume.'
    }
}
$selfContainedValue = $SelfContained.IsPresent.ToString().ToLowerInvariant()
dotnet restore (Join-Path $projectRoot 'src/Qiban.Desktop/Qiban.Desktop.csproj') -r win-x64 -p:SelfContained=$selfContainedValue --configfile (Join-Path $projectRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
dotnet publish (Join-Path $projectRoot 'src/Qiban.Desktop/Qiban.Desktop.csproj') -c Release -r win-x64 --self-contained $selfContainedValue --no-restore -p:PublishSingleFile=false -p:DebugType=none -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
# Retire only the previous app identity inside the new output. Preserve Core, user data,
# and every legacy release directory; the mutex and data format retain compatibility.
foreach ($obsoleteName in @('Qiban.exe', 'Qiban.dll', 'Qiban.deps.json', 'Qiban.runtimeconfig.json',
    'Laidian.exe', 'Laidian.dll', 'Laidian.deps.json', 'Laidian.runtimeconfig.json')) {
    $obsoletePath = [IO.Path]::GetFullPath((Join-Path $output $obsoleteName))
    $outputBoundary = [IO.Path]::GetFullPath($output).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $obsoletePath.StartsWith($outputBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid obsolete output path.' }
    if (Test-Path -LiteralPath $obsoletePath) { Remove-Item -LiteralPath $obsoletePath -Force }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $output 'README.md')
foreach ($notice in @('LICENSE', 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $notice) -Destination (Join-Path $output $notice)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'upgrade-data.ps1') -Destination (Join-Path $output 'Upgrade-Data.ps1')
New-Item -ItemType Directory -Force -Path (Join-Path $output 'docs') | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/KOTORI.md') -Destination (Join-Path $output 'docs/KOTORI.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/THEME.md') -Destination (Join-Path $output 'docs/THEME.md')
if (Test-Path -LiteralPath (Join-Path $projectRoot 'docs/VOICE_ENGINE.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/VOICE_ENGINE.md') -Destination (Join-Path $output 'docs/VOICE_ENGINE.md')
}
if (-not (Test-Path -LiteralPath (Join-Path $output 'VoiceEngine/gpt_sovits_worker.py') -PathType Leaf)) { throw 'Published voice worker is missing.' }
if ($engineSource) {
    New-Item -ItemType Directory -Force -Path $engineDestination | Out-Null
    foreach ($file in $engineFiles) {
        $relative = $file.FullName.Substring($engineSource.Length).TrimStart('\')
        $target = [IO.Path]::GetFullPath((Join-Path $engineDestination $relative))
        if (-not $target.StartsWith($destinationBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Voice engine publish path escaped destination.' }
        Assert-NoReparsePath $target
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        # Unlink first: overwriting an old hardlink in place would also modify its other copies.
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
        if ($HardlinkVoiceEngine) { New-Item -ItemType HardLink -Path $target -Target $file.FullName | Out-Null }
        else { Copy-Item -LiteralPath $file.FullName -Destination $target }
    }
    Write-Host "Bundled voice engine: $($engineFiles.Count) files"
} else {
    Write-Warning 'Only application files were published. For embedded GPT-SoVITS, publish with -VoiceEngineDirectory pointing to the prepared offline engine; no dependencies were downloaded or installed.'
}
if ($SelfContained) {
    $licenseDir = Join-Path $output 'licenses'
    New-Item -ItemType Directory -Force -Path $licenseDir | Out-Null
    foreach ($pack in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
        $packRoot = Join-Path $env:NUGET_PACKAGES $pack
        $packVersion = Get-ChildItem -LiteralPath $packRoot -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        foreach ($licenseName in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            $source = Join-Path $packVersion.FullName $licenseName
            if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $licenseDir ($pack + '-' + $licenseName + '.txt')) }
        }
    }
}
Write-Host "Published: $output"
