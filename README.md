# Home Sky Q Live Streaming Player

Blazor Server application for securely viewing and controlling a live home Sky Q box over the internet with low latency.

## Recommended Linux Install

For transparency, the short URL and the direct raw GitHub URL are both shown below. The short URL currently redirects to the same installer script.

Short URL:

```bash
curl -fsSL https://bit.ly/4naEQhR | sudo bash
```

Direct raw GitHub URL:

```bash
curl -fsSL https://raw.githubusercontent.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/main/scripts/install-from-github.sh | sudo bash
```

Short URL fallback:

```bash
wget -qO- https://bit.ly/4naEQhR | sudo bash
```

Direct raw GitHub fallback:

```bash
wget -qO- https://raw.githubusercontent.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/main/scripts/install-from-github.sh | sudo bash
```

Detailed Linux deployment notes are in [LINUX-INSTALL.md](LINUX-INSTALL.md).

## What It Does

- discovers Sky Q boxes on the local private network
- sends Sky Q remote-control commands over the LAN control socket
- plays browser-friendly direct streams such as MPEG-TS or HLS
- supports FFmpeg-managed ingest/transcode for awkward HTTP or RTSP sources
- stores presets that bind a stream source to a specific Sky Q box

## Security Model

- trusted setup is limited to localhost, RFC1918 private networks, and Tailscale
- external users authenticate with enrolled TOTP authenticator codes
- the recommended deployment model is to keep the app private and place Tailscale or Cloudflare Zero Trust in front of it

## Linux Installer Summary

The bootstrap installer:

- clones or updates the public repo
- hard-resets and cleans the source checkout on re-runs so it matches the latest remote branch exactly
- installs `ffmpeg` if needed
- installs the `.NET 10` SDK if needed
- publishes the app in `Release`
- stops `SkyStreamingService` before replacing the deployed app
- fully replaces `/opt/skystreamingservice/app` with the latest published build
- writes or updates the `SkyStreamingService` systemd service and restarts it
- preserves local runtime state across upgrades

After install, open:

```text
http://127.0.0.1:5221/setup
```

From there:

1. Confirm the FFmpeg path.
2. Save setup.
3. Enter the email address to enroll for remote access.
4. Generate and scan the authenticator QR code.
5. External users then sign in at `/auth/login`.

## Notes

- FFmpeg is not bundled. Install it locally and save the detected path on the `Setup` page.
- Browser HEVC/H.265 support still varies by browser and platform. `H.264` remains the safer compatibility option.
- Machine-local runtime files such as auth settings, presets, cache data, and transcoder state are intentionally not published in the public repo.
