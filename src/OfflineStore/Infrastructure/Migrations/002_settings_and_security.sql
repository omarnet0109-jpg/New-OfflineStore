-- Only app presentation values are stored here. No plaintext passwords.
CREATE TABLE IF NOT EXISTS app_settings (
    setting_key TEXT PRIMARY KEY CHECK(setting_key IN ('theme','accent')),
    setting_value TEXT NOT NULL CHECK(length(setting_value) BETWEEN 1 AND 32)
);
INSERT OR IGNORE INTO app_settings(setting_key,setting_value) VALUES('theme','light');
INSERT OR IGNORE INTO app_settings(setting_key,setting_value) VALUES('accent','blue');

-- One optional local operator password; PBKDF2-HMAC-SHA256; no password recovery.
-- Protects app entry, not the SQLite file from direct access or tampering.
CREATE TABLE IF NOT EXISTS local_auth (
    id INTEGER PRIMARY KEY CHECK (id=1),
    salt BLOB NOT NULL CHECK(length(salt)=32),
    password_hash BLOB NOT NULL CHECK(length(password_hash)=32),
    iterations INTEGER NOT NULL CHECK(iterations >= 100000),
    updated_utc TEXT NOT NULL
);
