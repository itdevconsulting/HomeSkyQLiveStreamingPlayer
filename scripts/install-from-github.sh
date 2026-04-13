#!/usr/bin/env bash

set -Eeuo pipefail

REPO_URL="${REPO_URL:-https://github.com/itdevconsulting/HomeSkyQLiveStreamingPlayer.git}"
REPO_BRANCH="${REPO_BRANCH:-master}"
CHECKOUT_DIR="${CHECKOUT_DIR:-/usr/local/src/homeskyqlivestreamingplayer}"
INSTALL_SCRIPT_RELATIVE="scripts/install-linux.sh"
BUILD_USER="${SUDO_USER:-$(id -un)}"
BUILD_GROUP="$(id -gn "$BUILD_USER" 2>/dev/null || echo "$BUILD_USER")"

log() {
    printf '\n[HomeSkyQLiveStreamingPlayer] %s\n' "$1"
}

fail() {
    printf '\n[HomeSkyQLiveStreamingPlayer] ERROR: %s\n' "$1" >&2
    exit 1
}

require_root() {
    if [[ "${EUID}" -ne 0 ]]; then
        fail "Run this script as root or via sudo."
    fi
}

have_command() {
    command -v "$1" >/dev/null 2>&1
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

    fail "No supported package manager found. Install git manually, then rerun."
}

ensure_git() {
    if have_command git; then
        return
    fi

    log "Installing git"
    install_packages git
    have_command git || fail "git is still unavailable after install."
}

sync_checkout() {
    mkdir -p "$(dirname "$CHECKOUT_DIR")"

    if [[ ! -d "$CHECKOUT_DIR/.git" ]]; then
        log "Cloning $REPO_URL"
        rm -rf "$CHECKOUT_DIR"
        git clone --depth 1 --branch "$REPO_BRANCH" "$REPO_URL" "$CHECKOUT_DIR"
        chown -R "$BUILD_USER:$BUILD_GROUP" "$CHECKOUT_DIR"
        return
    fi

    log "Updating existing checkout in $CHECKOUT_DIR"
    git -C "$CHECKOUT_DIR" remote set-url origin "$REPO_URL"
    git -C "$CHECKOUT_DIR" fetch --depth 1 origin "$REPO_BRANCH"
    git -C "$CHECKOUT_DIR" checkout -B "$REPO_BRANCH" "origin/$REPO_BRANCH"
    git -C "$CHECKOUT_DIR" reset --hard "origin/$REPO_BRANCH"
    chown -R "$BUILD_USER:$BUILD_GROUP" "$CHECKOUT_DIR"
}

run_installer() {
    local installer="$CHECKOUT_DIR/$INSTALL_SCRIPT_RELATIVE"
    [[ -f "$installer" ]] || fail "Installer not found at $installer"
    chmod +x "$installer"
    "$installer"
}

main() {
    require_root
    ensure_git
    sync_checkout
    run_installer
}

main "$@"
