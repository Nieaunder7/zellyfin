"""Second-pass synopsis cleanup and source-grounded Korean keyword indexing.

This pass removes known product boilerplate, applies narrow wording fixes,
extracts controlled terms from Japanese source text, and flags translations
that still need human review. It never overwrites the original/first pass.
"""
from __future__ import annotations

import argparse
import csv
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import sqlite3

VERSION = "synopsis-review-rules-v3"
HANGUL = re.compile(r"[가-힣]")
JAPANESE_REMAINS = re.compile(r"[ぁ-んァ-ン一-龥]")
ARTIFACTS = re.compile(
    r"하메|오징어|베팅|미각으로|파이 빵|자종|종전|본상품은|본 작품에 있어서|"
    r"구입하실 수|미개봉|이키|즈브리|삐스톤|뽀로득|치포|펀치라",
    re.I,
)

# Source-language evidence is required; keywords never come from a scene image.
KEYWORDS: dict[str, str] = {
    "침대": r"ベッ[ドト]|寝台",
    "침실": r"寝室|ベッドルーム",
    "잠든": r"眠|寝て|寝た|寝る|寝落ち|就寝|熟睡",
    "이불": r"布団|毛布|掛け布団",
    "집": r"自宅|実家|家で|家に|家の中|家へ",
    "거실": r"リビング|居間",
    "학교": r"学校|学園|校内",
    "교실": r"教室",
    "사무실": r"オフィス|事務所",
    "회사": r"会社|職場",
    "공장": r"工場",
    "병원": r"病院",
    "호텔": r"ホテル",
    "여관": r"旅館|宿屋",
    "온천": r"温泉",
    "욕실": r"浴室|風呂場|お風呂|浴槽",
    "수영장": r"プール",
    "해변": r"海辺|海岸|ビーチ",
    "야외": r"屋外|野外|外で",
    "차량": r"車内|車の中|自動車",
    "기차": r"電車|列車|新幹線",
    "화장실": r"トイレ|便所",
    "편의점": r"コンビニ",
    "술집": r"居酒屋|バーで",
    "여행": r"旅行|旅先|旅館",
    "수학여행": r"修学旅行",
    "축제": r"祭り|フェスティバル",
    "생일": r"誕生日",
    "파티": r"パーティー|パーティ",
    "코스프레": r"コスプレ",
    "목욕": r"入浴|お風呂に入|風呂に入",
    "음주": r"飲酒|お酒|酔っ|酔い",
    "재회": r"再会|久しぶりに会",
    "몰래": r"こっそり|内緒|秘密|バレない|隠れて",
    "엿보기": r"覗|のぞき|盗み見",
    "동거": r"同棲|シェアハウス",
    "결혼": r"結婚|新婚",
    "간호": r"看護|介護|看病",
    "여동생": r"妹",
    "누나": r"姉|お姉ちゃん",
    "자매": r"姉妹",
    "남매": r"兄妹|姉弟",
    "어머니": r"母親|お母さん|母の",
    "아버지": r"父親|お父さん|父の",
    "할아버지": r"お爺ちゃん|おじいちゃん|老人",
    "선생님": r"先生|教師",
    "학생": r"学生|女子大生|生徒",
    "직장동료": r"同僚|部下|上司",
    "이웃": r"隣人|お隣|近所",
    "남편": r"旦那|夫",
    "아내": r"奥さん|妻|人妻",
    "친구": r"友達|友人",
    "여성2명": r"(?:女性|女の子|女子|人妻).{0,8}(?:二人|2人|２人)|(?:二人|2人|２人).{0,8}(?:女性|女の子|女子|人妻)",
}
COMPILED = {key: re.compile(pattern) for key, pattern in KEYWORDS.items()}


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def digest(ja: str, ko: str) -> str:
    return hashlib.sha256((ja + "\0" + ko).encode("utf-8")).hexdigest()


