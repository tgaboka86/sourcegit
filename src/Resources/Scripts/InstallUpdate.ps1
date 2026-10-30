param(
    [Parameter(Mandatory=$true)][string]$Destination,
    [Parameter(Mandatory=$true)][string]$Archive,
    [Parameter(Mandatory=$true)][string]$ExpectedHash,
    [Parameter(Mandatory=$true)][int]$ParentId
)

$ErrorActionPreference = 'Stop'
$backup = Join-Path $PSScriptRoot 'backup'
$staging = Join-Path $PSScriptRoot 'staging'
$changed = [System.Collections.Generic.List[string]]::new()
$succeeded = $false
try {
    $Destination = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    if (!(Test-Path -LiteralPath (Join-Path $Destination 'SourceGit.exe') -PathType Leaf)) {
        throw 'The installation directory does not contain SourceGit.exe.'
    }
    Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ready') -Value 'ready'
    $parent = Get-Process -Id $ParentId -ErrorAction SilentlyContinue
    if ($parent -and !$parent.WaitForExit(120000)) { throw 'SourceGit did not exit within two minutes. No files were installed.' }

    # Verify again immediately before extraction, after SourceGit exits.
    if ($ExpectedHash -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $ExpectedHash) {
        throw 'SHA-256 verification failed. No files were installed.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $staging)
    $source = Join-Path $staging 'SourceGit'
    if (!(Test-Path -LiteralPath (Join-Path $source 'SourceGit.exe') -PathType Leaf)) { throw 'Invalid SourceGit package.' }

    $files = @(Get-ChildItem -LiteralPath $source -Recurse -File)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($source.Length + 1)
        if ($relative -match '^(?i:data)(\\|$)') { throw 'Packages must not overwrite portable user data.' }
        $target = Join-Path $Destination $relative
        # Refuse junctions/symlinks anywhere along the destination path.
        $ancestor = $target
        while ($ancestor) {
            if (Test-Path -LiteralPath $ancestor) {
                if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Cannot update a linked installation path: $ancestor"
                }
            }
            $ancestor = [IO.Path]::GetDirectoryName($ancestor)
        }
        if (Test-Path -LiteralPath $target) {
            $saved = Join-Path $backup $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved)) | Out-Null
            Copy-Item -LiteralPath $target -Destination $saved
        }
    }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($source.Length + 1)
        $target = Join-Path $Destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        $changed.Add($relative)
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
    Start-Process -FilePath (Join-Path $Destination 'SourceGit.exe') -WorkingDirectory $Destination
    $succeeded = $true
} catch {
    $failure = $_.ToString()
    # Restore every overwritten file; keep the backup if restoration itself fails.
    foreach ($relative in $changed) {
        try {
            $target = Join-Path $Destination $relative
            $saved = Join-Path $backup $relative
            if (Test-Path -LiteralPath $saved) {
                Copy-Item -LiteralPath $saved -Destination $target -Force
            } elseif (Test-Path -LiteralPath $target) {
                Remove-Item -LiteralPath $target -Force
            }
        } catch { $failure += "`r`nRestore failed for ${relative}: $_" }
    }
    $log = Join-Path $PSScriptRoot 'update-error.txt'
    "SourceGit installation failed. $failure`r`nBackup files: $backup`r`nYou can restart SourceGit from $Destination." | Set-Content -LiteralPath $log
    Start-Process -FilePath 'notepad.exe' -ArgumentList @('"' + $log + '"')
    exit 1
} finally {
    if ($succeeded) {
        # Only remove this specific temporary workspace, never the installation folder.
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $workspace = [IO.Path]::GetFullPath($PSScriptRoot)
        if ($workspace.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($workspace) -match '^SourceGit-update-[a-f0-9]{32}$') {
            [Environment]::CurrentDirectory = $tempRoot
            Set-Location -LiteralPath $tempRoot
            Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
