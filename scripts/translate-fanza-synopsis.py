"""Translate harvested Japanese synopses in a separate, resumable batch.

Uses Google's unofficial web translation endpoint. The collector may write
concurrently to the same WAL database. No ChatGPT tokens, GPU or media access.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import sqlite3
import sys
import time

import requests

VERSION = "google-web-ja-ko-glossary-v1"
URL = "https://translate.googleapis.com/translate_a/single"
HANGUL = re.compile(r"[가-힣]")
GLOSSARY = (
    ("パンチラ", "下着が見えること"),
    ("アソコ", "性器"),
    ("チ●ポ", "性器"),
    ("チ○ポ", "性器"),
    ("ボク", "僕"),
)


class AccessBlocked(RuntimeError):
    pass


def now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def source_hash(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def normalize(value: str) -> str:
    for original, replacement in GLOSSARY:
        value = value.replace(original, replacement)
    return value


def chunks(value: str, limit: int = 350) -> list[str]:
    pieces = re.split(r"(?<=[。！？!?])", value.strip())
    result: list[str] = []
    current = ""
    for piece in pieces:
        if current and len(current) + len(piece) > limit:
            result.append(current)
            current = ""
        while len(piece) > limit:
            result.append(piece[:limit])
            piece = piece[limit:]
        current += piece
    if current:
        result.append(current)
    return result


def open_index(path: Path) -> sqlite3.Connection:
    db = sqlite3.connect(path, timeout=30)
    db.execute("PRAGMA journal_mode=WAL")
    db.execute("PRAGMA busy_timeout=30000")
    db.execute("""CREATE TABLE IF NOT EXISTS SynopsisTranslation (
        Code TEXT PRIMARY KEY, SourceHash TEXT NOT NULL,
        ModelVersion TEXT NOT NULL, Status TEXT NOT NULL,
        SynopsisKo TEXT, Attempts INTEGER NOT NULL DEFAULT 0,
        UpdatedUtc TEXT, Error TEXT)""")
    db.commit()
    return db


def counts(db: sqlite3.Connection) -> dict:
    source = db.execute("SELECT COUNT(*) FROM Work WHERE Status='success'").fetchone()[0]
    rows = dict(db.execute(
        "SELECT Status, COUNT(*) FROM SynopsisTranslation WHERE ModelVersion=? GROUP BY Status",
        (VERSION,),
    ))
    stale = db.execute(
        "SELECT COUNT(*) FROM SynopsisTranslation WHERE ModelVersion<>?", (VERSION,)
    ).fetchone()[0]
    return {"source_synopses": source, "translation_version": VERSION,
            "stale_translations": stale, **rows}


def pending(db: sqlite3.Connection) -> tuple[list[tuple[str, str, str]], int]:
    rows = db.execute(
        """SELECT w.Code,w.Synopsis,t.SourceHash,t.ModelVersion,t.Status
           FROM Work w LEFT JOIN SynopsisTranslation t ON t.Code=w.Code
           WHERE w.Status='success' AND length(w.Synopsis)>0
           ORDER BY w.Code"""
    ).fetchall()
    source_pending = db.execute(
        "SELECT COUNT(*) FROM Work WHERE Status IN ('pending','error','blocked')"
    ).fetchone()[0]
    todo = []
    for code, original, old_hash, version, status in rows:
        digest = source_hash(original)
        if old_hash == digest and version == VERSION and status in ("complete", "failed"):
            continue
        todo.append((code, original, digest))
    return todo, source_pending


def translate(original: str, session: requests.Session, timeout: int, delay: float) -> str:
    translated = []
    for index, piece in enumerate(chunks(normalize(original))):
        if index:
            time.sleep(delay)
        response = session.get(
            URL,
            params={"client": "gtx", "sl": "ja", "tl": "ko", "dt": "t", "q": piece},
            timeout=timeout,
        )
        if response.status_code in (403, 429):
            raise AccessBlocked(f"HTTP {response.status_code}")
        response.raise_for_status()
        try:
            segments = response.json()[0]
            translated.append("".join(s[0] for s in segments if s and s[0]).strip())
        except (ValueError, IndexError, TypeError, KeyError) as exc:
            raise AccessBlocked("Translation response format changed or access challenge appeared") from exc
    korean = " ".join(translated)
    if len(korean) < 10 or not HANGUL.search(korean):
        raise ValueError("Translation did not contain enough Korean text")
    return korean


def save(db: sqlite3.Connection, code: str, digest: str, status: str,
         korean: str | None = None, error: str | None = None) -> None:
    db.execute(
        """INSERT INTO SynopsisTranslation
           (Code,SourceHash,ModelVersion,Status,SynopsisKo,Attempts,UpdatedUtc,Error)
           VALUES (?,?,?,?,?,1,?,?)
           ON CONFLICT(Code) DO UPDATE SET
             SourceHash=excluded.SourceHash,ModelVersion=excluded.ModelVersion,
             Status=excluded.Status,SynopsisKo=excluded.SynopsisKo,
             Attempts=SynopsisTranslation.Attempts+1,
             UpdatedUtc=excluded.UpdatedUtc,Error=excluded.Error""",
        (code, digest, VERSION, status, korean, now(), error),
    )
    db.commit()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--index-db", required=True, type=Path)
    parser.add_argument("--delay", type=float, default=2.5)
    parser.add_argument("--timeout", type=int, default=20)
    parser.add_argument("--max-items", type=int, default=0)
    parser.add_argument("--max-seconds", type=int, default=72000)
    parser.add_argument("--poll-seconds", type=int, default=20)
    parser.add_argument("--idle-seconds", type=int, default=1200)
    parser.add_argument("--status", action="store_true")
    args = parser.parse_args()
    if args.delay < 1 or args.timeout < 1 or args.max_seconds < 1:
        parser.error("Invalid delay, timeout, or time limit")

    db = open_index(args.index_db)
    try:
        if args.status:
            print(json.dumps(counts(db), ensure_ascii=False), flush=True)
            return 0
        todo, source_pending = pending(db)
        print(json.dumps({"event": "start", "available": len(todo),
                          "source_pending": source_pending, "counts": counts(db)},
                         ensure_ascii=False), flush=True)
        if not todo and not source_pending:
            return 0
        session = requests.Session()
        session.headers.update({"User-Agent": "Mozilla/5.0"})
        started = last_progress = time.monotonic()
        last_request = 0.0
        completed = failed = network_errors = 0
        while time.monotonic() - started < args.max_seconds:
            todo, source_pending = pending(db)
            if not todo:
                if not source_pending or time.monotonic() - last_progress >= args.idle_seconds:
                    break
                time.sleep(args.poll_seconds)
                continue
            for code, original, digest in todo:
                if args.max_items and completed + failed >= args.max_items:
                    break
                if time.monotonic() - started >= args.max_seconds:
                    break
                time.sleep(max(0.0, args.delay - (time.monotonic() - last_request)))
                last_request = time.monotonic()
                try:
                    korean = translate(original, session, args.timeout, args.delay)
                    save(db, code, digest, "complete", korean)
                    completed += 1
                    network_errors = 0
                except AccessBlocked as exc:
                    print(json.dumps({"event": "blocked", "code": code,
                                      "reason": str(exc)}, ensure_ascii=False), flush=True)
                    return 2
                except requests.HTTPError as exc:
                    if exc.response is not None and exc.response.status_code == 400:
                        save(db, code, digest, "failed", error="HTTP 400 for source text")
                        failed += 1
                        continue
                    network_errors += 1
                    if network_errors >= 10:
                        return 3
                    time.sleep(15)
                    continue
                except requests.RequestException as exc:
                    network_errors += 1
                    print(json.dumps({"event": "network_retry", "code": code,
                                      "attempt": network_errors,
                                      "reason": str(exc)[:250]}, ensure_ascii=False), flush=True)
                    if network_errors >= 10:
                        return 3
                    time.sleep(15)
                    continue
                except ValueError as exc:
                    save(db, code, digest, "failed", error=str(exc)[:250])
                    failed += 1
                last_progress = time.monotonic()
                if completed + failed <= 5 or (completed + failed) % 25 == 0:
                    print(json.dumps({"event": "progress", "code": code,
                                      "translated": completed, "failed": failed,
                                      "counts": counts(db)}, ensure_ascii=False), flush=True)
            if args.max_items and completed + failed >= args.max_items:
                break
        print(json.dumps({"event": "stop", "translated_this_run": completed,
                          "failed_this_run": failed, "counts": counts(db)},
                         ensure_ascii=False), flush=True)
        return 0
    finally:
        db.close()


if __name__ == "__main__":
    sys.exit(main())
