# SkyStreamRemote

Browser-based Sky Stream IR remote assets for an ESP32 running ESPHome from Home Assistant.

The HTTP Sky Stream remote in this repo talks to TCP 8091. That path cannot wake a sleeping puck, and it is easy to leave hanging. This project is the IR fallback: Home Assistant flashes an ESP32, and the ESP32’s web UI loads its JS and CSS from this GitHub repository.

## Files

- `sky_remote.js` — remote UI, key clicks, keyboard, and the TV Guide macro.
- `sky_remote.css` — handset styling.
- `esphome/sky-stream-remote.yaml.example` — ESPHome example. Copy it into Home Assistant and point `css_url` / `js_url` at GitHub.
- `SkyStreamRemote.csproj` — Rider project so these assets sit in the same solution as the player.

## GitHub URLs for ESPHome

After these files are on `main`, the ESP32 web page loads them from GitHub at browse time. You do not copy JS/CSS onto the device, and you do not rebuild firmware when you only change the remote look or layout.

Use jsDelivr, not `raw.githubusercontent.com`. GitHub raw serves `text/plain`, so browsers will not apply the CSS.

```yaml
web_server:
  port: 80
  version: 3
  css_url: "https://cdn.jsdelivr.net/gh/itdevconsulting/HomeSkyQLiveStreamingPlayer@main/SkyStreamRemote/sky_remote.css"
  js_url: "https://cdn.jsdelivr.net/gh/itdevconsulting/HomeSkyQLiveStreamingPlayer@main/SkyStreamRemote/sky_remote.js"
```

Those URLs read the files from this repo:

- https://github.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/blob/main/SkyStreamRemote/sky_remote.css
- https://github.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/blob/main/SkyStreamRemote/sky_remote.js

Push to `main`, hard-refresh the ESP32 page (`http://sky-stream-ir.local/`). Firmware only needs flashing again if the YAML itself changed. jsDelivr can lag a few minutes behind `main`.

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

`Home → Down → Down → OK → Back → Down`

Each command waits for the previous HTTP POST to complete, followed by a
250 ms inter-command delay.
