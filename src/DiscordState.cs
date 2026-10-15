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
    public class WhitelistRequest
    {
        public string Id = "";
        public string Username = "";       // the account name they asked for
        public string DiscordUserId = "", DiscordName = "";
        public string GuildId = "", ChannelId = "", MessageId = "";   // the admin post with Approve / Deny
        public string At = "";              // when, "o" format
    }

    public class DayStats
    {
        public string Date = "";            // yyyy-MM-dd
        public int PeakPlayers, UptimeSeconds, Deaths, Crashes;
        public double Kills, LastKillCounter = -1;
        public List<string> Players = new List<string>();
        public bool Posted;
    }

    // What ezpz remembers per server for Discord between runs: open whitelist requests, the live status
    // message, the day's numbers for the summary, and which survival milestones were already announced.
    // Kept in %APPDATA%\ezpz\servers\<id>.discord.json.
    public class DiscordState
    {
        public string StatusMessageId = "", StatusWebhook = "";   // StatusWebhook: a hash, so a new webhook gets a new message
        public List<WhitelistRequest> Requests = new List<WhitelistRequest>();
        public Dictionary<string, int> Milestones = new Dictionary<string, int>();   // lower-case name -> days announced for this character
        public DayStats Day = new DayStats();
        public long TicketSeenId = -1;   // newest ticket already announced (-1: not looked yet)

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static string PathFor(string profileId) { return Path.Combine(AppSettings.ManagerDir, "servers", profileId + ".discord.json"); }

        public static DiscordState Load(string profileId)
        {
            try
            {
                var p = PathFor(profileId);
                if (File.Exists(p)) return Json.Deserialize<DiscordState>(File.ReadAllText(p)) ?? new DiscordState();
            }
            catch (Exception ex) { AppLog.Error("reading the Discord state", ex); }
            return new DiscordState();
        }

        public void Save(string profileId)
        {
            try
            {
                var p = PathFor(profileId);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p + ".tmp", Json.Serialize(this));
                if (File.Exists(p)) File.Replace(p + ".tmp", p, null); else File.Move(p + ".tmp", p);
            }
            catch (Exception ex) { AppLog.Error("saving the Discord state", ex); }
        }
    }

    // Follows the current run's *_PerkLog.txt for deaths and hours survived. The first read of a file only
    // catches up silently, so old events are never announced.
    public class PerkFeed
    {
        public class Event { public string Name; public bool Died; public int Hours; }

        string file;
        long pos;
        string partial = "";
        static readonly Regex Bracket = new Regex(@"\[([^\]]*)\]");
        static readonly Regex Hours = new Regex(@"Hours Survived:\s*(\d+)");

        // New events since the last call. "silent" is true on a file's first read (catch-up only).
        public List<Event> Poll(AppSettings s, out bool silent)
        {
            silent = false;
            var list = new List<Event>();
            string newest = null;
            try
            {
                if (Directory.Exists(s.LogsDir))
                    newest = Directory.GetFiles(s.LogsDir, "*_PerkLog.txt").OrderByDescending(f => File.GetLastWriteTime(f)).FirstOrDefault();
            }
            catch { }
            if (newest == null) return list;
            if (newest != file) { silent = file == null; file = newest; pos = 0; partial = ""; }
            string text;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < pos) { pos = 0; partial = ""; }
                    if (fs.Length == pos) return list;
                    fs.Seek(pos, SeekOrigin.Begin);
                    text = partial + new StreamReader(fs, Encoding.UTF8).ReadToEnd();
                    pos = fs.Length;
                }
            }
            catch { return list; }
            int lastNl = text.LastIndexOf('\n');
            partial = lastNl < text.Length - 1 ? text.Substring(lastNl + 1) : "";
            foreach (var line in text.Substring(0, lastNl + 1).Split('\n'))
            {
                // [time] [steamid][name][x,y,z][Died][Hours Survived: 12].
                var b = Bracket.Matches(line).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
                if (b.Count < 6 || b[2] == "" || b[2] == "null") continue;
                var hm = Hours.Match(b[b.Count - 1]);
                if (!hm.Success) continue;
                list.Add(new Event { Name = b[2], Died = b[4] == "Died", Hours = int.Parse(hm.Groups[1].Value) });
            }
            return list;
        }
    }
}
