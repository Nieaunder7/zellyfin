"""Checkpointed, read-only FANZA synopsis collection for indexed home videos.

Network routing is supplied by a launcher or an explicit proxy. No media files
are opened and Jellyfin's database is opened read-only.
"""

from __future__ import annotations

import argparse
from collections import Counter
import csv
from datetime import datetime, timezone
import html
import json
import os
from pathlib import Path
import re
import sqlite3
import sys
import time
from urllib.parse import quote, urlparse

import requests


HOME_VIDEO_NAMES = ("홈 비디오 및 사진", "홈 비디오 및 사진2", "홈 비디오 및 사진4")
CODE_RE = re.compile(r"(?<![A-Za-z0-9])([A-Za-z]{2,6})[-_ ]?(\d{3,5})(?!\d)")
NON_PRODUCT_PREFIXES = {
    "BD", "CD", "DVD", "EP", "FHD", "HD", "MOVIE", "PART", "RIP", "VOL",
}
SEARCH_BASE = "https://www.dmm.co.jp/mono/dvd/-/search/=/searchstr="
DETAIL_RE = re.compile(r'''href=["']([^"']*mono/dvd/-/detail/=/cid=[^"']+)["']''', re.I)
AGE_RE = re.compile(r'''href=["']([^"']*age_check/=/declared=yes/[^"']*)["']''', re.I)
PRODUCT_RE = re.compile(r"品番：</td>\s*<td[^>]*>(.*?)</td>", re.I | re.S)
SYNOPSIS_RE = re.compile(
    r'<div class="mg-b20 lh4">\s*<p class="mg-b20">(.*?)</p>', re.I | re.S
)
OG_TITLE_RE = re.compile(
    r'''<meta[^>]+property=["']og:title["'][^>]+content=["']([^"']*)["']''',
    re.I | re.S,
)
TERMINAL = ("success", "no_match", "no_synopsis", "code_mismatch")
FAILURE_REASONS = {
    "no_match": "검색 결과 없음",
    "no_synopsis": "상세 페이지에 40자 이상 소개 본문 없음",
    "code_mismatch": "상세 페이지 품번 불일치",
    "error": "요청 오류",
    "blocked": "접근 제한 또는 연결 문제로 중단",
}


def now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def text(value: str) -> str:
    return re.sub(r"\s+", " ", html.unescape(re.sub(r"<[^>]+>", " ", value))).strip()


def code_from_name(name: str, path: str) -> str | None:
    # The indexed name is preferred, then the media basename. Path is never read.
    for source in (name or "", Path(path or "").name):
        for match in CODE_RE.finditer(source):
            prefix = match.group(1).upper()
            if prefix not in NON_PRODUCT_PREFIXES:
                return f"{prefix}-{match.group(2)}"
    return None


def inventory(jellyfin_db: Path) -> list[tuple[str, str]]:
    conn = sqlite3.connect(f"file:{jellyfin_db.as_posix()}?mode=ro", uri=True)
    try:
        rows = conn.execute(
            """SELECT DISTINCT b.Id, b.Name, b.Path FROM BaseItems b
               JOIN AncestorIds a ON a.ItemId = b.Id
               JOIN BaseItems lib ON lib.Id = a.ParentItemId
               WHERE lib.Type = 'MediaBrowser.Controller.Entities.CollectionFolder'
                 AND lib.Name IN (?, ?, ?)
                 AND b.MediaType = 'Video' AND b.IsFolder = 0
                 AND b.IsVirtualItem = 0 AND b.Path IS NOT NULL""",
            HOME_VIDEO_NAMES,
        ).fetchall()
    finally:
        conn.close()
    found = [(item_id, code_from_name(name, path)) for item_id, name, path in rows]
    return [(item_id, code) for item_id, code in found if code]


