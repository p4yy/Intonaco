# Builds the Inno Setup installer from an already-published application.
# Usage:
#   .\installer\Build-Installer.ps1 -PublishDir .\publish\installer-app -Version 1.3.0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$OutputDir = "release"
)

$ErrorActionPreference = "Stop"

function Find-InnoCompiler {
    $candidates = @()

    $fromPath = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($fromPath) {
        $candidates += $fromPath.Source
    }

    if (${env:ProgramFiles(x86)}) {
        $candidates += (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
    }
    if ($env:ProgramFiles) {
        $candidates += (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    }
    if ($env:ChocolateyInstall) {
        $candidates += (Join-Path $env:ChocolateyInstall "bin\ISCC.exe")
        $candidates += (Join-Path $env:ChocolateyInstall "lib\innosetup\tools\ISCC.exe")
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    return $null
}

$publishPath = (Resolve-Path -LiteralPath $PublishDir).Path
$appExe = Join-Path $publishPath "Intonaco.exe"
if (-not (Test-Path -LiteralPath $appExe)) {
    throw "Published application not found: $appExe"
}

if (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path (Get-Location).Path $OutputDir
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$outputPath = (Resolve-Path -LiteralPath $OutputDir).Path

$iscc = Find-InnoCompiler
if (-not $iscc) {
    $choco = Get-Command "choco.exe" -ErrorAction SilentlyContinue
    if ($choco) {
        Write-Host "Inno Setup was not found; installing it with Chocolatey..."
        & $choco.Source install innosetup -y --no-progress
        if ($LASTEXITCODE -ne 0) {
            throw "Chocolatey could not install Inno Setup."
        }
        $iscc = Find-InnoCompiler
    }
}

if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) was not found. Install Inno Setup and try again."
}

# Generate both wizard images from the exact app icon on each clean build.
# BMP works with every supported Inno Setup 6 compiler, including CI.
& (Join-Path $PSScriptRoot "Generate-Branding.ps1")
# The script uses PowerShell/.NET only; there is no native exit code.
# A terminating PowerShell error is automatically promoted by Stop.

$scriptPath = Join-Path $PSScriptRoot "Intonaco.iss"
Write-Host "Building installer with $iscc"
Write-Host "  Payload: $publishPath"
Write-Host "  Version: $Version"

$compilerOutput = & $iscc `
    "/DPublishDir=$publishPath" `
    "/DMyAppVersion=$Version" `
    "/DOutputDir=$outputPath" `
    $scriptPath 2>&1
$compilerExitCode = $LASTEXITCODE
$compilerOutput | Out-Host

if ($compilerExitCode -ne 0) {
    $detail = ($compilerOutput | Out-String).Trim()
    throw "Inno Setup compilation failed with exit code $compilerExitCode.`n$detail"
}

$setupPath = Join-Path $outputPath "Intonaco-Wallpaper-v$Version-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "Installer was not created: $setupPath"
}

$setup = Get-Item -LiteralPath $setupPath
$sizeMB = [math]::Round($setup.Length / 1MB, 2)
Write-Host "Installer created: $setupPath ($sizeMB MB)"

return $setupPath
