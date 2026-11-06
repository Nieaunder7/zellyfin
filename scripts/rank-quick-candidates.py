"""Rank quick-search video candidates with temporal and visibility clues."""

import argparse
from collections import defaultdict
from pathlib import Path
import sqlite3
import sys


def rank(rows):
    grouped = defaultdict(list)
    for item_id, fraction, ticks, score, positive, male, brightness, name in rows:
        grouped[item_id].append({
            "fraction": fraction, "ticks": ticks, "score": score,
            "positive": positive, "male": male, "brightness": brightness, "name": name,
        })
    result = []
    for item_id, frames in grouped.items():
        frames.sort(key=lambda frame: frame["fraction"])
        dark = [frame for frame in frames if frame["brightness"] < 90]
        best = max(dark or frames, key=lambda frame: frame["score"])
        later_light = any(
            frame["fraction"] > best["fraction"] and frame["brightness"] - best["brightness"] > 35
            for frame in frames
        )
        male = max(frame["male"] for frame in frames)
        rank_score = best["score"] - 0.25 * male + (0.10 if later_light else 0)
        result.append((rank_score, item_id, best, later_light, male, len(frames)))
    return sorted(result, reverse=True)


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser()
    parser.add_argument("--scores-db", type=Path, required=True)
    parser.add_argument("--limit", type=int, default=20)
    args = parser.parse_args()
    connection = sqlite3.connect(args.scores_db)
    rows = connection.execute(
        "SELECT ItemId, Fraction, TimeTicks, Score, Positive, Male, Brightness, Name FROM Scores"
    ).fetchall()
    for position, (score, item_id, best, later_light, male, count) in enumerate(rank(rows)[:args.limit], 1):
        print(f"{position:02d} {item_id} {best['name']} rank={score:.3f} "
              f"at={best['fraction']:.2f} ({best['ticks'] / 600_000_000:.1f}m) "
              f"brightness={best['brightness']:.0f} laterLight={later_light} male={male:.3f} samples={count}")


if __name__ == "__main__":
    main()
