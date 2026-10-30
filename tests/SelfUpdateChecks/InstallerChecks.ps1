$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('SourceGit-installer-checks-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
try {
    $wrapper = Join-Path $fixture 'run.ps1'
    @'
param($Installer, $Destination, $Archive, $ExpectedHash, $FailRestart)
$ErrorActionPreference = 'Stop'
function Start-Process {
    param($FilePath, $WorkingDirectory, $ArgumentList)
    if ($FilePath.EndsWith('SourceGit.exe') -and $FailRestart -eq 'yes') { throw 'Simulated restart failure' }
    # Fixtures contain text files instead of executables. Never launch them or Notepad.
}
& $Installer -Destination $Destination -Archive $Archive -ExpectedHash $ExpectedHash -ParentId 2147483647
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath $wrapper

    foreach ($scenario in @('success', 'bad-hash', 'restart-failure', 'missing-exe', 'portable-data')) {
        $root = Join-Path $fixture $scenario
        $destination = Join-Path $root 'installed with spaces'
        $package = Join-Path $root 'package/SourceGit'
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        [IO.Directory]::CreateDirectory($package) | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $destination 'data')) | Out-Null
        Set-Content -LiteralPath (Join-Path $destination 'SourceGit.exe') -Value 'old app'
        Set-Content -LiteralPath (Join-Path $destination 'data/preferences.json') -Value 'user settings'
        Set-Content -LiteralPath (Join-Path $package 'new-library.dll') -Value 'new library'
        if ($scenario -eq 'portable-data') {
            [IO.Directory]::CreateDirectory((Join-Path $package 'data')) | Out-Null
            Set-Content -LiteralPath (Join-Path $package 'data/preferences.json') -Value 'must not replace settings'
        }
        if ($scenario -ne 'missing-exe') {
            Set-Content -LiteralPath (Join-Path $package 'SourceGit.exe') -Value 'new app'
        }
        $archive = Join-Path $root 'package.zip'
        Compress-Archive -LiteralPath $package -DestinationPath $archive
        $digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        if ($scenario -eq 'bad-hash') { $digest = '0' * 64 }
        $installer = Join-Path $root 'install.ps1'
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../src/Resources/Scripts/InstallUpdate.ps1') -Destination $installer
        $failRestart = if ($scenario -eq 'restart-failure') { 'yes' } else { 'no' }
        & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $wrapper -Installer $installer -Destination $destination -Archive $archive -ExpectedHash $digest -FailRestart $failRestart
        $expectedExit = if ($scenario -eq 'success') { 0 } else { 1 }
        if ($LASTEXITCODE -ne $expectedExit) { throw "Unexpected exit in ${scenario}: $LASTEXITCODE" }
        $expectedApp = if ($scenario -eq 'success') { 'new app' } else { 'old app' }
        if ((Get-Content -LiteralPath (Join-Path $destination 'SourceGit.exe') -Raw).Trim() -ne $expectedApp) { throw "App contents wrong in $scenario" }
        if ((Test-Path -LiteralPath (Join-Path $destination 'new-library.dll')) -ne ($scenario -eq 'success')) { throw "New files not rolled back in $scenario" }
        if ((Get-Content -LiteralPath (Join-Path $destination 'data/preferences.json') -Raw).Trim() -ne 'user settings') { throw 'User data changed' }
        if ($scenario -ne 'success' -and !(Test-Path -LiteralPath (Join-Path $root 'update-error.txt'))) { throw 'Failure log missing' }
        Write-Output "PASS: $scenario"
    }
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^SourceGit-installer-checks-[a-f0-9]{32}$') { throw 'Invalid fixture cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
