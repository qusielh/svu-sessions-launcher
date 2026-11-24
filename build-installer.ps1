# Builds the framework-dependent release zip and the Inno Setup installer.
# Usage: .\build-installer.ps1
[CmdletBinding()]
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$outputDir = Join-Path $root 'dist'
$stage = Join-Path $outputDir 'stage'
$project = Join-Path $root 'src\SvuSessionsLauncher\SvuSessionsLauncher.csproj'
$version = ([xml](Get-Content -Raw $project)).Project.PropertyGroup.Version |
    Where-Object { $_ } |
    Select-Object -First 1

if (-not $version) {
    throw 'Could not read <Version> from the launcher project.'
}

$isccCandidates = @()
if (${env:ProgramFiles(x86)}) {
    $isccCandidates += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
}
if ($env:ProgramFiles) {
    $isccCandidates += Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'
}
if ($env:LOCALAPPDATA) {
    $isccCandidates += Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
}

$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php and run this script again.'
}

try {
    & (Join-Path $root 'build-release.ps1') -Configuration $Configuration -OutputDir $outputDir -StageDir $stage
    if (-not $?) {
        throw 'build-release.ps1 failed.'
    }

    # Compile into a clean temp folder. Antivirus scanners often lock a setup file
    # that was just executed, and Inno cannot update resources in a locked file.
    $setupOut = Join-Path $env:TEMP 'opencode\svu-setup-out'
    if (Test-Path $setupOut) {
        Remove-Item $setupOut -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $setupOut | Out-Null

    $iss = Join-Path $root 'installer\SvuSessionsLauncher.iss'
    & $iscc "/DAppVersion=$version" "/O$setupOut" $iss
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE."
    }

    $builtSetup = Join-Path $setupOut "SVU-Sessions-Launcher-$version-win-x64-setup.exe"
    if (-not (Test-Path $builtSetup)) {
        throw "Installer was not created at $builtSetup."
    }

    $setup = Join-Path $outputDir "SVU-Sessions-Launcher-$version-win-x64-setup.exe"
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try {
            if (Test-Path $setup) {
                Remove-Item $setup -Force -ErrorAction Stop
            }
            Move-Item $builtSetup $setup -Force -ErrorAction Stop
            break
        }
        catch {
            if ($attempt -eq 4) {
                throw "Could not replace $setup. Close any running setup and try again. $($_.Exception.Message)"
            }
            Start-Sleep -Seconds 2
        }
    }

    if (-not (Test-Path $setup)) {
        throw "Installer was not moved to $setup."
    }

    $zip = Join-Path $outputDir "SVU-Sessions-Launcher-$version-win-x64.zip"
    $zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash

    $sums = Join-Path $outputDir 'SHA256SUMS.txt'
    @(
        "SHA256  $zipHash  $([System.IO.Path]::GetFileName($zip))",
        "SHA256  $hash  $([System.IO.Path]::GetFileName($setup))"
    ) | Set-Content -Path $sums -Encoding ASCII

    Write-Host "Created $zip"
    Write-Host "SHA256  $zipHash"
    Write-Host "Created $setup"
    Write-Host "SHA256  $hash"
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
