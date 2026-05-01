[CmdletBinding()]
param(
    [string]$InstallRoot = "$env:ProgramData\SkyQStreamingService",
    [string]$DotnetRoot = "${env:ProgramFiles}\dotnet",
    [int]$Port = 5221,
    [string]$DotnetChannel = "10.0",
    [string]$DotnetQuality = "GA",
    [string]$ServiceName = "SkyStreamingService",
    [string]$ServiceDisplayName = "SkyQ Streaming Service",
    [string]$FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
    [string]$ProjectFile = ""
)

$ErrorActionPreference = "Stop"

$script:AppName = $ServiceName
$script:ProjectFile = if ([string]::IsNullOrWhiteSpace($ProjectFile)) {
    Join-Path $PSScriptRoot "..\H265Player\H265Player.csproj"
} else {
    $ProjectFile
}
$script:ProjectFile = (Resolve-Path $script:ProjectFile).Path
$script:AppDir = Join-Path $InstallRoot "app"
$script:FfmpegDir = Join-Path $InstallRoot "ffmpeg"
$script:RuntimeDir = Join-Path $script:AppDir "runtime"
$script:DotnetExe = Join-Path $DotnetRoot "dotnet.exe"
$script:PublishTemp = Join-Path ([System.IO.Path]::GetTempPath()) ("skyq-publish-" + [Guid]::NewGuid().ToString("N"))
$script:BackupTemp = Join-Path ([System.IO.Path]::GetTempPath()) ("skyq-backup-" + [Guid]::NewGuid().ToString("N"))

function Write-Log {
    param([string]$Message)
    Write-Host "`n[$script:AppName] $Message"
}

function Fail {
    param([string]$Message)
    throw "[$script:AppName] $Message"
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Ensure-Administrator {
    if (-not (Test-IsAdministrator)) {
        Fail "Run this script from an elevated PowerShell session."
    }
}

function Ensure-Path {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Invoke-Download {
    param(
        [string]$Url,
        [string]$Destination
    )

    Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing
}

function Ensure-DotnetSdk {
    if (Test-Path $script:DotnetExe) {
        $sdks = & $script:DotnetExe --list-sdks 2>$null
        if ($sdks | Select-String -Pattern '^10\.') {
            Write-Log ".NET 10 SDK already present"
            return
        }
    }

    Write-Log "Installing .NET $DotnetChannel SDK into $DotnetRoot"

    Ensure-Path $DotnetRoot

    $installerPath = Join-Path ([System.IO.Path]::GetTempPath()) ("dotnet-install-" + [Guid]::NewGuid().ToString("N") + ".ps1")
    Invoke-Download -Url "https://dot.net/v1/dotnet-install.ps1" -Destination $installerPath

    try {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installerPath `
            -Channel $DotnetChannel `
            -Quality $DotnetQuality `
            -InstallDir $DotnetRoot `
            -Version latest | Out-Host
    }
    finally {
        Remove-Item $installerPath -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path $script:DotnetExe)) {
        Fail "dotnet.exe was not installed to $DotnetRoot."
    }

    $installedSdks = & $script:DotnetExe --list-sdks
    if (-not ($installedSdks | Select-String -Pattern '^10\.')) {
        Fail ".NET 10 SDK install did not complete correctly."
    }
}

function Get-InstalledFfmpegPath {
    if (-not (Test-Path $script:FfmpegDir)) {
        return $null
    }

    $ffmpeg = Get-ChildItem -Path $script:FfmpegDir -Filter ffmpeg.exe -File -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if ($ffmpeg) {
        return $ffmpeg.FullName
    }

    return $null
}

function Ensure-Ffmpeg {
    $existing = Get-InstalledFfmpegPath
    if ($existing) {
        Write-Log "FFmpeg already present at $existing"
        return $existing
    }

    Write-Log "Downloading FFmpeg release build"

    $archivePath = Join-Path ([System.IO.Path]::GetTempPath()) ("ffmpeg-" + [Guid]::NewGuid().ToString("N") + ".zip")
    $extractPath = Join-Path ([System.IO.Path]::GetTempPath()) ("ffmpeg-" + [Guid]::NewGuid().ToString("N"))

    try {
        Invoke-Download -Url $FfmpegZipUrl -Destination $archivePath
        Expand-Archive -Path $archivePath -DestinationPath $extractPath -Force

        $ffmpegExe = Get-ChildItem -Path $extractPath -Filter ffmpeg.exe -File -Recurse -ErrorAction Stop |
            Select-Object -First 1
        if (-not $ffmpegExe) {
            Fail "ffmpeg.exe was not found in the downloaded archive."
        }

        $ffmpegRoot = Split-Path -Path (Split-Path -Path $ffmpegExe.FullName -Parent) -Parent
        if (-not $ffmpegRoot) {
            Fail "Unable to determine FFmpeg extraction root."
        }

        if (Test-Path $script:FfmpegDir) {
            Remove-Item $script:FfmpegDir -Recurse -Force
        }

        Ensure-Path $script:FfmpegDir
        Copy-Item -Path (Join-Path $ffmpegRoot "*") -Destination $script:FfmpegDir -Recurse -Force
    }
    finally {
        Remove-Item $archivePath -Force -ErrorAction SilentlyContinue
        Remove-Item $extractPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    $installed = Get-InstalledFfmpegPath
    if (-not $installed) {
        Fail "FFmpeg was downloaded but ffmpeg.exe is still missing."
    }

    return $installed
}

function Backup-ExistingState {
    Ensure-Path $script:BackupTemp

    if (-not (Test-Path $script:AppDir)) {
        return
    }

    foreach ($entry in @(
        "local-settings.json",
        "auth-settings.json",
        "transcoder-settings.json",
        "direct-skyq-presets.json",
        "skyq-cache.json",
        "runtime"
    )) {
        $source = Join-Path $script:AppDir $entry
        if (Test-Path $source) {
            Copy-Item -Path $source -Destination $script:BackupTemp -Recurse -Force
        }
    }
}

function Seed-LocalSettings {
    param([string]$FfmpegPath)

    $localSettingsPath = Join-Path $script:AppDir "local-settings.json"
    if (Test-Path $localSettingsPath) {
        return
    }

    $payload = [ordered]@{
        FfmpegPath          = $FfmpegPath
        DefaultHttpStreamUrl = ""
        DefaultRtspStreamUrl = ""
    } | ConvertTo-Json

    Set-Content -Path $localSettingsPath -Value $payload -Encoding UTF8
}

function Deploy-App {
    param([string]$FfmpegPath)

    Backup-ExistingState

    if (Test-Path $script:AppDir) {
        Remove-Item $script:AppDir -Recurse -Force
    }

    Ensure-Path $script:AppDir
    Copy-Item -Path (Join-Path $script:PublishTemp "*") -Destination $script:AppDir -Recurse -Force

    foreach ($entry in Get-ChildItem -Path $script:BackupTemp -Force -ErrorAction SilentlyContinue) {
        Copy-Item -Path $entry.FullName -Destination $script:AppDir -Recurse -Force
    }

    Ensure-Path (Join-Path $script:RuntimeDir "live")
    Seed-LocalSettings -FfmpegPath $FfmpegPath
}

function Grant-ServiceAccess {
    Write-Log "Granting LOCAL SERVICE access to $InstallRoot"
    & icacls.exe $InstallRoot /grant "*S-1-5-19:(OI)(CI)(M)" /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Fail "Failed to update ACLs under $InstallRoot."
    }
}

function Stop-ExistingService {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $service) {
        return
    }

    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Log "Stopping existing service"
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(20))
    }
}