def polish(ja: str, ko: str) -> tuple[str, list[str]]:
    flags: list[str] = []
    text = ko.strip()
    if "※" in ja and "※" in text:
        text = text.split("※", 1)[0].rstrip()
        flags.append("product_note_removed")
    elif "※" in ja:
        cut = re.search(r"(?:본상품은|본 작품에 있어서|미개봉|18세 미만의 출연자)", text)
        if cut:
            text = text[:cut.start()].rstrip()
            flags.append("product_note_removed")
    if re.search(r"^\d{4}年\d{1,2}月\d{1,2}日発売の商品です", ja):
        text = re.sub(r"^\d{4}년\s*\d{1,2}월\s*\d{1,2}일\s*발매(?:의)?\s*상품입니다\s*", "", text)
    if re.search(r"ベッ[ドト]", ja):
        text = text.replace("베팅으로", "침대에서").replace("베팅", "침대")
    if "終電" in ja:
        text = text.replace("종전을", "막차를").replace("종전", "막차")
    if "パイパン" in ja:
        text = text.replace("파이 빵", "체모를 제거한")
    if "子種" in ja:
        text = text.replace("자종", "정액")
    if "パンチラ" in ja:
        text = text.replace("펀치라", "속옷 노출")
    if re.search(r"チ[ン●○]?ポ", ja):
        text = text.replace("치포", "성기")
    if "鉢合わせ" in ja:
        text = text.replace("화분", "마주침")
    if "うだるような暑さ" in ja:
        text = text.replace("시끄러운 더위", "무더위")
    if "お爺ちゃん" in ja and "動揺" in ja:
        text = text.replace("동요하는 아줌마", "동요하는 할아버지")
    if "寝てしまう" in ja:
        text = text.replace("자고 버린다", "잠들고 만다")
    text = re.sub(r"\s+", " ", text)
    text = re.sub(r"\s+([,.!?…。])", r"\1", text)
    text = re.sub(r"([!?])\s+([!?])", r"\1\2", text)
    text = re.sub(r"([.!?]){4,}", lambda m: m.group(0)[:2], text)
    text = text.strip()
    if not HANGUL.search(text) or len(text) < 15:
        flags.append("translation_too_short")
    if JAPANESE_REMAINS.search(text):
        flags.append("japanese_remaining")
    if ARTIFACTS.search(text):
        flags.append("machine_artifact")
    if len(ja) > 1000:
        flags.append("long_source")
    if len(text) < len(ja) * 0.35:
        flags.append("possible_omission")
    return text, flags


def keywords(ja: str) -> list[str]:
    return [term for term, pattern in COMPILED.items() if pattern.search(ja)]


def open_db(path: Path) -> sqlite3.Connection:
    db = sqlite3.connect(path, timeout=30)
    db.execute("PRAGMA journal_mode=WAL")
    db.execute("PRAGMA busy_timeout=30000")
    db.execute("""CREATE TABLE IF NOT EXISTS SynopsisReview (
        Code TEXT PRIMARY KEY, SourceHash TEXT NOT NULL, RuleVersion TEXT NOT NULL,
        RefinedKo TEXT NOT NULL, ReviewStatus TEXT NOT NULL, FlagsJson TEXT NOT NULL,
        UpdatedUtc TEXT NOT NULL)""")
    db.execute("""CREATE TABLE IF NOT EXISTS SynopsisKeyword (
        Code TEXT NOT NULL, Keyword TEXT NOT NULL, Evidence TEXT NOT NULL,
        PRIMARY KEY (Code, Keyword))""")
    db.execute("CREATE INDEX IF NOT EXISTS IX_SynopsisKeyword_Keyword ON SynopsisKeyword(Keyword)")
    db.commit()
    return db


def status(db: sqlite3.Connection) -> dict:
    return {
        "translated": db.execute("SELECT COUNT(*) FROM SynopsisTranslation WHERE Status='complete'").fetchone()[0],
        "reviewed": db.execute("SELECT COUNT(*) FROM SynopsisReview WHERE RuleVersion=?", (VERSION,)).fetchone()[0],
        "needs_review": db.execute("SELECT COUNT(*) FROM SynopsisReview WHERE ReviewStatus='needs_review' AND RuleVersion=?", (VERSION,)).fetchone()[0],
        "approved": db.execute("SELECT COUNT(*) FROM SynopsisReview WHERE ReviewStatus='approved'").fetchone()[0],
        "keyword_links": db.execute("SELECT COUNT(*) FROM SynopsisKeyword").fetchone()[0],
        "unique_keywords": db.execute("SELECT COUNT(DISTINCT Keyword) FROM SynopsisKeyword").fetchone()[0],
    }


def export(db: sqlite3.Connection, review_csv: Path | None, keyword_csv: Path | None) -> None:
    if review_csv:
        review_csv.parent.mkdir(parents=True, exist_ok=True)
        with review_csv.open("w", encoding="utf-8-sig", newline="") as handle:
            writer = csv.writer(handle)
            writer.writerow(("품번", "상태", "검수사유", "정리된 한국어 시놉시스", "키워드", "일본어 원문", "승인"))
            for code, state, flags, refined, original in db.execute("""SELECT r.Code,r.ReviewStatus,r.FlagsJson,r.RefinedKo,w.Synopsis
                FROM SynopsisReview r JOIN Work w ON w.Code=r.Code
                WHERE r.ReviewStatus='needs_review' ORDER BY r.Code"""):
                terms = ", ".join(row[0] for row in db.execute(
                    "SELECT Keyword FROM SynopsisKeyword WHERE Code=? ORDER BY Keyword", (code,)))
                writer.writerow((code, state, flags, refined, terms, original, ""))
    if keyword_csv:
        keyword_csv.parent.mkdir(parents=True, exist_ok=True)
        with keyword_csv.open("w", encoding="utf-8-sig", newline="") as handle:
            writer = csv.writer(handle)
            writer.writerow(("키워드", "작품수"))
            writer.writerows(db.execute("""SELECT Keyword,COUNT(*) FROM SynopsisKeyword
                GROUP BY Keyword ORDER BY COUNT(*) DESC,Keyword"""))


