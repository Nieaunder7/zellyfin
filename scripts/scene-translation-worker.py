"""Translate completed English scene captions to Korean without reading video files.

The source captions remain in Description. Each translated batch is checkpointed
in the item's JSON; the search index is refreshed when a full item is complete.
"""

from __future__ import annotations

import argparse
from contextlib import closing
import json
import os
from pathlib import Path
import re
import sqlite3
import time

MODEL_ID = "facebook/nllb-200-distilled-600M"
MODEL_VERSION = "nllb-200-distilled-600m-en-ko-v1"
HANGUL = re.compile(r"[가-힣]")


def read_record(path: Path) -> dict | None:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def write_record(path: Path, record: dict) -> None:
    temporary = path.with_suffix(path.suffix + ".translation.tmp")
    temporary.write_text(json.dumps(record, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    os.replace(temporary, path)


def needs_translation(record: dict | None) -> bool:
    if not record or record.get("Status") != "complete":
        return False
    scenes = record.get("Scenes") or []
    if not scenes:
        return False
    if record.get("TranslationModelVersion") != MODEL_VERSION or record.get("TranslationStatus") != "complete":
        return True
    return any(scene.get("Description") and not scene.get("DescriptionKo") for scene in scenes)


def translate_batch(texts: list[str], tokenizer, model, torch) -> list[str]:
    inputs = tokenizer(texts, return_tensors="pt", padding=True, truncation=True, max_length=512).to(model.device)
    with torch.inference_mode():
        generated = model.generate(**inputs,
                                   forced_bos_token_id=tokenizer.convert_tokens_to_ids("kor_Hang"),
                                   max_length=200, num_beams=2)
    translated = [text.strip() for text in tokenizer.batch_decode(generated, skip_special_tokens=True)]
    if len(translated) != len(texts) or any(not HANGUL.search(text) for text in translated):
        raise RuntimeError("Local translation model did not produce Korean text")
    return translated


def refresh_search(root: Path, record: dict) -> None:
    with closing(sqlite3.connect(root / "scene-search.db", timeout=30)) as connection, connection:
        connection.execute("PRAGMA busy_timeout=30000")
        connection.execute(
            """CREATE VIRTUAL TABLE IF NOT EXISTS SceneSearch USING fts5(
                  ItemId UNINDEXED, TimeTicks UNINDEXED, Description, Tags, FrameIndex UNINDEXED
               )"""
        )
        connection.execute("DELETE FROM SceneSearch WHERE ItemId = ?", (record["ItemId"],))
        connection.executemany(
            "INSERT INTO SceneSearch (ItemId, TimeTicks, Description, Tags, FrameIndex) VALUES (?, ?, ?, ?, ?)",
            [(record["ItemId"], scene["TimeTicks"],
              "\n".join(filter(None, (scene.get("Description", ""), scene.get("DescriptionKo", "")))),
              " ".join(scene.get("Tags", [])), scene["FrameIndex"]) for scene in record["Scenes"]],
        )


def translate_record(path: Path, record: dict, root: Path, tokenizer, model, torch, batch_size: int) -> None:
    scenes = record["Scenes"]
    same_version = record.get("TranslationModelVersion") == MODEL_VERSION
    if not same_version:
        for scene in scenes:
            scene.pop("DescriptionKo", None)
    pending = [index for index, scene in enumerate(scenes)
               if scene.get("Description") and not scene.get("DescriptionKo")]
    record["TranslationModelVersion"] = MODEL_VERSION
    record["TranslationStatus"] = "running"
    record["TranslationError"] = None
    record["ExpectedTranslations"] = sum(bool(scene.get("Description")) for scene in scenes)
    record["TranslatedSamples"] = record["ExpectedTranslations"] - len(pending)
    write_record(path, record)

    for start in range(0, len(pending), batch_size):
        batch = pending[start:start + batch_size]
        translated = translate_batch([scenes[index]["Description"] for index in batch], tokenizer, model, torch)
        for index, text in zip(batch, translated):
            scenes[index]["DescriptionKo"] = text
        record["TranslatedSamples"] += len(batch)
        write_record(path, record)

    refresh_search(root, record)
    record["TranslationStatus"] = "complete"
    write_record(path, record)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--priority-ids", type=Path)
    parser.add_argument("--max-items", type=int, default=300)
    parser.add_argument("--time-limit-seconds", type=int, default=3600)
    parser.add_argument("--batch-size", type=int, default=8)
    args = parser.parse_args()
    if args.max_items < 1 or args.time_limit_seconds < 1 or not 1 <= args.batch_size <= 32:
        parser.error("Invalid batch limits")

    priorities = []
    if args.priority_ids and args.priority_ids.exists():
        priorities = list(dict.fromkeys(args.priority_ids.read_text(encoding="utf-8").split()))
    priority_order = {item_id.lower().replace("-", ""): index for index, item_id in enumerate(priorities)}
    pending = []
    for path in (args.output / "items").glob("*.json"):
        record = read_record(path)
        if needs_translation(record):
            item_id = record.get("ItemId", "").lower().replace("-", "")
            pending.append((priority_order.get(item_id, len(priority_order)), path))
    pending.sort(key=lambda value: (value[0], value[1].name))
    print(json.dumps({"pendingVideos": len(pending), "priorityVideos": len(priorities)}), flush=True)
    if not pending:
        return

    import torch
    from transformers import AutoModelForSeq2SeqLM, AutoTokenizer

    tokenizer = AutoTokenizer.from_pretrained(MODEL_ID, src_lang="eng_Latn")
    model = AutoModelForSeq2SeqLM.from_pretrained(MODEL_ID).to(
        "cuda" if torch.cuda.is_available() else "cpu").eval()
    started = time.monotonic()
    completed = failed = 0
    for _, path in pending:
        if completed + failed >= args.max_items or time.monotonic() - started > args.time_limit_seconds:
            break
        record = read_record(path)
        if not needs_translation(record):
            continue
        try:
            translate_record(path, record, args.output, tokenizer, model, torch, args.batch_size)
            completed += 1
            print(json.dumps({"itemId": record["ItemId"], "translatedVideos": completed}), flush=True)
        except (OSError, RuntimeError, ValueError, sqlite3.Error) as error:
            failed += 1
            latest = read_record(path)
            if latest and latest.get("Status") == "complete":
                latest["TranslationStatus"] = "failed"
                latest["TranslationError"] = str(error)[:200]
                write_record(path, latest)
            print(json.dumps({"itemId": record["ItemId"], "failed": str(error)}), flush=True)
    print(json.dumps({"translated": completed, "failed": failed,
                      "elapsedSeconds": round(time.monotonic() - started), "done": True}), flush=True)


if __name__ == "__main__":
    main()
