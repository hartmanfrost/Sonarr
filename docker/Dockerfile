# Sonarr image for the ash cluster with the anime full-season-pack episode-search
# fix (upstream PR hartmanfrost/Sonarr -> Sonarr/Sonarr). Built by
# .github/workflows/ash-image.yml. Build stages run natively (BUILDPLATFORM),
# backend cross-publishes to linux-x64.
FROM --platform=$BUILDPLATFORM node:20-bookworm AS frontend
WORKDIR /build
COPY package.json yarn.lock tsconfig.json ./
COPY frontend ./frontend
RUN yarn install --frozen-lockfile --network-timeout 120000
RUN yarn run build --env production

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:6.0 AS backend
WORKDIR /build
# Root .editorconfig carries the StyleCop severity overrides; Logo/ is embedded
# as a resource by NzbDrone.Core.
COPY .editorconfig ./
COPY Logo ./Logo
COPY src ./src
RUN --mount=type=cache,target=/root/.nuget \
    dotnet publish src/NzbDrone.Console/Sonarr.Console.csproj \
      -c Release -f net6.0 -r linux-x64 --self-contained \
      -p:SolutionDir=/build/src/ \
      -p:Platform=Posix \
      -o /build/publish \
    # Sonarr.Mono is loaded dynamically by name on POSIX, publish it alongside
    && dotnet publish src/NzbDrone.Mono/Sonarr.Mono.csproj \
      -c Release -f net6.0 -r linux-x64 --self-contained \
      -p:SolutionDir=/build/src/ \
      -p:Platform=Posix \
      -o /build/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:6.0-bookworm-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends libsqlite3-0 libicu72 curl ca-certificates mediainfo \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=backend /build/publish ./bin
COPY --from=frontend /build/_output/UI ./bin/UI
EXPOSE 8989
VOLUME /config
ENTRYPOINT ["/app/bin/Sonarr", "-nobrowser", "-data=/config"]
