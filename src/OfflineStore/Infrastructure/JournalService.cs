using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using OfflineStore.Security;

namespace OfflineStore.Infrastructure
{
    public sealed class JournalLine
    {
        public string AccountCode { get; set; }
        public long? PartyId { get; set; }
        public long DebitCents { get; set; }
        public long CreditCents { get; set; }
    }

    // Foundation API only. Existing invoices do not automatically post journal entries.
    public sealed class JournalService
    {
        private readonly StoreDb _db;
        private readonly UserSession _actor;
        public JournalService(StoreDb db, UserSession actor) { _db=db; _actor=actor; }

        public long PostManual(string sourceKind, string sourceKey, DateTime date, IList<JournalLine> lines)
        {
            if (!new UserAuthService(_db).Authorize(_actor,"USERS"))
                throw new UnauthorizedAccessException("القيود الافتتاحية والتسويات لمدير النظام فقط.");
            if(sourceKind!="OPENING" && sourceKind!="ADJUSTMENT")
                throw new ArgumentException("هذا المسار للرصيد الافتتاحي والتسويات فقط.");
            using(var c=_db.Open())
            using(var tx=c.BeginTransaction())
            {
                if(date.Date>DateTime.Today)throw new ArgumentException("لا يمكن اعتماد قيد يدوي بتاريخ مستقبلي.");
                if(FinanceService.IsActiveWithin(c,tx))
                {
                    if(sourceKind=="OPENING")throw new InvalidOperationException("الحسابات مفعلة؛ لا يمكن إضافة افتتاح جديد.");
                    FinanceService.ValidateDate(c,tx,date);
                    if(lines!=null)foreach(var line in lines)if(line!=null && string.Equals(line.AccountCode==null?null:line.AccountCode.Trim(),"INVENTORY",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("حساب المخزون مرتبط بكميته وتقييمه؛ استخدم حركة مخزون أو مسار مراجعة فرق التقييم.");
                }
                long id=PostWithin(c,tx,sourceKind,sourceKey,date,_actor.Id,lines);
                tx.Commit();
                return id;
            }
        }

        // Future invoice/payment integration must use the SAME caller-owned transaction.
        internal static long PostWithin(SQLiteConnection c, SQLiteTransaction tx, string kind,
            string key, DateTime date, long actor, IList<JournalLine> lines, long? documentId = null)
        {
            if(c==null || tx==null || tx.Connection!=c)
                throw new ArgumentException("قيد الأموال يحتاج معاملة SQLite قائمة على نفس الاتصال.");
            if(string.IsNullOrWhiteSpace(key) || key.Trim().Length>120 || lines==null || lines.Count<2 || lines.Count>500)
                throw new ArgumentException("مرجع القيد وسطران متزنان على الأقل مطلوبان.");
            long debit=0,credit=0;
            foreach(var line in lines)
            {
                if(line==null || string.IsNullOrWhiteSpace(line.AccountCode) || line.DebitCents<0 || line.CreditCents<0 ||
                    (line.DebitCents==0)==(line.CreditCents==0))
                    throw new ArgumentException("كل سطر يجب أن يحمل مبلغًا موجبًا في جانب واحد فقط.");
                debit=checked(debit+line.DebitCents);credit=checked(credit+line.CreditCents);
            }
            if(debit!=credit) throw new ArgumentException("القيد غير متزن.");
            long cashOut=0;
            foreach(var line in lines)
                if(string.Equals(line.AccountCode.Trim(),"CASH",StringComparison.OrdinalIgnoreCase))
                    cashOut=checked(cashOut+line.CreditCents-line.DebitCents);
            if(cashOut>0)
            {
                bool hasActivation;
                using(var table=new SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='finance_activation'",c,tx))
                    hasActivation=Convert.ToInt64(table.ExecuteScalar())!=0;
                if(hasActivation && FinanceService.IsActiveWithin(c,tx))FinanceService.RequireCashWithin(c,tx,cashOut);
            }
            string now=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture);
            using(var cmd=new SQLiteCommand(@"INSERT INTO journal_entries
                (source_kind,source_key,entry_date,created_by_user_id,created_utc,document_id)
                VALUES(@kind,@key,@date,@actor,@now,@document)",c,tx))
            {
                cmd.Parameters.AddWithValue("@kind",kind);cmd.Parameters.AddWithValue("@key",key.Trim());
                cmd.Parameters.AddWithValue("@date",date.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@actor",actor);cmd.Parameters.AddWithValue("@now",now);
                cmd.Parameters.AddWithValue("@document",documentId.HasValue ? (object)documentId.Value : DBNull.Value);cmd.ExecuteNonQuery();
            }
            long id;
            using(var cmd=new SQLiteCommand("SELECT last_insert_rowid()",c,tx)) id=Convert.ToInt64(cmd.ExecuteScalar());
            for(int i=0;i<lines.Count;i++)
            {
                var line=lines[i];
                using(var cmd=new SQLiteCommand(@"INSERT INTO journal_lines
                    (entry_id,line_no,account_code,party_id,debit_cents,credit_cents)
                    VALUES(@id,@n,@account,@party,@debit,@credit)",c,tx))
                {
                    cmd.Parameters.AddWithValue("@id",id);cmd.Parameters.AddWithValue("@n",i+1);
                    cmd.Parameters.AddWithValue("@account",line.AccountCode.Trim().ToUpperInvariant());
                    cmd.Parameters.AddWithValue("@party",line.PartyId.HasValue ? (object)line.PartyId.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@debit",line.DebitCents);cmd.Parameters.AddWithValue("@credit",line.CreditCents);
                    cmd.ExecuteNonQuery();
                }
            }
            using(var cmd=new SQLiteCommand("UPDATE journal_entries SET state='POSTED' WHERE id=@id",c,tx))
            {cmd.Parameters.AddWithValue("@id",id);cmd.ExecuteNonQuery();}
            using(var cmd=new SQLiteCommand(@"INSERT INTO audit_log(action,entity_id,details,actor_user_id,created_utc)
                VALUES('JOURNAL_POSTED',@id,@key,@actor,@now)",c,tx))
            {cmd.Parameters.AddWithValue("@id",id);cmd.Parameters.AddWithValue("@key",key.Trim());
             cmd.Parameters.AddWithValue("@actor",actor);cmd.Parameters.AddWithValue("@now",now);cmd.ExecuteNonQuery();}
            return id;
        }
    }
}
