#!/usr/bin/env bash

set -Eeuo pipefail

APP_NAME="SkyStreamingService"
SERVICE_USER="skystreamingservice"
SERVICE_GROUP="skystreamingservice"
INSTALL_ROOT="${INSTALL_ROOT:-/opt/skystreamingservice}"
APP_DIR="$INSTALL_ROOT/app"
STATE_DIR="${STATE_DIR:-/var/lib/skystreamingservice}"
SERVICE_FILE="/etc/systemd/system/${APP_NAME}.service"
DOTNET_ROOT_DIR="${DOTNET_ROOT_DIR:-/usr/share/dotnet}"
DOTNET_BIN="/usr/bin/dotnet"
PORT="${PORT:-5221}"
UNAUTHENTICATED_PORT="${UNAUTHENTICATED_PORT:-}"
DOTNET_CHANNEL="${DOTNET_CHANNEL:-10.0}"
DOTNET_QUALITY="${DOTNET_QUALITY:-GA}"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
PROJECT_FILE="${PROJECT_FILE:-$REPO_ROOT/H265Player/H265Player.csproj}"
PUBLISH_TMP="$(mktemp -d /tmp/skystreamingservice-publish.XXXXXX)"
BACKUP_TMP="$(mktemp -d /tmp/skystreamingservice-backup.XXXXXX)"
BUILD_USER="${SUDO_USER:-$(id -un)}"
BUILD_GROUP="$(id -gn "$BUILD_USER" 2>/dev/null || echo "$BUILD_USER")"

cleanup() {
    rm -rf "$PUBLISH_TMP" "$BACKUP_TMP"
}
trap cleanup EXIT

log() {
    printf '\n[%s] %s\n' "$APP_NAME" "$1"
}

fail() {
    printf '\n[%s] ERROR: %s\n' "$APP_NAME" "$1" >&2
    exit 1
}

require_root() {
    if [[ "${EUID}" -ne 0 ]]; then
        fail "Run this script as root or via sudo."
    fi
}

require_systemd() {
    command -v systemctl >/dev/null 2>&1 || fail "systemd is required."
}

ensure_build_user_exists() {
    id -u "$BUILD_USER" >/dev/null 2>&1 || fail "Build user '$BUILD_USER' does not exist."
}

detect_arch() {
    case "$(uname -m)" in
        x86_64|amd64) echo "x64" ;;
        aarch64|arm64) echo "arm64" ;;
        armv7l) echo "arm" ;;
        *)
            fail "Unsupported architecture: $(uname -m)"
            ;;
    esac
}

have_command() {
    command -v "$1" >/dev/null 2>&1
}

fetch_to() {
    local url="$1"
    local destination="$2"

    if have_command curl; then
        curl -fsSL "$url" -o "$destination"
        return
    fi

    if have_command wget; then
        wget -qO "$destination" "$url"
        return
    fi

    fail "Neither curl nor wget is available."
}

install_packages() {
    local packages=("$@")
    [[ "${#packages[@]}" -eq 0 ]] && return

    if have_command apt-get; then
        export DEBIAN_FRONTEND=noninteractive
        apt-get update
        apt-get install -y "${packages[@]}"
        return
    fi

    if have_command dnf; then
        dnf install -y "${packages[@]}"
        return
    fi

    if have_command yum; then
        yum install -y "${packages[@]}"
        return
    fi

    if have_command zypper; then
        zypper --non-interactive install "${packages[@]}"
        return
    fi

    if have_command pacman; then
        pacman -Sy --noconfirm "${packages[@]}"
        return
    fi

    if have_command apk; then
        apk add --no-cache "${packages[@]}"
        return
    fi

    fail "No supported package manager found. Install the required packages manually: ${packages[*]}"
}

ensure_base_prereqs() {
    local missing=()

    have_command tar || missing+=("tar")
    have_command gzip || missing+=("gzip")
    have_command curl || have_command wget || missing+=("curl")
    [[ -e /etc/ssl/certs ]] || missing+=("ca-certificates")

    if [[ "${#missing[@]}" -gt 0 ]]; then
        log "Installing base prerequisites: ${missing[*]}"
        install_packages "${missing[@]}"
    fi
}

ensure_ffmpeg() {
    if have_command ffmpeg; then
        FFMPEG_PATH="$(command -v ffmpeg)"
        log "FFmpeg already present at $FFMPEG_PATH"
        return
    fi

    log "Installing FFmpeg"

    if have_command apt-get; then
        install_packages ffmpeg
    elif have_command dnf || have_command yum || have_command zypper || have_command pacman || have_command apk; then
        install_packages ffmpeg
    else
        fail "Unable to install FFmpeg automatically on this distribution."
    fi

    have_command ffmpeg || fail "FFmpeg install completed but ffmpeg is still not on PATH."
    FFMPEG_PATH="$(command -v ffmpeg)"
}

