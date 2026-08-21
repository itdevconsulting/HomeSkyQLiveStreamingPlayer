#!/usr/bin/env bash
set -euo pipefail

export SKYQ_DATA_DIR="${SKYQ_DATA_DIR:-/data}"
export SKYQ_HOMEASSISTANT=true
export DOTNET_RUNNING_IN_CONTAINER=true
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"

PORT="5221"
if [[ -f /data/options.json ]] && command -v jq >/dev/null 2>&1; then
    PORT="$(jq -r '.port // 5221' /data/options.json)"
fi

export ASPNETCORE_URLS="http://0.0.0.0:${PORT}"
exec dotnet /app/H265Player.dll
