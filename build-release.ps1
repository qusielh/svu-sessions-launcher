# Builds the framework-dependent Windows release zip.
# Usage: .\build-release.ps1
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $OutputDir = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\SvuSessionsLauncher\SvuSessionsLauncher.csproj'
$version = ([xml](Get-Content -Raw $project)).Project.PropertyGroup.Version |
    Where-Object { $_ } |
    Select-Object -First 1

if (-not $version) {
    throw 'Could not read <Version> from the launcher project.'
}

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ('svu-release-' + [guid]::NewGuid().ToString('N'))
$app = Join-Path $stage 'app'
New-Item -ItemType Directory -Path $app -Force | Out-Null

try {
    dotnet publish $project -c $Configuration --nologo -o $app
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    # The voice helper is built separately for x86; the publish target does not pick it up.
    $bin = Join-Path $root "src\SvuSessionsLauncher\bin\$Configuration\net10.0-windows"
    Get-ChildItem $bin -Filter 'SvuVoiceHelper.*' -File |
        Where-Object { $_.Extension -ne '.xml' } |
        Copy-Item -Destination $app -Force

    # WebView2 ships docs and native loaders for every platform. Keep only what win-x64 uses.
    Get-ChildItem $app -Filter '*.xml' -File | Remove-Item -Force
    Remove-Item (Join-Path $app 'runtimes\win-arm64') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $app 'runtimes\win-x86') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $app 'Microsoft.Web.WebView2.WinForms.dll') -Force -ErrorAction SilentlyContinue

    # Remove a WebView2 profile left by a previous local run.
    Remove-Item (Join-Path $app 'SvuSessionsLauncher.exe.WebView2') -Recurse -Force -ErrorAction SilentlyContinue

    $releaseReadme = @"
SVU Sessions Launcher $version
$('=' * ("SVU Sessions Launcher $version".Length))

Beta software. The main flows work, but bugs are expected. Feedback:
https://github.com/qusielh/svu-sessions-launcher/issues

Requirements
------------
- Windows 10 or 11 (64-bit)
- Microsoft Edge WebView2 Runtime
- .NET 10 Desktop Runtime (x64)
- .NET 10 Runtime (x86) for tutor voice playback
- TrueSpeech ACM codec for tutor voice playback

Download the .NET 10 runtimes from:
https://dotnet.microsoft.com/download/dotnet/10.0

Run
---
Start SvuSessionsLauncher.exe.

Notes
-----
- The launcher is unofficial and not endorsed by SVU.
- Student accounts only; tutor and host accounts are blocked in this beta.
- App state and logs live under %APPDATA%\SVU Sessions Launcher.
"@
    Set-Content -Path (Join-Path $app 'README.txt') -Value $releaseReadme -Encoding UTF8

    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    $zip = Join-Path $OutputDir "SVU-Sessions-Launcher-$version-win-x64.zip"
    if (Test-Path $zip) {
        Remove-Item $zip -Force
    }

    Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip -CompressionLevel Optimal

    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    Write-Host "Created $zip"
    Write-Host "SHA256  $hash"
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
