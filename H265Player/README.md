# Home Sky Q Live Streaming Player

Blazor Server application for securely viewing and controlling a live home Sky Q box over the internet with low latency.

## Why This Exists

This project came about from being an F1 fan and wanting a secure, low-latency way to view a live Sky Q box remotely when out and about.

The main motivation was being sick of the delay introduced by Sky Go and other streaming services. This app is designed to give a much lower latency live view of your own Sky box.

## How It Works

The physical setup is:

- Sky Q box HDMI output
- HDMI splitter
- one HDMI output to the main TV
- the second HDMI output to an H.264/H.265 encoder
- the encoder converts the HDMI video into a stream that this app can display

The app then:

- controls the Sky Q box over the local network using the Sky Q HTTP/control APIs
- displays the video stream coming from the HDMI encoder
- allows a Sky box and its matching stream URL to be linked together as presets

The stream URL must match the encoder physically connected to that specific Sky box.

## Sky Q Support

The solution is intended to work with:

- Sky Q main boxes
- Sky Q mini boxes

Sky Q discovery and remote-control behavior is based on the local network APIs/protocols exposed by the boxes. If a box sits on a routed private subnet that is not on a local NIC, add that CIDR on Setup or the Sky Q / Sky Stream pages.

## Streaming Support

The streaming side is built around the many H.264/H.265 HDMI encoders available from places such as:

- AliExpress
- eBay
- Amazon

The app supports several playback/ingest paths because encoder behavior varies quite a lot between models.

### Direct Streaming

The app includes direct browser playback pages for encoder outputs such as:

- MPEG-TS over HTTP
- HLS

This is useful when the encoder already produces a browser-friendly stream and you want the simplest or lowest-latency path.

### FFmpeg Managed Streaming

The app can also use FFmpeg for sources that need to be normalized, transcoded, or repackaged before browser playback.

This is especially useful for:

- RTSP streams
- awkward H.264/H.265 streams that play in VLC but not cleanly in the browser
- sources that need converting into browser-friendlier HLS output

FFmpeg is not bundled with the app. You install it locally and then configure the `ffmpeg.exe` path in the app's `Setup` page. The managed `FFmpeg` and `RTSP` pages, and any presets that depend on them, require this to be configured before use.

For Windows service deployments, the PowerShell installer can download the FFmpeg Windows essentials build automatically and seed the local setup file with the detected `ffmpeg.exe` path on first install.

## Windows Service Install

The repo now includes a Windows installer path that can:

- publish the app locally
- download FFmpeg if it is not already present
- install the app as the Windows service `SkyStreamingService`
- use the display name `SkyQ Streaming Service`
- preserve local runtime files across re-runs

The installer deploys the running app under `C:\ProgramData\SkyQStreamingService\app`.

See the repo root documentation for details:

- `WINDOWS-INSTALL.md`
- `scripts/install-windows.ps1`
- `scripts/install-from-github.ps1`

## External Access

The solution is designed to work externally as well as on the local network.

From a trusted local network you can:

- configure FFmpeg
- enroll authenticator access using QR codes
- create QR-based authenticator access that can be shared with family members

From outside the home network, users authenticate with their enrolled authenticator and then access the app securely.

Trusted local access includes:

- localhost
- RFC1918 private network ranges
- Tailscale address space

## Presets

The app lets you link:

- a Sky Q box
- a matching video stream URL
- the source type used to play it

This makes it possible to launch a known box/stream pair quickly, with the on-screen mini remote available alongside the live video.

## Technology

Built using:

- JetBrains Rider
- .NET 10
- Codex 5.4

## Local Setup Files

Machine-local configuration and runtime state are intentionally kept out of source control. Important local files include:

- `local-settings.json`
- `auth-settings.json`
- `transcoder-settings.json`
- `direct-skyq-presets.json`
- `skyq-cache.json`
- `sky-stream-cache.json`
- `runtime/`

## Project References

This project builds on, or was directly informed by, the following upstream projects and protocol implementations.

### Browser Playback

- `h265web.js`
  - Used for managed HEVC/H.265 browser playback experiments and managed host rendering.
  - Upstream: https://github.com/numberwolf/h265web.js

- `mpegts.js`
  - Used for direct MPEG-TS playback testing in the browser.
  - Upstream: https://github.com/xqq/mpegts.js

- `hls.js`
  - Used for HLS playback in the browser.
  - Upstream: https://github.com/video-dev/hls.js

- `video.js`
  - Used as an alternate HLS player engine.
  - Upstream: https://github.com/videojs/video.js

### Sky Q Control

- `pyskyqremote`
  - The Sky Q discovery and remote control behavior in this app was informed by this Python implementation.
  - Upstream package: https://pypi.org/project/pyskyqremote/

- `skyq_remote`
  - Source repository for the Sky Q protocol implementation referenced while porting the discovery and remote-control behavior to C#.
  - Upstream: https://github.com/RogerSelwyn/skyq_remote

### Sky Stream Control

- `sky_stream_remote`
  - The Sky Stream remote uses the reverse-engineered Sky Remote LAN protocol documented here (mDNS `_rdk-rics._tcp`, mTLS WebSocket on port 8091). Run the service on the UK-egress gateway box beside the puck; Tailscale subnet routing is not a substitute for TCP 8091.
  - Upstream: https://github.com/jatatech/sky_stream_remote
  - For IR wake/control from Home Assistant, see the `SkyStreamRemote` ESP32 assets at the repo root. ESPHome loads `sky_remote.js` from GitHub.

## Notes

- This application is designed primarily for trusted local/private network use, with controlled authenticated external access.
- Proxy and control endpoints should not be exposed publicly without proper authentication and authorization.
- Browser HEVC/H.265 support depends on the browser and platform decode path. `H.264` remains the safer compatibility option.
