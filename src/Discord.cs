using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Ezpz
{
    // Things ezpz can post to Discord. Stored per server as a bit mask (AppSettings.DiscordEvents).
    [Flags]
    public enum DiscordEvent
    {
        Online = 1,
        Stopped = 2,
        Crash = 4,
        RestartWarning = 8,
        Updates = 16,
        Players = 32,
        Health = 64,
        Status = 128,       // the live status message that edits itself
        Summary = 256,      // daily summary
        Deaths = 512,
        Milestones = 1024,  // "Rick has survived 30 days"
        Tickets = 2048,     // a player filed a ticket in game
        Wipes = 4096,       // scheduled wipe warnings and "fresh world is up"
        Seasons = 8192,     // weekly standings, THE END IS NEAR, season results
    }

    // Posts to a Discord webhook. One message at a time, honouring Discord's rate limits.
    public static class DiscordWebhook
    {
        static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static readonly Regex UrlPattern = new Regex(@"^https://(?:(?:ptb|canary)\.)?discord(?:app)?\.com/api/webhooks/\d+/[\w-]+$");

        public static Dictionary<string, object> Embed(string title, string text, int color, string footer)
        {
            return new Dictionary<string, object>
            {
                { "title", Clip(title, 250) },
                { "description", Clip(text ?? "", 3900) },
                { "color", color },
                { "footer", new Dictionary<string, object> { { "text", Clip(footer, 200) } } },
                { "timestamp", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") },
            };
        }

        public static Task<string> Send(string url, string title, string text, int color, string footer)
        {
            return SendEmbed(url, Embed(title, text, color, footer));
        }

        public static async Task<string> SendEmbed(string url, Dictionary<string, object> embed)
        {
            var r = await Request(url, "POST", Body(embed));
            return r.Error;
        }

        // Posts and returns the new message's id, so it can be edited later (the live status message).
        public static async Task<Result> Post(string url, Dictionary<string, object> embed)
        {
            var r = await Request(url + "?wait=true", "POST", Body(embed));
            if (r.Error == null)
            {
                var d = Json.DeserializeObject(r.Body) as Dictionary<string, object>;
                object id;
                if (d != null && d.TryGetValue("id", out id)) r.Id = Convert.ToString(id);
            }
            return r;
        }

        // Edits a message this webhook posted. Result.Gone means it was deleted (post a new one).
        public static Task<Result> Edit(string url, string messageId, Dictionary<string, object> embed)
        {
            return Request(url + "/messages/" + messageId, "PATCH", Body(embed));
        }

        public class Result { public string Error, Body, Id; public bool Gone; }

        static string Body(Dictionary<string, object> embed)
        {
            return Json.Serialize(new Dictionary<string, object>
            {
                { "username", "ezpz" },
                { "embeds", new object[] { embed } },
                { "allowed_mentions", new Dictionary<string, object> { { "parse", new object[0] } } },
            });
        }

        static async Task<Result> Request(string url, string method, string body)
        {
            if (string.IsNullOrEmpty(url)) return new Result { Error = "No webhook URL set." };
            await gate.WaitAsync();
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    double retryAfter = -1;
                    try
                    {
                        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                        using (var wc = new WebClient())
                        {
                            wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                            wc.Encoding = Encoding.UTF8;
                            return new Result { Body = await wc.UploadStringTaskAsync(url, method, body) };
                        }
                    }
                    catch (WebException ex)
                    {
                        var r = ex.Response as HttpWebResponse;
                        if (r != null && (int)r.StatusCode == 429)
                        {
                            double.TryParse(r.Headers["Retry-After"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out retryAfter);
                        }
                        else if (r != null && method == "PATCH" && r.StatusCode == HttpStatusCode.NotFound)
                            return new Result { Error = "The message was deleted.", Gone = true };
                        else if (r != null && (r.StatusCode == HttpStatusCode.NotFound || r.StatusCode == HttpStatusCode.Unauthorized))
                            return new Result { Error = "Discord says this webhook doesn't exist any more. It may have been deleted. Create a new one and paste its URL." };
                        else return new Result { Error = "Couldn't post to Discord: " + ex.Message };
                    }
                    // rate-limited: wait as long as Discord asks, then try again
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Math.Min(30, retryAfter))));
                }
                return new Result { Error = "Discord is rate-limiting messages. Try again in a minute." };
            }
            finally { gate.Release(); }
        }

        public static string Clip(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }
    }

    // Checks a bot token and channel names the way the game's built-in bot will use them.
    public static class DiscordBotCheck
    {
        const string Api = "https://discord.com/api/v10";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        static object Get(string token, string path)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.Authorization] = "Bot " + token;
                wc.Headers[HttpRequestHeader.UserAgent] = "DiscordBot (https://github.com/kruchone/ezpz, 1.1)";
                wc.Encoding = Encoding.UTF8;
                return Json.DeserializeObject(wc.DownloadString(Api + path));
            }
        }

        static string Str(object o, string k)
        {
            var d = o as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : "";
        }

        // Returns human-readable findings; the first line says whether the token works.
        // View Channels, Send Messages, Embed Links, Read Message History: what the chat bridge and ezpz's bot use.
        public const long InvitePermissions = 1024 | 2048 | 16384 | 65536;

        // The link that adds the bot to a Discord server with the scopes and permissions both bots need.
        // The application id comes from the token, so nobody has to dig through the developer portal.
        public static string InviteUrl(string token)
        {
            token = (token ?? "").Trim();
            if (token == "") throw new Exception("Paste the bot token first (step 2).");
            object app;
            try { app = Get(token, "/oauth2/applications/@me"); }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                throw new Exception(r != null && r.StatusCode == HttpStatusCode.Unauthorized
                    ? "Discord rejected the token. Copy it again from the Bot tab (Reset Token gives a new one)."
                    : "Couldn't reach Discord: " + ex.Message);
            }
            return "https://discord.com/oauth2/authorize?client_id=" + Str(app, "id") + "&scope=bot%20applications.commands&permissions=" + InvitePermissions;
        }

        public static List<string> Run(string token, string[] channelNames)
        {
            var lines = new List<string>();
            token = (token ?? "").Trim();
            if (token == "") { lines.Add("✕ No bot token entered."); return lines; }
            object me;
            try { me = Get(token, "/users/@me"); }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                lines.Add(r != null && r.StatusCode == HttpStatusCode.Unauthorized
                    ? "✕ Discord rejected the token. Copy it again from the Bot tab (Reset Token gives a new one)."
                    : "✕ Couldn't reach Discord: " + ex.Message);
                return lines;
            }
            lines.Add("✓ Token works. The bot is \"" + Str(me, "username") + "\".");

            var guilds = (Get(token, "/users/@me/guilds") as object[]) ?? new object[0];
            if (guilds.Length == 0)
            {
                lines.Add("✕ The bot hasn't been added to any Discord server yet. Use the invite link from step 4.");
                return lines;
            }
            lines.Add("✓ The bot is in: " + string.Join(", ", guilds.Select(g => Str(g, "name"))) + ".");

            var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var g in guilds)
            {
                object[] chans;
                try { chans = (Get(token, "/guilds/" + Str(g, "id") + "/channels") as object[]) ?? new object[0]; }
                catch { continue; }
                foreach (var c in chans)
                {
                    if (Str(c, "type") != "0") continue;   // text channels only
                    string n = Str(c, "name");
                    if (!found.ContainsKey(n)) found[n] = new List<string>();
                    found[n].Add(Str(g, "name"));
                }
            }
            foreach (var name in channelNames.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                string n = name.Trim().TrimStart('#');
                List<string> where;
                if (found.TryGetValue(n, out where))
                    lines.Add("✓ Channel \"" + n + "\" found in " + string.Join(", ", where.Distinct()) + (where.Count > 1 ? ". (More than one channel has this name, so the game may pick either.)" : "."));
                else
                {
                    var close = found.Keys.FirstOrDefault(k => k.Equals(n, StringComparison.OrdinalIgnoreCase) || k.Replace("-", "").Equals(n.Replace("-", "").Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
                    lines.Add("✕ No text channel named \"" + n + "\" that the bot can see." + (close != null ? " Did you mean \"" + close + "\"? Names must match exactly." : " Check the spelling, and that the bot's role can view the channel."));
                }
            }
            // Application flags: 1<<18 = message content (verified bots), 1<<19 = message content (toggle on, unverified bots).
            try
            {
                long flags;
                long.TryParse(Str(Get(token, "/oauth2/applications/@me"), "flags"), out flags);
                lines.Add((flags & ((1L << 18) | (1L << 19))) != 0
                    ? "✓ Message Content Intent is on, so the game can read chat from Discord."
                    : "✕ Message Content Intent is off. Without it, chat from Discord won't reach the game (step 3).");
            }
            catch { lines.Add("• Couldn't check Message Content Intent. Make sure it's on (step 3)."); }
            return lines;
        }
    }
}
