param(
    [string]$OutputDirectory = '.build/voice-engine/gpt-sovits',
    [switch]$SkipSeal,
    [string[]]$SmokeStateFiles = @(),
    [ValidateSet('auto', 'cpu', 'cuda')][string]$SmokeDevice = 'auto',
    [string[]]$VerifiedVariants = @()
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$boundary = $project.TrimEnd('\') + '\'
$engine = [IO.Path]::GetFullPath((Join-Path $project $OutputDirectory))
if (-not $engine.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Engine output must remain inside this project.' }
$build = Join-Path $project '.build/voice-distribution'
$commit = '04a44eb977586645f33594b72af13a035a2056c6'
$requirements = Join-Path $project 'requirements-voice.lock.txt'
if (-not (Test-Path -LiteralPath $requirements -PathType Leaf)) { throw 'Missing requirements-voice.lock.txt.' }
if ($VerifiedVariants.Count -and -not $SmokeStateFiles.Count) { throw 'VerifiedVariants requires model smoke tests through SmokeStateFiles.' }

function Assert-RealPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Linked build paths are not supported: $current"
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
Assert-RealPath $engine
Assert-RealPath $build
foreach ($state in $SmokeStateFiles) {
    Assert-RealPath $state
    if (-not (Test-Path -LiteralPath $state -PathType Leaf)) { throw 'A smoke-test state file is unavailable.' }
}
$seal = Join-Path $engine 'laidian-engine.json'
if (Test-Path -LiteralPath $seal) {
    $manifest = Get-Content -LiteralPath $seal -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.sourceCommit -ne $commit) { throw 'Existing engine uses a different source/manifest; use a separate output directory.' }
    Write-Host "A sealed engine already exists at $engine. It was not overwritten. Use seal-voice-engine.py --verify to check its files."
    return
}
New-Item -ItemType Directory -Force -Path $build,$engine | Out-Null
foreach ($name in @('TEMP','TMP','TMPDIR','APPDATA','LOCALAPPDATA','USERPROFILE','HOME','XDG_CACHE_HOME','PIP_CACHE_DIR','HF_HOME','HF_HUB_CACHE','HUGGINGFACE_HUB_CACHE','TRANSFORMERS_CACHE','TORCH_HOME','TORCHINDUCTOR_CACHE_DIR','TRITON_CACHE_DIR','NUMBA_CACHE_DIR','MPLCONFIGDIR','PYTHONUSERBASE','PYTHONPYCACHEPREFIX','CUDA_CACHE_PATH','NLTK_DATA','JIEBA_CACHE_DIR')) {
    $directory = Join-Path $build ('environment/' + $name.ToLowerInvariant())
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    [Environment]::SetEnvironmentVariable($name, $directory, 'Process')
}
$env:PYTHONDONTWRITEBYTECODE = '1'
$env:PYTHONNOUSERSITE = '1'
$env:PYTHONUTF8 = '1'
$env:PIP_DISABLE_PIP_VERSION_CHECK = '1'
$env:HF_HUB_DISABLE_TELEMETRY = '1'
Remove-Item Env:PYTHONHOME -ErrorAction SilentlyContinue
Remove-Item Env:PYTHONPATH -ErrorAction SilentlyContinue
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-PinnedFile([string]$Url, [string]$Name, [string]$Sha256) {
    $destination = Join-Path $build $Name
    Assert-RealPath $destination
    if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $Sha256) { return $destination }
    $partial = $destination + '.part'
    Assert-RealPath $partial
    try {
        Invoke-WebRequest -Uri $Url -UseBasicParsing -OutFile $partial
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Sha256) { throw "Pinned download hash mismatch: $Name" }
        Move-Item -LiteralPath $partial -Destination $destination -Force
    } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
    return $destination
}

function Expand-CheckedZip([string]$Archive, [string]$Destination, [string]$StripPrefix = '', [switch]$InferenceSource) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    $destinationBoundary = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
    try {
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($StripPrefix) {
                if (-not $name.StartsWith($StripPrefix, [StringComparison]::Ordinal)) { throw 'Unexpected source archive root.' }
                $name = $name.Substring($StripPrefix.Length)
            }
            if (-not $name -or $name.EndsWith('/')) { continue }
            if ($InferenceSource -and -not ($name.StartsWith('GPT_SoVITS/') -or $name.StartsWith('tools/') -or $name -in @('config.py','LICENSE'))) { continue }
            if (($name.Split('/') -contains '..') -or [IO.Path]::IsPathRooted($name) -or $name.Contains(':')) { throw 'Unsafe archive entry.' }
            if ((($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000) { throw 'Archive symbolic links are not supported.' }
            $destinationFile = [IO.Path]::GetFullPath((Join-Path $Destination $name))
            if (-not $destinationFile.StartsWith($destinationBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive entry escaped output.' }
            Assert-RealPath $destinationFile
            if (Test-Path -LiteralPath $destinationFile) { throw "Extraction would overwrite an existing file: $name" }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationFile) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destinationFile, $false)
        }
    } finally { $zip.Dispose() }
}

