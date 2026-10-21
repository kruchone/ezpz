using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Ezpz
{
    // ---------------------------------------------------------------- stored state

    // What the logs can't tell us, collected from EZPZ Server Tools while the season runs.
    public class SeasonPlayer
    {
        public string Name = "";
        public double FirstKills = -1;   // the character's kill counter when first seen this season (not ours to count)
        public double LastKills = -1;    // latest kill counter of the current character
        public double KillsBanked;       // kills from earlier characters this season
        public List<string> Cells = new List<string>();   // 100x100 map areas visited, "x,y"
        public int PvpKills;
        public double Adjust;            // admin adjustment to the Survivor Score, in points
        public bool Disqualified;
        public string DqReason = "";

        public double Kills
        {
            get
            {
                double current = LastKills < 0 ? 0 : LastKills - Math.Max(0, FirstKills);
                return KillsBanked + Math.Max(0, current);
            }
        }
    }

    public class AuditEntry { public string At = "", Who = "", What = "", Before = "", After = "", Reason = ""; }

    public class Standing
    {
        public string Name = "";
        public int Rank;
        public double Score;
        public bool Qualified = true;
        public string Note = "";
        public double HoursPlayed;
        public Dictionary<string, double> Raw = new Dictionary<string, double>();
        public Dictionary<string, double> Pct = new Dictionary<string, double>();
    }

    public class SeasonRecord
    {
        public int Number = 1;
        public string Start = "", End = "", EndReason = "";
        public bool ModSeen;                         // EZPZ Server Tools reported during this season
        public bool FreshStart;                      // began with a wipe that deleted characters: everyone's hours and kills start at zero
        public Dictionary<string, SeasonPlayer> Players = new Dictionary<string, SeasonPlayer>(StringComparer.OrdinalIgnoreCase);
        public List<AuditEntry> Audit = new List<AuditEntry>();
        public List<string> EndNearSteps = new List<string>();   // announcements already made
        public List<Standing> Final = new List<Standing>();      // set when the season ends
        public Dictionary<string, string> Titles = new Dictionary<string, string>();   // title -> player

        public DateTime StartTime { get { return Parse(Start); } }
        public DateTime EndTime { get { return Parse(End); } }
        static DateTime Parse(string s) { DateTime d; return DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind, out d) ? d : DateTime.MinValue; }

        public SeasonPlayer Player(string name)
        {
            SeasonPlayer p;
            if (!Players.TryGetValue(name, out p)) { p = new SeasonPlayer { Name = name }; Players[name] = p; }
            return p;
        }
    }

    public class SeasonReward
    {
        public int Season;
        public string Player = "", Item = "", Title = "", Delivered = "";
    }

    // Everything about seasons for one server: the current season, finished ones (the Hall of Fame) and rewards.
    // Kept in %APPDATA%\ezpz\servers\<id>.seasons.json.
    public class SeasonStore
    {
        public SeasonRecord Current;
        public List<SeasonRecord> Archive = new List<SeasonRecord>();
        public List<SeasonReward> Rewards = new List<SeasonReward>();
        public string LastWeeklyPost = "";

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static string PathFor(string id) { return Path.Combine(AppSettings.ManagerDir, "servers", id + ".seasons.json"); }

        public static SeasonStore Load(string id)
        {
            try
            {
                var p = PathFor(id);
                if (File.Exists(p))
                {
                    var st = Json.Deserialize<SeasonStore>(File.ReadAllText(p)) ?? new SeasonStore();
                    // dictionaries come back case-sensitive; player names aren't
                    foreach (var r in new[] { st.Current }.Concat(st.Archive).Where(x => x != null))
                        r.Players = new Dictionary<string, SeasonPlayer>(r.Players ?? new Dictionary<string, SeasonPlayer>(), StringComparer.OrdinalIgnoreCase);
                    return st;
                }
            }
            catch (Exception ex) { AppLog.Error("reading the seasons file", ex); }
            return new SeasonStore();
        }

        public void Save(string id)
        {
            try
            {
                var p = PathFor(id);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p + ".tmp", Json.Serialize(this));
                if (File.Exists(p)) File.Replace(p + ".tmp", p, null); else File.Move(p + ".tmp", p);
            }
            catch (Exception ex) { AppLog.Error("saving the seasons file", ex); }
        }
    }

    // ---------------------------------------------------------------- scoring

    public static class Ladder
    {
        // Category keys, in the order the weights are stored in AppSettings.SeasonWeights.
        public static readonly string[] Categories = { "Endurance", "Skills", "Slayer", "Explorer", "Resilience", "FinalStand", "PvP" };
        public static readonly string[] CategoryNames = { "Endurance", "Skills", "Slayer", "Explorer", "Resilience", "Final Stand", "PvP" };
        public static readonly string[] Titles = { "Last One Standing", "Jack of All Trades", "Butcher of Knox", "Wanderer", "Unkillable", null, "Apex Predator" };
        public const string ChampionTitle = "The Survivor";

        public static int[] Weights(AppSettings s)
        {
            var w = (s.SeasonWeights ?? "").Split(',').Select(x => { int v; return int.TryParse(x.Trim(), out v) ? Math.Max(0, v) : -1; }).ToArray();
            return w.Length == Categories.Length && w.All(x => x >= 0) ? w : new[] { 30, 20, 20, 10, 10, 10, 0 };
        }

        // Which categories count: Slayer and Explorer need the mod; any with weight 0 is off.
        public static bool[] Active(AppSettings s, SeasonRecord season)
        {
            var w = Weights(s);
            return Categories.Select((c, i) => w[i] > 0 && ((c != "Slayer" && c != "Explorer") || season.ModSeen)).ToArray();
        }

        static readonly Regex Stamp = new Regex(@"^\[(\d\d-\d\d-\d\d \d\d:\d\d:\d\d)");
        static readonly Regex Bracket = new Regex(@"\[([^\]]*)\]");
        static readonly Regex HoursRx = new Regex(@"Hours Survived:\s*(\d+)");
        static readonly Regex Attr = new Regex("([\\w-]+)=\"([^\"]*)\"");
        static readonly Regex Cheat = new Regex(@"(godmode|god mode|invisible|noclip|no-clip)", RegexOptions.IgnoreCase);

        static DateTime When(string line)
        {
            var m = Stamp.Match(line);
            DateTime t;
            return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "dd-MM-yy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t : DateTime.MinValue;
        }

        // Only files written since the season began: older runs' logs can't hold anything for it.
        static IEnumerable<string> LogFiles(AppSettings s, string suffix, DateTime since)
        {
            if (!Directory.Exists(s.LogsDir)) return new string[0];
            return Directory.GetFiles(s.LogsDir, "*" + suffix, SearchOption.AllDirectories).Where(f => File.GetLastWriteTime(f) >= since).OrderBy(File.GetLastWriteTime);
        }

        static IEnumerable<string> Lines(string file)
        {
            string text;
            try { text = GameData.ReadShared(file); } catch { yield break; }
            foreach (var l in text.Split('\n')) yield return l.TrimEnd('\r');
        }

        // MaxHours is the longest life this season, in hours lived during the season: a character kept from before
        // (a "keep characters" wipe, or a season started without a wipe) only counts the hours since it began.
        class Raw { public double MaxHours, Levels, Deaths, Played, LifeBase = -1, LastHours = -1; public bool Cheated, Seen; }

        // Scores everyone who played in [season start, until]. alive: who is alive right now (for Final Stand);
        // roles: account roles (to leave staff out).
        public static List<Standing> Compute(AppSettings s, SeasonRecord season, DateTime until, ICollection<string> alive, IDictionary<string, string> roles)
        {
            DateTime from = season.StartTime;
            var raw = new Dictionary<string, Raw>(StringComparer.OrdinalIgnoreCase);
            Func<string, Raw> R = n => { Raw r; if (!raw.TryGetValue(n, out r)) { r = new Raw(); raw[n] = r; } return r; };

            // lives, deaths and level-ups from the PerkLog
            foreach (var f in LogFiles(s, "_PerkLog.txt", from))
                foreach (var line in Lines(f))
                {
                    var t = When(line);
                    if (t < from || t > until) continue;
                    var b = Bracket.Matches(line).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
                    if (b.Count < 5 || b[2] == "" || b[2] == "null") continue;
                    var r = R(b[2]);
                    r.Seen = true;
                    var hm = HoursRx.Match(b[b.Count - 1]);
                    if (hm.Success)
                    {
                        double h = double.Parse(hm.Groups[1].Value, CultureInfo.InvariantCulture);
                        if (r.LastHours >= 0 && h + 1 < r.LastHours) r.LifeBase = 0;   // hours went down: a new character, born this season
                        if (r.LifeBase < 0) r.LifeBase = season.FreshStart ? 0 : h;   // first time seen this season
                        r.MaxHours = Math.Max(r.MaxHours, h - r.LifeBase);
                        r.LastHours = h;
                    }
                    if (b[4] == "Died") r.Deaths++;
                    else if (b[4] == "Level Changed") r.Levels++;
                }

            // playtime from the connection log: fully-connected .. disconnect (or the end of that run's log)
            foreach (var f in LogFiles(s, "_connections.txt", from))
            {
                var open = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                var guidName = new Dictionary<string, string>();
                DateTime last = DateTime.MinValue;
                foreach (var line in Lines(f))
                {
                    var t = When(line);
                    if (t == DateTime.MinValue) continue;
                    last = t;
                    string ev = null, msg = "", guid = null, user = null;
                    foreach (Match a in Attr.Matches(line))
                        switch (a.Groups[1].Value) { case "event": ev = a.Groups[2].Value; break; case "message": msg = a.Groups[2].Value; break; case "guid": guid = a.Groups[2].Value; break; case "username": user = a.Groups[2].Value; break; }
                    if (ev == "fully-connected" && !string.IsNullOrEmpty(user) && user != "null") { open[user] = t; if (guid != null) guidName[guid] = user; }
                    else if (ev == "disconnect" || msg.Contains("disconnection") || msg.Contains("connection-lost") || msg.Contains("lost-connection"))
                    {
                        string name = !string.IsNullOrEmpty(user) && user != "null" ? user : null;
                        if (name == null && guid != null) guidName.TryGetValue(guid, out name);
                        DateTime st;
                        if (name != null && open.TryGetValue(name, out st)) { AddPlayed(R(name), st, t, from, until); open.Remove(name); }
                    }
                }
                // still connected when the log ends: count to now for the live log, to its last line otherwise
                bool liveLog = (DateTime.Now - File.GetLastWriteTime(f)).TotalMinutes < 10;
                foreach (var kv in open) AddPlayed(R(kv.Key), kv.Value, liveLog ? until : last, from, until);
            }

            // god mode / invisible / no-clip used on someone this season (console and admin logs)
            foreach (var suffix in new[] { "_admin.txt", "_DebugLog-server.txt" })
                foreach (var f in LogFiles(s, suffix, from))
                    foreach (var line in Lines(f))
                    {
                        if (!Cheat.IsMatch(line) || line.IndexOf("-false", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        var t = When(line);
                        if (t < from || t > until) continue;
                        foreach (var name in raw.Keys.ToList())
                            if (line.IndexOf("\"" + name + "\"", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf(" " + name + " ", StringComparison.OrdinalIgnoreCase) >= 0
                                || line.EndsWith(" " + name + ".", StringComparison.OrdinalIgnoreCase))
                                raw[name].Cheated = true;
                    }

            foreach (var p in season.Players.Values) R(p.Name).Seen = true;
            var pvp = PvpKills(s, from, until);

            var w = Weights(s);
            var active = Active(s, season);
            var list = new List<Standing>();
            foreach (var kv in raw.Where(x => x.Value.Seen || x.Value.Played > 0))
            {
                SeasonPlayer sp;
                season.Players.TryGetValue(kv.Key, out sp);
                var r = kv.Value;
                var st = new Standing { Name = kv.Key, HoursPlayed = r.Played };
                double kills = sp != null ? sp.Kills : 0;
                if (r.Played > 0) kills = Math.Min(kills, s.SeasonKillCap * Math.Max(1, r.Played));
                st.Raw["Endurance"] = r.MaxHours;
                st.Raw["Skills"] = r.Levels;
                st.Raw["Slayer"] = kills;
                st.Raw["Explorer"] = sp != null ? sp.Cells.Count : 0;
                st.Raw["Resilience"] = r.Played > 0 ? r.Deaths / r.Played * 10 : r.Deaths;   // deaths per 10 hours
                st.Raw["FinalStand"] = alive != null && alive.Contains(kv.Key) ? 1 : 0;
                int pk;
                st.Raw["PvP"] = (pvp.TryGetValue(kv.Key, out pk) ? pk : 0) + (sp != null ? sp.PvpKills : 0);
                string role;
                bool staff = roles != null && roles.TryGetValue(kv.Key, out role) && (role == "admin" || role == "moderator" || role == "gm" || role == "overseer");
                if (sp != null && sp.Disqualified) { st.Qualified = false; st.Note = "Disqualified" + (sp.DqReason != "" ? ": " + sp.DqReason : ""); }
                else if (s.SeasonExcludeStaff && staff) { st.Qualified = false; st.Note = "Staff (" + roles[kv.Key] + ")"; }
                else if (s.SeasonExcludeCheats && r.Cheated) { st.Qualified = false; st.Note = "Used god mode, invisibility or no-clip"; }
                else if (r.Played < s.SeasonMinHours) { st.Qualified = false; st.Note = "Under " + s.SeasonMinHours + " h played"; }
                list.Add(st);
            }

            // percentile within the qualifying players, per active category
            var q = list.Where(x => x.Qualified).ToList();
            double totalW = Enumerable.Range(0, Categories.Length).Where(i => active[i]).Sum(i => (double)w[i]);
            foreach (var st in list)
            {
                double score = 0;
                for (int i = 0; i < Categories.Length; i++)
                {
                    string c = Categories[i];
                    double v = st.Raw[c];
                    bool lowerBetter = c == "Resilience";
                    double pct;
                    if (q.Count <= 1) pct = 100;
                    else
                    {
                        int better = q.Count(o => o != st && (lowerBetter ? o.Raw[c] > v : o.Raw[c] < v));
                        int equal = q.Count(o => o != st && o.Raw[c] == v);
                        pct = (better + 0.5 * equal) / (q.Count - 1) * 100;
                    }
                    st.Pct[c] = pct;
                    if (active[i] && totalW > 0) score += w[i] * pct / totalW;
                }
                SeasonPlayer sp;
                if (season.Players.TryGetValue(st.Name, out sp)) score += sp.Adjust;
                st.Score = Math.Round(score, 1);
            }
            int rank = 0;
            foreach (var st in list.Where(x => x.Qualified).OrderByDescending(x => x.Score).ThenByDescending(x => x.Raw["Endurance"])) st.Rank = ++rank;
            return list.OrderBy(x => x.Qualified ? 0 : 1).ThenBy(x => x.Rank).ThenByDescending(x => x.Score).ToList();
        }

        // Players killed by each player in the window, from EZPZ Server Tools' Lua\ezpz_events.json (one JSON object per line).
        public static Dictionary<string, int> PvpKills(AppSettings s, DateTime from, DateTime until)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(s.DataDir, "Lua", "ezpz_events.json");
            if (!File.Exists(path)) return d;
            var json = new JavaScriptSerializer();
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (var line in Lines(path))
            {
                if (line.Trim() == "") continue;
                Dictionary<string, object> e;
                try { e = json.DeserializeObject(line) as Dictionary<string, object>; } catch { continue; }
                object killer, victim, at, ev;
                if (e == null || !e.TryGetValue("event", out ev) || (ev as string) != "death" || !e.TryGetValue("killer", out killer) || !(killer is string)) continue;
                if (!e.TryGetValue("victim", out victim) || string.Equals(victim as string, (string)killer, StringComparison.OrdinalIgnoreCase)) continue;
                double ms;
                if (!e.TryGetValue("at", out at) || !double.TryParse(Convert.ToString(at, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out ms)) continue;
                var t = epoch.AddMilliseconds(ms).ToLocalTime();
                if (t < from || t > until) continue;
                int n;
                d.TryGetValue((string)killer, out n);
                d[(string)killer] = n + 1;
            }
            return d;
        }

        // EZPZ Server Tools appends a line per death to Lua\ezpz_events.json forever. Once it passes 512 KB, keep only
        // what the ladder can still use: this season and the last 90 days. Only while the server is stopped, so the
        // game isn't writing to it at the same moment.
        public static void TrimEvents(AppSettings s, DateTime keepFrom)
        {
            string path = Path.Combine(s.DataDir, "Lua", "ezpz_events.json");
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length < 512 * 1024) return;
                var cutoff = MemoryHistory.ToUnix(keepFrom.ToUniversalTime()) * 1000.0;
                var keep = File.ReadAllLines(path).Where(l =>
                {
                    var m = Regex.Match(l, "\"at\":\\s*(\\d+)");
                    return m.Success && double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) >= cutoff;
                }).ToArray();
                File.WriteAllLines(path + ".tmp", keep);
                File.Replace(path + ".tmp", path, null);
                AppLog.Write("[seasons] Trimmed ezpz_events.json to " + keep.Length + " events since " + keepFrom.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + ".");
            }
            catch (Exception ex) { AppLog.Error("trimming ezpz_events.json", ex); }
        }

        static void AddPlayed(Raw r, DateTime a, DateTime b, DateTime from, DateTime until)
        {
            if (a < from) a = from;
            if (b > until) b = until;
            if (b > a) r.Played += (b - a).TotalHours;
        }

        // Category leaders among qualifying players (title -> player). Leaders need something to show for it.
        public static Dictionary<string, string> TitleHolders(AppSettings s, SeasonRecord season, List<Standing> standings)
        {
            var d = new Dictionary<string, string>();
            var q = standings.Where(x => x.Qualified).ToList();
            var champ = q.FirstOrDefault(x => x.Rank == 1);
            if (champ != null) d[ChampionTitle] = champ.Name;
            var active = Active(s, season);
            for (int i = 0; i < Categories.Length; i++)
            {
                if (Titles[i] == null || !active[i] || q.Count == 0) continue;
                string c = Categories[i];
                var best = c == "Resilience"
                    ? q.OrderBy(x => x.Raw[c]).ThenByDescending(x => x.HoursPlayed).First()
                    : q.OrderByDescending(x => x.Raw[c]).ThenBy(x => x.Rank).First();
                if (c != "Resilience" && best.Raw[c] <= 0) continue;
                d[Titles[i]] = best.Name;
            }
            return d;
        }

        // ---------- wording

        public static string Show(string cat, double v)
        {
            switch (cat)
            {
                case "Endurance": { int h = (int)v; return h >= 24 ? (h / 24) + "d " + (h % 24) + "h" : h + "h"; }
                case "Skills": return "+" + v.ToString("0");
                case "Slayer": return v.ToString("N0");
                case "Explorer": return v.ToString("0") + " areas";
                case "Resilience": return v.ToString("0.0") + " /10h";
                case "FinalStand": return v > 0 ? "alive" : "—";
                case "PvP": return v.ToString("0");
            }
            return v.ToString("0");
        }

        // The file the EZPZ Server Tools server half reads: "key=value" lines and "|"-separated rows
        // (the game's Lua has no JSON reader, and loading code from files is not allowed).
        public static string ModFile(AppSettings s, SeasonRecord season, List<Standing> standings, DateTime? endAt, DateTime? restartAt, bool showLadder, IEnumerable<string> notices)
        {
            var sb = new StringBuilder();
            Func<DateTime?, string> ms = d => d == null ? "0" : ((long)(d.Value.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            sb.Append("v=1\n");
            sb.Append("written=").Append(ms(DateTime.Now)).Append('\n');
            sb.Append("season=").Append(season != null ? season.Number : 0).Append('\n');
            sb.Append("seasonStart=").Append(season != null ? ms(season.StartTime) : "0").Append('\n');
            sb.Append("endAt=").Append(ms(endAt)).Append('\n');
            sb.Append("endNearAt=").Append(endAt != null ? ms(endAt.Value.AddHours(-s.SeasonEndHours)) : "0").Append('\n');
            sb.Append("restartAt=").Append(ms(restartAt)).Append('\n');
            sb.Append("ladder=").Append(showLadder ? 1 : 0).Append('\n');
            if (standings != null)
                foreach (var st in standings.Where(x => x.Qualified).Take(50))
                    sb.Append("p|").Append(st.Name.Replace("|", "")).Append('|').Append(st.Rank).Append('|').Append(st.Score.ToString("0.0", CultureInfo.InvariantCulture))
                      .Append('|').Append(string.Join("|", Categories.Select(c => Show(c, st.Raw[c]).Replace("|", "").Replace("—", "-")))).Append('\n');   // the game's fonts lack "—"
            foreach (var n in notices ?? new string[0]) sb.Append("notice|").Append(n.Replace("\n", " ")).Append('\n');
            return sb.ToString();
        }
    }
}