ensure_dotnet_sdk() {
    if have_command dotnet && dotnet --list-sdks 2>/dev/null | awk '{print $1}' | grep -Eq '^10\.'; then
        log ".NET 10 SDK already present"
        return
    fi

    local arch
    arch="$(detect_arch)"
    local installer="/tmp/dotnet-install.sh"

    log "Installing .NET ${DOTNET_CHANNEL} SDK"
    fetch_to "https://dot.net/v1/dotnet-install.sh" "$installer"
    chmod +x "$installer"

    mkdir -p "$DOTNET_ROOT_DIR"
    "$installer" \
        --channel "$DOTNET_CHANNEL" \
        --quality "$DOTNET_QUALITY" \
        --install-dir "$DOTNET_ROOT_DIR" \
        --architecture "$arch" \
        --version latest \
        --verbose

    ln -sf "$DOTNET_ROOT_DIR/dotnet" "$DOTNET_BIN"

    have_command dotnet || fail "dotnet command is still unavailable after install."
    dotnet --list-sdks | awk '{print $1}' | grep -Eq '^10\.' || fail ".NET 10 SDK was not installed correctly."
}

ensure_service_account() {
    if ! getent group "$SERVICE_GROUP" >/dev/null 2>&1; then
        groupadd --system "$SERVICE_GROUP"
    fi

    if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
        useradd \
            --system \
            --gid "$SERVICE_GROUP" \
            --home-dir "$STATE_DIR" \
            --create-home \
            --shell /usr/sbin/nologin \
            "$SERVICE_USER"
    fi

    mkdir -p "$STATE_DIR"
    chown -R "$SERVICE_USER:$SERVICE_GROUP" "$STATE_DIR"
    chmod 750 "$STATE_DIR"
}

ensure_build_workspace_writable() {
    local repo_root
    repo_root="$(cd -- "$(dirname -- "$PROJECT_FILE")/.." && pwd)"

    mkdir -p "$PUBLISH_TMP" "$BACKUP_TMP"
    chown -R "$BUILD_USER:$BUILD_GROUP" "$PUBLISH_TMP"

    if [[ -d "$repo_root" ]]; then
        chown -R "$BUILD_USER:$BUILD_GROUP" "$repo_root"
    fi
}

run_publish() {
    local command="dotnet publish \"$PROJECT_FILE\" -c Release -o \"$PUBLISH_TMP\" --nologo"

    if [[ "$BUILD_USER" == "$(id -un)" ]]; then
        bash -lc "$command"
        return
    fi

    if have_command runuser; then
        runuser -u "$BUILD_USER" -- bash -lc "$command"
        return
    fi

    su -s /bin/bash "$BUILD_USER" -c "$command"
}

