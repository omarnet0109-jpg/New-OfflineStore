-- v7: employee notes, rich text RTF, independent of financial documents.
-- Stored within the same locally backed-up SQLite database.
CREATE TABLE employee_notes (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  title TEXT NOT NULL CHECK(length(trim(title)) BETWEEN 1 AND 120),
  body_rtf TEXT NOT NULL CHECK(length(body_rtf) <= 200000),
  created_by_user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  created_utc TEXT NOT NULL,
  updated_utc TEXT NOT NULL
);
CREATE INDEX ix_employee_notes_updated ON employee_notes(updated_utc DESC, id DESC);
