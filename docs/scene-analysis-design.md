# Independent scene analysis

## Scope

Scene analysis is independent of Jellyfin's trickplay images, chapter images, and the three-frame story preview. It reads original video files and keeps its own resumable index. It must not trigger a Jellyfin library rescan or write to the media files.

## Index and status

For each video, store the Jellyfin item ID, a source fingerprint (path, size, modification time), analysis model/version, expected sample count, completed sample count, timestamps, image references, tags, confidence, and status in a separate database. Use `queued`, `running`, `complete`, `failed`, and `stale` statuses. Set `complete` only after every planned sample and its tags are committed. A changed source or model version becomes `stale` until reprocessed. A worker interruption retains completed samples and resumes from the next missing sample.

The first pass extracts source frames at approximately 15–30 second intervals and scene changes, at a resolution chosen for the visual model rather than Jellyfin's 320 px trickplay format. Nearby frames can be sampled more densely for promising or ambiguous scenes. The model assigns searchable descriptions and structured, confidence-scored tags such as person count, indoor/outdoor, approximate setting, bed, and lying posture. Apparent gender, closed eyes, and sleeping are uncertain visual cues; results must not assert identity or actual sleep as fact.

## Item detail page

Place a `장면 분석` section immediately below the synopsis. Show analysis status and `completed / expected` progress without refreshing the page. Show available results while processing, clearly marked as partial. A completed item shows a chronological, expandable list of timestamped scene cards with a frame, short description, and tags. Group similar adjacent samples for readability, but label times as approximate unless boundaries were verified. Selecting a timestamp is an explicit scene jump; ordinary playback still starts at the beginning.

## Library and search

Add an inline `분석 완료만` filter to library controls. Apply it on the server before pagination so it composes with favorite/non-favorite, sort order, and the 50/100/200-item batch size. Only status `complete` with a current source fingerprint and model version passes the filter; `running`, `failed`, `stale`, and `queued` do not. Changing the filter restarts results from page one; background progress updates must not reset scroll position.

Scene search uses the same independent index. Text search retrieves candidate timestamps, then rechecks the strongest candidates against higher-resolution source frames. Results link to the item, timestamp, and preview. Existing trickplay images are not an input or prerequisite.

## Validation

- An item under analysis exposes partial timestamps and progress, then becomes `complete` only when all samples are committed.
- `분석 완료만` includes completed items and excludes queued, running, failed, and stale items across every page.
- The filter combines correctly with `즐겨찾기 아님` and oldest-first sorting, including 50/100/200-item loads.
- Returning to a scrolled library page or receiving a progress update does not jump to the top.
- Clicking a scene timestamp seeks to that scene, while ordinary play still begins at zero.
- A changed video is marked stale and can be reanalyzed without rescanning Jellyfin's library.
