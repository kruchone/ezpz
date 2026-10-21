using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace Ezpz
{
    public class LivePlayer
    {
        public string Username = "";
        public double Health = -1;
        public bool Infected, Asleep, Driving;
        public int Bitten, Scratched, Bleeding, Deep, Fractures, Burns, Kills;
        public double Hours, X, Y, Z;
        public string Vehicle = "";
        public Dictionary<string, int> Moodles = new Dictionary<string, int>();
        public Dictionary<string, double> Stats = new Dictionary<string, double>();
    }

    public class LiveSnapshot
    {
        public DateTime Written = DateTime.MinValue;
        public string ModVersion = "";
        public List<LivePlayer> Players = new List<LivePlayer>();
        public bool Fresh { get { return (DateTime.Now - Written).TotalSeconds < 30; } }
        public LivePlayer Find(string name) { return Players.FirstOrDefault(p => p.Username.Equals(name ?? "", StringComparison.OrdinalIgnoreCase)); }
    }

    // Reads what the EZPZ Server Tools mod writes: <data folder>\Lua\ezpz_players.json, the online players'
    // health, injuries, moodles, stats and position, reported by their own games.
    public static class LiveFeed
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static string PathFor(AppSettings s) { return Path.Combine(s.DataDir, "Lua", "ezpz_players.json"); }

        // null if the mod has never written anything for this server
        public static LiveSnapshot Read(AppSettings s)
        {
            string path = PathFor(s);
            if (!File.Exists(path)) return null;
            string text;
            try { text = GameData.ReadShared(path); } catch { return null; }
            var root = Json.DeserializeObject(text) as Dictionary<string, object>;
            if (root == null) return null;
            var snap = new LiveSnapshot { ModVersion = Str(root, "mod") };
            double ms = Num(root, "written");
            snap.Written = ms > 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime() : File.GetLastWriteTime(path);
            object pl;
            var arr = root.TryGetValue("players", out pl) ? pl as object[] : null;   // an empty list comes out as {}
            foreach (var o in arr ?? new object[0])
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                var p = new LivePlayer
                {
                    Username = Str(d, "username"), Health = Num(d, "health", -1), Infected = Bool(d, "infected"), Asleep = Bool(d, "asleep"),
                    Driving = Bool(d, "driving"), Bitten = (int)Num(d, "bitten"), Scratched = (int)Num(d, "scratched"), Bleeding = (int)Num(d, "bleeding"),
                    Deep = (int)Num(d, "deep"), Fractures = (int)Num(d, "fractures"), Burns = (int)Num(d, "burns"), Kills = (int)Num(d, "kills"),
                    Hours = Num(d, "hours"), X = Num(d, "x"), Y = Num(d, "y"), Z = Num(d, "z"), Vehicle = Str(d, "vehicle"),
                };
                object m;
                var md = d.TryGetValue("moodles", out m) ? m as Dictionary<string, object> : null;
                if (md != null) foreach (var kv in md) p.Moodles[kv.Key] = (int)ToNum(kv.Value);
                var sd = d.TryGetValue("stats", out m) ? m as Dictionary<string, object> : null;
                if (sd != null) foreach (var kv in sd) p.Stats[kv.Key] = ToNum(kv.Value);
                if (p.Username != "") snap.Players.Add(p);
            }
            return snap;
        }

        // ---------- wording

        static readonly Dictionary<string, string> MoodleNames = new Dictionary<string, string>
        {
            { "HUNGRY", "Hungry" }, { "THIRST", "Thirsty" }, { "TIRED", "Tired" }, { "ENDURANCE", "Exhausted" }, { "PANIC", "Panicked" },
            { "STRESS", "Stressed" }, { "UNHAPPY", "Unhappy" }, { "BORED", "Bored" }, { "PAIN", "In pain" }, { "SICK", "Sick" },
            { "DRUNK", "Drunk" }, { "INJURED", "Injured" }, { "BLEEDING", "Bleeding" }, { "HAS_A_COLD", "Has a cold" },
            { "HYPOTHERMIA", "Too cold" }, { "HYPERTHERMIA", "Too hot" }, { "WET", "Wet" }, { "WINDCHILL", "Windchill" },
            { "HEAVY_LOAD", "Heavy load" }, { "ZOMBIE", "Infection" }, { "ANGRY", "Angry" }, { "UNCOMFORTABLE", "Uncomfortable" },
            { "NOXIOUS_SMELL", "Bad smell" }, { "CANT_SPRINT", "Can't sprint" },
        };

        // "Hungry 2, Tired 1" (worst first)
        public static string MoodleText(LivePlayer p)
        {
            if (p.Moodles.Count == 0) return "no moodles";
            return string.Join(", ", p.Moodles.OrderByDescending(kv => kv.Value).Select(kv =>
            {
                string n;
                return (MoodleNames.TryGetValue(kv.Key, out n) ? n : kv.Key) + " " + kv.Value;
            }));
        }

        public static string InjuryText(LivePlayer p)
        {
            var parts = new List<string>();
            if (p.Infected) parts.Add("INFECTED");
            if (p.Bitten > 0) parts.Add(p.Bitten + " bite" + (p.Bitten == 1 ? "" : "s"));
            if (p.Deep > 0) parts.Add(p.Deep + " deep wound" + (p.Deep == 1 ? "" : "s"));
            if (p.Scratched > 0) parts.Add(p.Scratched + " scratch" + (p.Scratched == 1 ? "" : "es"));
            if (p.Bleeding > 0) parts.Add("bleeding (" + p.Bleeding + ")");
            if (p.Fractures > 0) parts.Add(p.Fractures + " fracture" + (p.Fractures == 1 ? "" : "s"));
            if (p.Burns > 0) parts.Add(p.Burns + " burn" + (p.Burns == 1 ? "" : "s"));
            return parts.Count == 0 ? "no injuries" : string.Join(", ", parts);
        }

        // "hunger 5%, thirst 8%, tired 0%"
        public static string NeedsText(LivePlayer p)
        {
            Func<string, string, string> pct = (k, label) => { double v; return p.Stats.TryGetValue(k, out v) ? label + " " + Math.Round(v * 100) + "%" : null; };
            return string.Join(", ", new[] { pct("HUNGER", "hunger"), pct("THIRST", "thirst"), pct("FATIGUE", "tired") }.Where(x => x != null));
        }

        public static string HealthText(LivePlayer p) { return p.Health < 0 ? "?" : Math.Round(p.Health) + "%"; }

        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : ""; }
        static bool Bool(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is bool && (bool)v; }
        static double Num(Dictionary<string, object> d, string k, double def = 0) { object v; return d.TryGetValue(k, out v) && v != null ? ToNum(v) : def; }
        static double ToNum(object v)
        {
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }
    }
}
