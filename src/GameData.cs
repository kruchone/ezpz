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
        public bool HasAccount;   // listed in the server's account database
    }

    public class LevelUp { public DateTime When; public string Perk; public int Level; }

    // One player's current character, rebuilt from the server's *_PerkLog.txt files:
    //   [time] [steamid][name][x,y,z][Login][Hours Survived: N].
    //   [time] [steamid][name][x,y,z][Perk=level, Perk=level, ...][Hours Survived: N].   (snapshot at login)
    //   [time] [steamid][name][x,y,z][Level Changed][Perk][level][Hours Survived: N].
    // Hours Survived going back down means a new character (the previous one died).
    public class CharacterSheet
    {
        public string Name, SteamId = "", Position = "";
        public int HoursSurvived, Characters = 1;
        public DateTime LastSeen = DateTime.MinValue, CharacterSince = DateTime.MinValue;
        public readonly Dictionary<string, int> Skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public readonly List<LevelUp> LevelUps = new List<LevelUp>();   // this character's, oldest first
    }

    public static class Characters
    {
        static readonly Regex Bracket = new Regex(@"\[([^\]]*)\]");
        static readonly Regex Hours = new Regex(@"Hours Survived:\s*(\d+)");

        // Skill groups as the game's Skills panel shows them (translation keys for the group names).
        public static readonly KeyValuePair<string, string[]>[] Groups =
        {
            new KeyValuePair<string, string[]>("PhysicalCategory", new[] { "Fitness", "Strength", "Sprinting", "Lightfoot", "Nimble", "Sneak" }),
            new KeyValuePair<string, string[]>("CombatMelee", new[] { "Axe", "Blunt", "SmallBlunt", "LongBlade", "SmallBlade", "Spear", "Maintenance" }),
            new KeyValuePair<string, string[]>("CombatFirearms", new[] { "Aiming", "Reloading" }),
            new KeyValuePair<string, string[]>("Crafting", new[] { "Woodwork", "Cooking", "Doctor", "Electricity", "MetalWelding", "Mechanics", "Tailoring", "Blacksmith", "Pottery", "Masonry", "Carving", "Glassmaking", "FlintKnapping" }),
            new KeyValuePair<string, string[]>("FarmingCategory", new[] { "Farming", "Husbandry", "Butchering" }),
            new KeyValuePair<string, string[]>("Survivalist", new[] { "Fishing", "Trapping", "PlantScavenging", "Tracking" }),
        };

        // Code names whose translation key is spelled differently.
        static readonly Dictionary<string, string> Alias = new Dictionary<string, string> { { "Lightfoot", "Lightfooted" }, { "Sneak", "Sneaking" }, { "PlantScavenging", "Foraging" } };

        public static Dictionary<string, string> LoadNames(string installDir)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string f = Path.Combine(installDir, "media", "lua", "shared", "Translate", "EN", "IG_UI.json");
                if (File.Exists(f))
                    foreach (Match m in Regex.Matches(File.ReadAllText(f), "\"IGUI_perks_([A-Za-z]+)\"\\s*:\\s*\"([^\"]*)\""))
                        d[m.Groups[1].Value] = m.Groups[2].Value;
            }
            catch { }
            return d;
        }

        public static string Name(Dictionary<string, string> names, string key)
        {
            string v, alias;
            if (names.TryGetValue(key, out v)) return v;
            if (Alias.TryGetValue(key, out alias) && names.TryGetValue(alias, out v)) return v;
            return Regex.Replace(key, @"(?<=[a-z])(?=[A-Z])", " ");
        }

        public static Dictionary<string, CharacterSheet> Read(AppSettings s)
        {
            var d = new Dictionary<string, CharacterSheet>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(s.LogsDir)) return d;
            var files = Directory.GetFiles(s.LogsDir, "*_PerkLog.txt", SearchOption.AllDirectories).OrderBy(f => File.GetLastWriteTime(f));
            foreach (var f in files)
            {
                string text;
                try { text = GameData.ReadShared(f); } catch { continue; }
                foreach (var line in text.Split('\n'))
                {
                    var b = Bracket.Matches(line).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
                    if (b.Count < 6) continue;
                    DateTime t;
                    if (!DateTime.TryParseExact(b[0].Length >= 17 ? b[0].Substring(0, 17) : b[0], "dd-MM-yy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                    string name = b[2];
                    if (name == "" || name == "null") continue;
                    var hm = Hours.Match(b[b.Count - 1]);
                    int hours = hm.Success ? int.Parse(hm.Groups[1].Value) : -1;

                    CharacterSheet c;
                    if (!d.TryGetValue(name, out c)) { c = new CharacterSheet { Name = name, CharacterSince = t }; d[name] = c; }
                    if (hours >= 0 && hours + 1 < c.HoursSurvived)
                    {
                        // a new character: the old one died (or was reset)
                        c.Characters++;
                        c.CharacterSince = t;
                        c.Skills.Clear();
                        c.LevelUps.Clear();
                    }
                    if (hours >= 0) c.HoursSurvived = hours;
                    if (b[1] != "" && b[1] != "0") c.SteamId = b[1];
                    c.Position = b[3];
                    if (t > c.LastSeen) c.LastSeen = t;

                    string kind = b[4];
                    if (kind.Contains("="))
                    {
                        foreach (var part in kind.Split(','))
                        {
                            var kv = part.Split('=');
                            int lv;
                            if (kv.Length == 2 && int.TryParse(kv[1].Trim(), out lv)) c.Skills[kv[0].Trim()] = lv;
                        }
                    }
                    else if (kind == "Level Changed" && b.Count >= 8)
                    {
                        int lv;
                        if (int.TryParse(b[6], out lv))
                        {
                            c.Skills[b[5]] = lv;
                            c.LevelUps.Add(new LevelUp { When = t, Perk = b[5], Level = lv });
                        }
                    }
                }
            }
            return d;
        }
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
            // accounts from the server's database: adds people who have an account but never joined (e.g. just
            // whitelisted), and gives everyone their current role even if it changed while they were offline
            foreach (var a in ServerDb.ReadAccounts(s))
            {
                PlayerInfo p;
                if (!d.TryGetValue(a.Name, out p)) { p = new PlayerInfo { Name = a.Name, LastSeen = a.LastConnection }; d[a.Name] = p; }
                if (a.Role != "") p.Role = a.Role;
                if (p.SteamId == "" && a.SteamId != "") p.SteamId = a.SteamId;
                p.HasAccount = true;
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
