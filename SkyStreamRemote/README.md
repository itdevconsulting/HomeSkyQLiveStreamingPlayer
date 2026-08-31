# SkyStreamRemote

Browser-based Sky Stream IR remote assets for an ESP32 running ESPHome from Home Assistant.

The HTTP Sky Stream remote in this repo talks to TCP 8091. That path cannot wake a sleeping puck, and it is easy to leave hanging. This project is the IR fallback: Home Assistant flashes an ESP32, and the ESP32’s web UI loads `sky_remote.js` from this GitHub repository.

## Files

- `sky_remote.js` — remote UI, styles, key clicks, keyboard, TV Guide, Live TV, and macros (including factory TV Guide and Channel select).
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

Factory sequence (this Sky Stream box):

`Home → wait 5 s → Down → wait 3 s → Down → wait 2 s → OK → wait 2 s → Back → wait 1 s → Down → wait 5 s`

That sequence is now a **factory macro** named TV Guide. Setup → Edit lets you change the waits. Reset restores the factory steps. The TV Guide button on the remote (and the chip on the left) always runs the current saved copy.

The footer must read `Locked during sequences`. If it does not, the browser still has an old script.

## Setup

**Setup** opens the macro list to the **right** of the remote. Saved user macros (and TV Guide) stay on the **left**. Channel select is not on the left because Live TV has to supply a channel number. The ESP32 firmware is unchanged: one IR button per key, no sequences, no macros.

Changes are this browser only. Clearing site data forgets them. A different phone or a different HA user profile has its own copy.

## Macros

**Setup** lists **TV Guide** and **Channel select** first. Those are factory macros: Edit, then Save, or **Reset** if the timings get wrecked.

Channel select is Guide, then the Live TV digits (with a gap wait), wait, OK, wait, OK. Live TV always runs this macro.

Your own macros sit below. Each step is a key, a wait, **channel digits**, **TV Guide** (live reference to the factory Guide, not a snapshot), or another saved macro. Up to 12 user macros, 40 steps each. Factory macros do not count toward the 12.

## Live TV

The dropdown uses the same channel list as the Blazor Sky Stream picker (search + category groups). Choosing a channel runs the **Channel select** factory macro, which currently starts with TV Guide, then types the number, waits, OK, waits, OK. Edit that macro (or TV Guide) if your box needs longer gaps. Reset restores the factory timings.

## IR transmitter (ESPHome)

Pinout for the ESP32E-N4 is GPIO21 power (always on) and GPIO4 TX at 50% carrier. There is no IR receiver in this firmware. YAML changes only take effect after you paste them into Home Assistant and install/flash the device. A browser refresh is not enough.
