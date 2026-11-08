"""Semantic integrity tests against SQLite and the exact C# startup SQL."""

import pathlib
import re
import sqlite3
import unittest

from test_v6_posted_only_stock import make_database, draft, post, stock

ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCE = (ROOT / "src" / "OfflineStore" / "Infrastructure" / "StoreDb.cs").read_text(encoding="utf8")
QUERY = re.search(r'const string invalidPosted = @"(.*?)";', SOURCE, re.S)
assert QUERY, "Production startup integrity query missing"
INVALID_POSTED_SQL = QUERY.group(1)


def invalid_posted(connection):
    return connection.execute(INVALID_POSTED_SQL).fetchone()


def bad_draft(connection):
    return connection.execute("""SELECT d.id FROM documents d
       WHERE d.state='DRAFT' AND EXISTS
       (SELECT 1 FROM stock_movements m WHERE m.document_id=d.id)
       LIMIT 1""").fetchone()


class DatabaseSemanticIntegrityTests(unittest.TestCase):
    def setUp(self):
        self.c = make_database()

    def tearDown(self):
        self.c.close()

    def test_empty_database_has_no_error(self):
        self.assertIsNone(invalid_posted(self.c))
        self.assertIsNone(bad_draft(self.c))

    def test_valid_purchase_and_sale_are_consistent(self):
        post(self.c, draft(self.c,"PURCHASE",[12]))
        post(self.c, draft(self.c,"SALE",[3]))
        self.assertEqual(9000,stock(self.c))
        self.assertIsNone(invalid_posted(self.c))

    def test_legacy_invoice_with_wrong_total_is_detected(self):
        doc=draft(self.c,"PURCHASE",[12])
        post(self.c,doc)
        self.c.execute("DROP TRIGGER immutable_posted_document")
        self.c.execute("UPDATE documents SET total_cents=1 WHERE id=?",(doc,))
        self.assertEqual((doc,),invalid_posted(self.c))

    def test_legacy_missing_movement_is_detected(self):
        doc=draft(self.c,"PURCHASE",[10])
        post(self.c,doc)
        self.c.execute("DROP TRIGGER prevent_movement_delete")
        self.c.execute("DELETE FROM stock_movements WHERE document_id=?",(doc,))
        self.assertEqual((doc,),invalid_posted(self.c))

    def test_legacy_line_with_wrong_total_is_detected_even_if_header_agrees(self):
        doc=draft(self.c,"PURCHASE",[2])
        post(self.c,doc)
        self.c.execute("DROP TRIGGER prevent_line_update")
        self.c.execute("DROP TRIGGER immutable_posted_document")
        self.c.execute("UPDATE document_lines SET line_total_cents=500 WHERE document_id=?",(doc,))
        self.c.execute("UPDATE documents SET total_cents=500 WHERE id=?",(doc,))
        self.assertEqual((doc,),invalid_posted(self.c))

    def test_legacy_line_with_wrong_unit_snapshot_is_detected(self):
        doc=draft(self.c,"PURCHASE",[2])
        post(self.c,doc)
        self.c.execute("DROP TRIGGER prevent_line_update")
        self.c.execute("UPDATE document_lines SET entered_qty_milli=1000 WHERE document_id=?",(doc,))
        self.assertEqual((doc,),invalid_posted(self.c))

    def test_staged_movement_is_detected_but_not_counted(self):
        doc=draft(self.c,"PURCHASE",[5])
        self.assertEqual(0,stock(self.c))
        self.assertEqual((doc,),bad_draft(self.c))
        self.assertIsNone(invalid_posted(self.c))

    def test_no_correction_to_historical_data_is_attempted(self):
        self.assertIn("SELECT d.id FROM documents d", QUERY.group(0))
        self.assertNotIn("UPDATE documents", QUERY.group(0))
        self.assertNotIn("DELETE FROM", QUERY.group(0))

    def test_posted_movement_must_match_line_and_document(self):
        doc=draft(self.c,"PURCHASE",[3])
        post(self.c,doc)
        self.c.execute("DROP TRIGGER prevent_movement_update")
        self.c.execute("UPDATE stock_movements SET delta_qty_milli=1000 WHERE document_id=?",(doc,))
        self.assertEqual((doc,),invalid_posted(self.c))

if __name__=="__main__":
    unittest.main()
