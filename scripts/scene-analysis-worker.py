"""Incremental source-video scene analysis for Zellyfin's scene timeline/search.

Reads Jellyfin's existing SQLite index. Never asks Jellyfin to rescan media.
Each video is checkpointed after every frame and can resume after interruption.
"""

from __future__ import annotations

import argparse
import io
import json
import os
from pathlib import Path
import re
import sqlite3
import subprocess
import time
from PIL import Image, ImageEnhance, ImageStat

MODEL_ID = "florence-community/Florence-2-base"
MODEL_VERSION = "scene-v1"
DOTNET_UNIX_EPOCH_TICKS = 621355968000000000
HOME_VIDEO_ID = "7167A695-4FAF-707C-7364-088E637E2B43"


def source_ticks(stat: os.stat_result) -> int:
    return stat.st_mtime_ns // 100 + DOTNET_UNIX_EPOCH_TICKS


def inventory(database: Path, priorities: dict[str, int]):
    connection = sqlite3.connect(f"file:{database.as_posix()}?mode=ro", uri=True)
    rows = connection.execute(
        """SELECT b.Id, b.Path, b.Name, b.RunTimeTicks,
                  CASE WHEN a.ParentItemId IS NULL THEN 0 ELSE 1 END AS homeVideo
           FROM BaseItems b LEFT JOIN AncestorIds a
             ON a.ItemId = b.Id AND a.ParentItemId = ?
           WHERE b.MediaType = 'Video' AND b.IsFolder = 0 AND b.IsVirtualItem = 0
             AND b.Path IS NOT NULL AND b.RunTimeTicks > 0
           ORDER BY homeVideo DESC, b.DateCreated ASC, b.Id ASC""",
        (HOME_VIDEO_ID,),
    ).fetchall()
    connection.close()
    rows.sort(key=lambda row: (priorities.get(row[0], len(priorities)), not row[4]))
    return rows


def record_path(root: Path, item_id: str) -> Path:
    return root / "items" / f"{item_id.replace('-', '').lower()}.json"


