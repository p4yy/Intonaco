# End-to-end installer smoke test used by CI and the release workflow.
#
# It performs a silent per-user install into a temporary directory, verifies
# shortcuts, launches the installed app, reinstalls over the top, verifies
# that the Start-with-Windows value is removed on uninstall, and confirms
# that files under %LocalAppData%\Intonaco survive uninstall.
#
# Run this only in an isolated account/CI image: it installs and then
# uninstalls the real per-user application and shortcuts.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SetupPath,

    [string]$InstallDir = (Join-Path $env:TEMP "IntonacoInstallerSmoke-$PID"),

    [switch]$SkipLaunch
)

$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-CheckedProcess {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$Description
    )

    Write-Host $Description
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "$Description failed with exit code $($process.ExitCode)."
    }
}

$resolvedSetup = (Resolve-Path -LiteralPath $SetupPath).Path
$installedExe = Join-Path $InstallDir "Intonaco.exe"
$uninstaller = Join-Path $InstallDir "unins000.exe"

$programsFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$desktopFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$startShortcut = Join-Path $programsFolder "Intonaco Wallpaper.lnk"
$desktopShortcut = Join-Path $desktopFolder "Intonaco Wallpaper.lnk"

$settingsDir = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "Intonaco"
$sentinelPath = Join-Path $settingsDir "installer-smoke-sentinel.txt"
$sentinelContent = "preserve-this-file-across-upgrade-and-uninstall"
$hadSentinel = Test-Path -LiteralPath $sentinelPath
$oldSentinelContent = $null
if ($hadSentinel) {
    $oldSentinelContent = Get-Content -LiteralPath $sentinelPath -Raw
}

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$oldRunValue = $null
$hadRunValue = $false
if (Test-Path $runKey) {
    $runProperty = Get-ItemProperty -Path $runKey -Name "Intonaco" -ErrorAction SilentlyContinue
    if ($null -ne $runProperty) {
        $oldRunValue = $runProperty.Intonaco
        $hadRunValue = $null -ne $oldRunValue
    }
}

$appProcess = $null
$uninstallAttempted = $false

try {
    Assert-True (-not (Test-Path -LiteralPath $startShortcut)) `
        "An Intonaco Wallpaper Start Menu shortcut already exists; run this test in an isolated account."

    if (Test-Path -LiteralPath $InstallDir) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null
    Set-Content -LiteralPath $sentinelPath -Value $sentinelContent -NoNewline -Encoding ascii

    $installArguments = @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/SP-",
        "/DIR=`"$InstallDir`"",
        "/TASKS=desktopicon"
    )

    Invoke-CheckedProcess $resolvedSetup $installArguments "Installing Intonaco Wallpaper..."

    Assert-True (Test-Path -LiteralPath $installedExe) "Installed Intonaco.exe was not found."
    Assert-True (Test-Path -LiteralPath $uninstaller) "Installed uninstaller was not found."
    $metadata = (Get-Item -LiteralPath $installedExe).VersionInfo
    Assert-True ($metadata.ProductName -eq "Intonaco Wallpaper") "Windows product name does not match."
    Assert-True ($metadata.CompanyName -eq "Acev Dev Co") "Windows company metadata does not match."
    Assert-True (Test-Path -LiteralPath $startShortcut) "Start Menu shortcut was not created."
    Assert-True (Test-Path -LiteralPath $desktopShortcut) "Optional desktop shortcut was not created."
    Assert-True ((Get-Content -LiteralPath $sentinelPath -Raw) -eq $sentinelContent) `
        "Installer changed the preserved settings sentinel."

    if (-not $SkipLaunch) {
        Write-Host "Launching the installed application..."
        $appProcess = Start-Process -FilePath $installedExe -PassThru
        Start-Sleep -Seconds 5
        $appProcess.Refresh()
        Assert-True (-not $appProcess.HasExited) "Installed application exited during launch."

        Stop-Process -Id $appProcess.Id -Force
        $appProcess.WaitForExit()
        $appProcess = $null
    }

    Invoke-CheckedProcess $resolvedSetup $installArguments "Reinstalling/upgrading Intonaco Wallpaper..."
    Assert-True (Test-Path -LiteralPath $installedExe) "Intonaco.exe was missing after reinstall."
    Assert-True ((Get-Content -LiteralPath $sentinelPath -Raw) -eq $sentinelContent) `
        "Reinstall changed the preserved settings sentinel."

    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name "Intonaco" -Value "`"$installedExe`" --minimized"

    $uninstallAttempted = $true
    Invoke-CheckedProcess $uninstaller @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART"
    ) "Uninstalling Intonaco Wallpaper..."

    # Inno's uninstaller relaunches itself from %TEMP%; wait for file
    # removal rather than trusting only the first process exit.
    $deadline = (Get-Date).AddSeconds(30)
    while ((Test-Path -LiteralPath $installedExe) -and ((Get-Date) -lt $deadline)) {
        Start-Sleep -Milliseconds 250
    }

    Assert-True (-not (Test-Path -LiteralPath $installedExe)) "Intonaco.exe remained after uninstall."
    Assert-True (-not (Test-Path -LiteralPath $startShortcut)) "Start Menu shortcut remained after uninstall."
    Assert-True (-not (Test-Path -LiteralPath $desktopShortcut)) "Desktop shortcut remained after uninstall."
    Assert-True ((Get-Content -LiteralPath $sentinelPath -Raw) -eq $sentinelContent) `
        "Uninstall deleted data from the preserved settings folder."

    $runAfterUninstall = Get-ItemProperty -Path $runKey -Name "Intonaco" -ErrorAction SilentlyContinue
    Assert-True ($null -eq $runAfterUninstall) "Start-with-Windows value remained after uninstall."

    Write-Host "Installer smoke test passed."
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
    }

    if (-not $uninstallAttempted -and (Test-Path -LiteralPath $uninstaller)) {
        try {
            $cleanup = Start-Process -FilePath $uninstaller `
                -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" `
                -Wait -PassThru
            $deadline = (Get-Date).AddSeconds(30)
            while ((Test-Path -LiteralPath $installedExe) -and ((Get-Date) -lt $deadline)) {
                Start-Sleep -Milliseconds 250
            }
        }
        catch {
            Write-Warning "Installer test cleanup could not run the uninstaller: $_"
        }
    }

    if (Test-Path -LiteralPath $InstallDir) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($hadSentinel) {
        Set-Content -LiteralPath $sentinelPath -Value $oldSentinelContent -NoNewline -Encoding ascii
    }
    elseif (Test-Path -LiteralPath $sentinelPath) {
        Remove-Item -LiteralPath $sentinelPath -Force -ErrorAction SilentlyContinue
    }

    if ($hadRunValue) {
        New-Item -Path $runKey -Force | Out-Null
        Set-ItemProperty -Path $runKey -Name "Intonaco" -Value $oldRunValue
    }
    elseif (Test-Path $runKey) {
        Remove-ItemProperty -Path $runKey -Name "Intonaco" -ErrorAction SilentlyContinue
    }
}
