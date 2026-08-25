# Docker and Home Assistant

This app can run as a Linux container. That is the practical way to use it from Home Assistant OS or Supervised.

FFmpeg is included in the image. Settings, authenticator enrolment, presets, and transcode output are stored in `/data` so they survive image rebuilds.

Sky Q discovery and remote control need to see your LAN, so **host networking is required**. Docker Desktop on macOS cannot do that the same way; use a Linux host, Home Assistant OS, or a Linux VM.

Sky Stream remote is stricter: the container host must have a NIC on the puck’s subnet. TCP 8091 does not work over a Tailscale subnet route that only forwards ping. Put Tailscale in front of the web UI, not between this container and the puck.

Container installs are updated by pulling a newer image or rebuilding the add-on. The in-app GitHub installer path is for systemd/Windows installs only.

## Docker Compose

From a checkout of this repo:

```bash
docker compose up -d --build
```

Then open:

```text
http://127.0.0.1:5221/setup
```

The compose file uses `network_mode: host` and a named volume for `/data`.

To rebuild after a git pull:

```bash
docker compose up -d --build
```

## Plain Docker

```bash
docker build -t homeskyq-livestreamingplayer .
docker run -d --name skyq-player --restart unless-stopped --network host \
  -e SKYQ_DATA_DIR=/data \
  -v skyq-data:/data \
  homeskyq-livestreamingplayer
```

## Home Assistant add-on

Home Assistant OS and Supervised can install this repo as a custom add-on store.

1. Go to **Settings → Add-ons → Add-on Store**.
2. Open the three-dot menu and choose **Repositories**.
3. Add `https://github.com/itdevconsulting/HomeSkyQLiveStreamingPlayer`.
4. Install **Home Sky Q Player**.
5. Start the add-on, then use **Open Web UI**.

The first install builds from source and can take several minutes. Later rebuilds are faster once Docker has cached the .NET and FFmpeg layers.

The add-on:

- uses host networking so Sky Q scan and remote commands reach boxes on your LAN
- listens on port `5221` by default
- keeps runtime state in the add-on `/data` directory
- ships FFmpeg, so Setup should already show `/usr/bin/ffmpeg`

This is a Blazor Server app, so it does **not** use Home Assistant Ingress. Open the web UI on port 5221, or add a sidebar iframe:

```yaml
panel_iframe:
  skyq:
    title: Sky Q
    icon: mdi:television
    url: http://homeassistant.local:5221
    require_admin: true
```

Use the hostname or IP you already use to reach Home Assistant on your LAN or Tailscale.

## Home Assistant with Portainer / Docker

If you already run containers on the Home Assistant host, the compose file above is enough. You do not also need the add-on.

Keep `--network host` (or `network_mode: host`). Bridge NAT will usually break Sky Q discovery even if the web UI still loads.

## After first start

1. Open `/setup`.
2. Confirm the FFmpeg path.
3. Save setup.
4. Enrol an authenticator for remote access if you will reach the player from outside the trusted LAN.
5. Create a stream preset and point it at your encoder.

## Notes

- Port `5221` must be free on the host.
- The encoder URL in a preset should be a LAN address the container can reach. With host networking, `192.168.x.x` encoder URLs work the same as on a normal Linux install.
- Do not publish this container to the public internet. Put Tailscale or Cloudflare Zero Trust in front, the same as the native install.
- `linux/amd64` and `linux/arm64` both work with the Microsoft .NET 10 images. Raspberry Pi 4/5 class Home Assistant hardware is `aarch64`.
