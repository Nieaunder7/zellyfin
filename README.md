# Zellyfin customizations

Personal Jellyfin customizations for the Windows AMD64 portable build of
Jellyfin `10.11.11`. This repository contains reproducible source, patches,
and deployment scripts. It intentionally does not contain a Jellyfin runtime,
server data, credentials, media paths, databases, or generated preview images.

## Features

- `Seek Statistics` server plugin that infers and stores forward/backward seek
  events in a separate SQLite database.
- `Seek count` library sorting in Jellyfin Web.
- Infinite scrolling for the legacy library list view.
- Inline media information on item detail pages.
- Read-only duplicate-media dashboard that groups indexed videos by normalized
  unique code and never deletes, moves, or renames media files during scanning.
- Batched repair task for videos missing generated chapters or chapter images.

Seek events are inferred from playback progress position changes. They are not
explicit seek events from every Jellyfin client, so delayed progress reports or
session resumes can require detector threshold tuning.

## Repository layout

- `src/Jellyfin.Plugin.SeekStatistics`: seek-statistics plugin source.
- `src/Jellyfin.Plugin.DuplicateMedia`: duplicate-media dashboard and scanner.
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

Build and install the duplicate-media dashboard with:

```powershell
.\scripts\build-install-duplicate-media.ps1 -RestartJellyfin
```

After restart, open `중복 미디어` from the Jellyfin administration menu or run
`Scan duplicate media by unique code` from Scheduled Tasks. The scanner reads
Jellyfin's existing video index and stores candidate results in its own local
SQLite database. Scanning is read-only. In each result group, files can be
selected explicitly for preservation; the global largest/smallest controls only
prepare a default keep-selection per visible group and never delete automatically.
Individual keep checkboxes can then be adjusted to preserve multiple files.
Selected groups can then be processed in one batch. Deletion requires a
second confirmation containing the selected group count, leaves one group item,
revalidates the current library root, path, file size, and modification time,
and records its outcome in the plugin database. It deletes only the selected
media file, not its containing folder or adjacent sidecar files. Permanent
deletion bypasses the recycle bin and may not be recoverable on NAS storage.
When Jellyfin chapter images exist, the dashboard shows up to five evenly
sampled chapter thumbnails beneath each video for visual comparison.
The `Repair missing chapters and chapter images` scheduled task runs in small
batches, preserves existing chapter images, and retries unfinished videos
without modifying source media files.

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
