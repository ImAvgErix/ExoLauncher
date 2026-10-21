param([string]$PayloadRoot, [switch]$DefinitionsOnly)
$ErrorActionPreference = 'Stop'

function Get-ExoPayloadHash([string]$Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = $null
    try {
        $stream = [IO.File]::OpenRead($Path)
        return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
    } finally {
        if ($null -ne $stream) { $stream.Dispose() }
        $algorithm.Dispose()
    }
}

function Get-ExoPayloadFiles([string]$Root) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($rootPath)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        if (([IO.File]::GetAttributes($directory) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Payload links are not allowed.' }
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Payload links are not allowed.' }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $pending.Push($entry) }
            elseif (-not [StringComparer]::OrdinalIgnoreCase.Equals($entry, (Join-Path $rootPath 'payload-manifest.json'))) { $entry }
        }
    }
}

function Test-ExoPayload([string]$Root) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $manifestPath = Join-Path $rootPath 'payload-manifest.json'
    if (-not [IO.File]::Exists($manifestPath) -or ([IO.FileInfo]$manifestPath).Length -gt 8MB) { throw 'Payload manifest missing or too large.' }
    if (([IO.File]::GetAttributes($manifestPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Payload manifest cannot be a link.' }
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    if ($manifest.schema -ne 1 -or @($manifest.files).Count -eq 0) { throw 'Invalid payload manifest.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        if ([string]::IsNullOrWhiteSpace($entry.path) -or [IO.Path]::IsPathRooted($entry.path) -or $entry.path -match '(^|[\\/])\.\.([\\/]|$)' -or $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $entry.size -lt 0) { throw 'Invalid payload manifest entry.' }
        $path = [IO.Path]::GetFullPath((Join-Path $rootPath $entry.path))
        if (-not $path.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase) -or -not $seen.Add($path)) { throw 'Duplicate or escaped payload path.' }
        if (-not [IO.File]::Exists($path) -or ([IO.FileInfo]$path).Length -ne $entry.size) { throw "Payload size mismatch: $($entry.path)" }
    }
    $files = @(Get-ExoPayloadFiles $rootPath)
    if ($files.Count -ne $seen.Count) { throw 'Payload contains an unexpected or missing file.' }
    foreach ($entry in $manifest.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $rootPath $entry.path))
        if (-not [StringComparer]::OrdinalIgnoreCase.Equals((Get-ExoPayloadHash $path), $entry.sha256)) { throw "Payload checksum mismatch: $($entry.path)" }
    }
    return $manifest
}

if ($DefinitionsOnly) { return }
try { $manifest = Test-ExoPayload $PayloadRoot; Write-Output "Verified $(@($manifest.files).Count) payload files."; exit 0 }
catch { Write-Error $_ -ErrorAction Continue; exit 1 }
