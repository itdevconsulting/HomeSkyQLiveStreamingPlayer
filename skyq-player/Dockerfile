FROM mcr.microsoft.com/dotnet/sdk:10.0-bookworm-slim AS build
ARG REPO_URL=https://github.com/itdevconsulting/HomeSkyQLiveStreamingPlayer.git
ARG REPO_REF=main
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && git clone --depth 1 --branch "${REPO_REF}" "${REPO_URL}" . \
    && GIT_SHA="$(git rev-parse HEAD)" \
    && GIT_BRANCH="$(git rev-parse --abbrev-ref HEAD)" \
    && APP_VERSION="$(tr -d '[:space:]' < VERSION)" \
    && dotnet publish H265Player/H265Player.csproj -c Release -o /app/publish \
    && printf '{\n  "commitSha": "%s",\n  "branch": "%s",\n  "builtAt": "%s",\n  "version": "%s",\n  "repoOwner": "itdevconsulting",\n  "repoName": "HomeSkyQLiveStreamingPlayer"\n}\n' \
        "$GIT_SHA" "$GIT_BRANCH" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$APP_VERSION" \
        > /app/publish/version.json

FROM mcr.microsoft.com/dotnet/aspnet:10.0-bookworm-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg ca-certificates jq \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./
COPY run.sh /run.sh
RUN chmod +x /run.sh \
    && mkdir -p /data

ENV ASPNETCORE_ENVIRONMENT=Production
ENV SKYQ_DATA_DIR=/data
ENV SKYQ_HOMEASSISTANT=true
ENV DOTNET_RUNNING_IN_CONTAINER=true

ENTRYPOINT ["/run.sh"]