def import_approved(db: sqlite3.Connection, path: Path) -> int:
    approved = 0
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        required = {"품번", "정리된 한국어 시놉시스", "승인"}
        if not required.issubset(reader.fieldnames or []):
            raise ValueError("Review CSV needs 품번, 정리된 한국어 시놉시스, 승인 columns")
        for row in reader:
            if (row.get("승인") or "").strip().lower() not in {"예", "승인", "yes", "y", "1"}:
                continue
            code = (row.get("품번") or "").strip()
            refined = (row.get("정리된 한국어 시놉시스") or "").strip()
            if len(refined) < 15 or not HANGUL.search(refined):
                raise ValueError(f"Invalid approved Korean synopsis: {code}")
            source = db.execute("""SELECT w.Synopsis,t.SynopsisKo FROM Work w
                JOIN SynopsisTranslation t ON t.Code=w.Code
                WHERE w.Code=? AND w.Status='success' AND t.Status='complete'""", (code,)).fetchone()
            if source is None:
                raise ValueError(f"Unknown or untranslated code: {code}")
            db.execute("""UPDATE SynopsisReview SET SourceHash=?,RuleVersion=?,RefinedKo=?,
                ReviewStatus='approved',FlagsJson='[]',UpdatedUtc=? WHERE Code=?""",
                (digest(*source), VERSION, refined, utc_now(), code))
            if db.execute("SELECT changes()").fetchone()[0] != 1:
                raise ValueError(f"Missing review row: {code}")
            if (row.get("키워드") or "").strip():
                terms = list(dict.fromkeys(term.strip() for term in row["키워드"].split(",") if term.strip()))
                if len(terms) > 30 or any(len(term) > 30 or not HANGUL.search(term) for term in terms):
                    raise ValueError(f"Invalid keywords: {code}")
                db.execute("DELETE FROM SynopsisKeyword WHERE Code=?", (code,))
                db.executemany("INSERT INTO SynopsisKeyword(Code,Keyword,Evidence) VALUES (?,?,?)",
                               ((code, term, "human_review") for term in terms))
            db.commit()
            approved += 1
    return approved


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--index-db", type=Path, required=True)
    parser.add_argument("--review-csv", type=Path)
    parser.add_argument("--keyword-csv", type=Path)
    parser.add_argument("--import-approved-csv", type=Path)
    parser.add_argument("--status", action="store_true")
    args = parser.parse_args()
    db = open_db(args.index_db)
    try:
        imported = import_approved(db, args.import_approved_csv) if args.import_approved_csv else 0
        if not args.status:
            rows = db.execute("""SELECT w.Code,w.Synopsis,t.SynopsisKo,r.SourceHash,r.RuleVersion,r.ReviewStatus
                FROM Work w JOIN SynopsisTranslation t ON t.Code=w.Code
                LEFT JOIN SynopsisReview r ON r.Code=w.Code
                WHERE w.Status='success' AND t.Status='complete' ORDER BY w.Code""").fetchall()
            processed = 0
            for code, ja, ko, old_hash, version, review_status in rows:
                source_hash = digest(ja, ko)
                if old_hash == source_hash and (review_status == "approved" or version == VERSION):
                    continue
                refined, flags = polish(ja, ko)
                terms = keywords(ja)
                review_status = "needs_review" if any(flag != "product_note_removed" for flag in flags) else "auto_refined"
                db.execute("""INSERT INTO SynopsisReview
                    (Code,SourceHash,RuleVersion,RefinedKo,ReviewStatus,FlagsJson,UpdatedUtc)
                    VALUES (?,?,?,?,?,?,?)
                    ON CONFLICT(Code) DO UPDATE SET
                    SourceHash=excluded.SourceHash,RuleVersion=excluded.RuleVersion,
                    RefinedKo=excluded.RefinedKo,ReviewStatus=excluded.ReviewStatus,
                    FlagsJson=excluded.FlagsJson,UpdatedUtc=excluded.UpdatedUtc""",
                    (code, source_hash, VERSION, refined, review_status,
                     json.dumps(flags, ensure_ascii=False), utc_now()))
                db.execute("DELETE FROM SynopsisKeyword WHERE Code=?", (code,))
                db.executemany("INSERT INTO SynopsisKeyword (Code,Keyword,Evidence) VALUES (?,?,?)",
                               ((code, term, "japanese_synopsis") for term in terms))
                db.commit()
                processed += 1
            export(db, args.review_csv, args.keyword_csv)
            print(json.dumps({"processed_this_run": processed, "imported_approvals": imported,
                              **status(db)}, ensure_ascii=False))
        else:
            print(json.dumps(status(db), ensure_ascii=False))
    finally:
        db.close()


if __name__ == "__main__":
    main()
