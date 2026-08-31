# SkyStreamRemote

Browser-based Sky Stream IR remote assets for an ESP32 running ESPHome from Home Assistant.

The HTTP Sky Stream remote in this repo talks to TCP 8091. That path cannot wake a sleeping puck, and it is easy to leave hanging. This project is the IR fallback: Home Assistant flashes an ESP32, and the ESP32’s web UI loads `sky_remote.js` from this GitHub repository.

## Files

- `sky_remote.js` — remote UI, styles, key clicks, keyboard, TV Guide, and Live TV channel picker.
- `sky_remote.loader.js` / `sky_remote.boot.js` — what ESPHome’s `js_url` loads; they then fetch `sky_remote.js` from GitHub. Use `boot.js` in firmware so jsDelivr cannot keep an old cached loader.
- `esphome/sky-stream-remote.yaml.example` — ESPHome example. Copy it into Home Assistant.
- `SkyStreamRemote.csproj` — Rider project so these assets sit in the same solution as the player.

## GitHub URLs for ESPHome

Do not put this in `js_url`:

`https://raw.githubusercontent.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/main/SkyStreamRemote/sky_remote.js`

GitHub serves that as `text/plain` with `nosniff`. The browser will not run it as a script. That is the file the **loader** fetches with `fetch()` (plain text is fine there), then injects as JavaScript.

```yaml
web_server:
  port: 80
  version: 3
  css_url: ""
  js_url: "https://cdn.jsdelivr.net/gh/itdevconsulting/HomeSkyQLiveStreamingPlayer@main/SkyStreamRemote/sky_remote.boot.js"
```
## Keyboard

- Arrow keys: navigation
- Enter: OK
- Home: Home
- Escape / Backspace: Back
- Space: Play/Pause
- `+` / `=`: Volume Up
- `-`: Volume Down
- `M`: Mute
- `0`–`9`: send digit immediately

## TV Guide

TV Guide is driven in `sky_remote.js` only. YAML has one IR button per key. There is no `sky_stream_tv_guide` button.

The remote locks until the sequence finishes: send a key, wait until that HTTP press has fully finished, then count down 3 s after that key before the next one. The caption shows `After home · 3.0s then down` so the wait is visibly after the command, not before it.

`Home → 3 s → Down → 3 s → Down → 3 s → OK → 3 s → Back → 3 s → Down`

The footer must read `Locked during sequences`. If it does not, the browser still has an old script.

## Live TV

The dropdown uses the same channel list as the Blazor Sky Stream picker (search + category groups). Choosing a channel runs that same locked sequence, then a 3 s bar, then the channel digits with 500 ms bars, then OK, OK.

## IR transmitter (ESPHome)

Pinout for the ESP32E-N4 is GPIO21 power (always on) and GPIO4 TX at 50% carrier. There is no IR receiver in this firmware. YAML changes only take effect after you paste them into Home Assistant and install/flash the device. A browser refresh is not enough.
