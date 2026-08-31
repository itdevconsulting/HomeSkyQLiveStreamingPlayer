# SkyStreamRemote

Browser-based Sky Stream IR remote assets for an ESP32 running ESPHome from Home Assistant.

The HTTP Sky Stream remote in this repo talks to TCP 8091. That path cannot wake a sleeping puck, and it is easy to leave hanging. This project is the IR fallback: Home Assistant flashes an ESP32, and the ESP32’s web UI loads `sky_remote.js` from this GitHub repository.

## Files

- `sky_remote.js` — remote UI, styles, key clicks, keyboard, TV Guide, Live TV, delay setup, and user macros.
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

The remote locks until the sequence finishes. Each item is either a press or a wait — the delay is its own step, not attached to the IR button.

Defaults (this Sky Stream box):

`Home → wait 5 s → Down → wait 3 s → Down → wait 2 s → OK → wait 2 s → Back → wait 1 s → Down → wait 5 s`

The footer must read `Locked during sequences`. If it does not, the browser still has an old script.

## Setup (delays)

**Setup** opens delays and the macro builder in a panel to the **right** of the remote. Saved macros stay on the **left**. The ESP32 firmware is unchanged: one IR button per key, no sequences, no macros.

Save writes this browser’s overlay. Defaults restores the delay values above (macros are left alone). Clearing site data forgets them. A different phone or a different HA user profile has its own copy.

## Macros

**Setup → Macros** builds quick-access buttons that sit beside the remote. Each step is a key, a wait, **TV Guide** (uses the delay fields above), or another saved macro, so you can chain Guide then extra keys without putting sequences on the ESP32.

Save macro writes to the same `localStorage` object as the delays. Up to 12 macros, 40 steps each. The rail is empty until you save one.

## Live TV

The dropdown uses the same channel list as the Blazor Sky Stream picker (search + category groups). Choosing a channel runs that Guide sequence, then the digits, a wait before OK, OK, a wait, OK. Those waits are the Setup fields for digits / after number / between OKs.

## IR transmitter (ESPHome)

Pinout for the ESP32E-N4 is GPIO21 power (always on) and GPIO4 TX at 50% carrier. There is no IR receiver in this firmware. YAML changes only take effect after you paste them into Home Assistant and install/flash the device. A browser refresh is not enough.