def open_index(path: Path) -> sqlite3.Connection:
    path.parent.mkdir(parents=True, exist_ok=True)
    db = sqlite3.connect(path, timeout=30)
    db.execute("PRAGMA journal_mode=WAL")
    db.execute("""CREATE TABLE IF NOT EXISTS Work (
        Code TEXT PRIMARY KEY, Status TEXT NOT NULL DEFAULT 'pending',
        ProductCode TEXT, MatchType TEXT, Title TEXT, Synopsis TEXT,
        SourceUrl TEXT, HttpStatus INTEGER, Attempts INTEGER NOT NULL DEFAULT 0,
        UpdatedUtc TEXT, Error TEXT)""")
    db.execute("""CREATE TABLE IF NOT EXISTS ItemWork (
        ItemId TEXT PRIMARY KEY, Code TEXT NOT NULL)""")
    db.execute("CREATE INDEX IF NOT EXISTS IX_ItemWork_Code ON ItemWork(Code)")
    db.commit()
    return db


def prepare_index(db: sqlite3.Connection, items: list[tuple[str, str]]) -> list[str]:
    db.executemany("INSERT OR IGNORE INTO Work(Code) VALUES (?)", ((c,) for _, c in items))
    db.executemany(
        "INSERT INTO ItemWork(ItemId,Code) VALUES (?,?) "
        "ON CONFLICT(ItemId) DO UPDATE SET Code=excluded.Code", items,
    )
    db.commit()
    # Stable order: frequent prefixes first, then code. No media rescans are needed.
    counts = Counter(code.split("-", 1)[0] for _, code in items)
    return sorted({code for _, code in items}, key=lambda c: (-counts[c.split("-", 1)[0]], c))


class StopHarvest(Exception):
    pass


class Client:
    def __init__(self, delay: float, proxy_url: str | None = None):
        self.session = requests.Session()
        if proxy_url:
            parsed = urlparse(proxy_url)
            if parsed.scheme not in ("http", "https", "socks5h") or not parsed.hostname:
                raise ValueError("proxy_url must be an HTTP, HTTPS, or socks5h URL")
            if parsed.scheme == "socks5h":
                try:
                    import socks  # noqa: F401  - required by requests for SOCKS proxies
                except ImportError as exc:
                    raise RuntimeError("SOCKS proxy requires PySocks (pip install pysocks)") from exc
            # Ignore ambient HTTP(S)_PROXY and NO_PROXY so the worker cannot
            # silently bypass its explicitly selected route.
            self.session.trust_env = False
            self.session.proxies.update({"http": proxy_url, "https": proxy_url})
        self.session.headers["User-Agent"] = (
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
            "AppleWebKit/537.36 Chrome/130.0 Safari/537.36"
        )
        self.delay = delay
        self.last_request = 0.0
        self.requests = 0

    def get(self, url: str) -> requests.Response:
        if urlparse(url).hostname != "www.dmm.co.jp":
            raise StopHarvest("unexpected_host")
        remaining = self.delay - (time.monotonic() - self.last_request)
        if remaining > 0:
            time.sleep(remaining)
        try:
            response = self.session.get(url, timeout=30)
        finally:
            self.last_request = time.monotonic()
            self.requests += 1
        response.encoding = response.apparent_encoding
        if urlparse(response.url).hostname != "www.dmm.co.jp":
            raise StopHarvest("redirected_off_fanza")
        if response.status_code in (403, 429):
            raise StopHarvest(f"http_{response.status_code}")
        if response.status_code >= 500:
            raise StopHarvest(f"http_{response.status_code}")
        if response.status_code != 200:
            raise StopHarvest(f"http_{response.status_code}")
        if "このページはお住まいの地域から" in response.text:
            raise StopHarvest("region_blocked")
        if "captcha" in response.text.lower() or "アクセスが集中" in response.text:
            raise StopHarvest("challenge_or_busy")
        return response

    def page(self, url: str) -> str:
        response = self.get(url)
        title = re.search(r"<title[^>]*>(.*?)</title>", response.text, re.I | re.S)
        if title and "年齢認証" in title.group(1):
            age = AGE_RE.search(response.text)
            if not age:
                raise StopHarvest("age_check_without_link")
            response = self.get(html.unescape(age.group(1)))
            title = re.search(r"<title[^>]*>(.*?)</title>", response.text, re.I | re.S)
            if title and "年齢認証" in title.group(1):
                raise StopHarvest("age_check_persisted")
        return response.text


