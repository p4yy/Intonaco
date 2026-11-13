# Builds the original Intonaco.Video native renderer with the Windows SDK.
# Produces only a DLL shipped inside the normal Intonaco installer/portable EXE.
# No MPV, VLC, external service, downloads or redistribution of Lively code.
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$source = Join-Path $root "native/IntonacoVideo"
$build = Join-Path $root "native/IntonacoVideo/build"
$target = Join-Path $root "src/Intonaco/native/Intonaco.Video.dll"

& cmake -S $source -B $build -A x64
if ($LASTEXITCODE -ne 0) { throw "Native video CMake configure failed." }
& cmake --build $build --config $Configuration --parallel 2
if ($LASTEXITCODE -ne 0) { throw "Native video compilation failed." }

$compiled = Join-Path $build "$Configuration/Intonaco.Video.dll"
if (-not (Test-Path -LiteralPath $compiled)) {
    throw "Expected native video DLL missing: $compiled"
}
New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
Copy-Item -LiteralPath $compiled -Destination $target -Force
Write-Host "Built in-process hardware video renderer: $target"