$runtime = Join-Path $engine 'runtime'
$python = Join-Path $runtime 'python.exe'
if (-not (Test-Path -LiteralPath $python)) {
    if ((Test-Path -LiteralPath $runtime) -and (Get-ChildItem -LiteralPath $runtime -Force | Select-Object -First 1)) {
        throw 'An incomplete runtime already exists. Use a clean output directory; existing files were not overwritten.'
    }
    $pythonZip = Get-PinnedFile 'https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip' 'python-3.11.9-embed-amd64.zip' '009d6bf7e3b2ddca3d784fa09f90fe54336d5b60f0e0f305c37f400bf83cfd3b'
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    Expand-CheckedZip $pythonZip $runtime
    [IO.File]::WriteAllText((Join-Path $runtime 'python311._pth'), "python311.zip`n.`nLib/site-packages`nimport site`n", [Text.Encoding]::ASCII)
} else {
    Assert-RealPath $python
    $pythonVersion = & $python -I -B -c 'import platform; print(platform.python_version())'
    if ($LASTEXITCODE -ne 0 -or $pythonVersion -ne '3.11.9') { throw 'Existing runtime is not the pinned Python 3.11.9; it was not overwritten.' }
    if (-not ((Get-Content -LiteralPath (Join-Path $runtime 'python311._pth') -Raw) -match 'Lib/site-packages')) { throw 'Existing runtime has an incompatible search path; it was not overwritten.' }
}
$sourceMarker = Join-Path $engine 'laidian-source-commit.txt'
if (-not (Test-Path -LiteralPath (Join-Path $engine 'GPT_SoVITS/TTS_infer_pack/TTS.py'))) {
    $sourceZip = Get-PinnedFile "https://codeload.github.com/RVC-Boss/GPT-SoVITS/zip/$commit" 'gpt-sovits-source.zip' '21c421dc7734e276433787008f097b871041b1e11b2855b64df23492aeaf6467'
    Expand-CheckedZip $sourceZip $engine "GPT-SoVITS-$commit/" -InferenceSource
    [IO.File]::WriteAllText($sourceMarker, $commit, [Text.Encoding]::ASCII)
} elseif (-not (Test-Path -LiteralPath $sourceMarker) -or (Get-Content -LiteralPath $sourceMarker -Raw).Trim() -ne $commit) {
    throw 'Existing source has no matching pinned revision marker; it was not overwritten.'
}

