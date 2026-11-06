import importlib.util
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest


SOURCE = Path(__file__).resolve().parents[1] / "scripts" / "scene-translation-worker.py"
SPEC = importlib.util.spec_from_file_location("scene_translation_worker", SOURCE)
worker = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(worker)


class SceneTranslationTests(unittest.TestCase):
    def test_resume_preserves_english_and_indexes_both_languages(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / "items" / "123.json"
            path.parent.mkdir()
            record = {
                "ItemId": "123", "Status": "complete", "TranslationStatus": "running",
                "TranslationModelVersion": worker.MODEL_VERSION,
                "Scenes": [
                    {"TimeTicks": 100, "FrameIndex": 0, "Description": "A dark bedroom.",
                     "DescriptionKo": "어두운 침실.", "Tags": ["bedroom", "침실"]},
                    {"TimeTicks": 200, "FrameIndex": 1, "Description": "The lights turn on.",
                     "Tags": ["light"]}
                ]
            }
            worker.write_record(path, record)
            original = worker.translate_batch
            worker.translate_batch = lambda texts, *_: ["불이 켜집니다."]
            try:
                self.assertTrue(worker.needs_translation(record))
                worker.translate_record(path, record, root, None, None, None, 8)
            finally:
                worker.translate_batch = original

            saved = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(saved["TranslationStatus"], "complete")
            self.assertEqual(saved["TranslatedSamples"], 2)
            self.assertEqual(saved["Scenes"][1]["Description"], "The lights turn on.")
            self.assertEqual(saved["Scenes"][1]["DescriptionKo"], "불이 켜집니다.")
            self.assertFalse(worker.needs_translation(saved))
            with closing(sqlite3.connect(root / "scene-search.db")) as connection:
                self.assertEqual(connection.execute(
                    "SELECT count(*) FROM SceneSearch WHERE SceneSearch MATCH ?", ("불이",)
                ).fetchone()[0], 1)
                self.assertEqual(connection.execute(
                    "SELECT count(*) FROM SceneSearch WHERE SceneSearch MATCH ?", ("lights",)
                ).fetchone()[0], 1)


if __name__ == "__main__":
    unittest.main()
