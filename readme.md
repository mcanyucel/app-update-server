# App Update Server

A self-hosted application update server with a web UI and CLI tool. Built with ASP.NET Core, Blazor Server, Fluent UI, and SQLite.

## Overview

App Update Server allows you to host and distribute updates for your desktop applications. Installed apps check in on launch, receive the latest version info, and download updates directly from your server.

## Features

- Web UI for managing applications and versions, with light and dark themes.
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

`GET /health` (public) reports the state of the database and the binaries directory — `200 Healthy`, or `503` when either is unavailable. The Docker image uses it as its `HEALTHCHECK`.

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

**Forward auth** — with `Auth:ForwardAuth` enabled, UI routes require a `Remote-User` header, which your SSO proxy sets after authenticating the user. That header is only meaningful if the request genuinely came from the proxy, so also set `Auth:TrustedProxies` to the proxy's address or range:

```yaml
- Auth__TrustedProxies=172.18.0.0/16
```

Without it the server accepts `Remote-User` from any source — anything able to reach the container directly (another container on the same Docker network, or a published port) can authenticate itself — and logs a warning at startup.

Use the address of the proxy that forwards the request, which is not necessarily the service that authenticated the user: with Caddy's `forward_auth`, Caddy queries Authelia and then forwards the request itself, so Caddy's address is the one to trust. `docker network inspect <network>` lists each container's address.

A bare address (`172.18.0.11`) matches exactly. A CIDR range trusts every container in it, and **host bits are masked off** — `172.18.0.11/16` silently means `172.18.0.0/16`, the whole network, so write `/32` or no prefix if you mean one host. Docker assigns these addresses dynamically; if your proxy's address changes, the rejection is logged with the address the server actually saw:

```
Rejected /: Remote-User is only trusted from a configured proxy, not 172.18.0.19
```

IPv4-mapped IPv6 addresses are normalised before matching, but list IPv4 and IPv6 forms separately if your proxy may connect over either.

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
| `Storage:KeysPath` | `keys` beside `BinariesPath` | Data protection keys |
| `Auth:ApiKey` | — | Key for the `X-API-Key` header; uploads are rejected if unset |
| `Auth:ForwardAuth` | `false` | Require a `Remote-User` header on UI routes |
| `Auth:TrustedProxies` | — | Comma-separated addresses or CIDR ranges allowed to assert `Remote-User` |

`Storage:MaxUploadBytes` is applied to the Kestrel request body limit, the multipart form limit, and the web UI's file picker together, so the API and the UI always accept the same size. Raise it before pushing installers larger than 1 GiB.

Data protection keys default to a `keys` directory next to the binaries, so they live on the same volume and survive the container being recreated. Without that, every restart invalidates the antiforgery tokens browsers are holding and the log fills with `The antiforgery token could not be decrypted`. If the directory cannot be created the server still starts, logs a warning, and falls back to keys that live only as long as the container.

## Deployment

Copy `docker-compose.example.yml` to `docker-compose.yml` and `.env.example` to `.env`, fill in the values, then:

```bash
docker compose up -d
```

To pick up a new release, `docker compose pull && docker compose up -d` — `up -d` alone will not fetch a new image behind an unchanged tag.

### Releasing

Pushing to `main` only runs the tests. Images are published when a `v*` tag is pushed:

```bash
git tag v1.2.3
git push origin v1.2.3
```

That publishes `1.2.3`, `1.2`, `1`, `latest` and `sha-<commit>`. The git tag is also the application version: CI passes it to the Docker build, which stamps it into the assembly, so there is no version number in the repository to bump by hand. The running version is shown in the UI footer and logged at startup:

```
App Update Server v1.2.3 · abc1234 starting
```

Local and non-CI builds report `dev build` instead.

### Reverse proxy

The web UI should be placed behind a reverse proxy with authentication. The following paths must remain publicly accessible for installed apps to function:

- `POST /api/update` — version check
- `GET /api/update/download/*` — binary download

The upload endpoint (`/api/update/upload`) is protected by the API key header `X-API-Key` and does not require SSO. The other management endpoints (`/api/update/apps/...`) do sit behind forward auth, so `aus list`, `versions`, `rollback` and the delete commands need a proxy bypass rule for them if you run them against a protected server.

TLS is expected to terminate at the proxy — the app serves plain HTTP on port 8080 and does not redirect. If your proxy enforces a request body limit of its own (nginx defaults to 1 MB via `client_max_body_size`), raise it to match `Storage:MaxUploadBytes` or uploads will fail before reaching the server.

## Development

Requirements: .NET 10 SDK

```bash
git clone https://github.com/mcanyucel/app-update-server
cd app-update-server
cd AppUpdateServer
dotnet run
```

The development environment uses a local SQLite database and binaries folder. No Docker required for development.

### Tests

```bash
dotnet test
```

`AppUpdateServer.Tests` covers `UpdateService` against a temporary SQLite database and binaries directory, and drives the HTTP pipeline through `WebApplicationFactory` for the upload form, the API key check, and the forward-auth middleware.

## License

MIT


## Future Features

- [ ] Built-in auth page for using without an SSO provider.
- [ ] Better state check for preventing MITM (asymmetric checks? symmetric HMAC signing?)
