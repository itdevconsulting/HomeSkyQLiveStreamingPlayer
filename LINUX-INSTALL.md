# Linux Install

Quick install from the public GitHub repo:

```bash
curl -fsSL https://raw.githubusercontent.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/main/scripts/install-from-github.sh | sudo bash
```

Alternative one-liners:

```bash
wget -qO- https://raw.githubusercontent.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/main/scripts/install-from-github.sh | sudo bash
```

Local repo install from the repository root on the target Linux machine:

```bash
chmod +x scripts/install-linux.sh
sudo ./scripts/install-linux.sh
```

What it does:

- clones or updates `https://github.com/itdevconsulting/HomeSkyQLiveStreamingPlayer`
- hard-resets and cleans the source checkout on re-runs so it matches the latest remote branch exactly
- installs FFmpeg if `ffmpeg` is missing
- installs the `.NET 10` SDK if no `10.x` SDK is available
- publishes the app in `Release`
- stops `SkyStreamingService` before replacing the deployed app
- fully replaces `/opt/skystreamingservice/app` with the latest published build
- writes or updates the `SkyStreamingService` systemd service and restarts it
- preserves local app state on re-runs:
  - `local-settings.json`
  - `auth-settings.json`
  - `transcoder-settings.json`
  - `direct-skyq-presets.json`
  - `skyq-cache.json`
  - `runtime/`

Useful commands:

```bash
sudo systemctl status SkyStreamingService
sudo systemctl start SkyStreamingService
sudo systemctl stop SkyStreamingService
sudo systemctl restart SkyStreamingService
sudo journalctl -u SkyStreamingService -f
```

Default app URL:

```text
http://127.0.0.1:5221/
```

Setup and login:

1. Open `http://127.0.0.1:5221/setup` from the server itself, your LAN, or over Tailscale.
2. Confirm the detected FFmpeg path and save setup.
3. Enter the email address you want to use for remote access.
4. Generate the QR code and scan it with your authenticator app.
5. External users then sign in at `/auth/login` with that email and the current TOTP code.

Trusted setup networks:

- `localhost`
- RFC1918 LAN ranges such as `192.168.x.x`
- Tailscale range `100.64.0.0/10`

Cloudflare Zero Trust / Tailscale:

- keep the app private and put Cloudflare Zero Trust or Tailscale in front of it
- first-time authenticator enrollment must still be done from a trusted local/Tailscale network
- for Cloudflare, point the private origin/tunnel at `http://127.0.0.1:5221`

Source checkout used by the bootstrap installer:

```text
/usr/local/src/homeskyqlivestreamingplayer
```
