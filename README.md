# Zellyfin customizations

Seek-aware playback statistics and large-library UI improvements for Jellyfin.

> **Compatibility:** Jellyfin Server and Jellyfin Web `10.11.11` on Windows
> AMD64 portable. Patches are pinned to the matching upstream Web tag.

## Custom features

| Feature | What it adds | Implementation |
| --- | --- | --- |
| **Seek statistics** | Counts forward and backward seeks per user, item, and playback session in a separate SQLite database. | Server plugin |
| **Seek-count sorting** | Adds `Seek count` / `탐색 횟수` to the library sort menu, including ascending and descending order. | Server API + Web patch |
| **Infinite library scrolling** | Automatically loads the next 100-item batch, making large libraries continuous instead of page-based. | Web patch |
| **Inline media information** | Shows codec, resolution, bitrate, audio, and subtitle details directly on the item page. | Web patch |

This repository contains the reproducible plugin source, Jellyfin Web patches,
and Windows build/deployment scripts for those features. It intentionally does
not contain a Jellyfin runtime, server data, credentials, private media paths,
databases, or generated preview images.

Seek events are inferred from playback progress position changes. They are not
explicit seek events from every Jellyfin client, so delayed progress reports or
session resumes can require detector threshold tuning.

## Repository layout

- `src/Jellyfin.Plugin.SeekStatistics`: plugin source.
- `patches`: patches pinned to Jellyfin Web `v10.11.11`.
- `scripts`: local build, install, migration, and launch scripts.
- `examples`: redacted local-configuration examples.
- `versions.json`: pinned upstream versions and package checksum.

The following local directories are ignored by Git:

- `runtime`: installed binaries, configuration, users, databases, logs,
  plugins, caches, trickplay images, chapter images, and backups.
- `build`: temporary Jellyfin Web checkout and dependency tree.
- `artifacts`, `bin`, and `obj`: generated build outputs.
- `migration/emby-library-paths.json`: private library names and media paths.

## Prepare the local runtime

Extract the official Jellyfin `10.11.11` Windows AMD64 portable archive into:

```text
runtime/system/
```

The expected archive and SHA-256 checksum are recorded in `versions.json`.
Runtime network binding and library settings live under `runtime/data` and are
not version-controlled.

Start Jellyfin in the foreground:

```powershell
.\scripts\start-jellyfin.ps1
```

The launcher uses isolated data/config/cache/log directories under `runtime`
and expects port `8097` to be available. Open <http://localhost:8097> after it
starts.

## Build and install the customizations

Build and install only the plugin:

```powershell
.\scripts\build-install-seek-statistics.ps1
```

Build and install the plugin plus the patched Jellyfin Web client:

```powershell
.\scripts\build-install-seek-sort.ps1
```

The full installer clones Jellyfin Web `v10.11.11` into `build`, applies the
combined seek-sort/infinite-scroll patch and the inline-media-info patch, runs
the production build, backs up the installed web client under
`runtime/backups`, deploys the result, and restarts Jellyfin.

> **Service impact:** Git operations and documentation changes do not affect a
> running server. The build/install scripts intentionally stop and restart the
> local Jellyfin process while deploying files.

## Optional Emby library-path import

Create the private manifest from the redacted example and edit it locally:

```powershell
Copy-Item .\examples\emby-library-paths.example.json `
    .\migration\emby-library-paths.json
```

Preview and validate paths without changing Jellyfin:

```powershell
.\scripts\import-emby-library-paths.ps1
```

Apply them to a configured server and request a library refresh:

```powershell
.\scripts\import-emby-library-paths.ps1 -Apply -RefreshLibrary
```

The script requests administrator credentials through a masked local prompt.
It does not write the password or access token to disk.

## Automatic start

Automatic startup is a per-machine setting and is not included in this
repository. The current deployment uses a shortcut named `Zellyfin Server.lnk`
in the signed-in user's Windows Startup folder so mapped network drives remain
available. The shortcut launches the portable server with the same isolated
runtime paths used by the scripts.

## License

Original work in this repository is licensed under GPL-2.0-only. Jellyfin Web
patches are derivative work of the GPL-2.0-licensed Jellyfin Web project and
remain subject to its copyright and license notices.