function Invoke-Sc {
    param([string[]]$Arguments)

    & sc.exe @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Fail "sc.exe $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Remove-ExistingService {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $service) {
        return
    }

    Write-Log "Removing existing service definition"
    Invoke-Sc -Arguments @("delete", $ServiceName)

    for ($i = 0; $i -lt 20; $i++) {
        if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
            return
        }

        Start-Sleep -Seconds 1
    }

    Fail "Timed out waiting for the old service definition to be deleted."
}

function Create-Service {
    $appExe = Join-Path $script:AppDir "H265Player.exe"
    if (-not (Test-Path $appExe)) {
        Fail "Published app host not found at $appExe."
    }

    $binaryPath = "`"$appExe`" --urls `"http://0.0.0.0:$Port`""
    $serviceAccount = "NT AUTHORITY\LocalService"

    Write-Log "Creating Windows service $ServiceDisplayName"
    Invoke-Sc -Arguments @(
        "create", $ServiceName,
        "binPath=", $binaryPath,
        "start=", "auto",
        "DisplayName=", $ServiceDisplayName,
        "obj=", $serviceAccount
    )

    Invoke-Sc -Arguments @("description", $ServiceName, "Home Sky Q Live Streaming Player")
    Invoke-Sc -Arguments @("failure", $ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/5000")
}

function Start-ServiceAndWait {
    Write-Log "Starting service"
    Start-Service -Name $ServiceName
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(20))
}

function Get-LanAddresses {
    $addresses = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object {
            $_.IPAddress -notlike "127.*" -and
            $_.PrefixOrigin -ne "WellKnown"
        } |
        Select-Object -ExpandProperty IPAddress -Unique

    return @($addresses)
}

function Print-Summary {
    $lanAddresses = Get-LanAddresses

    Write-Host ""
    Write-Host "$ServiceDisplayName is installed and running."
    Write-Host ""
    Write-Host "Service management:"
    Write-Host "  Get-Service $ServiceName"
    Write-Host "  Start-Service $ServiceName"
    Write-Host "  Stop-Service $ServiceName"
    Write-Host "  Restart-Service $ServiceName"
    Write-Host ""
    Write-Host "Application paths:"
    Write-Host "  Install root: $InstallRoot"
    Write-Host "  App directory: $script:AppDir"
    Write-Host "  FFmpeg path:   $(Get-InstalledFfmpegPath)"
    Write-Host "  Project file:  $script:ProjectFile"
    Write-Host ""
    Write-Host "Useful URLs:"
    Write-Host "  Local: http://127.0.0.1:$Port/"
    Write-Host "  Setup: http://127.0.0.1:$Port/setup"
    Write-Host "  Login: http://127.0.0.1:$Port/auth/login"

    foreach ($address in $lanAddresses) {
        Write-Host "  LAN:   http://$address`:$Port/"
    }

    Write-Host ""
    Write-Host "The first setup run still needs to happen from localhost, your LAN, or over Tailscale."
}

try {
    Ensure-Administrator
    Ensure-Path $script:PublishTemp
    Ensure-Path $script:BackupTemp
    Ensure-Path $InstallRoot

    Write-Log "Installing .NET SDK prerequisites"
    Ensure-DotnetSdk

    Write-Log "Ensuring FFmpeg is available"
    $ffmpegPath = Ensure-Ffmpeg

    Write-Log "Publishing application"
    & $script:DotnetExe publish $script:ProjectFile -c Release -o $script:PublishTemp --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Fail "dotnet publish failed."
    }

    Stop-ExistingService

    Write-Log "Deploying application"
    Deploy-App -FfmpegPath $ffmpegPath

    Grant-ServiceAccess
    Remove-ExistingService
    Create-Service
    Start-ServiceAndWait
    Print-Summary
}
finally {
    Remove-Item $script:PublishTemp -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $script:BackupTemp -Recurse -Force -ErrorAction SilentlyContinue
}
