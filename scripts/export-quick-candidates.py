"""Extract source frames for manual review of top quick-search candidates."""

import argparse
from pathlib import Path
import sqlite3
import subprocess


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--library-db", type=Path, required=True)
    parser.add_argument("--scores-db", type=Path, required=True)
    parser.add_argument("--ffmpeg", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--count", type=int, default=12)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    scores = sqlite3.connect(args.scores_db)
    library = sqlite3.connect(f"file:{args.library_db.as_posix()}?mode=ro", uri=True)
    rows = scores.execute(
        """SELECT ItemId, TimeTicks, Score, Brightness FROM Scores
           WHERE (ItemId, Score) IN (SELECT ItemId, MAX(Score) FROM Scores GROUP BY ItemId)
           ORDER BY Score DESC LIMIT ?""", (args.count,),
    ).fetchall()
    for rank, (item_id, ticks, score, brightness) in enumerate(rows, 1):
        item = library.execute("SELECT Path FROM BaseItems WHERE Id = ?", (item_id,)).fetchone()
        if not item:
            continue
        target = args.output / f"{rank:02d}-{item_id}-{ticks}.jpg"
        subprocess.run([
            str(args.ffmpeg), "-hide_banner", "-loglevel", "error", "-ss", f"{ticks / 10_000_000:.3f}",
            "-i", item[0], "-map", "0:v:0", "-frames:v", "1", "-vf", "scale=800:-2",
            "-q:v", "3", "-y", str(target),
        ], check=False, timeout=40)
        print(f"{rank:02d} {item_id} score={score:.3f} luminance={brightness:.0f} image={target}")


if __name__ == "__main__":
    main()
