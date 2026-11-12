# Builds a Store-only MSIX. Identity/publisher MUST match Partner Center.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$PackageIdentityName,
    [Parameter(Mandatory = $true)][string]$PublisherDistinguishedName,
    [Parameter(Mandatory = $true)][string]$PublisherDisplayName,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutputPath = "artifacts/store/IntonacoWallpaper.msix"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "MSIX version must use four numeric parts (example: 2.1.1.0)."
}
if ($PackageIdentityName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{2,49}$') {
    throw "Use the exact Partner Center package Identity Name."
}
if ($PublisherDistinguishedName -notmatch '^CN=') {
    throw "Provide the full Partner Center Publisher DN (CN=...)."
}

$source = (Resolve-Path -LiteralPath $PublishDir).Path
if (-not (Test-Path -LiteralPath (Join-Path $source "Intonaco.exe"))) {
    throw "Expected a self-contained Intonaco.exe in $source."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$iconPath = Join-Path $repoRoot "src/Intonaco/Assets/intonaco.ico"
$stage = Join-Path $env:TEMP ("IntonacoWallpaperMSIX-" + [guid]::NewGuid().ToString("N"))
$outputFull = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path (Split-Path $outputFull -Parent) | Out-Null

function Escape-Xml([string]$value) {
    return [System.Security.SecurityElement]::Escape($value)
}

try {
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item -Path (Join-Path $source "*") -Destination $stage -Recurse -Force
    Copy-Item (Join-Path $repoRoot "LICENSE"), (Join-Path $repoRoot "THIRD-PARTY-NOTICES.md") -Destination $stage
    $assets = Join-Path $stage "Assets"
    New-Item -ItemType Directory -Force -Path $assets | Out-Null

    # Derive every Store tile from the application's genuine Windows logo.
    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Icon]::new($iconPath, [System.Drawing.Size]::new(256, 256))
    $original = $icon.ToBitmap()
    try {
        foreach ($size in @(44, 50, 150)) {
            $bitmap = [System.Drawing.Bitmap]::new($size, $size)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.DrawImage($original, 0, 0, $size, $size)
                $bitmap.Save((Join-Path $assets "Logo$size.png"),
                    [System.Drawing.Imaging.ImageFormat]::Png)
            }
            finally {
                $graphics.Dispose()
                $bitmap.Dispose()
            }
        }
    }
    finally {
        $original.Dispose()
        $icon.Dispose()
    }

    $identity = Escape-Xml $PackageIdentityName
    $publisher = Escape-Xml $PublisherDistinguishedName
    $publisherDisplay = Escape-Xml $PublisherDisplayName

    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap desktop rescap">
  <Identity Name="$identity" Publisher="$publisher"
            Version="$Version" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>Intonaco Wallpaper</DisplayName>
    <PublisherDisplayName>$publisherDisplay</PublisherDisplayName>
    <Description>Native video wallpapers for Windows</Description>
    <Logo>Assets\Logo50.png</Logo>
  </Properties>
  <Resources>
    <Resource Language="en-us" />
  </Resources>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop"
                        MinVersion="10.0.18362.0"
                        MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Applications>
    <Application Id="IntonacoWallpaper"
                 Executable="Intonaco.exe"
                 EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="Intonaco Wallpaper"
                          Description="Native video wallpapers for Windows"
                          BackgroundColor="transparent"
                          Square150x150Logo="Assets\Logo150.png"
                          Square44x44Logo="Assets\Logo44.png" />
      <Extensions>
        <desktop:Extension Category="windows.startupTask"
                           Executable="Intonaco.exe"
                           EntryPoint="Windows.FullTrustApplication">
          <desktop:StartupTask TaskId="IntonacoWallpaperStartup"
                               Enabled="false"
                               DisplayName="Intonaco Wallpaper" />
        </desktop:Extension>
      </Extensions>
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
"@
    [xml]$xml = $manifest
    $xml.Save((Join-Path $stage "AppxManifest.xml"))

    $sdkRoot = Join-Path ([Environment]::GetFolderPath("ProgramFilesX86")) "Windows Kits/10/bin"
    $makeAppx = Get-ChildItem -Path $sdkRoot -Recurse -Filter "makeappx.exe" -File |
        Where-Object { $_.DirectoryName -match '[\\/]x64$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $makeAppx) { throw "Windows SDK makeappx.exe (x64) was not found." }

    & $makeAppx.FullName pack /d $stage /p $outputFull /o
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outputFull)) {
        throw "MSIX packaging or manifest validation failed."
    }

    $file = Get-Item -LiteralPath $outputFull
    Write-Host "MSIX created: $($file.FullName)"
    Write-Host "SHA256: $((Get-FileHash $file.FullName -Algorithm SHA256).Hash)"
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
