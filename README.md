# Zellyfin customizations

Seek-aware playback statistics and large-library UI improvements for Jellyfin.

> **Compatibility:** Jellyfin Server and Jellyfin Web `10.11.11` on Windows
> AMD64 portable. Patches are pinned to the matching upstream Web tag.

## Custom features

| Feature | What it adds | Implementation |
| --- | --- | --- |
| **Seek statistics** | Counts forward and backward seeks per user, item, and playback session in a separate SQLite database. | Server plugin |
| **Seek-count sorting** | Adds `Seek count` / `탐색 횟수` to the library sort menu, including ascending and descending order. | Server API + Web patch |
| **File-size sorting** | Adds `File size` / `파일 크기`; ascending shows the smallest files first and descending shows the largest. Unknown sizes and folders follow sized files. | Server API + Web patch |
| **Infinite library scrolling** | Automatically loads the next 100-item batch, making large libraries continuous instead of page-based. | Web patch |
| **Vertical favorites and search** | Wrapped card grids load further results in 100-item batches near the bottom; failed requests can be retried. Search changes reset the list. | Web patch |
| **Player file actions** | Shows the selected source's file size. A delete button beside favorites asks for confirmation, stops playback and server transcoding, deletes the file, then plays the remaining queue. | Web patch |
| **Inline list controls** | Sort, combine filters including non-favorites, and choose 50/100/200 items per load without opening a filter or sort dialog. | Web patch |
| **Inline media information** | Shows codec, resolution, bitrate, audio, and subtitle details directly on the item page. | Web patch |
| **Scene previews** | Shows 25%, 50%, and 75% frames on video hover and detail pages. Missing Jellyfin trickplay frames are extracted on demand and cached without a library scan. | Server plugin + Web patch |
| **Unified video search** | One search box finds filenames and analyzed scenes. All terms are combined across a video's filename and one scene; scene hits include a timestamp. | Server plugin + Web patch |

This repository contains the reproducible plugin source, Jellyfin Web patches,
and Windows build/deployment scripts for those features. It intentionally does
not contain a Jellyfin runtime, server data, credentials, private media paths,
databases, or generated preview images.

Seek events are inferred from playback progress position changes. They are not
explicit seek events from every Jellyfin client, so delayed progress reports or
session resumes can require detector threshold tuning.

File-size sorting uses existing indexed byte sizes and does not scan media or
generate thumbnails. Custom sorts apply library access rules and filters before
sorting the full candidate list and taking a page. Equal values use name and ID
as stable tie breakers. Changing sorting or filters resets infinite scrolling;
late responses from earlier requests cannot replace the current sort.

List filters and sorting are edited above the results. `Not a favorite` uses
the server's `IsFavorite=false` query; date and size sorts apply to the full
filtered result before paging. The per-load preference is kept per user and
server in the browser and also controls favorites and search batches. Changing
filters, order, or batch size restarts the list at the first result.

Player deletion is available for permitted single-file local videos. Alternate
versions, multipart videos, and remote media are excluded because the server's
item-deletion endpoint can remove more than the selected source. Confirmation
shows the file path and warns about associated subtitles/artwork. Cancellation
leaves playback intact; deletion failures do not advance the queue. Sizes use
the selected source's indexed size and show unknown when unavailable.
Run `node --test tests/player-file-actions.test.cjs` with `JELLYFIN_WEB_SOURCE`
set to validate confirmation, ordering, failures, queue transitions, and scope.

The patch chain preserves the deployed home-video folder/video controls. Use
`scripts/install-library-sorting.ps1` with an explicit runtime project, built web
directory and plugin DLL for Windows-service deployments. It stages and backs
up files, restarts the service with administrator privileges, and checks health.

Verify the patched web controller and actual server data with:

```powershell
$env:JELLYFIN_WEB_SOURCE = 'C:\path\to\build\jellyfin-web-10.11.11'
node --test tests/library-sorting.test.cjs
python scripts/verify-library-sorts.py --runtime C:\path\to\runtime --output artifacts/sort-verification.json
```

The read-only verifier uses an existing local administrator session in memory,
checks every home-video library in both directions, verifies page boundaries,
and compares custom-filter counts with the standard API. Its report includes
the number of distinct values: all-equal or missing ratings do not demonstrate
meaningful ordering differences.

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
web customization patches (including scene analysis), runs
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