$site = Join-Path $runtime 'Lib/site-packages'
if (-not (Test-Path -LiteralPath (Join-Path $site 'pip/__main__.py'))) {
    $pipWheel = Get-PinnedFile 'https://files.pythonhosted.org/packages/07/25/ee8c38ae72d66b844ce8eb9711ba905dba21ca1a8a0c960057c41cd23337/pip-26.3-py3-none-any.whl' 'pip-26.3-py3-none-any.whl' 'f3b60b20ffaadd4415ed7957212a38ac63fa4c5fa375bd0f4a797777940fad13'
    New-Item -ItemType Directory -Force -Path $site | Out-Null
    Expand-CheckedZip $pipWheel $site
}
$pipVersion = & $python -I -B -c 'import pip; print(pip.__version__)'
if ($LASTEXITCODE -ne 0 -or $pipVersion -ne '26.3') { throw 'Build bootstrap requires pinned pip 26.3.' }
$support = @('setuptools==80.10.2', 'wheel==0.48.0')
& $python -I -B -m pip install --disable-pip-version-check --no-compile --only-binary=:all: --no-deps @support
if ($LASTEXITCODE -ne 0) { throw 'Locked build support installation failed.' }
& $python -I -B -m pip install --disable-pip-version-check --no-compile --prefer-binary --no-build-isolation --extra-index-url https://download.pytorch.org/whl/cu124 -r $requirements
if ($LASTEXITCODE -ne 0) { throw 'Locked inference dependency installation failed.' }
$trimReportText = & $python -I -B (Join-Path $PSScriptRoot 'trim-voice-source.py') --engine-root $engine
if ($LASTEXITCODE -ne 0) { throw 'Inference source preparation failed.' }
$trimReport = ($trimReportText -join "`n") | ConvertFrom-Json
$trimReportPath = Join-Path $engine 'laidian-source-trim.json'
if (Test-Path -LiteralPath $trimReportPath) {
    $previousReport = Get-Content -LiteralPath $trimReportPath -Raw | ConvertFrom-Json
    if ($previousReport.upstream_commit -ne $commit) { throw 'Source trim report has a different upstream revision.' }
    foreach ($field in @('modified', 'deleted', 'removed_empty_directories')) {
        $trimReport.$field = @(@($previousReport.$field) + @($trimReport.$field) | Sort-Object -Unique)
    }
}
[IO.File]::WriteAllText($trimReportPath, ($trimReport | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
foreach ($script in @('prepare-voice-engine.py', 'fetch-voice-resources.py', 'fetch-voice-dictionaries.py', 'fetch-voice-licenses.py')) {
    & $python -I -B (Join-Path $PSScriptRoot $script) --engine-root $engine
    if ($LASTEXITCODE -ne 0) { throw "Voice engine preparation failed: $script" }
}
$vcStage = Join-Path $build 'vc-runtime/staged'
& $python -I -B (Join-Path $PSScriptRoot 'prepare-vc-runtime.py') --engine-root $engine --stage-directory $vcStage
if ($LASTEXITCODE -ne 0) { throw 'Application-local VC runtime preparation failed.' }
# The embedded Python process must exit before replacing the DLLs it was using.
foreach ($relative in @('runtime/msvcp140.dll', 'runtime/msvcp140_1.dll', 'runtime/vcomp140.dll', 'runtime/vcruntime140.dll', 'runtime/vcruntime140_1.dll', 'licenses/Microsoft-VC-Runtime-License.docx', 'vc-runtime-manifest.json')) {
    $target = Join-Path $engine $relative
    Assert-RealPath $target
    $temporary = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        Copy-Item -LiteralPath (Join-Path $vcStage $relative) -Destination $temporary
        if (Test-Path -LiteralPath $target) { [IO.File]::Replace($temporary, $target, $null) }
        else { [IO.File]::Move($temporary, $target) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
Copy-Item -LiteralPath $requirements -Destination (Join-Path $engine 'requirements-voice.lock.txt') -Force
function Invoke-ModelSmoke([string]$Stage) {
    foreach ($state in $SmokeStateFiles) {
        $smokeWork = Join-Path $build ('smoke-' + $Stage + '-' + [Guid]::NewGuid().ToString('N'))
        & $python -I -B (Join-Path $PSScriptRoot 'smoke-embedded-voice.py') --engine-root $engine --state-file ([IO.Path]::GetFullPath($state)) --work-root $smokeWork --device $SmokeDevice
        if ($LASTEXITCODE -ne 0) { throw "Inference smoke test failed during $Stage; the engine has not been sealed." }
    }
}
if ($SmokeStateFiles.Count) {
    Invoke-ModelSmoke 'before-trim'
    & $python -I -B (Join-Path $PSScriptRoot 'trim-voice-runtime.py') --engine-root $engine --apply --report (Join-Path $engine 'laidian-runtime-trim.json')
    if ($LASTEXITCODE -ne 0) { throw 'Runtime trimming failed; the engine has not been sealed.' }
    Invoke-ModelSmoke 'after-trim'
    $verification = [ordered]@{ passed = $true; beforeTrim = $true; afterTrim = $true; modelCount = $SmokeStateFiles.Count; device = $SmokeDevice; languages = @('zh', 'ja'); verifiedVariants = $VerifiedVariants }
    [IO.File]::WriteAllText((Join-Path $engine 'laidian-inference-verification.json'), ($verification | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
} else {
    Write-Warning 'No model smoke state was supplied. Runtime trimming is skipped and no model variant is marked verified.'
}
if (-not $SkipSeal) {
    $sealArguments = @('--engine-root', $engine, '--source-commit', $commit)
    if ($VerifiedVariants.Count) { $sealArguments += '--verified-variants'; $sealArguments += $VerifiedVariants }
    & $python -I -B (Join-Path $PSScriptRoot 'seal-voice-engine.py') @sealArguments
    if ($LASTEXITCODE -ne 0) { throw 'Voice engine sealing failed.' }
}
Write-Host "Prepared offline inference engine: $engine"
if (-not $SmokeStateFiles.Count) { Write-Host 'Build-only pip/wheel remain. Supply SmokeStateFiles to validate, trim and seal the final runtime; setuptools/pkg_resources are retained.' }