def write_json(path: Path, value: dict):
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(value, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    os.replace(temp, path)


def existing_record(root: Path, item_id: str, stat: os.stat_result):
    try:
        record = json.loads(record_path(root, item_id).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    if (record.get("ModelVersion") != MODEL_VERSION
            or record.get("SourceSize") != stat.st_size
            or record.get("SourceModifiedUtcTicks") != source_ticks(stat)):
        return None
    return record


def extract(ffmpeg: Path, path: str, seconds: float) -> tuple[Image.Image, float]:
    # Old or damaged videos can have a brief region where seeking yields no frame.
    for backoff in (0, 15, 30, 60):
        actual_seconds = max(0, seconds - backoff)
        try:
            result = subprocess.run(
                [str(ffmpeg), "-hide_banner", "-loglevel", "error", "-ss", f"{actual_seconds:.3f}",
                 "-i", path, "-map", "0:v:0", "-frames:v", "1", "-vf", "scale=960:-2",
                 "-q:v", "3", "-f", "image2pipe", "pipe:1"],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=50, check=False,
            )
        except subprocess.TimeoutExpired:
            continue
        if result.returncode == 0 and result.stdout:
            return Image.open(io.BytesIO(result.stdout)).convert("RGB"), actual_seconds
    raise RuntimeError("Could not extract source frame")


def analyze(model, processor, torch, image: Image.Image):
    brightness = ImageStat.Stat(image.convert("L")).mean[0]
    enhanced = (ImageEnhance.Brightness(image).enhance(min(2.6, 105 / max(20, brightness)))
                if brightness < 75 else image)
    result = {}
    for task in ("<MORE_DETAILED_CAPTION>", "<OD>"):
        inputs = processor(text=task, images=enhanced, return_tensors="pt")
        inputs = {key: value.to(model.device) for key, value in inputs.items()}
        with torch.inference_mode():
            ids = model.generate(**inputs, max_new_tokens=128, num_beams=3)
        response = processor.batch_decode(ids, skip_special_tokens=False)[0]
        result.update(processor.post_process_generation(response, task=task, image_size=image.size))
    description = str(result.get("<MORE_DETAILED_CAPTION>", "")).strip()
    labels = result.get("<OD>", {}).get("labels", [])
    return description, tags_for(description, labels, brightness)


def tags_for(description: str, labels: list[str], brightness: float):
    """Small bilingual vocabulary; labels are scene hints, not identity claims."""
    words = description.lower()
    labels = [label.lower() for label in labels]
    tags = set()
    for term, names in (
        ("bed", ("bed", "침대")),
        ("bedroom", ("bedroom", "침실")),
        ("sleep", ("sleeping", "잠든")),
        ("sofa", ("sofa", "소파")),
        ("office", ("office", "사무실")),
        ("factory", ("factory", "공장")),
        ("outside", ("outdoor", "야외")),
        ("street", ("street", "거리")),
        ("kitchen", ("kitchen", "부엌")),
    ):
        if term in words or term in labels:
            tags.update(names)
    if re.search(r"\b(two|2) women\b|\btwo girls\b", words) or labels.count("woman") + labels.count("girl") >= 2:
        tags.update(("two-women", "여성2명"))
    elif "woman" in words or "girl" in words or "woman" in labels or "girl" in labels:
        tags.update(("woman", "여성"))
    if "man" in labels or re.search(r"\b(man|male)\b", words):
        tags.update(("man", "남성"))
    if brightness < 65:
        tags.update(("dark", "어두움"))
    return sorted(tags)


def search_database(root: Path):
    root.mkdir(parents=True, exist_ok=True)
    connection = sqlite3.connect(root / "scene-search.db")
    connection.execute("PRAGMA journal_mode=WAL")
    connection.execute(
        """CREATE VIRTUAL TABLE IF NOT EXISTS SceneSearch USING fts5(
              ItemId UNINDEXED, TimeTicks UNINDEXED, Description, Tags, FrameIndex UNINDEXED
           )"""
    )
    connection.commit()
    return connection


def index_complete(connection: sqlite3.Connection, record: dict):
    with connection:
        connection.execute("DELETE FROM SceneSearch WHERE ItemId = ?", (record["ItemId"],))
        connection.executemany(
            "INSERT INTO SceneSearch (ItemId, TimeTicks, Description, Tags, FrameIndex) VALUES (?, ?, ?, ?, ?)",
            [(record["ItemId"], scene["TimeTicks"], scene["Description"],
              " ".join(scene["Tags"]), scene["FrameIndex"]) for scene in record["Scenes"]],
        )


def process_item(row, args, model, processor, torch, search):
    item_id, media_path, name, duration_ticks, _ = row
    stat = Path(media_path).stat()
    old = existing_record(args.output, item_id, stat)
    if (old and old.get("Status") == "complete" and old.get("ExpectedSamples") == args.samples
            and old.get("CompletedSamples") == args.samples):
        return "skip"
    count = args.samples
    frames = {scene["FrameIndex"]: scene for scene in (old or {}).get("Scenes", [])} \
        if old and old.get("ExpectedSamples") == count else {}
    record = {
        "ItemId": item_id, "Status": "running", "ModelVersion": MODEL_VERSION,
        "SourceSize": stat.st_size, "SourceModifiedUtcTicks": source_ticks(stat),
        "ExpectedSamples": count, "CompletedSamples": len(frames),
        "Scenes": sorted(frames.values(), key=lambda scene: scene["FrameIndex"]), "Error": None,
    }
    write_json(record_path(args.output, item_id), record)
    for index in range(count):
        if index in frames:
            continue
        seconds = duration_ticks / 10_000_000 * (index + 1) / (count + 1)
        image, actual_seconds = extract(args.ffmpeg, media_path, seconds)
        description, tags = analyze(model, processor, torch, image)
        frame_path = args.output / "frames" / item_id.replace("-", "").lower() / f"{index}.jpg"
        frame_path.parent.mkdir(parents=True, exist_ok=True)
        temp = frame_path.with_suffix(".jpg.tmp")
        image.thumbnail((800, 800))
        image.save(temp, format="JPEG", quality=85)
        os.replace(temp, frame_path)
        frames[index] = {"TimeTicks": round(actual_seconds * 10_000_000),
                         "Description": description, "Tags": tags, "FrameIndex": index}
        record["Scenes"] = [frames[key] for key in sorted(frames)]
        record["CompletedSamples"] = len(frames)
        write_json(record_path(args.output, item_id), record)
    index_complete(search, record)
    record["Status"] = "complete"
    write_json(record_path(args.output, item_id), record)
    return "complete"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--library-db", type=Path, required=True)
    parser.add_argument("--ffmpeg", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--priority-ids", type=Path)
    parser.add_argument("--only-priority", action="store_true")
    parser.add_argument("--samples", type=int, default=24)
    parser.add_argument("--max-items", type=int, default=100)
    parser.add_argument("--time-limit-seconds", type=int, default=7200)
    args = parser.parse_args()
    if args.samples < 3 or args.samples > 100:
        parser.error("--samples must be between 3 and 100")
    priority_ids = list(dict.fromkeys(args.priority_ids.read_text(encoding="utf-8").split())) if args.priority_ids else []
    priorities = {item_id: position for position, item_id in enumerate(priority_ids)}
    if args.only_priority and not priorities:
        parser.error("--only-priority requires --priority-ids")
    rows = inventory(args.library_db, priorities)
    if args.only_priority:
        rows = [row for row in rows if row[0] in priorities]
    pending = []
    for row in rows:
        try:
            stat = Path(row[1]).stat()
        except OSError:
            continue
        old = existing_record(args.output, row[0], stat)
        if (not old or old.get("Status") != "complete" or old.get("ExpectedSamples") != args.samples
                or old.get("CompletedSamples") != args.samples):
            pending.append(row)
    print(json.dumps({"indexedVideos": len(rows), "pendingVideos": len(pending),
                      "priorityVideos": len(priorities)}), flush=True)
    if not pending:
        return
    import torch
    from transformers import AutoProcessor, Florence2ForConditionalGeneration
    model = Florence2ForConditionalGeneration.from_pretrained(MODEL_ID).to(
        "cuda" if torch.cuda.is_available() else "cpu").eval()
    processor = AutoProcessor.from_pretrained(MODEL_ID)
    search = search_database(args.output)
    started = time.monotonic()
    completed = failed = 0
    for row in pending:
        if completed + failed >= args.max_items or time.monotonic() - started > args.time_limit_seconds:
            break
        try:
            result = process_item(row, args, model, processor, torch, search)
            if result == "complete":
                completed += 1
                print(json.dumps({"itemId": row[0], "name": row[2], "completedVideos": completed},
                                 ensure_ascii=True), flush=True)
        except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
            failed += 1
            print(json.dumps({"itemId": row[0], "failed": str(error)}, ensure_ascii=True), flush=True)
            try:
                stat = Path(row[1]).stat()
                record = existing_record(args.output, row[0], stat)
                if record:
                    record["Status"] = "failed"
                    record["Error"] = str(error)[:200]
                    write_json(record_path(args.output, row[0]), record)
            except OSError:
                pass
    print(json.dumps({"completed": completed, "failed": failed, "elapsedSeconds":
                      round(time.monotonic() - started), "done": True}), flush=True)


if __name__ == "__main__":
    main()
