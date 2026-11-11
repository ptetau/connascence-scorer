using System;
namespace App
{
    // Must call Open() before Query(): enforced only at run time.
    public class Db
    {
        bool _open;
        public void Open() { _open = true; }
        public int Query(string sql) { if (!_open) throw new InvalidOperationException("Open first"); return 1; }
    }

    // Set up in the constructor: no order to get wrong.
    public class SafeDb
    {
        readonly string _conn;
        public SafeDb(string conn) { _conn = conn; }
        public int Query(string sql) => _conn.Length;
    }

    public class Repo
    {
        public int Count()
        {
            var db = new Db();
            db.Open();
            return db.Query("select count(*) from t");
        }
        public int Safe() => new SafeDb("x").Query("select 1");
    }
}