def product_code(page: str) -> str:
    match = PRODUCT_RE.search(page)
    return text(match.group(1)) if match else ""


def synopsis(page: str) -> str:
    return max((text(m) for m in SYNOPSIS_RE.findall(page)), key=len, default="")


def code_match(found: str, expected: str) -> str:
    compact = re.sub(r"[^a-z0-9]", "", found.lower())
    wanted = re.sub(r"[^a-z0-9]", "", expected.lower())
    if compact == wanted:
        return "exact"
    if re.search(re.escape(wanted) + r"(?!\d)", compact):
        return "affix"
    return "none"


def fetch_work(client: Client, code: str) -> dict:
    search_url = SEARCH_BASE + quote(code) + "/"
    page = client.page(search_url)
    wanted = re.sub(r"[^a-z0-9]", "", code.lower())
    links = list(dict.fromkeys(html.unescape(url) for url in DETAIL_RE.findall(page)))
    links = [url for url in links if wanted in url.lower()]
    if not links:
        return {"Status": "no_match", "SourceUrl": search_url}
    # Search results can include DVD/Blu-ray variants. One verified detail is enough.
    url = links[0]
    detail = client.page(url)
    found = product_code(detail)
    match_type = code_match(found, code)
    title_match = OG_TITLE_RE.search(detail)
    body = synopsis(detail)
    result = {
        "Status": "success" if match_type != "none" and len(body) >= 40
                  else "code_mismatch" if match_type == "none" else "no_synopsis",
        "ProductCode": found, "MatchType": match_type,
        "Title": text(title_match.group(1)) if title_match else "",
        "Synopsis": body, "SourceUrl": url,
    }
    return result


def save(db: sqlite3.Connection, code: str, result: dict, error: str | None = None):
    db.execute(
        """UPDATE Work SET Status=?,ProductCode=?,MatchType=?,Title=?,Synopsis=?,
           SourceUrl=?,HttpStatus=?,Attempts=Attempts+1,UpdatedUtc=?,Error=?
           WHERE Code=?""",
        (result.get("Status", "error"), result.get("ProductCode"),
         result.get("MatchType"), result.get("Title"), result.get("Synopsis"),
         result.get("SourceUrl"), result.get("HttpStatus"), now(), error, code),
    )
    db.commit()


def summary(db: sqlite3.Connection) -> dict:
    values = dict(db.execute("SELECT Status,COUNT(*) FROM Work GROUP BY Status"))
    return {"total": sum(values.values()), **values}


