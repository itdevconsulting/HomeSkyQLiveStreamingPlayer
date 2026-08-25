# Linux Install

For transparency, the short URL and the direct raw GitHub URL are both shown below. The short URL currently redirects to the same installer script.

Quick install using the short URL:

```bash
curl -fsSL https://bit.ly/4naEQhR | sudo bash
```

Quick install using the direct raw GitHub URL:

```bash
curl -fsSL https://raw.githubusercontent.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/main/scripts/install-from-github.sh | sudo bash
```

Alternative one-liners using the short URL:

```bash
wget -qO- https://bit.ly/4naEQhR | sudo bash
```

Alternative one-liners using the direct raw GitHub URL:

```bash
wget -qO- https://raw.githubusercontent.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/main/scripts/install-from-github.sh | sudo bash
```

Local repo install from the repository root on the target Linux machine:

```bash
chmod +x scripts/install-linux.sh
sudo ./scripts/install-linux.sh
```

Optional second listener with app auth bypassed:

```bash
sudo UNAUTHENTICATED_PORT=5222 ./scripts/install-linux.sh
```

Use that second port only behind your existing edge authentication layer. The normal `PORT` listener keeps the app's built-in authenticator flow.
After install, you can also enable, disable, or change that second listener from the app's `Setup` page. Those listener settings are saved in `local-settings.json` and take effect after the service restarts.

What it does:

- clones or updates `https://github.com/itdevconsulting/HomeSkyQLiveStreamingPlayer`
- hard-resets and cleans the source checkout on re-runs so it matches the latest remote branch exactly
- installs FFmpeg if `ffmpeg` is missing
- installs the `.NET 10` SDK if no `10.x` SDK is available
- publishes the app in `Release`
- stops `SkyStreamingService` before replacing the deployed app
- fully replaces `/opt/skystreamingservice/app` with the latest published build
- writes or updates the `SkyStreamingService` systemd service and restarts it
- installs a root updater helper and a systemd path unit so the app can queue GitHub updates
- preserves local app state on re-runs:
  - `local-settings.json`
  - `auth-settings.json`
  - `transcoder-settings.json`
  - `direct-skyq-presets.json`
  - `skyq-cache.json`
  - `sky-stream-cache.json`
  - `runtime/`

Useful commands:

```bash
sudo systemctl status SkyStreamingService
sudo systemctl start SkyStreamingService
sudo systemctl stop SkyStreamingService
sudo systemctl restart SkyStreamingService
sudo journalctl -u SkyStreamingService -f
sudo journalctl -u SkyStreamingService-update -n 100 --no-pager
```

Uninstall:

Stop and remove the service only:

```bash
sudo systemctl stop SkyStreamingService
sudo systemctl disable SkyStreamingService
sudo systemctl stop SkyStreamingService-update.path
sudo systemctl disable SkyStreamingService-update.path
sudo rm -f /etc/systemd/system/SkyStreamingService.service
sudo rm -f /etc/systemd/system/SkyStreamingService-update.service
sudo rm -f /etc/systemd/system/SkyStreamingService-update.path
sudo rm -f /usr/local/sbin/skystreaming-update
sudo systemctl daemon-reload
```

Remove the deployed app and local runtime state as well:

```bash
sudo rm -rf /opt/skystreamingservice
sudo rm -rf /var/lib/skystreamingservice
sudo rm -rf /usr/local/src/homeskyqlivestreamingplayer
sudo userdel skystreamingservice 2>/dev/null || true
sudo groupdel skystreamingservice 2>/dev/null || true
```

Notes:

- This does not uninstall system-wide `.NET` or `ffmpeg` packages because they may be shared with other apps.
- If you want to preserve your local presets and auth/runtime files, skip removing `/opt/skystreamingservice` and `/var/lib/skystreamingservice`.

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
- if you enable `UNAUTHENTICATED_PORT`, point your edge at that second port instead and let the edge own authentication

Source checkout used by the bootstrap installer:

```text
/usr/local/src/homeskyqlivestreamingplayer
```
