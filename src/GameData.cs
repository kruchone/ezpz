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
