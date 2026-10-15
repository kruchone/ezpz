using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ezpz
{
    // Reads the server's own SQLite databases with the SQLite library that ships with Windows 10 and 11
    // (winsqlite3.dll): accounts, bans and tickets from <data>\db\<server>.db, and characters from the world's
    // players.db. Everything is read-only except answering and deleting tickets, which run the same SQL the game uses.
    public static class ServerDb
    {
        public class Account { public string Name, Role = "", SteamId = ""; public DateTime LastConnection = DateTime.MinValue; }
        public class Ticket { public long Id; public string Author = "", Message = ""; public long AnsweredId = -1; public List<Ticket> Answers = new List<Ticket>(); }
        public class Ban { public string Kind, Value, Username = "", Reason = ""; }   // Kind: account, steam, ip
        public class Character { public string Username, Name = ""; public double X, Y, Z; public bool Dead; }

        const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101, SQLITE_OPEN_READONLY = 1, SQLITE_OPEN_READWRITE = 2;
        static readonly IntPtr SQLITE_TRANSIENT = new IntPtr(-1);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern double sqlite3_column_double(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_type(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_bind_text(IntPtr stmt, int idx, byte[] text, int n, IntPtr destructor);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_bind_int64(IntPtr stmt, int idx, long value);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_errmsg(IntPtr db);

        public static string PathFor(AppSettings s) { return Path.Combine(s.DataDir, "db", s.ServerName + ".db"); }
        public static string PlayersDbFor(AppSettings s) { return Path.Combine(s.SaveDir, "players.db"); }

        // One result row while a query runs.
        public class Row
        {
            internal IntPtr St;
            public string Text(int c) { return ColText(St, c); }
            public double Double(int c) { return sqlite3_column_double(St, c); }
            public long Long(int c) { return sqlite3_column_int64(St, c); }
            public bool IsNull(int c) { return sqlite3_column_type(St, c) == 5; }
        }

        // Runs one statement with ? parameters (string or long). Returns rows affected for writes.
        // Missing database file: does nothing. Errors other than "Windows has no SQLite" are thrown.
        static void Run(string path, bool write, string sql, object[] args, Action<Row> each)
        {
            if (!File.Exists(path)) return;
            IntPtr db = IntPtr.Zero, st = IntPtr.Zero;
            try
            {
                if (sqlite3_open_v2(Utf8(path), out db, write ? SQLITE_OPEN_READWRITE : SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK) throw Fail(db, "open");
                sqlite3_busy_timeout(db, 3000);   // the running server may hold the database for a moment
                if (sqlite3_prepare_v2(db, Utf8(sql), -1, out st, IntPtr.Zero) != SQLITE_OK) throw Fail(db, "prepare");
                for (int i = 0; i < (args ?? new object[0]).Length; i++)
                {
                    if (args[i] is string) sqlite3_bind_text(st, i + 1, Utf8((string)args[i]), -1, SQLITE_TRANSIENT);
                    else sqlite3_bind_int64(st, i + 1, Convert.ToInt64(args[i]));
                }
                var row = new Row { St = st };
                while (true)
                {
                    int r = sqlite3_step(st);
                    if (r == SQLITE_ROW) { if (each != null) each(row); continue; }
                    if (r == SQLITE_DONE) break;
                    throw Fail(db, "step");
                }
            }
            finally
            {
                if (st != IntPtr.Zero) sqlite3_finalize(st);
                if (db != IntPtr.Zero) sqlite3_close(db);
            }
        }

        static Exception Fail(IntPtr db, string what)
        {
            string m = db == IntPtr.Zero ? "" : ColTextPtr(sqlite3_errmsg(db));
            return new Exception("The server database couldn't be read (" + what + (m != "" ? ": " + m : "") + ").");
        }

        // Reads, logging problems (once per call) instead of failing: these feed lists that work without them.
        static List<T> Read<T>(string path, string what, string sql, Func<Row, T> make)
        {
            var list = new List<T>();
            try { Run(path, false, sql, null, r => list.Add(make(r))); }
            catch (DllNotFoundException) { }   // older Windows without winsqlite3
            catch (Exception ex) { AppLog.Error("reading " + what + " from the server database", ex); }
            return list;
        }

        // ---------- accounts

        public static List<Account> ReadAccounts(AppSettings s)
        {
            return Read(PathFor(s), "accounts",
                "SELECT w.username, COALESCE(r.name, ''), COALESCE(w.steamid, ''), COALESCE(w.lastConnection, '') FROM whitelist w LEFT JOIN role r ON r.id = w.role",
                r =>
                {
                    var a = new Account { Name = r.Text(0), Role = r.Text(1), SteamId = r.Text(2) == "0" ? "" : r.Text(2) };
                    DateTime t;
                    if (DateTime.TryParse(r.Text(3), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t)) a.LastConnection = t;
                    return a;
                }).Where(a => !string.IsNullOrEmpty(a.Name)).ToList();
        }

        // ---------- bans: banned accounts (their role is "banned"), Steam IDs and IP addresses

        public static List<Ban> ReadBans(AppSettings s)
        {
            string p = PathFor(s);
            var list = new List<Ban>();
            list.AddRange(Read(p, "banned accounts", "SELECT w.username FROM whitelist w JOIN role r ON r.id = w.role WHERE r.name = 'banned'",
                r => new Ban { Kind = "account", Value = r.Text(0), Username = r.Text(0) }));
            list.AddRange(Read(p, "banned Steam IDs", "SELECT steamid, COALESCE(reason, '') FROM bannedid",
                r => new Ban { Kind = "steam", Value = r.Text(0), Reason = r.Text(1) }));
            list.AddRange(Read(p, "banned IPs", "SELECT ip, COALESCE(username, ''), COALESCE(reason, '') FROM bannedip",
                r => new Ban { Kind = "ip", Value = r.Text(0), Username = r.Text(1), Reason = r.Text(2) }));
            // put the account name on Steam ID bans where we know it
            var bySteam = ReadAccounts(s).Where(a => a.SteamId != "").GroupBy(a => a.SteamId).ToDictionary(g => g.Key, g => g.First().Name);
            foreach (var b in list.Where(x => x.Kind == "steam")) { string n; if (bySteam.TryGetValue(b.Value, out n)) b.Username = n; }
            return list;
        }

        // ---------- tickets (players' help requests from the in-game ticket window)

        // Open tickets with any admin answers, oldest first.
        public static List<Ticket> ReadTickets(AppSettings s)
        {
            var all = Read(PathFor(s), "tickets", "SELECT id, COALESCE(author, ''), COALESCE(message, ''), answeredID FROM tickets ORDER BY id",
                r => new Ticket { Id = r.Long(0), Author = r.Text(1), Message = r.Text(2), AnsweredId = r.IsNull(3) ? -1 : r.Long(3) });
            var open = all.Where(t => t.AnsweredId < 0).ToList();
            foreach (var a in all.Where(t => t.AnsweredId >= 0))
            {
                var q = open.FirstOrDefault(t => t.Id == a.AnsweredId);
                if (q != null) q.Answers.Add(a);
            }
            return open;
        }

        // An answer the player sees in the game's ticket window (the game's own SQL for admin replies).
        public static void AnswerTicket(AppSettings s, long ticketId, string author, string message)
        {
            Run(PathFor(s), true, "INSERT INTO tickets (author, message, answeredID) VALUES (?, ?, ?)", new object[] { author, message, ticketId }, null);
        }

        public static void DeleteTicket(AppSettings s, long ticketId)
        {
            Run(PathFor(s), true, "DELETE FROM tickets WHERE id = ? OR answeredID = ?", new object[] { ticketId, ticketId }, null);
        }

        // ---------- characters (the world's players.db): character name, alive or dead, last saved position

        public static Dictionary<string, Character> ReadCharacters(AppSettings s)
        {
            var d = new Dictionary<string, Character>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Read(PlayersDbFor(s), "characters", "SELECT username, COALESCE(name, ''), x, y, z, COALESCE(isDead, 0) FROM networkPlayers WHERE playerIndex = 0 ORDER BY id",
                r => new Character { Username = r.Text(0), Name = r.Text(1), X = r.Double(2), Y = r.Double(3), Z = r.Double(4), Dead = r.Long(5) != 0 }))
                if (!string.IsNullOrEmpty(c.Username)) d[c.Username] = c;   // the newest row wins
            return d;
        }

        static byte[] Utf8(string s)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(s);
            Array.Resize(ref b, b.Length + 1);   // NUL-terminated
            return b;
        }

        static string ColText(IntPtr st, int col) { return ColTextPtr(sqlite3_column_text(st, col)); }

        static string ColTextPtr(IntPtr p)
        {
            if (p == IntPtr.Zero) return "";
            int n = 0;
            while (Marshal.ReadByte(p, n) != 0) n++;
            var b = new byte[n];
            Marshal.Copy(p, b, 0, n);
            return System.Text.Encoding.UTF8.GetString(b);
        }
    }
}
