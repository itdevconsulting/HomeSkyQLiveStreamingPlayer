# SkyStreamRemote

Browser-based Sky Stream IR remote assets for an ESP32 running ESPHome from Home Assistant.

The HTTP Sky Stream remote in this repo talks to TCP 8091. That path cannot wake a sleeping puck, and it is easy to leave hanging. This project is the IR fallback: Home Assistant flashes an ESP32, and the ESP32’s web UI loads `sky_remote.js` from this GitHub repository.

## Files

- `sky_remote.js` — remote UI, styles, key clicks, keyboard, and the TV Guide macro.
- `sky_remote.loader.js` — fetched by ESPHome; pulls `sky_remote.js` from GitHub on every page load.
- `esphome/sky-stream-remote.yaml.example` — ESPHome example. Copy it into Home Assistant.
- `SkyStreamRemote.csproj` — Rider project so these assets sit in the same solution as the player.

## GitHub URLs for ESPHome

The firmware only stores a tiny loader URL. On every visit to `http://sky-stream-ir.local/`, that loader fetches `sky_remote.js` from GitHub `main` with `cache: no-store`. A refresh picks up a JS push without flashing the ESP32 again.

```yaml
web_server:
  port: 80
  version: 3
  css_url: ""
  js_url: "https://cdn.jsdelivr.net/gh/itdevconsulting/HomeSkyQLiveStreamingPlayer@main/SkyStreamRemote/sky_remote.loader.js"
```

Flash once after this YAML change. After that, push JS to `main` and refresh the ESP32 page.

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

The TV Guide button sends, strictly in sequence:

`Home → 250 ms → Down → 250 ms → Down → 250 ms → OK → 250 ms → Back → 250 ms → Down`

After you flash the example YAML, that timing runs on the ESP32 so the IR blasts cannot bunch up. Until then, the page walks the same steps in the browser and waits 250 ms after each HTTP press.
