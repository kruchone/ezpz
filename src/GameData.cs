using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ezpz
{
    public class PlayerInfo
    {
        public string Name;
        public string Role = "";
        public string SteamId = "";
        public string Ip = "";
        public DateTime FirstSeen = DateTime.MinValue;
        public DateTime LastSeen = DateTime.MinValue;
    }

    // Follows the current run's *_connections.txt to know who is online without sending console commands.
    // Join: event="fully-connected". Leave: event="disconnect", or a RakNet disconnection/connection-lost line
    // that only carries the session guid (mapped back to the name from the join).
    public class ConnectionFeed
    {
        string file;
        long pos;
        string partial = "";
        readonly Dictionary<string, string> guidToName = new Dictionary<string, string>();
        public readonly HashSet<string> Online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Regex Attr = new Regex("([\\w-]+)=\"([^\"]*)\"");

        // Returns true if the online set changed since the last call. The first read of a file
        // (ezpz opened mid-run, or a new server run) rebuilds the set from the whole file.
        public bool Poll(AppSettings s, out bool rebuilt)
        {
            rebuilt = false;
            string newest = null;
            try
            {
                if (Directory.Exists(s.LogsDir))
                    newest = Directory.GetFiles(s.LogsDir, "*_connections.txt").OrderByDescending(f => File.GetLastWriteTime(f)).FirstOrDefault();
            }
            catch { }
            if (newest == null) return false;
            if (newest != file) { file = newest; pos = 0; partial = ""; guidToName.Clear(); Online.Clear(); rebuilt = true; }
            string text;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < pos) { pos = 0; partial = ""; Online.Clear(); guidToName.Clear(); rebuilt = true; }
                    if (fs.Length == pos) return rebuilt;
                    fs.Seek(pos, SeekOrigin.Begin);
                    text = partial + new StreamReader(fs, Encoding.UTF8).ReadToEnd();
                    pos = fs.Length;
                }
            }
            catch { return false; }
            int lastNl = text.LastIndexOf('\n');
            partial = lastNl < text.Length - 1 ? text.Substring(lastNl + 1) : "";
            bool changed = rebuilt;
            foreach (var line in text.Substring(0, lastNl + 1).Split('\n'))
            {
                string ev = null, msg = "", guid = null, user = null;
                foreach (Match a in Attr.Matches(line))
                {
                    switch (a.Groups[1].Value)
                    {
                        case "event": ev = a.Groups[2].Value; break;
                        case "message": msg = a.Groups[2].Value; break;
                        case "guid": guid = a.Groups[2].Value; break;
                        case "username": user = a.Groups[2].Value; break;
                    }
                }
                if (ev == null) continue;
                if (ev == "fully-connected" && !string.IsNullOrEmpty(user) && user != "null")
                {
                    if (guid != null) guidToName[guid] = user;
                    changed |= Online.Add(user);
                }
                else if (ev == "disconnect" || msg.Contains("disconnection") || msg.Contains("connection-lost") || msg.Contains("lost-connection"))
                {
                    string name = !string.IsNullOrEmpty(user) && user != "null" ? user : null;
                    if (name == null && guid != null) guidToName.TryGetValue(guid, out name);
                    if (name != null) changed |= Online.Remove(name);
                }
            }
            return changed;
        }

        public void Reset() { file = null; pos = 0; partial = ""; guidToName.Clear(); Online.Clear(); }
    }

    // Reads facts the server writes to disk: player history from the connection logs,
    // and the Emergency Broadcast frequency from the startup log.
    public static class GameData
    {
        public static readonly string[] Roles = { "admin", "moderator", "gm", "observer", "priority", "user" };

        // Skill IDs accepted by addxp (from PerkFactory$Perks in B42).
        public static readonly string[] Perks =
        {
            "Aiming", "Axe", "Blacksmith", "Blunt", "Butchering", "Carving", "Cooking", "Doctor", "Electricity",
            "Farming", "Fishing", "Fitness", "FlintKnapping", "Glassmaking", "Husbandry", "Lightfoot", "LongBlade",
            "Maintenance", "Masonry", "Mechanics", "MetalWelding", "Nimble", "PlantScavenging", "Pottery", "Reloading",
            "SmallBlade", "SmallBlunt", "Sneak", "Spear", "Sprinting", "Strength", "Tailoring", "Tracking", "Trapping", "Woodwork",
        };

        public static readonly Regex EbsLine = new Regex(@"Automated Emergency Broadcast System, freq = (\d+)");
        static readonly Regex Attr = new Regex("([\\w-]+)=\"([^\"]*)\"");
        static readonly Regex Stamp = new Regex(@"^\[(\d\d-\d\d-\d\d \d\d:\d\d:\d\d)");

        public static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8))
                return r.ReadToEnd();
        }

        public static Dictionary<string, PlayerInfo> ReadRoster(AppSettings s)
        {
            var d = new Dictionary<string, PlayerInfo>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(s.LogsDir)) return d;
            var files = Directory.GetFiles(s.LogsDir, "*_connections.txt", SearchOption.AllDirectories)
                .OrderBy(f => File.GetLastWriteTime(f));
            foreach (var f in files)
            {
                string text;
                try { text = ReadShared(f); } catch { continue; }
                foreach (var line in text.Split('\n'))
                {
                    var tm = Stamp.Match(line);
                    DateTime t;
                    if (!tm.Success || !DateTime.TryParseExact(tm.Groups[1].Value, "dd-MM-yy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                    string user = null, role = null, sid = null, ip = null;
                    foreach (Match a in Attr.Matches(line))
                    {
                        string k = a.Groups[1].Value, v = a.Groups[2].Value;
                        if (k == "username") user = v;
                        else if (k == "role") role = v;
                        else if (k == "steam-id") sid = v;
                        else if (k == "ip") ip = v;
                    }
                    if (string.IsNullOrEmpty(user) || user == "null") continue;
                    PlayerInfo p;
                    if (!d.TryGetValue(user, out p)) { p = new PlayerInfo { Name = user, FirstSeen = t }; d[user] = p; }
                    if (t > p.LastSeen) p.LastSeen = t;
                    if (!string.IsNullOrEmpty(role)) p.Role = role;
                    if (!string.IsNullOrEmpty(sid) && sid != "0") p.SteamId = sid;
                    if (!string.IsNullOrEmpty(ip)) p.Ip = ip;
                }
            }
            return d;
        }

        // Newest server debug log that mentions the EBS channel. Returns frequency in kHz, or null.
        public static string FindEbs(AppSettings s, out DateTime when)
        {
            when = DateTime.MinValue;
            if (!Directory.Exists(s.LogsDir)) return null;
            var files = Directory.GetFiles(s.LogsDir, "*DebugLog-server*.txt", SearchOption.AllDirectories)
                .OrderByDescending(f => File.GetLastWriteTime(f));
            foreach (var f in files)
            {
                try
                {
                    var ms = EbsLine.Matches(ReadShared(f));
                    if (ms.Count > 0)
                    {
                        when = File.GetLastWriteTime(f);
                        return ms[ms.Count - 1].Groups[1].Value;
                    }
                }
                catch { }
            }
            return null;
        }

        public static string FormatFreq(string khz)
        {
            int k;
            return int.TryParse(khz, out k) ? (k / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " MHz" : khz;
        }
    }
}
