# SkyStreamRemote

Browser-based Sky Stream IR remote assets for an ESP32 running ESPHome from Home Assistant.

The HTTP Sky Stream remote in this repo talks to TCP 8091. That path cannot wake a sleeping puck, and it is easy to leave hanging. This project is the IR fallback: Home Assistant flashes an ESP32, and the ESP32’s web UI loads `sky_remote.js` from this GitHub repository.

## Files

- `sky_remote.js` — remote UI, styles, key clicks, keyboard, and TV Guide.
- `sky_remote.loader.js` — what ESPHome’s `js_url` loads; it then fetches `sky_remote.js` from GitHub.
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
  js_url: "https://cdn.jsdelivr.net/gh/itdevconsulting/HomeSkyQLiveStreamingPlayer@main/SkyStreamRemote/sky_remote.loader.js"
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

## TV Guide macro

The ESP32 fires the whole TV Guide burst on one HTTP press. Browser timers cannot space those IR codes. `sky_stream_tv_guide` now transmits Home, Down, Down, OK, Back, Down on the device with a **2 second** `delay` after each blast. Nested `button.press` does not wait, which is why it raced.

You must copy `esphome/sky-stream-remote.yaml.example` into Home Assistant and flash the ESP32 once.

`Home → 2 s → Down → 2 s → Down → 2 s → OK → 2 s → Back → 2 s → Down`
