"""Progressive source-video search for the two-women-on-a-bed scene.

Reads Jellyfin's existing index without rescanning it. Saves every scored frame so
an interrupted search can resume. It never marks a video analysis-complete.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import io
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import time
from PIL import Image, ImageEnhance, ImageStat

LIBRARY_ID = "7167A695-4FAF-707C-7364-088E637E2B43"  # 홈 비디오 및 사진 (not ...4)
MODEL_ID = "openai/clip-vit-base-patch32"
PROMPTS = [
    "Two women lying together on a bed in a dark bedroom, no man visible.",
    "Two women sleeping side by side in a dimly lit bedroom.",
    "Two women resting on a bed in a dark room.",
    "A dark bedroom with two women on a bed.",
    "A man and a woman lying on a bed.",
    "A man standing in a bedroom with a woman.",
    "A single woman lying alone on a bed.",
    "Two women standing in a bright room.",
    "Two men lying on a bed.",
    "A bedroom with an empty bed and no people.",
]
FOCUSED_PROMPTS = [
    "Two women sleeping peacefully side by side on a bed in a dark bedroom.",
    "Two women lying asleep under blankets in a dim bedroom.",
    "Two women lying motionless on a bed with their eyes closed in darkness.",
    "Two sleeping women in a dark room in an old low quality video.",
    "Two women awake and talking on a bed.",
    "Two women engaged in sexual activity on a bed.",
    "A man and a woman together on a bed.",
    "One woman asleep alone in bed.",
    "Two people standing in a bedroom.",
    "An empty dark bedroom.",
]


def inventory(library_db: Path, max_items: int | None):
    connection = sqlite3.connect(f"file:{library_db.as_posix()}?mode=ro", uri=True)
    try:
        rows = connection.execute(
            """SELECT b.Id, b.Path, b.Name, b.Width, b.Height, b.RunTimeTicks, b.DateCreated
               FROM AncestorIds a JOIN BaseItems b ON b.Id = a.ItemId
               WHERE a.ParentItemId = ? AND b.MediaType = 'Video' AND b.IsFolder = 0
                 AND b.IsVirtualItem = 0 AND b.Path IS NOT NULL AND b.RunTimeTicks > 0
               ORDER BY CASE WHEN b.Height <= 480 THEN 0 WHEN b.Height <= 720 THEN 1 ELSE 2 END,
                        b.DateCreated ASC, b.Id ASC""",
            (LIBRARY_ID,),
        ).fetchall()
        return rows[:max_items] if max_items else rows
    finally:
        connection.close()


def create_database(path: Path):
    path.parent.mkdir(parents=True, exist_ok=True)
    connection = sqlite3.connect(path)
    connection.execute("PRAGMA journal_mode=WAL")
    connection.execute(
        """CREATE TABLE IF NOT EXISTS Scores (
            ItemId TEXT NOT NULL, Fraction REAL NOT NULL, TimeTicks INTEGER NOT NULL,
            SourceSize INTEGER NOT NULL, SourceMtimeNs INTEGER NOT NULL,
            Score REAL NOT NULL, Positive REAL NOT NULL, Male REAL NOT NULL,
            Brightness REAL NOT NULL, Name TEXT NOT NULL,
            PRIMARY KEY (ItemId, Fraction)
        )"""
    )
    connection.commit()
    return connection


def extract_frame(task):
    item_id, media_path, name, width, height, duration_ticks, fraction, ffmpeg = task
    try:
        source = Path(media_path)
        stat = source.stat()
        seconds = max(0.0, duration_ticks / 10_000_000 * fraction)
        result = subprocess.run(
            [str(ffmpeg), "-hide_banner", "-loglevel", "error", "-ss", f"{seconds:.3f}",
             "-i", str(source), "-map", "0:v:0", "-frames:v", "1",
             "-vf", "scale=640:-2", "-q:v", "5", "-f", "image2pipe", "pipe:1"],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=35, check=False,
        )
        if result.returncode != 0 or not result.stdout:
            return item_id, fraction, "extract-failed", None
        image = Image.open(io.BytesIO(result.stdout)).convert("RGB")
        brightness = ImageStat.Stat(image.convert("L")).mean[0]
        if brightness < 75:
            enhanced = ImageEnhance.Brightness(image).enhance(min(2.6, 105 / max(20, brightness)))
        else:
            enhanced = image
        return item_id, fraction, None, {
            "image": enhanced, "brightness": brightness, "name": name,
            "timeTicks": round(seconds * 10_000_000), "sourceSize": stat.st_size,
            "sourceMtimeNs": stat.st_mtime_ns,
        }
    except (OSError, subprocess.TimeoutExpired, ValueError):
        return item_id, fraction, "unavailable", None


def score_batch(model, processor, torch, batch, prompt_set):
    images = [data["image"] for _, _, data in batch]
    prompts = FOCUSED_PROMPTS if prompt_set == "sleep" else PROMPTS
    inputs = processor(text=prompts, images=images, return_tensors="pt", padding=True)
    inputs = {key: value.to(model.device) for key, value in inputs.items()}
    with torch.inference_mode():
        logits = model(**inputs).logits_per_image.float()
        probabilities = logits.softmax(dim=-1).cpu().tolist()
    result = []
    for ((item_id, fraction, data), values) in zip(batch, probabilities):
        positive = sum(values[:4])
        if prompt_set == "sleep":
            male = values[6]
            score = positive - 0.7 * sum(values[4:7]) - 0.2 * values[7]
        else:
            male = values[4] + values[5] + values[8]
            score = positive - 0.7 * male - 0.2 * values[6]
        result.append((item_id, fraction, data, score, positive, male))
    return result


def run(args):
    import torch
    from transformers import CLIPModel, CLIPProcessor

    rows = inventory(args.library_db, args.max_items)
    if args.candidate_ids:
        selected_ids = set(args.candidate_ids.read_text(encoding="utf-8").split())
        rows = [row for row in rows if row[0] in selected_ids]
    connection = create_database(args.output)
    existing = {(row[0], row[1]): (row[2], row[3]) for row in connection.execute(
        "SELECT ItemId, Fraction, SourceSize, SourceMtimeNs FROM Scores")}
    tasks = []
    for item_id, media_path, name, width, height, duration_ticks, _ in rows:
        try:
            stat = Path(media_path).stat()
        except OSError:
            continue
        for fraction in args.fractions:
            if existing.get((item_id, fraction)) == (stat.st_size, stat.st_mtime_ns):
                continue
            tasks.append((item_id, media_path, name, width, height, duration_ticks, fraction, args.ffmpeg))
    print(json.dumps({"libraryVideos": len(rows), "framesToScore": len(tasks), "device": "cuda" if torch.cuda.is_available() else "cpu"}), flush=True)
    if not tasks:
        return
    model = CLIPModel.from_pretrained(MODEL_ID).to("cuda" if torch.cuda.is_available() else "cpu").eval()
    processor = CLIPProcessor.from_pretrained(MODEL_ID)
    scored = failed = 0
    batch = []
    started = time.monotonic()
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = {pool.submit(extract_frame, task): task for task in tasks}
        for future in concurrent.futures.as_completed(futures):
            item_id, fraction, error, data = future.result()
            if error:
                failed += 1
                continue
            batch.append((item_id, fraction, data))
            if len(batch) < args.batch_size and scored + failed + len(batch) < len(tasks):
                continue
            for item_id, fraction, record, score, positive, male in score_batch(model, processor, torch, batch, args.prompt_set):
                connection.execute(
                    """INSERT OR REPLACE INTO Scores
                       (ItemId, Fraction, TimeTicks, SourceSize, SourceMtimeNs, Score,
                        Positive, Male, Brightness, Name)
                       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                    (item_id, fraction, record["timeTicks"], record["sourceSize"],
                     record["sourceMtimeNs"], score, positive, male,
                     record["brightness"], record["name"]),
                )
                scored += 1
            connection.commit()
            batch.clear()
            if scored and scored % 64 < args.batch_size:
                print(json.dumps({"scored": scored, "failed": failed, "total": len(tasks),
                                  "elapsedSeconds": round(time.monotonic() - started)}), flush=True)
    if batch:
        for item_id, fraction, record, score, positive, male in score_batch(model, processor, torch, batch, args.prompt_set):
            connection.execute(
                """INSERT OR REPLACE INTO Scores
                   (ItemId, Fraction, TimeTicks, SourceSize, SourceMtimeNs, Score,
                    Positive, Male, Brightness, Name) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                (item_id, fraction, record["timeTicks"], record["sourceSize"],
                 record["sourceMtimeNs"], score, positive, male, record["brightness"], record["name"]),
            )
        connection.commit()
    print(json.dumps({"scored": scored, "failed": failed, "total": len(tasks), "done": True}), flush=True)


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--library-db", type=Path, required=True)
    parser.add_argument("--ffmpeg", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--max-items", type=int)
    parser.add_argument("--candidate-ids", type=Path)
    parser.add_argument("--prompt-set", choices=["general", "sleep"], default="general")
    parser.add_argument("--fractions", type=float, nargs="+", default=[0.60, 0.78, 0.90])
    parser.add_argument("--workers", type=int, default=3)
    parser.add_argument("--batch-size", type=int, default=16)
    return parser.parse_args()


if __name__ == "__main__":
    run(parse_args())
