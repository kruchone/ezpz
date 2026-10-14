using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ezpz
{
    // App settings, persisted to %APPDATA%\ezpz\manager.ini
    public class AppSettings
    {
        public string InstallDir = @"C:\PZServer\server";
        public string SteamCmdDir = @"C:\PZServer\steamcmd";
        public string DataDir = @"C:\PZServer\data";
        public string BackupDir = @"C:\PZServer\backups";
        public string ServerName = "pzserver";
        public string Branch = "public";
        public int MinRamGB = 4;
        public int MaxRamGB = 8;
        public string AdminPasswordProtected = "";
        public bool AutoRestart = true;
        public int RestartHours = 0;
        public bool UpdateOnRestart = false;
        public bool BackupOnStart = true;
        public int KeepAutoBackups = 10;

        public static string ManagerDir
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("EZPZ_HOME");   // for testing with separate settings
                return !string.IsNullOrEmpty(over) ? over : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ezpz");
            }
        }
        static string FilePath { get { return Path.Combine(ManagerDir, "manager.ini"); } }

        // Settings folder used before the app was renamed to ezpz.
        static string LegacyFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PZServerManager", "manager.ini"); }
        }

        public static AppSettings Load()
        {
            var s = new AppSettings();
            if (!File.Exists(FilePath) && File.Exists(LegacyFile) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EZPZ_HOME")))
            {
                try { Directory.CreateDirectory(ManagerDir); File.Copy(LegacyFile, FilePath); } catch { }
            }
            if (!File.Exists(FilePath)) return s;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                var f = typeof(AppSettings).GetField(line.Substring(0, i).Trim(), BindingFlags.Public | BindingFlags.Instance);
                if (f == null) continue;
                string v = line.Substring(i + 1);
                try
                {
                    if (f.FieldType == typeof(string)) f.SetValue(s, v);
                    else if (f.FieldType == typeof(int)) f.SetValue(s, int.Parse(v));
                    else if (f.FieldType == typeof(bool)) f.SetValue(s, bool.Parse(v));
                }
                catch { }
            }
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(ManagerDir);
            var sb = new StringBuilder();
            foreach (var f in typeof(AppSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                sb.AppendLine(f.Name + "=" + Convert.ToString(f.GetValue(this)));
            File.WriteAllText(FilePath, sb.ToString());
        }

        // Admin password is encrypted with Windows DPAPI (only this Windows user can read it).
        public string AdminPassword
        {
            get
            {
                if (string.IsNullOrEmpty(AdminPasswordProtected)) return "";
                try
                {
                    var b = ProtectedData.Unprotect(Convert.FromBase64String(AdminPasswordProtected), null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(b);
                }
                catch { return ""; }
            }
            set
            {
                if (string.IsNullOrEmpty(value)) { AdminPasswordProtected = ""; return; }
                var b = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
                AdminPasswordProtected = Convert.ToBase64String(b);
            }
        }

        // ---- Derived paths (PZ layout inside -cachedir) ----
        public string ServerConfigDir { get { return Path.Combine(DataDir, "Server"); } }
        public string IniPath { get { return Path.Combine(ServerConfigDir, ServerName + ".ini"); } }
        public string SandboxPath { get { return Path.Combine(ServerConfigDir, ServerName + "_SandboxVars.lua"); } }
        public string SpawnRegionsPath { get { return Path.Combine(ServerConfigDir, ServerName + "_spawnregions.lua"); } }
        public string SpawnPointsPath { get { return Path.Combine(ServerConfigDir, ServerName + "_spawnpoints.lua"); } }
        public string SaveDir { get { return Path.Combine(DataDir, "Saves", "Multiplayer", ServerName); } }
        public string DbPath { get { return Path.Combine(DataDir, "db", ServerName + ".db"); } }
        public string LogsDir { get { return Path.Combine(DataDir, "Logs"); } }
        public string ConsoleLogPath { get { return Path.Combine(DataDir, "server-console.txt"); } }
        public string StartBat { get { return Path.Combine(InstallDir, "StartServer64.bat"); } }
        public string SteamCmdExe { get { return Path.Combine(SteamCmdDir, "steamcmd.exe"); } }
        public string AppManifest { get { return Path.Combine(InstallDir, "steamapps", "appmanifest_380870.acf"); } }

        public IEnumerable<string> ConfigFiles
        {
            get { return new[] { IniPath, SandboxPath, SpawnRegionsPath, SpawnPointsPath }; }
        }

        public static bool ValidServerName(string n)
        {
            return !string.IsNullOrEmpty(n) && Regex.IsMatch(n, @"^[A-Za-z0-9_\-]{1,40}$");
        }
    }

    // Order- and comment-preserving editor for PZ's servername.ini
    public class IniFile
    {
        public List<string> Lines = new List<string>();
        public string FilePath;

        public static IniFile Load(string path)
        {
            var f = new IniFile();
            f.FilePath = path;
            if (File.Exists(path)) f.Lines.AddRange(File.ReadAllLines(path));
            return f;
        }

        int Find(string key)
        {
            for (int i = 0; i < Lines.Count; i++)
            {
                string l = Lines[i];
                if (l.StartsWith("#")) continue;
                int eq = l.IndexOf('=');
                if (eq > 0 && string.Equals(l.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        public bool Has(string key) { return Find(key) >= 0; }

        public string Get(string key, string def)
        {
            int i = Find(key);
            if (i < 0) return def;
            return Lines[i].Substring(Lines[i].IndexOf('=') + 1);
        }

        public void Set(string key, string value)
        {
            int i = Find(key);
            string line = key + "=" + (value ?? "");
            if (i >= 0) Lines[i] = line; else Lines.Add(line);
        }

        public List<KeyValuePair<string, string>> All()
        {
            var r = new List<KeyValuePair<string, string>>();
            foreach (var l in Lines)
            {
                if (l.StartsWith("#")) continue;
                int eq = l.IndexOf('=');
                if (eq > 0) r.Add(new KeyValuePair<string, string>(l.Substring(0, eq).Trim(), l.Substring(eq + 1)));
            }
            return r;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllLines(FilePath, Lines.ToArray(), new UTF8Encoding(false));
        }
    }

    // Minimal Source RCON client (PZ supports it when RCONPort/RCONPassword are set)
    public static class Rcon
    {
        public static string Send(int port, string password, string command)
        {
            using (var c = new TcpClient())
            {
                c.ReceiveTimeout = 5000;
                c.SendTimeout = 5000;
                c.Connect("127.0.0.1", port);
                var s = c.GetStream();
                Write(s, 1, 3, password);
                while (true)
                {
                    int id, type; string body;
                    Read(s, out id, out type, out body);
                    if (type == 2)
                    {
                        if (id == -1) throw new Exception("RCON authentication failed (check RCONPassword).");
                        break;
                    }
                }
                Write(s, 2, 2, command);
                int rid, rtype; string resp;
                Read(s, out rid, out rtype, out resp);
                return resp;
            }
        }

        static void Write(NetworkStream s, int id, int type, string body)
        {
            byte[] b = Encoding.UTF8.GetBytes(body);
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(b.Length + 10);
            w.Write(id);
            w.Write(type);
            w.Write(b);
            w.Write((short)0);
            var all = ms.ToArray();
            s.Write(all, 0, all.Length);
        }

        static void Read(NetworkStream s, out int id, out int type, out string body)
        {
            byte[] lenB = ReadExact(s, 4);
            int len = BitConverter.ToInt32(lenB, 0);
            byte[] p = ReadExact(s, len);
            id = BitConverter.ToInt32(p, 0);
            type = BitConverter.ToInt32(p, 4);
            body = Encoding.UTF8.GetString(p, 8, Math.Max(0, len - 10));
        }

        static byte[] ReadExact(NetworkStream s, int n)
        {
            var b = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = s.Read(b, off, n - off);
                if (r <= 0) throw new IOException("RCON connection closed.");
                off += r;
            }
            return b;
        }
    }

    public static class Util
    {
        // Windows command-line quoting (MSVCRT rules) for arguments passed to java.exe
        public static string QuoteArg(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char ch in a)
            {
                if (ch == '\\') { bs++; continue; }
                if (ch == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
                sb.Append('\\', bs); bs = 0; sb.Append(ch);
            }
            sb.Append('\\', bs * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static long DirSize(string path)
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    try { total += new FileInfo(f).Length; } catch { }
            }
            catch { }
            return total;
        }

        public static string Size(long b)
        {
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.0") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0.0") + " MB";
            if (b >= 1L << 10) return (b / 1024.0).ToString("0") + " KB";
            return b + " B";
        }

        public static string RandomPassword(int len)
        {
            const string chars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = new byte[len];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder();
            foreach (var x in bytes) sb.Append(chars[x % chars.Length]);
            return sb.ToString();
        }

        // Safety net: never delete anything outside the data folder.
        public static bool IsInside(string child, string parent)
        {
            string c = Path.GetFullPath(child).TrimEnd('\\') + "\\";
            string p = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
            return c.StartsWith(p, StringComparison.OrdinalIgnoreCase) && c.Length > p.Length;
        }

        public static void DeletePath(string path)
        {
            if (Directory.Exists(path))
            {
                foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(path, true);
            }
            else if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
    }
}
