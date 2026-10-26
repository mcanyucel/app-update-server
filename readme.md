# App Update Server

A self-hosted application update server with a web UI and CLI tool. Built with ASP.NET Core, Blazor, and SQLite.

## Overview

App Update Server allows you to host and distribute updates for your desktop applications. Installed apps check in on launch, receive the latest version info, and download updates directly from your server.

## Features

- Web UI for managing applications and versions.
- REST API for version checks and binary downloads.
- CLI tool (`aus`) for uploading new versions from build pipelines.
- Rollback support - deleting the latest version promotes the previous one automatically.
- SHA-256 integrity verification for all uploaded binaries.
- Forward auth support for SSO integration (Authelia, etc).

## Projects

- `AppUpdateServer`: ASP.NET Core + Blazor Server web application.
- `AppUpdateServer.Cli`: `aus` CLI tool for uploading and managing versions.

## API

All endpoints under `/api/update`:

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| POST | `/api/update` | Public | Version check |
| GET | `/api/update/download/{slug}/{version}` | Public | Download binary |
| POST | `/api/update/upload` | API Key | Upload new version |
| GET | `/api/update/apps` | API Key | List apps |
| GET | `/api/update/apps/{slug}/versions` | API Key | List versions |
| POST | `/api/update/apps/{slug}/rollback` | API Key | Rollback latest version |
| DELETE | `/api/update/apps/{slug}` | API Key | Delete app |
| DELETE | `/api/update/apps/{slug}/versions/{id}` | API Key | Delete specific version |

### Upload Request

`POST /api/update/upload` takes a multipart form:

| Field | Required | Description |
|-------|----------|-------------|
| `appName` | yes | App display name; the app is created automatically if the slug is new |
| `version` | yes | Version string (see format below) |
| `updateFile` | yes | The binary |
| `notes` | no | Release notes (`releaseNotes` is also accepted, for older CLI versions) |

Version strings must be 1-64 characters of letters, digits, dot, dash, underscore or plus, and start with a letter or digit — e.g. `3.2.0.0`, `1.2.0`, `1.0.0-beta+build.7`. The version string becomes part of the stored file name, so anything outside that set is rejected with a 400.

Re-uploading an existing version replaces its binary, hash, notes, and timestamp in place.

### Version Check Request

```json
{
  "appIdentity": "{ Name=my-app, Version=1.0.0, ProcessorArchitecture=MSIL }",
  "stateSeed": 42
}
```

## Security

**State verification** — the version check response includes a `state` field derived from the client-supplied `stateSeed` via SHA-256. This allows clients to detect replayed or tampered responses. Since the algorithm is open source, this is not a cryptographic guarantee but a basic integrity check.

**Binary integrity** — all uploaded binaries are SHA-256 hashed at upload time. Clients should verify the downloaded binary matches the `sha256Hash` field in the version check response before installing.

**API key** — the upload and management endpoints require an `X-API-Key` header. Use a strong random value and pass it via environment variable, never hardcode it.

## CLI

The `aus` tool is not published to NuGet yet — build and install it from this repository:

```bash
# From the repository root
dotnet pack AppUpdateServer.Cli -c Release
dotnet tool install -g --add-source AppUpdateServer.Cli/bin/Release AppUpdateServer.Cli
```

To upgrade after pulling new changes, bump `<Version>` in `AppUpdateServer.Cli.csproj`, re-pack, and run `dotnet tool update -g --add-source AppUpdateServer.Cli/bin/Release AppUpdateServer.Cli`.

If you would rather not install it globally, run it straight from the checkout — `dotnet run --project AppUpdateServer.Cli -- upload ...` — or publish a self-contained binary with `dotnet publish AppUpdateServer.Cli -c Release` and copy it onto your PATH.

Usage:

```bash
# Upload a new version
aus upload --server https://your-server.com --api-key your-key --app "My App" --version 1.2.0 --file ./MyApp.exe --notes "Bug fixes"

# List apps
aus list --server https://your-server.com --api-key your-key

# List versions for an app
aus versions --server https://your-server.com --api-key your-key --app my-app

# Rollback latest version
aus rollback --server https://your-server.com --api-key your-key --app my-app

# Delete a specific version
aus delete-version --server https://your-server.com --api-key your-key --app my-app --version-id 5

# Delete an app
aus delete-app --server https://your-server.com --api-key your-key --app my-app
```

## Configuration

Settings come from `appsettings.{Environment}.json` or environment variables (`__` replaces `:`, e.g. `Storage__MaxUploadBytes`).

| Setting | Default | Description |
|---------|---------|-------------|
| `ConnectionStrings:Default` | `Data Source=/data/updateserver.db` | SQLite connection string |
| `Storage:BinariesPath` | `/data/binaries` | Where uploaded binaries are stored |
| `Storage:MaxUploadBytes` | `1073741824` (1 GiB) | Largest accepted upload |
| `Auth:ApiKey` | — | Key for the `X-API-Key` header; uploads are rejected if unset |
| `Auth:ForwardAuth` | `false` | Require a `Remote-User` header on UI routes |

`Storage:MaxUploadBytes` is applied to the Kestrel request body limit, the multipart form limit, and the web UI's file picker together, so the API and the UI always accept the same size. Raise it before pushing installers larger than 1 GiB.

## Deployment

Copy `docker-compose.example.yml` to `docker-compose.yml` and `.env.example` to `.env`, fill in the values, then:

```bash
docker compose up -d
```

### Reverse proxy

The web UI should be placed behind a reverse proxy with authentication. The following paths must remain publicly accessible for installed apps to function:

- `POST /api/update` — version check
- `GET /api/update/download/*` — binary download

The upload endpoint (`/api/update/upload`) is protected by the API key header `X-API-Key` and does not require SSO.

## Development

Requirements: .NET 10 SDK

```bash
git clone https://github.com/mcanyucel/app-update-server
cd app-update-server
cd AppUpdateServer
dotnet run
```

The development environment uses a local SQLite database and binaries folder. No Docker required for development.

## License

MIT


## Future Features

- [ ] Built-in auth page for using without an SSO provider.
- [ ] Better state check for preventing MITM (asymmetric checks? symmetric HMAC signing?)