## Scene analysis

The scene timeline, completed-only filter, and scene search are included in the
web and Seek Statistics plugin builds. Install the local analysis and translation
workers on the machine hosting the runtime with:

```powershell
.\scripts\install-scene-analysis-batch.ps1 -ProjectRoot C:\path\to\zellyfin
```

The global search box also searches scene descriptions and tags. All query terms
must match within one video, using its filename and at most one analyzed scene; filename-only
matches still work before analysis finishes. Results are grouped by video and
show up to three matching timestamps. This uses the existing indexes and does
not start a library scan.

The same box accepts `루루`, `아이자와`, `Ruru`, or `Aizawa` to find videos whose
filename or title contains a product code in the locally saved Ruru Aizawa
filmography at `runtime/worker/scene-analysis/filmography/ruru-aizawa.json`.
Actor name plus scene terms narrows results to matching videos and timestamps.
The catalog contains 293 AVBom codes and one user-reported additional code;
matches indicate product-code overlap, not verified face recognition or a
complete count of every appearance in the library.

The search-target selector offers All, Metadata, Performer name, and File name.
Metadata searches video synopses and analyzed scene descriptions/tags. Performer
name searches the saved performer catalog (currently Ruru Aizawa); File name
searches video titles and filenames. All combines these sources. The selected
mode is kept in the search URL, and every mode uses AND semantics for multiple
query terms.

The plugin adds `영상 장면 분석` and `장면 설명 한글 번역` under Dashboard → Scheduled Tasks.
Analysis runs daily at 02:00 for up to two hours; translation runs at 04:30 for
up to one hour. Either task can also be started or stopped in the dashboard.
Analysis reads the existing Jellyfin SQLite index, samples source video
independently of trickplay images, and checkpoints each video's English captions,
tags, preview frames, and FTS search rows under `runtime/data/data/scene-analysis`.
Translation uses the local `facebook/nllb-200-distilled-600M` model, keeps English
captions, adds Korean captions, and updates search without rereading video files.
The translation model is downloaded separately and is licensed CC BY-NC 4.0;
review its model card before using this setup commercially.
Neither task requests a Jellyfin library rescan. Interrupted tasks resume from
completed frames or translations. `runtime/worker/scene-analysis/priority-items.txt`,
when present, puts selected videos first. The earlier Windows task named
`Zellyfin Scene Analysis` must be disabled after switching to the plugin task.

When a video's scene analysis is complete, its preview uses evenly spaced
source-video analysis frames: six on library-card hover and twelve on the item
detail page. The scene timeline still exposes all analyzed frames. Videos that
are not yet analyzed retain the existing three-frame 25/50/75% preview.

## Automatic start

Automatic startup is a per-machine setting and is not included in this
repository. The current deployment uses a shortcut named `Zellyfin Server.lnk`
in the signed-in user's Windows Startup folder so mapped network drives remain
available. The shortcut launches the portable server with the same isolated
runtime paths used by the scripts.

## FANZA collector network route

`scripts/run-synopsis-harvest-proxy.ps1` runs the FANZA worker through a
dedicated proxy without connecting or disconnecting the computer-wide VPN.
Set `FANZA_PROXY_URL` in the launching process to a Japan-based HTTP(S) proxy
or `socks5h://` proxy, then run the wrapper. SOCKS requires `pysocks` in the
worker Python environment. The URL may contain proxy service credentials;
keep it out of shell history and checked-in files. The wrapper first probes a
FANZA search page and starts the batch only if the route is usable. A proxy
failure stops the worker without a direct-network retry. Existing successful
codes remain checkpointed in `synopsis-index.db`.

NordVPN's published SOCKS5 server list currently has no Japan endpoint, so
the NordVPN subscription by itself does not supply the Japan proxy URL needed
for this launcher. The older `run-synopsis-harvest.ps1` still uses a temporary
computer-wide NordVPN connection; use the proxy launcher when an appropriate
proxy endpoint is available.

## License

Original work in this repository is licensed under GPL-2.0-only. Jellyfin Web
patches are derivative work of the GPL-2.0-licensed Jellyfin Web project and
remain subject to its copyright and license notices.
