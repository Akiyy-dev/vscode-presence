# VSCodePresence-SteamClient

VSCodePresence-SteamClient is a .NET 8 console application that connects to the vscode-presence bridge over WebSocket and pushes incoming editor status data into Steam Rich Presence via Steamworks.NET.

## Overview

- Runtime: .NET 8
- Platform target: win-x64
- Steam integration: Steamworks.NET
- Status source: WebSocket bridge, default `ws://127.0.0.1:31337`
- Message contract: `{ "type": "status", "version": 1, "payload": { ... } }`

## Configuration

Application settings live in [appsettings.json](appsettings.json).

```json
{
  "AppId": 480,
  "EnsureSteamAppIdFile": true,
  "CallRestartAppIfNecessary": true,
  "WebSocketUrl": "ws://127.0.0.1:31337",
  "ReconnectIntervalMs": 3000,
  "UpdateIntervalMs": 1000
}
```

Key settings:

- `AppId`: Steam AppID used for `SteamAPI.RestartAppIfNecessary` and `SteamAPI.Init`.
- `EnsureSteamAppIdFile`: When `true`, the app creates or updates `steam_appid.txt` in the output directory.
- `CallRestartAppIfNecessary`: When `true`, the app lets Steam relaunch the executable under Steam when required.
- `WebSocketUrl`: Bridge endpoint.
- `ReconnectIntervalMs`: Delay before reconnect attempts.
- `UpdateIntervalMs`: Callback loop delay.

## Local Build

Requirements:

- Windows
- .NET 8 SDK
- Steam client installed and running

Build locally:

```powershell
dotnet restore
dotnet build
```

Build output path:

```text
bin/Debug/net8.0/win-x64/
```

Run locally:

```powershell
./bin/Debug/net8.0/win-x64/VSCodePresence-SteamClient.exe
```

## Release Workflow

This repository includes a GitHub Actions workflow that reacts to manually published GitHub Releases.

Release flow:

1. Create or edit a GitHub Release in the repository UI.
2. Publish the release.
3. GitHub Actions starts the `Release Build` workflow automatically.
4. The workflow validates required files, restores dependencies, builds the project, publishes a win-x64 package, zips it, and uploads the zip to the same release.

Generated release asset name format:

```text
VSCodePresence-SteamClient-<tag>-win-x64.zip
```

## Workflow Checks

Before uploading a release asset, the workflow verifies:

- [VSCodePresence-SteamClient.csproj](VSCodePresence-SteamClient.csproj) exists
- [appsettings.json](appsettings.json) exists
- [native/win-x64/steam_api64.dll](native/win-x64/steam_api64.dll) exists
- Release build completes successfully
- Published output contains:
  - `VSCodePresence-SteamClient.exe`
  - `VSCodePresence-SteamClient.dll`
  - `appsettings.json`
  - `steam_api64.dll`

If any check fails, the workflow stops before attaching anything to the release.
