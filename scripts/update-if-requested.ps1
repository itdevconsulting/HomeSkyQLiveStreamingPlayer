[CmdletBinding()]
param(
    [string]$InstallRoot = $PSScriptRoot,
    [string]$RepoOwner = "itdevconsulting",
    [string]$RepoName = "HomeSkyQLiveStreamingPlayer"
)

$ErrorActionPreference = "Stop"
$flagPath = Join-Path $InstallRoot "update.request"
$logPath = Join-Path $InstallRoot "update.log"
$lockPath = Join-Path $InstallRoot "update.lock"

function Write-UpdateLog {
    param([string]$Message)
    $line = "[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $Message
    Add-Content -Path $logPath -Value $line
}

if (-not (Test-Path $flagPath)) {
    exit 0
}

if (Test-Path $lockPath) {
    $lockAge = (Get-Date) - (Get-Item $lockPath).LastWriteTime
    if ($lockAge.TotalHours -lt 2) {
        exit 0
    }
}

New-Item -ItemType File -Path $lockPath -Force | Out-Null
Remove-Item $flagPath -Force -ErrorAction SilentlyContinue

try {
    Write-UpdateLog "Starting GitHub update"
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("skyq-update-" + [Guid]::NewGuid().ToString("N") + ".ps1")
    $installerUrl = "https://raw.githubusercontent.com/$RepoOwner/$RepoName/main/scripts/install-from-github.ps1"
    Invoke-WebRequest -Uri $installerUrl -OutFile $tmp -UseBasicParsing
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $tmp -InstallRoot $InstallRoot
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub installer exited with code $LASTEXITCODE."
    }

    Write-UpdateLog "GitHub update finished"
}
catch {
    Write-UpdateLog "GitHub update failed: $($_.Exception.Message)"
    throw
}
finally {
    Remove-Item $lockPath -Force -ErrorAction SilentlyContinue
}
