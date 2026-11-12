# Wrap a validated, unsigned Store-identity MSIX in the preferred
# Microsoft Store submission container (.msixupload). This is a ZIP with
# the MSIX at its root, as documented by Microsoft. No signing or upload
# to Partner Center is performed here.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$MsixPath,
    [Parameter(Mandatory = $true)][string]$ExpectedIdentityName,
    [Parameter(Mandatory = $true)][string]$ExpectedPublisherDn,
    [Parameter(Mandatory = $true)][string]$ExpectedPublisherDisplayName
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$source = (Resolve-Path -LiteralPath $MsixPath).Path
$package = Get-Item -LiteralPath $source
if ($package.Extension -ne '.msix') { throw "Expected an .msix file: $source" }
if ($package.Length -lt 1024) { throw "MSIX file is unexpectedly small." }

# Read the package's actual manifest from the generated MSIX, not a
# duplicated template or a file left behind in temporary staging.
$zip = [System.IO.Compression.ZipFile]::OpenRead($source)
try {
    $entry = $zip.GetEntry('AppxManifest.xml')
    if ($null -eq $entry) { throw 'MSIX does not contain AppxManifest.xml.' }
    $stream = $entry.Open()
    $reader = [System.IO.StreamReader]::new($stream)
    try {
        [xml]$manifest = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
    $identity = $manifest.Package.Identity
    $property = $manifest.Package.Properties
    $app = $manifest.Package.Applications.Application
    $visual = @($app.ChildNodes | Where-Object { $_.LocalName -eq 'VisualElements' }) | Select-Object -First 1
    if ($null -eq $visual) { throw 'MSIX application has no VisualElements.' }
    $entryNames = @($zip.Entries | ForEach-Object { $_.FullName.Replace([char]92, [char]47) })
    $actual = @{
        Name = [string]$identity.Name
        Publisher = [string]$identity.Publisher
        PublisherDisplay = [string]$property.PublisherDisplayName
        ProductDisplay = [string]$property.DisplayName
        AppDisplay = [string]$visual.GetAttribute('DisplayName')
    }

    if ($actual.Name -cne $ExpectedIdentityName) {
        throw "MSIX identity mismatch: $($actual.Name)"
    }
    if ($actual.Publisher -cne $ExpectedPublisherDn) {
        throw "MSIX publisher DN mismatch: $($actual.Publisher)"
    }
    if ($actual.PublisherDisplay -cne $ExpectedPublisherDisplayName) {
        throw "MSIX publisher display mismatch: $($actual.PublisherDisplay)"
    }
    if ($actual.ProductDisplay -cne 'Intonaco Wallpaper' -or
        $actual.AppDisplay -cne 'Intonaco Wallpaper') {
        throw 'MSIX product display name must be Intonaco Wallpaper.'
    }
    if ([string]$app.Executable -cne 'Intonaco.exe') {
        throw 'MSIX application executable is not the intended desktop app.'
    }
    if ('Intonaco.exe' -cnotin $entryNames) {
        throw 'MSIX is missing its Intonaco.exe payload.'
    }
    foreach ($logo in @('Assets/Logo44.png', 'Assets/Logo150.png',
                          'Assets/Logo50.png')) {
        if ($logo -cnotin $entryNames) {
            throw "MSIX is missing brand asset: $logo"
        }
    }
    Write-Host "Store manifest verified: $($actual.Name)"
    Write-Host "Publisher: $($actual.Publisher) / $($actual.PublisherDisplay)"
}
finally {
    $zip.Dispose()
}

$output = [System.IO.Path]::ChangeExtension($source, '.msixupload')
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
$tempZip = $output + '.zip'
try {
    Compress-Archive -LiteralPath $source -DestinationPath $tempZip -CompressionLevel NoCompression -Force
    Move-Item -LiteralPath $tempZip -Destination $output -Force

    $upload = [System.IO.Compression.ZipFile]::OpenRead($output)
    try {
        if ($upload.Entries.Count -ne 1 -or $upload.Entries[0].Name -cne $package.Name) {
            throw 'Store upload container must include exactly one MSIX at its root.'
        }
    }
    finally {
        $upload.Dispose()
    }
    Write-Host "Store upload container prepared: $output"
    Write-Host "SHA256: $((Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash)"
}
finally {
    if (Test-Path -LiteralPath $tempZip) {
        Remove-Item -LiteralPath $tempZip -Force
    }
}
