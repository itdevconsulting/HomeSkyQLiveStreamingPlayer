#!/usr/bin/env bash

set -Eeuo pipefail

FLAG="${UPDATE_FLAG:-/var/lib/skystreamingservice/update.request}"
CHECKOUT="${CHECKOUT_DIR:-/usr/local/src/homeskyqlivestreamingplayer}"
LOG="${UPDATE_LOG:-/var/lib/skystreamingservice/update.log}"
GITHUB_INSTALLER_URL="${GITHUB_INSTALLER_URL:-https://raw.githubusercontent.com/itdevconsulting/HomeSkyQLiveStreamingPlayer/main/scripts/install-from-github.sh}"

mkdir -p "$(dirname "$LOG")"
rm -f "$FLAG"

{
    echo "[$(date -Is)] Starting GitHub update"
    if [[ -f "$CHECKOUT/scripts/install-from-github.sh" ]]; then
        bash "$CHECKOUT/scripts/install-from-github.sh"
    elif command -v curl >/dev/null 2>&1; then
        curl -fsSL "$GITHUB_INSTALLER_URL" | bash
    elif command -v wget >/dev/null 2>&1; then
        wget -qO- "$GITHUB_INSTALLER_URL" | bash
    else
        echo "No local installer checkout and neither curl nor wget is available." >&2
        exit 1
    fi
    echo "[$(date -Is)] GitHub update finished"
} >>"$LOG" 2>&1
