# HomeSkyQLiveStreamingPlayer

Recommended install on Linux:

```bash
curl -fsSL https://raw.githubusercontent.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/main/scripts/install-from-github.sh | sudo REPO_BRANCH=main bash
```

Fallback:

```bash
wget -qO- https://raw.githubusercontent.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/main/scripts/install-from-github.sh | sudo REPO_BRANCH=main bash
```

If you already have a broken install or root-owned checkout from an earlier run, clean it up first:

```bash
sudo systemctl stop SkyStreamingService 2>/dev/null || true
sudo rm -rf /usr/local/src/homeskyqlivestreamingplayer
curl -fsSL https://raw.githubusercontent.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/main/scripts/install-from-github.sh | sudo REPO_BRANCH=main bash
```

The installer will:

- clone or update the repo
- install the `.NET 10` SDK if needed
- install `ffmpeg` if needed
- build and publish the app
- install and start the `SkyStreamingService` systemd service
- print the local setup and login URLs

After install, open:

```text
http://127.0.0.1:5221/setup
```

From there:

1. Confirm the FFmpeg path.
2. Save setup.
3. Enter your email address.
4. Generate the authenticator QR code.
5. External users then sign in at `/auth/login`.
