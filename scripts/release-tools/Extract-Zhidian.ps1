[CmdletBinding()]
param(
    [string]$DestinationDirectory,
    [switch]$KeepArchive
)

# Windows PowerShell 5.1; no network access or installed archive tool required.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$staging = $null
$zip = $null

function Assert-PlainName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or
        $Name.EndsWith('.') -or $Name -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') {
        throw 'The release manifest contains an unsafe file or directory name.'
    }
}

function Assert-NoReparsePath([string]$Path) {
    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Symbolic links and directory junctions are not supported for extraction.'
            }
        }
        $candidate = [IO.Path]::GetDirectoryName($candidate)
    }
}

function Assert-Hash([string]$Hash) {
    if ($Hash -notmatch '^[0-9a-fA-F]{64}$') { throw 'The release manifest contains an invalid SHA-256 value.' }
}

function Get-HexHash($Algorithm) {
    return ([BitConverter]::ToString($Algorithm.Hash)).Replace('-', '').ToLowerInvariant()
}

function Get-ExtendedPath([string]$Path) {
    # Prefix only absolute paths already validated by this script. This avoids
    # requiring the machine-wide Windows long-path policy for Python packages.
    if ($Path.StartsWith('\\?\')) { return $Path }
    if ($Path.StartsWith('\\')) { return '\\?\UNC\' + $Path.Substring(2) }
    return '\\?\' + $Path
}

function Assert-ZipName([string]$Name) {
    if ([string]::IsNullOrEmpty($Name) -or $Name.Contains('\') -or $Name.StartsWith('/') -or
        $Name -match '[\x00-\x1f<>:"|?*]') { throw 'The ZIP contains an unsafe entry path.' }
    $trimmed = $Name.TrimEnd('/')
    if (-not $trimmed) { throw 'The ZIP contains an empty entry path.' }
    foreach ($segment in $trimmed.Split('/')) {
        if (-not $segment -or $segment -eq '.' -or $segment -eq '..' -or
            $segment.EndsWith('.') -or $segment.EndsWith(' ') -or
            $segment -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') {
            throw 'The ZIP contains an unsafe Windows entry path.'
        }
    }
    return $trimmed
}

try {
    $source = [IO.Path]::GetFullPath($PSScriptRoot)
    $manifestPath = Join-Path $source 'release-manifest.json'
    Assert-NoReparsePath $manifestPath
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'release-manifest.json is missing. Download it beside all ZIP parts and both Extract-Zhidian files.'
    }
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 1MB) { throw 'The release manifest is unexpectedly large.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-PlainName $manifest.directory
    Assert-PlainName $manifest.archive.name
    if (-not $manifest.archive.name.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The manifest archive must be a ZIP file.'
    }
    Assert-Hash $manifest.archive.sha256
    [long]$archiveBytes = $manifest.archive.bytes
    [long]$unpackedBytes = $manifest.archive.unpackedBytes
    [long]$fileCount = $manifest.archive.fileCount
    if ($archiveBytes -le 0 -or $unpackedBytes -le 0 -or $fileCount -le 0 -or $fileCount -gt 200000) {
        throw 'The release manifest contains invalid archive sizes or file counts.'
    }
    $parts = @($manifest.parts)
    if ($parts.Count -lt 1 -or $parts.Count -gt 999) { throw 'Invalid number of ZIP parts.' }
    [long]$totalPartBytes = 0
    for ($index = 0; $index -lt $parts.Count; $index++) {
        $part = $parts[$index]
        Assert-PlainName $part.name
        $expectedName = '{0}.{1:D3}' -f $manifest.archive.name, ($index + 1)
        if ($part.name -cne $expectedName) { throw 'ZIP parts must be listed in consecutive numeric order.' }
        Assert-Hash $part.sha256
        [long]$partBytes = $part.bytes
        if ($partBytes -le 0 -or $partBytes -ge 2GB) { throw 'Invalid ZIP part size.' }
        $partPath = Join-Path $source $part.name
        Assert-NoReparsePath $partPath
        if (-not (Test-Path -LiteralPath $partPath -PathType Leaf)) { throw ('Missing ZIP part: ' + $part.name) }
        if ((Get-Item -LiteralPath $partPath).Length -ne $partBytes) { throw ('ZIP part size mismatch: ' + $part.name) }
        $totalPartBytes += $partBytes
    }
    if ($totalPartBytes -ne $archiveBytes) { throw 'The total ZIP part size does not match the archive size.' }

    if (-not $DestinationDirectory) { $DestinationDirectory = Join-Path $source $manifest.directory }
    $destination = [IO.Path]::GetFullPath($DestinationDirectory).TrimEnd('\', '/')
    Assert-NoReparsePath $destination
    if (Test-Path -LiteralPath $destination) {
        throw 'The destination already exists. Choose a new empty destination name; existing application data will not be overwritten.'
    }
    $parent = [IO.Path]::GetDirectoryName($destination)
    if (-not $parent -or -not (Test-Path -LiteralPath $parent -PathType Container)) {
        throw 'The destination parent directory must already exist.'
    }
    $keptArchive = Join-Path $parent $manifest.archive.name
    if ($KeepArchive -and (Test-Path -LiteralPath $keptArchive)) { throw 'The ZIP requested by -KeepArchive already exists.' }
    $requiredSpace = $archiveBytes + $unpackedBytes + 512MB
    $drive = New-Object IO.DriveInfo([IO.Path]::GetPathRoot($parent))
    if ($drive.AvailableFreeSpace -lt $requiredSpace) {
        throw ('Not enough disk space. Need at least {0:N1} GiB free on the destination drive for merging and extraction.' -f ($requiredSpace / 1GB))
    }

    $staging = Join-Path $parent ('.zhidian-extract-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging | Out-Null
    $merged = Join-Path $staging ($manifest.archive.name + '.part')
    $unpack = Join-Path $staging 'unpacked'
    $buffer = New-Object byte[] (4MB)
    $wholeHash = [Security.Cryptography.SHA256]::Create()
    $outputStream = [IO.File]::Open($merged, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        foreach ($part in $parts) {
            Write-Host ('Verifying and merging: ' + $part.name)
            $partHash = [Security.Cryptography.SHA256]::Create()
            $inputStream = [IO.File]::OpenRead((Join-Path $source $part.name))
            try {
                while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    [void]$partHash.TransformBlock($buffer, 0, $read, $null, 0)
                    [void]$wholeHash.TransformBlock($buffer, 0, $read, $null, 0)
                    $outputStream.Write($buffer, 0, $read)
                }
                [void]$partHash.TransformFinalBlock($buffer, 0, 0)
                if ((Get-HexHash $partHash) -ne $part.sha256) { throw ('ZIP part SHA-256 mismatch: ' + $part.name) }
            } finally {
                $inputStream.Dispose()
                $partHash.Dispose()
            }
        }
        [void]$wholeHash.TransformFinalBlock($buffer, 0, 0)
        if ((Get-HexHash $wholeHash) -ne $manifest.archive.sha256) { throw 'Combined ZIP SHA-256 mismatch.' }
        if ($outputStream.Length -ne $archiveBytes) { throw 'Combined ZIP size mismatch.' }
    } finally {
        $outputStream.Dispose()
        $wholeHash.Dispose()
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($merged)
    $names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $fileNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    [long]$actualBytes = 0
    [long]$actualFiles = 0
    foreach ($entry in $zip.Entries) {
        $safeName = Assert-ZipName $entry.FullName
        if (-not $names.Add($safeName)) { throw 'The ZIP contains duplicate entry names.' }
        $unixType = ($entry.ExternalAttributes -shr 16) -band 0xF000
        if ($unixType -eq 0xA000 -or ($entry.ExternalAttributes -band 0x400) -ne 0) {
            throw 'The ZIP contains a symbolic link or reparse entry.'
        }
        if (-not $entry.FullName.EndsWith('/')) {
            [void]$fileNames.Add($safeName)
            $actualFiles++
            $actualBytes += $entry.Length
        } elseif ($entry.Length -ne 0) { throw 'A ZIP directory entry contains data.' }
    }
    foreach ($name in $names) {
        $components = $name.Split('/')
        for ($i = 1; $i -lt $components.Length; $i++) {
            if ($fileNames.Contains(($components[0..($i - 1)] -join '/'))) { throw 'A ZIP file conflicts with an extraction directory.' }
        }
    }
    if ($actualFiles -ne $fileCount -or $actualBytes -ne $unpackedBytes) {
        throw 'The ZIP inventory does not match the release manifest.'
    }
    if (-not $fileNames.Contains('Zhidian.exe')) { throw 'The ZIP does not contain Zhidian.exe.' }
    New-Item -ItemType Directory -Path $unpack | Out-Null
    $unpackBoundary = [IO.Path]::GetFullPath($unpack).TrimEnd('\') + '\'
    [long]$extracted = 0
    foreach ($entry in $zip.Entries) {
        # Every entry segment was checked before writing anything. Concatenate
        # here so legacy Path.GetFullPath cannot reject a valid long filename.
        $target = $unpackBoundary + $entry.FullName.Replace('/', '\')
        if (-not $target.StartsWith($unpackBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP path escaped the extraction directory.' }
        $nativeTarget = Get-ExtendedPath $target
        if ($entry.FullName.EndsWith('/')) {
            [void][IO.Directory]::CreateDirectory($nativeTarget)
            continue
        }
        [void][IO.Directory]::CreateDirectory($nativeTarget.Substring(0, $nativeTarget.LastIndexOf('\')))
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $nativeTarget, $false)
        if ((New-Object IO.FileInfo($nativeTarget)).Length -ne $entry.Length) { throw 'An extracted file has an unexpected length.' }
        $extracted++
        if (($extracted % 1000) -eq 0) { Write-Host ('Extracted {0}/{1} files...' -f $extracted, $fileCount) }
    }
    $zip.Dispose()
    $zip = $null
    Assert-NoReparsePath $destination
    if (Test-Path -LiteralPath $destination) { throw 'The destination appeared during extraction; it will not be overwritten.' }
    # Directory.Move refuses an existing destination, including one created in a race.
    [IO.Directory]::Move($unpack, $destination)
    if ($KeepArchive) { [IO.File]::Move($merged, $keptArchive) }
    else { Remove-Item -LiteralPath $merged -Force }
    Remove-Item -LiteralPath $staging -Force
    $staging = $null
    Write-Host ''
    Write-Host 'SUCCESS: All ZIP parts were verified and the application was extracted.' -ForegroundColor Green
    Write-Host ('Open: ' + (Join-Path $destination 'Zhidian.exe'))
    Write-Host 'Keep the complete application directory. Configure your own chat service and optional voice models inside the application.'
    exit 0
} catch {
    if ($null -ne $zip) { $zip.Dispose() }
    Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    if ($staging -and (Test-Path -LiteralPath $staging)) {
        # A failure never recursively deletes files, nor modifies an existing application.
        Write-Host ('Incomplete temporary files remain here and may be removed: ' + $staging)
    }
    Write-Host 'The downloaded ZIP parts have not been changed. No application was started.'
    exit 1
}
