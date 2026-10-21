using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ezpz
{
    public class LogEntry
    {
        public DateTime Time;
        public string Kind;        // Chat, Admin, Console, PvP, Action, Join, Skill
        public string Player = ""; // who did it ("" if nobody in particular)
        public string Text;        // readable description
        public string Where = "";  // x, y, z when the log says
        public string File;        // source log file
        public string Raw;         // the original line
        public string Mentions = "";  // other names in the line, lower-case, for player filtering
        public bool Important;        // PZ marked it [IMPORTANT] (e.g. spawning hordes)
    }

    // Reads Project Zomboid's moderation-relevant logs from the data folder (current run and the
    // logs_<date> archives PZ makes at each start) into one timeline. Files that haven't changed
    // since the last read are not parsed again.
    public class LogViewerData
    {
        public static readonly string[] Kinds = { "Chat", "Admin", "Console", "PvP", "Action", "Join", "Skill" };

        class FileCache { public DateTime Mtime; public long Size; public List<LogEntry> Entries; }
        readonly Dictionary<string, FileCache> cache = new Dictionary<string, FileCache>(StringComparer.OrdinalIgnoreCase);

        static readonly Regex Stamp = new Regex(@"^\[(\d\d-\d\d-\d\d \d\d:\d\d:\d\d)(?:\.\d+)?\]");
        static readonly Regex ChatMsg = new Regex(@"Got message:ChatMessage\{chat=([^,]*), author='([^']*)', text='(.*)'\}");
        static readonly Regex Alert = new Regex(@"Server alert message: '(.*)' sent");
        static readonly Regex Quoted = new Regex("\"([^\"]{1,40})\"");
        static readonly Regex Pos = new Regex(@"(?<![\d.])-?\d+(?:\.\d+)?,-?\d+(?:\.\d+)?(?:,-?\d+)?(?![\d.])");   // x,y[,z], sometimes with decimals
        static readonly Regex Safety = new Regex("Safety: \"([^\"]+)\" \\(([^)]*)\\) (store|restore) (true|false)");
        static readonly Regex UserLine = new Regex("^\\d+ \"([^\"]+)\" (.*?)\\.?$");
        static readonly Regex ConsoleCmd = new Regex("command entered via (server console \\(System\\.in\\)|RCON[^:]*|[^:]+): \"(.*)\"");
        static readonly Regex Bracket = new Regex(@"\[([^\]]*)\]");
        // ezpz's own background checks; not moderation history
        static readonly Regex Polling = new Regex(@"^(players|stats \w+ all|stats \w+ list)$");

        // The Logs page shows the last KeepDays days: the game keeps every run's logs forever, and holding them
        // all in memory would grow without end on a long-lived server. (Older logs stay on disk.)
        public const int KeepDays = 30;

        public List<LogEntry> Load(AppSettings s, Dictionary<string, string> perkNames)
        {
            var all = new List<LogEntry>();
            if (!Directory.Exists(s.LogsDir)) return all;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(s.LogsDir, "*.txt", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(f);
                string kind = KindOf(name);
                if (kind == null) continue;
                seen.Add(f);
                FileInfo fi;
                try { fi = new FileInfo(f); } catch { continue; }
                if (fi.LastWriteTime < DateTime.Now.AddDays(-KeepDays)) continue;
                FileCache c;
                if (!cache.TryGetValue(f, out c) || c.Mtime != fi.LastWriteTimeUtc || c.Size != fi.Length)
                {
                    c = new FileCache { Mtime = fi.LastWriteTimeUtc, Size = fi.Length, Entries = Parse(f, kind, perkNames) };
                    cache[f] = c;
                }
                all.AddRange(c.Entries);
            }
            foreach (var gone in cache.Keys.Where(k => !seen.Contains(k)).ToList()) cache.Remove(gone);
            all.Sort((a, b) => b.Time.CompareTo(a.Time));
            return all;
        }

        static string KindOf(string file)
        {
            if (file.EndsWith("_chat.txt")) return "chat";
            if (file.EndsWith("_admin.txt")) return "admin";
            if (file.EndsWith("_pvp.txt")) return "pvp";
            if (file.EndsWith("_ClientActionLog.txt")) return "action";
            if (file.EndsWith("_user.txt")) return "user";
            if (file.EndsWith("_PerkLog.txt")) return "perk";
            if (file.EndsWith("_DebugLog-server.txt")) return "debug";
            return null;
        }

        static List<LogEntry> Parse(string file, string kind, Dictionary<string, string> perkNames)
        {
            var list = new List<LogEntry>();
            string text;
            try { text = GameData.ReadShared(file); } catch { return list; }
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                var sm = Stamp.Match(line);
                if (!sm.Success) continue;
                DateTime t;
                if (!DateTime.TryParseExact(sm.Groups[1].Value, "dd-MM-yy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                string rest = line.Substring(sm.Length).Trim();
                LogEntry e = null;
                switch (kind)
                {
                    case "chat": e = ParseChat(rest); break;
                    case "admin": e = ParseAdmin(rest); break;
                    case "pvp": e = ParsePvp(rest); break;
                    case "action": e = ParseAction(rest); break;
                    case "user": e = ParseUser(rest); break;
                    case "perk": e = ParsePerk(rest, perkNames); break;
                    case "debug": e = ParseDebug(rest); break;
                }
                if (e == null) continue;
                e.Time = t;
                e.File = file;
                e.Raw = line;
                if (e.Mentions == "") e.Mentions = string.Join("|", Quoted.Matches(rest).Cast<Match>().Select(m => m.Groups[1].Value.ToLowerInvariant()));
                list.Add(e);
            }
            return list;
        }

        static LogEntry ParseChat(string rest)
        {
            var m = ChatMsg.Match(rest);
            if (m.Success) return new LogEntry { Kind = "Chat", Player = m.Groups[2].Value, Text = "[" + m.Groups[1].Value + "] " + m.Groups[3].Value };
            m = Alert.Match(rest);
            if (m.Success) return new LogEntry { Kind = "Chat", Player = "(server)", Text = "[Server message] " + m.Groups[1].Value };
            return null;   // chat system bookkeeping
        }

        static LogEntry ParseAdmin(string rest)
        {
            // "admin granted admin access level on ezpz." / "admin added item Base.Axe in ezpz's inventory."
            string r = rest.TrimEnd('.');
            bool important = r.StartsWith("[IMPORTANT]");
            if (important) r = r.Substring(11).Trim();
            int sp = r.IndexOf(' ');
            var e = new LogEntry { Kind = "Admin", Player = sp > 0 ? r.Substring(0, sp) : "", Text = r, Important = important };
            var p = Pos.Match(r);
            if (p.Success) e.Where = p.Value;
            e.Mentions = r.ToLowerInvariant();   // the target is somewhere in the sentence
            return e;
        }

        static LogEntry ParsePvp(string rest)
        {
            string r = Regex.Replace(rest, @"^\[\w+\]\s*", "").TrimEnd('.');
            var m = Safety.Match(r);
            if (m.Success)
            {
                bool on = m.Groups[4].Value == "true";
                return new LogEntry
                {
                    Kind = "PvP", Player = m.Groups[1].Value, Where = m.Groups[2].Value,
                    Text = "PvP safety " + (on ? "on" : "off") + (m.Groups[3].Value == "restore" ? " (restored at login)" : " (saved at logout)"),
                };
            }
            var q = Quoted.Match(r);
            var e = new LogEntry { Kind = "PvP", Player = q.Success ? q.Groups[1].Value : "", Text = r };
            var p = Pos.Match(r);
            if (p.Success) e.Where = p.Value;
            return e;
        }

        static LogEntry ParseAction(string rest)
        {
            // [steamid][ISEnterVehicle][name][x,y,z][CarStationWagon]
            var b = Bracket.Matches(rest).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            if (b.Count < 4) return null;
            string action = Regex.Replace(Regex.Replace(b[1], "^IS", ""), @"(?<=[a-z])(?=[A-Z])", " ");
            string extra = b.Count > 4 ? string.Join(", ", b.Skip(4)) : "";
            return new LogEntry { Kind = "Action", Player = b[2], Where = b[3], Text = action + (extra != "" ? ": " + extra : "") };
        }

        static LogEntry ParseUser(string rest)
        {
            var m = UserLine.Match(rest);
            if (!m.Success) return null;   // "Connection add index=..." bookkeeping
            var e = new LogEntry { Kind = "Join", Player = m.Groups[1].Value, Text = m.Groups[2].Value };
            var p = Pos.Match(rest);
            if (p.Success) e.Where = p.Value;
            return e;
        }

        static LogEntry ParsePerk(string rest, Dictionary<string, string> perkNames)
        {
            var b = Bracket.Matches(rest).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            if (b.Count < 7 || b[3] != "Level Changed") return null;   // login snapshots are covered elsewhere
            return new LogEntry
            {
                Kind = "Skill", Player = b[1], Where = b[2],
                Text = Characters.Name(perkNames ?? new Dictionary<string, string>(), b[4]) + " reached level " + b[5],
            };
        }

        static LogEntry ParseDebug(string rest)
        {
            var m = ConsoleCmd.Match(rest);
            if (!m.Success) return null;
            string cmd = m.Groups[2].Value;
            if (Polling.IsMatch(cmd)) return null;
            string via = m.Groups[1].Value.StartsWith("server console") ? "server console" : m.Groups[1].Value.Trim();
            // hide the admin password if a command carried one
            cmd = Regex.Replace(cmd, "^(setpassword \"[^\"]*\" )\"[^\"]*\"", "$1\"********\"");
            return new LogEntry { Kind = "Console", Player = "(" + via + ")", Text = cmd, Mentions = cmd.ToLowerInvariant() };
        }
    }
}