def write_failures(db: sqlite3.Connection, jellyfin_db: Path, output: Path):
    failures = db.execute(
        """SELECT Code,Status,ProductCode,SourceUrl,Error,Attempts,UpdatedUtc
           FROM Work WHERE Status IN ('no_match','no_synopsis','code_mismatch',
                                      'error','blocked') ORDER BY Code"""
    ).fetchall()
    failed_codes = {row[0] for row in failures}
    code_items: dict[str, list[str]] = {code: [] for code in failed_codes}
    for item_id, code in db.execute("SELECT ItemId,Code FROM ItemWork"):
        if code in code_items:
            code_items[code].append(item_id)
    source = sqlite3.connect(f"file:{jellyfin_db.as_posix()}?mode=ro", uri=True)
    try:
        names = dict(source.execute(
            "SELECT Id,Name FROM BaseItems WHERE MediaType='Video' AND IsFolder=0"
        ))
    finally:
        source.close()
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.writer(handle)
        writer.writerow(("품번", "상태", "사유", "영상수", "파일명", "판매처품번",
                         "출처URL", "시도횟수", "최종확인UTC"))
        for code, status, product, url, error, attempts, updated in failures:
            item_ids = code_items[code]
            item_names = " | ".join(sorted({names.get(i, i) for i in item_ids}))
            reason = FAILURE_REASONS[status]
            if error:
                reason += f": {error}"
            writer.writerow((code, status, reason, len(item_ids), item_names,
                             product or "", url or "", attempts, updated or ""))
    os.replace(temporary, output)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--jellyfin-db", type=Path, required=True)
    parser.add_argument("--output-db", type=Path, required=True)
    parser.add_argument("--delay", type=float, default=2.5)
    parser.add_argument("--max-items", type=int, default=0)
    parser.add_argument("--max-seconds", type=int, default=43200)
    parser.add_argument("--failures-csv", type=Path)
    parser.add_argument("--proxy-url", help="Dedicated proxy for this worker; never falls back to a direct request")
    parser.add_argument("--probe-route", action="store_true", help="Check the FANZA route without changing the index")
    parser.add_argument("--status", action="store_true")
    args = parser.parse_args()
    proxy_url = args.proxy_url or os.environ.get("FANZA_PROXY_URL")
    if args.probe_route:
        try:
            client = Client(0, proxy_url)
            client.page(SEARCH_BASE + "RCT-781/")
        except (StopHarvest, requests.RequestException, ValueError, RuntimeError) as exc:
            print(json.dumps({"event": "route_failed", "reason": type(exc).__name__
                              if isinstance(exc, requests.RequestException) else str(exc),
                              "proxy": bool(proxy_url)}), flush=True)
            return 2
        print(json.dumps({"event": "route_ready", "proxy": bool(proxy_url)}), flush=True)
        return 0
    db = open_index(args.output_db)
    try:
        if args.status:
            if args.failures_csv:
                write_failures(db, args.jellyfin_db, args.failures_csv)
            print(json.dumps(summary(db), ensure_ascii=False), flush=True)
            return 0
        items = inventory(args.jellyfin_db)
        codes = prepare_index(db, items)
        print(json.dumps({"event": "start", "items": len(items), "codes": len(codes),
                          "progress": summary(db)}, ensure_ascii=False), flush=True)
        if args.failures_csv:
            write_failures(db, args.jellyfin_db, args.failures_csv)
        client = Client(args.delay, proxy_url)
        start = time.monotonic()
        processed = 0
        consecutive_request_errors = 0
        for code in codes:
            state = db.execute("SELECT Status FROM Work WHERE Code=?", (code,)).fetchone()[0]
            if state in TERMINAL:
                continue
            if args.max_items and processed >= args.max_items:
                break
            if time.monotonic() - start >= args.max_seconds:
                if args.failures_csv:
                    write_failures(db, args.jellyfin_db, args.failures_csv)
                print(json.dumps({"event": "stop", "reason": "time_limit",
                                  "progress": summary(db)}, ensure_ascii=False), flush=True)
                return 0
            try:
                result = fetch_work(client, code)
                save(db, code, result)
                consecutive_request_errors = 0
                if result["Status"] != "success" and args.failures_csv:
                    write_failures(db, args.jellyfin_db, args.failures_csv)
            except StopHarvest as exc:
                save(db, code, {"Status": "blocked"}, str(exc))
                if args.failures_csv:
                    write_failures(db, args.jellyfin_db, args.failures_csv)
                print(json.dumps({"event": "stop", "reason": str(exc), "code": code,
                                  "progress": summary(db)}, ensure_ascii=False), flush=True)
                return 2
            except requests.RequestException as exc:
                save(db, code, {}, type(exc).__name__)
                if args.failures_csv:
                    write_failures(db, args.jellyfin_db, args.failures_csv)
                consecutive_request_errors += 1
                if consecutive_request_errors >= 3:
                    print(json.dumps({"event": "stop", "reason": "repeated_request_errors",
                                      "code": code, "progress": summary(db)},
                                     ensure_ascii=False), flush=True)
                    return 3
            processed += 1
            if processed <= 5 or processed % 25 == 0:
                if args.failures_csv:
                    write_failures(db, args.jellyfin_db, args.failures_csv)
                print(json.dumps({"event": "progress", "processed_this_run": processed,
                                  "code": code, "progress": summary(db)},
                                 ensure_ascii=False), flush=True)
        if args.failures_csv:
            write_failures(db, args.jellyfin_db, args.failures_csv)
        print(json.dumps({"event": "complete", "processed_this_run": processed,
                          "requests": client.requests, "progress": summary(db)},
                         ensure_ascii=False), flush=True)
        return 0
    finally:
        db.close()


if __name__ == "__main__":
    sys.exit(main())
