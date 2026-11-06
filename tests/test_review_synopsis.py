import importlib.util
import csv
from pathlib import Path
import sqlite3
import tempfile
import unittest

SOURCE = Path(__file__).resolve().parents[1] / "scripts" / "review-synopsis.py"
SPEC = importlib.util.spec_from_file_location("review_synopsis", SOURCE)
review = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(review)


class SynopsisReviewTests(unittest.TestCase):
    def test_boilerplate_removed_without_changing_original(self):
        ja = "女性が旅館で眠る。 ※本商品は未開封です。"
        ko = "여성이 여관에서 잠든다. ※본상품은 미개봉입니다."
        refined, flags = review.polish(ja, ko)
        self.assertEqual(refined, "여성이 여관에서 잠든다.")
        self.assertIn("product_note_removed", flags)
        self.assertEqual(ko, "여성이 여관에서 잠든다. ※본상품은 미개봉입니다.")

    def test_keywords_require_source_evidence(self):
        self.assertEqual(review.keywords("ベッドで二人の女性が寝ている。"),
                         ["침대", "잠든", "여성2명"])
        self.assertNotIn("침대", review.keywords("女性が寝ている。"))
        self.assertNotIn("여성2명", review.keywords("一人の女性が寝ている。"))

    def test_punctuation_and_known_calque(self):
        refined, _ = review.polish("終電を逃した。", "종전을 놓쳤다 ! !")
        self.assertEqual(refined, "막차를 놓쳤다!!")
        refined, _ = review.polish("ベットで眠る。", "베팅으로 자고 있다.")
        self.assertEqual(refined, "침대에서 자고 있다.")

    def test_approved_correction_replaces_text_and_keywords(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            db = review.open_db(root / "index.db")
            try:
                db.execute("CREATE TABLE Work(Code TEXT PRIMARY KEY,Status TEXT,Synopsis TEXT)")
                db.execute("CREATE TABLE SynopsisTranslation(Code TEXT PRIMARY KEY,Status TEXT,SynopsisKo TEXT)")
                db.execute("INSERT INTO Work VALUES ('A-001','success','ベッドで寝ている。')")
                db.execute("INSERT INTO SynopsisTranslation VALUES ('A-001','complete','침대에서 자고 있습니다.')")
                db.execute("INSERT INTO SynopsisReview VALUES ('A-001','old',?,'침대에서 자고 있습니다.','needs_review','[]','now')", (review.VERSION,))
                path = root / "corrections.csv"
                with path.open("w", encoding="utf-8-sig", newline="") as handle:
                    writer = csv.writer(handle)
                    writer.writerow(("품번", "정리된 한국어 시놉시스", "키워드", "승인"))
                    writer.writerow(("A-001", "침대에서 잠든 인물을 보여줍니다.", "침대, 잠든", "예"))
                self.assertEqual(review.import_approved(db, path), 1)
                self.assertEqual(db.execute("SELECT ReviewStatus,RefinedKo FROM SynopsisReview").fetchone(),
                                 ("approved", "침대에서 잠든 인물을 보여줍니다."))
                self.assertEqual(db.execute("SELECT Keyword FROM SynopsisKeyword ORDER BY Keyword").fetchall(),
                                 [("잠든",), ("침대",)])
            finally:
                db.close()


if __name__ == "__main__":
    unittest.main()
