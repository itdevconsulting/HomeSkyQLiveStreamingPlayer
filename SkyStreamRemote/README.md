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

The page sends one key, then waits 2 seconds on a visible countdown, then sends the next:

`Home → 2 s → Down → 2 s → Down → 2 s → OK → 2 s → Back → 2 s → Down`

The footer must read `2s gap after every key`. If it does not, the browser still has an old script.

## Live TV

The dropdown uses the same channel list as the Blazor Sky Stream picker (search + category groups). Choosing a channel runs the existing TV Guide sequence, waits 2 seconds after the last Guide key (that wait is in the Live TV path, not inside TV Guide), types the channel digits with 250 ms between numbers, then OK, OK.
