# Windows Install

The Windows installer publishes the app locally, downloads FFmpeg if needed, and registers the app as a Windows service.

Service details:

- service name: `SkyStreamingService`
- display name: `SkyQ Streaming Service`
- startup type: automatic
- default URL: `http://127.0.0.1:5221/`
- optional unauthenticated URL: disabled by default, for example `http://127.0.0.1:5222/`

Quick install from GitHub:

```powershell
$tmp = Join-Path $env:TEMP "install-from-github.ps1"
Invoke-WebRequest https://raw.githubusercontent.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/main/scripts/install-from-github.ps1 -OutFile $tmp
powershell -NoProfile -ExecutionPolicy Bypass -File $tmp
```

Local repo install from the repository root on the target Windows machine:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-windows.ps1
```

Optional second listener with app auth bypassed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-windows.ps1 -UnauthenticatedPort 5222
```

Use that second port only behind your existing edge authentication layer. The normal `-Port` listener keeps the app's built-in authenticator flow.
After install, you can also enable, disable, or change that second listener from the app's `Setup` page. Those listener settings are saved in `local-settings.json` and take effect after the Windows service restarts.

What it does:

- installs the `.NET 10` SDK into `C:\Program Files\dotnet` if a `10.x` SDK is not already present
- downloads the FFmpeg Windows essentials build if `ffmpeg.exe` is not already installed under the app install root
- publishes the app in `Release`
- stops and recreates the Windows service on re-runs
- fully replaces `C:\ProgramData\SkyQStreamingService\app` with the latest published build
- installs a SYSTEM scheduled task so the app can queue GitHub updates
- preserves local app state on re-runs:
  - `local-settings.json`
  - `auth-settings.json`
  - `transcoder-settings.json`
  - `direct-skyq-presets.json`
  - `skyq-cache.json`
  - `runtime\`

Default paths:

```text
Install root: C:\ProgramData\SkyQStreamingService
App directory: C:\ProgramData\SkyQStreamingService\app
FFmpeg directory: C:\ProgramData\SkyQStreamingService\ffmpeg
```

Useful commands:

```powershell
Get-Service SkyStreamingService
Start-Service SkyStreamingService
Stop-Service SkyStreamingService
Restart-Service SkyStreamingService
Get-ScheduledTask SkyQStreamingServiceUpdate
```

Uninstall:

Stop and remove the service only:

```powershell
Stop-Service SkyStreamingService -ErrorAction SilentlyContinue
sc.exe delete SkyStreamingService
Unregister-ScheduledTask -TaskName SkyQStreamingServiceUpdate -Confirm:$false -ErrorAction SilentlyContinue
```

Remove the deployed app, FFmpeg copy, and local runtime state as well:

```powershell
Remove-Item 'C:\ProgramData\SkyQStreamingService' -Recurse -Force
```

Uninstall notes:

- This does not uninstall `C:\Program Files\dotnet` because that SDK/runtime install may be shared with other apps.
- The bundled FFmpeg copy installed by this script lives under `C:\ProgramData\SkyQStreamingService\ffmpeg`, so removing the install root removes that copy too.
- If you want to preserve your presets and auth/runtime files, remove the service but keep `C:\ProgramData\SkyQStreamingService`.

Setup and login:

1. Open `http://127.0.0.1:5221/setup` from the Windows machine itself, your LAN, or over Tailscale.
2. Confirm the detected FFmpeg path and save setup.
3. Enter the email address you want to use for remote access.
4. Generate the QR code and scan it with your authenticator app.
5. External users then sign in at `/auth/login`.

Trusted setup networks:

- `localhost`
- RFC1918 LAN ranges such as `192.168.x.x`
- Tailscale range `100.64.0.0/10`

Cloudflare Zero Trust / Tailscale:

- keep the app private and put Cloudflare Zero Trust or Tailscale in front of it
- first-time authenticator enrollment must still be done from a trusted local or Tailscale network
- for Cloudflare, point the private origin or tunnel at `http://127.0.0.1:5221`
- if you enable `-UnauthenticatedPort`, point your edge at that second port instead and let the edge own authentication

Notes:

- The Windows installer uses the FFmpeg Windows release essentials ZIP from `gyan.dev`.
- The service runs under the built-in `LOCAL SERVICE` account and the installer grants it write access to the app install root.
- Local runtime files remain intentionally outside source control.
