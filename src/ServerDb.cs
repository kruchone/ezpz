using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Ezpz
{
    // Reads the accounts from the server's own database (<data>\db\<server>.db, SQLite) using the SQLite library
    // that ships with Windows 10 and 11 (winsqlite3.dll). Opened read-only; the running server is never disturbed.
    public static class ServerDb
    {
        public class Account { public string Name, Role = "", SteamId = ""; public DateTime LastConnection = DateTime.MinValue; }

        const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_OPEN_READONLY = 1;

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int sqlite3_finalize(IntPtr stmt);

        public static string PathFor(AppSettings s) { return Path.Combine(s.DataDir, "db", s.ServerName + ".db"); }

        // Every account, with its role name. Empty if there's no database yet or it can't be read.
        public static List<Account> ReadAccounts(AppSettings s)
        {
            var list = new List<Account>();
            string path = PathFor(s);
            if (!File.Exists(path)) return list;
            IntPtr db = IntPtr.Zero, st = IntPtr.Zero;
            try
            {
                if (sqlite3_open_v2(Utf8(path), out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK) return list;
                sqlite3_busy_timeout(db, 2000);
                const string sql = "SELECT w.username, COALESCE(r.name, ''), COALESCE(w.steamid, ''), COALESCE(w.lastConnection, '') FROM whitelist w LEFT JOIN role r ON r.id = w.role";
                if (sqlite3_prepare_v2(db, Utf8(sql), -1, out st, IntPtr.Zero) != SQLITE_OK) return list;
                while (sqlite3_step(st) == SQLITE_ROW)
                {
                    string name = Text(st, 0);
                    if (string.IsNullOrEmpty(name)) continue;
                    var a = new Account { Name = name, Role = Text(st, 1), SteamId = Text(st, 2) };
                    DateTime t;
                    if (DateTime.TryParse(Text(st, 3), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t)) a.LastConnection = t;
                    if (a.SteamId == "0") a.SteamId = "";
                    list.Add(a);
                }
            }
            catch (DllNotFoundException) { }   // older Windows without winsqlite3: the list just comes from the logs
            catch (Exception ex) { AppLog.Error("reading the server's account database", ex); }
            finally
            {
                if (st != IntPtr.Zero) sqlite3_finalize(st);
                if (db != IntPtr.Zero) sqlite3_close(db);
            }
            return list;
        }

        static byte[] Utf8(string s)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(s);
            Array.Resize(ref b, b.Length + 1);   // NUL-terminated
            return b;
        }

        static string Text(IntPtr st, int col)
        {
            IntPtr p = sqlite3_column_text(st, col);
            if (p == IntPtr.Zero) return "";
            int n = 0;
            while (Marshal.ReadByte(p, n) != 0) n++;
            var b = new byte[n];
            Marshal.Copy(p, b, 0, n);
            return System.Text.Encoding.UTF8.GetString(b);
        }
    }
}
