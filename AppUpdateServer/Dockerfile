FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY AppUpdateServer.slnx .
COPY AppUpdateServer/AppUpdateServer.csproj AppUpdateServer/
COPY AppUpdateServer.Cli/AppUpdateServer.Cli.csproj AppUpdateServer.Cli/
RUN dotnet restore AppUpdateServer/AppUpdateServer.csproj

COPY AppUpdateServer/ AppUpdateServer/

# Supplied by CI from the git tag; stays 0.0.0-dev for local builds.
ARG VERSION=0.0.0-dev
ARG REVISION=""
RUN dotnet publish AppUpdateServer/AppUpdateServer.csproj -c Release -o /app/publish \
    -p:Version=${VERSION} -p:SourceRevisionId=${REVISION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is not in the runtime image, and HEALTHCHECK needs something to call /health with.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /data/binaries

COPY --from=build /app/publish .

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "AppUpdateServer.dll"]