write_version_stamp() {
    local sha=""
    local built_at=""
    local branch="main"
    local tmp

    if [[ -d "$REPO_ROOT/.git" ]] && have_command git; then
        sha="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || true)"
        built_at="$(git -C "$REPO_ROOT" log -1 --format=%cI 2>/dev/null || true)"
        branch="$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD 2>/dev/null || echo main)"
    fi

    if [[ -z "$sha" ]]; then
        tmp="$(mktemp)"
        if have_command curl; then
            curl -fsSL -H "User-Agent: HomeSkyQLiveStreamingPlayer-installer" \
                "https://api.github.com/repos/itdevconsulting/HomeSkyQLiveStreamingPlayer/commits/main" >"$tmp" || true
        elif have_command wget; then
            wget -qO "$tmp" --header="User-Agent: HomeSkyQLiveStreamingPlayer-installer" \
                "https://api.github.com/repos/itdevconsulting/HomeSkyQLiveStreamingPlayer/commits/main" || true
        fi
        if [[ -s "$tmp" ]]; then
            sha="$(sed -n 's/.*"sha": "\([0-9a-f]\{40\}\)".*/\1/p' "$tmp" | head -n 1)"
            built_at="$(sed -n 's/.*"date": "\([^"]*\)".*/\1/p' "$tmp" | head -n 1)"
        fi
        rm -f "$tmp"
    fi

    [[ -n "$sha" ]] || sha="unknown"
    [[ -n "$built_at" ]] || built_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

    local version=""
    if [[ -f "$REPO_ROOT/VERSION" ]]; then
        version="$(tr -d '[:space:]' < "$REPO_ROOT/VERSION")"
    fi
    if [[ -z "$version" ]]; then
        version="$(date +%Y.%-m.%-d.%-H.%-M)"
    fi

    cat >"$PUBLISH_TMP/version.json" <<EOF
{
  "commitSha": "$sha",
  "branch": "$branch",
  "builtAt": "$built_at",
  "version": "$version",
  "repoOwner": "itdevconsulting",
  "repoName": "HomeSkyQLiveStreamingPlayer"
}
EOF
}

backup_existing_state() {
    if [[ ! -d "$APP_DIR" ]]; then
        return 0
    fi

    local entries=(
        "local-settings.json"
        "auth-settings.json"
        "transcoder-settings.json"
        "direct-skyq-presets.json"
        "skyq-cache.json"
        "runtime"
    )

    local entry
    for entry in "${entries[@]}"; do
        if [[ -e "$APP_DIR/$entry" ]]; then
            cp -a "$APP_DIR/$entry" "$BACKUP_TMP/"
        fi
    done
}

seed_local_settings() {
    local target="$APP_DIR/local-settings.json"
    if [[ -f "$BACKUP_TMP/local-settings.json" ]]; then
        return
    fi

    cat >"$target" <<EOF
{
  "FfmpegPath": "${FFMPEG_PATH}",
  "DefaultHttpStreamUrl": "",
  "DefaultRtspStreamUrl": "",
  "EnableUnauthenticatedPort": $([[ -n "$UNAUTHENTICATED_PORT" ]] && echo "true" || echo "false"),
  "UnauthenticatedPort": $([[ -n "$UNAUTHENTICATED_PORT" ]] && echo "$UNAUTHENTICATED_PORT" || echo "null"),
  "AutoUpdateEnabled": false
}
EOF
}

deploy_app() {
    backup_existing_state

    rm -rf "$APP_DIR"
    mkdir -p "$APP_DIR"
    cp -a "$PUBLISH_TMP"/. "$APP_DIR"/

    if [[ -d "$BACKUP_TMP" ]]; then
        cp -a "$BACKUP_TMP"/. "$APP_DIR"/ 2>/dev/null || true
    fi

    mkdir -p "$APP_DIR/runtime/live"
    seed_local_settings
    chown -R "$SERVICE_USER:$SERVICE_GROUP" "$APP_DIR"
    chmod -R u=rwX,g=rX,o= "$APP_DIR"
}

stop_existing_service() {
    if [[ ! -f "$SERVICE_FILE" ]]; then
        return
    fi

    if systemctl is-active --quiet "$APP_NAME"; then
        log "Stopping existing service"
        systemctl stop "$APP_NAME"
    fi
}

write_service_unit() {
    cat >"$SERVICE_FILE" <<EOF
[Unit]
Description=Home Sky Q Live Streaming Player
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$SERVICE_USER
Group=$SERVICE_GROUP
WorkingDirectory=$APP_DIR
Environment=HOME=$STATE_DIR
Environment=DOTNET_ROOT=$DOTNET_ROOT_DIR
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:$PORT
ExecStart=$DOTNET_BIN $APP_DIR/H265Player.dll
Restart=always
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=$APP_NAME
NoNewPrivileges=true
PrivateTmp=true
ProtectControlGroups=true
ProtectKernelModules=true
ProtectKernelTunables=true
RestrictSUIDSGID=true
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK
LockPersonality=true
UMask=0027

[Install]
WantedBy=multi-user.target
EOF
}

write_update_units() {
    local helper_src="$SCRIPT_DIR/skystreaming-update.sh"
    local helper_dst="/usr/local/sbin/skystreaming-update"
    local update_service="/etc/systemd/system/${APP_NAME}-update.service"
    local update_path="/etc/systemd/system/${APP_NAME}-update.path"

    if [[ -f "$helper_src" ]]; then
        cp "$helper_src" "$helper_dst"
    else
        cat >"$helper_dst" <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
FLAG="${UPDATE_FLAG:-/var/lib/skystreamingservice/update.request}"
CHECKOUT="${CHECKOUT_DIR:-/usr/local/src/homeskyqlivestreamingplayer}"
LOG="${UPDATE_LOG:-/var/lib/skystreamingservice/update.log}"
mkdir -p "$(dirname "$LOG")"
rm -f "$FLAG"
{
    echo "[$(date -Is)] Starting GitHub update"
    if [[ -f "$CHECKOUT/scripts/install-from-github.sh" ]]; then
        bash "$CHECKOUT/scripts/install-from-github.sh"
    else
        curl -fsSL https://raw.githubusercontent.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/main/scripts/install-from-github.sh | bash
    fi
    echo "[$(date -Is)] GitHub update finished"
} >>"$LOG" 2>&1
EOF
    fi
    chmod 755 "$helper_dst"

    cat >"$update_service" <<EOF
[Unit]
Description=Home Sky Q Live Streaming Player updater
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
Nice=5
TimeoutStartSec=1800
ExecStart=$helper_dst
EOF

    cat >"$update_path" <<EOF
[Unit]
Description=Watch for Home Sky Q Live Streaming Player update requests

[Path]
PathExists=$STATE_DIR/update.request

[Install]
WantedBy=multi-user.target
EOF
}

reload_and_start_service() {
    systemctl daemon-reload
    systemctl enable "$APP_NAME"
    systemctl enable "${APP_NAME}-update.path"
    systemctl restart "$APP_NAME"
    systemctl restart "${APP_NAME}-update.path"
}

detect_primary_ip() {
    if have_command ip; then
        ip route get 1.1.1.1 2>/dev/null | awk '/src/ {print $7; exit}'
        return
    fi

    hostname -I 2>/dev/null | awk '{print $1}'
}

detect_tailscale_ip() {
    if have_command tailscale; then
        tailscale ip -4 2>/dev/null | head -n 1
    fi
}

print_summary() {
    local lan_ip
    local tailscale_ip
    lan_ip="$(detect_primary_ip || true)"
    tailscale_ip="$(detect_tailscale_ip || true)"

    cat <<EOF

${APP_NAME} is installed and running.

Service management:
  sudo systemctl status ${APP_NAME}
  sudo systemctl start ${APP_NAME}
  sudo systemctl stop ${APP_NAME}
  sudo systemctl restart ${APP_NAME}
  sudo journalctl -u ${APP_NAME} -f

Application paths:
  App directory:   ${APP_DIR}
  State directory: ${STATE_DIR}
  Service unit:    ${SERVICE_FILE}
  Update helper:   /usr/local/sbin/skystreaming-update
  FFmpeg path:     ${FFMPEG_PATH}

Local first-run:
  1. Open the app from a trusted network so setup is allowed.
  2. Visit http://127.0.0.1:${PORT}/setup on the server itself, or use a LAN/Tailscale address.
  3. Confirm the FFmpeg path, add default stream URLs if you want them, and save.
  4. Enter the email address you want to use for remote login.
  5. Generate the QR code and scan it with your authenticator app.
  6. After that, external users sign in at /auth/login with that email and the current 6-digit code.

Trusted network rules in this app:
  - localhost / loopback
  - RFC1918 private LAN ranges: 10.x, 172.16-31.x, 192.168.x
  - Tailscale CGNAT range: 100.64.0.0/10

Cloudflare / Tailscale notes:
  - The safe pattern is to keep the app private on ${PORT} and put Cloudflare Zero Trust or Tailscale in front of it.
  - First authenticator enrollment still needs to be done from localhost, LAN, or Tailscale.
  - With Cloudflare, point your origin/private tunnel at http://127.0.0.1:${PORT} or another local listener on this box.
  - With Tailscale, you can usually do first-run setup directly over the node's Tailscale IP because the app treats that range as trusted.

Useful URLs:
  Local:      http://127.0.0.1:${PORT}/
  Login:      http://127.0.0.1:${PORT}/auth/login
  Setup:      http://127.0.0.1:${PORT}/setup
EOF

    if [[ -n "$UNAUTHENTICATED_PORT" ]]; then
        cat <<EOF
  Edge:       http://127.0.0.1:${UNAUTHENTICATED_PORT}/ (no app auth)
EOF
    fi

    if [[ -n "$lan_ip" ]]; then
        cat <<EOF
  LAN:        http://${lan_ip}:${PORT}/
  LAN Setup:  http://${lan_ip}:${PORT}/setup
EOF
        if [[ -n "$UNAUTHENTICATED_PORT" ]]; then
            cat <<EOF
  LAN Edge:   http://${lan_ip}:${UNAUTHENTICATED_PORT}/ (no app auth)
EOF
        fi
    fi

    if [[ -n "$tailscale_ip" ]]; then
        cat <<EOF
  Tailscale:  http://${tailscale_ip}:${PORT}/
  TS Setup:   http://${tailscale_ip}:${PORT}/setup
EOF
        if [[ -n "$UNAUTHENTICATED_PORT" ]]; then
            cat <<EOF
  TS Edge:    http://${tailscale_ip}:${UNAUTHENTICATED_PORT}/ (no app auth)
EOF
        fi
    fi

    cat <<'EOF'

If the service does not come up cleanly, check:
  - sudo systemctl status SkyStreamingService
  - sudo journalctl -u SkyStreamingService -n 200 --no-pager

EOF
}

main() {
    require_root
    require_systemd
    ensure_build_user_exists
    [[ -f "$PROJECT_FILE" ]] || fail "Project file not found at $PROJECT_FILE"

    ensure_base_prereqs
    ensure_ffmpeg
    ensure_dotnet_sdk
    ensure_service_account
    ensure_build_workspace_writable

    log "Publishing application"
    run_publish
    write_version_stamp

    stop_existing_service

    log "Deploying application"
    deploy_app

    log "Writing systemd unit"
    write_service_unit
    write_update_units

    log "Starting service"
    reload_and_start_service

    print_summary
}

main "$@"
