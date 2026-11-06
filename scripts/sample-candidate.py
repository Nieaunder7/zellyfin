"""Extract specified original-video frames for one identified candidate."""

import argparse
from pathlib import Path
import sqlite3
import subprocess


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--library-db", type=Path, required=True)
    parser.add_argument("--ffmpeg", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--item-id", required=True)
    parser.add_argument("--fractions", type=float, nargs="+", required=True)
    args = parser.parse_args()
    connection = sqlite3.connect(f"file:{args.library_db.as_posix()}?mode=ro", uri=True)
    row = connection.execute("SELECT Path, RunTimeTicks FROM BaseItems WHERE Id = ?", (args.item_id,)).fetchone()
    if not row:
        raise SystemExit("Item not found")
    args.output.mkdir(parents=True, exist_ok=True)
    for fraction in args.fractions:
        seconds = row[1] / 10_000_000 * fraction
        target = args.output / f"{args.item_id}-{fraction:.3f}.jpg"
        subprocess.run([
            str(args.ffmpeg), "-hide_banner", "-loglevel", "error", "-ss", f"{seconds:.3f}",
            "-i", row[0], "-map", "0:v:0", "-frames:v", "1", "-vf", "scale=800:-2",
            "-q:v", "3", "-y", str(target),
        ], check=False, timeout=40)
        print(f"{fraction:.3f} {seconds:.0f}s {target}")


if __name__ == "__main__":
    main()
