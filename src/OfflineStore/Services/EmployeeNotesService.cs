using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using OfflineStore.Infrastructure;
using OfflineStore.Security;

namespace OfflineStore.Services
{
    public sealed class EmployeeNote
    {
        public long Id { get; set; }
        public string Title { get; set; }
        public string Rtf { get; set; }
        public long AuthorId { get; set; }
        public string Author { get; set; }
        public string UpdatedUtc { get; set; }
        public string DisplayName { get { return Title + "  •  " + Author; } }
    }

    // The notes are private to the local shop database, not an online Word service.
    public sealed class EmployeeNotesService
    {
        private readonly StoreDb _db;
        private readonly UserSession _session;
        public EmployeeNotesService(StoreDb db, UserSession session)
        {
            _db = db;
            _session = session;
        }

        private void RequireView()
        {
            if (!new UserAuthService(_db).Authorize(_session, "VIEW"))
                throw new UnauthorizedAccessException("انتهت جلسة الموظف. سجل الدخول مرة أخرى.");
        }
        private static void Validate(string title, string rtf)
        {
            if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 120)
                throw new ArgumentException("عنوان الملحوظة مطلوب، بحد أقصى 120 حرفًا.");
            if (rtf == null || rtf.Length > 200000 || !rtf.TrimStart().StartsWith("{\\rtf", StringComparison.Ordinal))
                throw new ArgumentException("نص الملحوظة غير صالح أو تجاوز الحجم المسموح.");
        }
        public List<EmployeeNote> ListAll()
        {
            RequireView();
            var result = new List<EmployeeNote>();
            using (var c = _db.Open())
            using (var cmd = new SQLiteCommand(@"SELECT n.id,n.title,n.body_rtf,n.created_by_user_id,
                     u.display_name,n.updated_utc
                     FROM employee_notes n JOIN users u ON u.id=n.created_by_user_id
                     ORDER BY n.updated_utc DESC,n.id DESC LIMIT 1000", c))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    result.Add(new EmployeeNote {
                        Id=r.GetInt64(0), Title=r.GetString(1), Rtf=r.GetString(2),
                        AuthorId=r.GetInt64(3), Author=r.GetString(4), UpdatedUtc=r.GetString(5)
                    });
            return result;
        }

        public long Create(string title, string rtf)
        {
            RequireView();
            Validate(title, rtf);
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                long id;
                using (var cmd = new SQLiteCommand(@"INSERT INTO employee_notes
                       (title,body_rtf,created_by_user_id,created_utc,updated_utc)
                       VALUES(@title,@rtf,@actor,@now,@now)", c, tx))
                {
                    cmd.Parameters.AddWithValue("@title", title.Trim());
                    cmd.Parameters.AddWithValue("@rtf", rtf);
                    cmd.Parameters.AddWithValue("@actor", _session.Id);
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = new SQLiteCommand("SELECT last_insert_rowid()", c, tx))
                    id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                using (var cmd = new SQLiteCommand(@"INSERT INTO audit_log(action,entity_id,actor_user_id,created_utc)
                   VALUES('NOTE_CREATED',@id,@actor,@now)", c, tx))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@actor", _session.Id);
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
                return id;
            }
        }

        public void Update(long id, string title, string rtf)
        {
            RequireView();
            Validate(title, rtf);
            // Administrator is allowed to edit a staff note; others edit only their own.
            var isAdmin = new UserAuthService(_db).Authorize(_session, "USERS");
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                using (var cmd = new SQLiteCommand(@"UPDATE employee_notes
                   SET title=@title,body_rtf=@rtf,updated_utc=@now
                   WHERE id=@id AND (created_by_user_id=@actor OR @admin=1)", c, tx))
                {
                    cmd.Parameters.AddWithValue("@title", title.Trim());
                    cmd.Parameters.AddWithValue("@rtf", rtf);
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@actor", _session.Id);
                    cmd.Parameters.AddWithValue("@admin", isAdmin ? 1 : 0);
                    if (cmd.ExecuteNonQuery() != 1)
                        throw new UnauthorizedAccessException("لا يمكن تعديل ملحوظة موظف آخر أو الملحوظة غير موجودة.");
                }
                using (var cmd = new SQLiteCommand(@"INSERT INTO audit_log(action,entity_id,actor_user_id,created_utc)
                    VALUES('NOTE_UPDATED',@id,@actor,@now)", c, tx))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@actor", _session.Id);
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
    }
}
