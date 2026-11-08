param(
    [Parameter(Mandatory=$true)][string]$SourceProgram,
    [Parameter(Mandatory=$true)][string]$DestinationProgram
)
$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath $SourceProgram).Path.TrimEnd('\')
$destinationRoot = (Resolve-Path -LiteralPath $DestinationProgram).Path.TrimEnd('\')
if ($sourceRoot -eq $destinationRoot) { throw 'Source and destination must be different.' }
if (-not ((Test-Path -LiteralPath (Join-Path $destinationRoot 'Zhidian.exe') -PathType Leaf) -or
    (Test-Path -LiteralPath (Join-Path $destinationRoot 'Laidian.exe') -PathType Leaf))) {
    throw 'Destination must contain Zhidian.exe or a compatible Laidian.exe.'
}
$sourceData = Join-Path $sourceRoot 'data'
$destinationData = Join-Path $destinationRoot 'data'
if (-not (Test-Path -LiteralPath (Join-Path $sourceData 'state.json')) -and -not (Test-Path -LiteralPath (Join-Path $sourceData 'state.backup.json'))) { throw 'No saved data in source.' }
if (Test-Path -LiteralPath $destinationData) { throw 'Destination already has data; nothing was overwritten.' }
foreach ($location in @($sourceData,$destinationRoot)) {
    for ($directory = [IO.DirectoryInfo]$location; $null -ne $directory; $directory = $directory.Parent) {
        if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked data directories are not supported.' }
    }
}
$mutexes = @()
$staging = Join-Path $destinationRoot ('.migration-' + [guid]::NewGuid().ToString('N'))
try {
    foreach ($programRoot in @($sourceRoot,$destinationRoot) | Sort-Object) {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($programRoot + '\').ToUpperInvariant())) } finally { $sha.Dispose() }
        $id = ([BitConverter]::ToString($bytes)).Replace('-','').Substring(0,20)
        $mutex = [Threading.Mutex]::new($false, ('Local\Qiban-' + $id))
        $owned = $false
        try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
        if (-not $owned) { $mutex.Dispose(); throw 'Close both versions before migrating data.' }
        $mutexes += $mutex
    }
    $entries = @(Get-ChildItem -LiteralPath $sourceData -Recurse -Force)
    if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) { throw 'Source contains linked files; migration canceled.' }
    New-Item -ItemType Directory -Path $staging | Out-Null
    foreach ($entry in $entries) {
        $relative = $entry.FullName.Substring($sourceData.Length + 1)
        $target = Join-Path $staging $relative
        if ($entry.PSIsContainer) { New-Item -ItemType Directory -Force -Path $target | Out-Null }
        else {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            Copy-Item -LiteralPath $entry.FullName -Destination $target
            if ((Get-FileHash -LiteralPath $entry.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash) { throw 'Data changed during migration; source preserved.' }
        }
    }
    [IO.Directory]::Move($staging, $destinationData)
    Write-Output 'Data migrated and verified. Original data preserved.'
}
finally {
    foreach ($mutex in $mutexes) { $mutex.ReleaseMutex(); $mutex.Dispose() }
    if (Test-Path -LiteralPath $staging) {
        $resolved = [IO.Path]::GetFullPath($staging)
        if ($resolved.StartsWith($destinationRoot + '\', [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolved).StartsWith('.migration-')) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
