-- v11: per-employee permission overrides. No data is overwritten.
-- Existing users keep their four legacy roles when no override rows exist.
-- Administrative user management is NEVER delegable to a non-ADMIN user.
CREATE TABLE user_permission_overrides (
    user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    permission TEXT NOT NULL CHECK(permission IN (
        'CATALOG','PURCHASE','SALE','REPORTS','BACKUP',
        'LOW_STOCK','OVERRIDE_PRICE','APPEARANCE')),
    allowed INTEGER NOT NULL CHECK(typeof(allowed)='integer' AND allowed IN (0,1)),
    changed_by_user_id INTEGER REFERENCES users(id) ON DELETE RESTRICT,
    changed_utc TEXT NOT NULL,
    PRIMARY KEY(user_id,permission)
);
CREATE INDEX ix_user_permission_overrides_actor ON user_permission_overrides(changed_by_user_id);
-- No custom permission settings on ADMIN accounts; ADMIN retains all privileges.
CREATE TRIGGER reject_admin_permission_override_insert
BEFORE INSERT ON user_permission_overrides
WHEN EXISTS(SELECT 1 FROM users WHERE id=NEW.user_id AND role='ADMIN')
BEGIN SELECT RAISE(ABORT,'ADMIN_PERMISSIONS_FIXED'); END;
CREATE TRIGGER reject_admin_permission_override_update
BEFORE UPDATE ON user_permission_overrides
WHEN EXISTS(SELECT 1 FROM users WHERE id=NEW.user_id AND role='ADMIN')
BEGIN SELECT RAISE(ABORT,'ADMIN_PERMISSIONS_FIXED'); END;
