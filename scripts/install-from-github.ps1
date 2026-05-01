[CmdletBinding()]
param(
    [string]$RepoOwner = "itdevconsulting",
    [string]$RepoName = "HomeSkyQLiveStreamingPlayer",
    [string]$RepoBranch = "main",
    [string]$InstallRoot = "$env:ProgramData\SkyQStreamingService",
    [string]$DotnetRoot = "${env:ProgramFiles}\dotnet",
    [int]$Port = 5221,
    [string]$DotnetChannel = "10.0",
    [string]$DotnetQuality = "GA",
    [string]$FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
)

$ErrorActionPreference = "Stop"

function Write-Log {
    param([string]$Message)
    Write-Host "`n[HomeSkyQLiveStreamingPlayer] $Message"
}

function Invoke-Download {
    param(
        [string]$Url,
        [string]$Destination
    )

    Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("skyq-install-" + [Guid]::NewGuid().ToString("N"))
$archivePath = Join-Path $tempRoot "repo.zip"
$extractPath = Join-Path $tempRoot "repo"

try {
    New-Item -ItemType Directory -Path $extractPath -Force | Out-Null

    $zipUrl = "https://github.com/$RepoOwner/$RepoName/archive/refs/heads/$RepoBranch.zip"
    Write-Log "Downloading $RepoName ($RepoBranch)"
    Invoke-Download -Url $zipUrl -Destination $archivePath

    Write-Log "Extracting repository archive"
    Expand-Archive -Path $archivePath -DestinationPath $extractPath -Force

    $repoRoot = Get-ChildItem -Path $extractPath -Directory | Select-Object -First 1
    if (-not $repoRoot) {
        throw "Repository archive did not contain an extracted root directory."
    }

    $installer = Join-Path $repoRoot.FullName "scripts\install-windows.ps1"
    if (-not (Test-Path $installer)) {
        throw "Windows installer script was not found at $installer."
    }

    Write-Log "Launching Windows installer"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer `
        -InstallRoot $InstallRoot `
        -DotnetRoot $DotnetRoot `
        -Port $Port `
        -DotnetChannel $DotnetChannel `
        -DotnetQuality $DotnetQuality `
        -FfmpegZipUrl $FfmpegZipUrl
}
finally {
    Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
