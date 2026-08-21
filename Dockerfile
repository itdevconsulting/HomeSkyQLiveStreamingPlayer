FROM mcr.microsoft.com/dotnet/sdk:10.0-bookworm-slim AS build
WORKDIR /src
COPY H265Player/H265Player.csproj H265Player/
RUN dotnet restore H265Player/H265Player.csproj
COPY H265Player/ H265Player/
ARG GIT_SHA=unknown
ARG GIT_BRANCH=main
RUN dotnet publish H265Player/H265Player.csproj -c Release -o /app/publish --no-restore \
    && printf '{\n  "commitSha": "%s",\n  "branch": "%s",\n  "builtAt": "%s",\n  "repoOwner": "itdevconsulting",\n  "repoName": "HomeSkyQLiveStreamingPlayer"\n}\n' \
        "$GIT_SHA" "$GIT_BRANCH" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        > /app/publish/version.json

FROM mcr.microsoft.com/dotnet/aspnet:10.0-bookworm-slim AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://0.0.0.0:5221
ENV SKYQ_DATA_DIR=/data
ENV DOTNET_RUNNING_IN_CONTAINER=true

RUN mkdir -p /data
EXPOSE 5221
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "H265Player.dll"]
