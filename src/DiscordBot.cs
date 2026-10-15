using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Ezpz
{
    // ezpz's own connection to Discord, using the same bot token as the game's chat bridge. It only asks for the
    // Guilds intent (no message content), so it never sees chat; it receives slash commands and button presses.
    // The game's bot and this one are two sessions of the same bot and don't get in each other's way.
    public class DiscordBot
    {
        const string Api = "https://discord.com/api/v10";
        const string Gateway = "wss://gateway.discord.gg/?v=10&encoding=json";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public readonly string Token;
        public string AppId = "", BotName = "";
        public volatile bool Connected;
        public string Status = "Connecting…";

        public event Action<Dictionary<string, object>> Interaction;   // raised on a background thread
        public event Action<string> StatusChanged;

        class Guild
        {
            public string Id, Name;
            public readonly Dictionary<string, string> Roles = new Dictionary<string, string>();      // id -> name
            public readonly Dictionary<string, string> Channels = new Dictionary<string, string>();   // id -> name (text channels)
        }
        readonly ConcurrentDictionary<string, Guild> guilds = new ConcurrentDictionary<string, Guild>();

        CancellationTokenSource cts;
        ClientWebSocket ws;
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        long? seq;
        volatile bool acked;

        public DiscordBot(string token) { Token = token; }

        public void Start()
        {
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            Task.Run(() => Loop(ct));
        }

        public void Stop()
        {
            if (cts != null) cts.Cancel();
            var w = ws;
            if (w != null) try { w.Abort(); } catch { }
            Connected = false;
        }

        void SetStatus(string s)
        {
            Status = s;
            var h = StatusChanged;
            if (h != null) h(s);
        }

        class FatalError : Exception { public FatalError(string m) : base(m) { } }

        async Task Loop(CancellationToken ct)
        {
            int backoff = 2;
            while (!ct.IsCancellationRequested)
            {
                string problem = null;
                try
                {
                    await RunOnce(ct);
                    backoff = 2;
                }
                catch (FatalError e) { Connected = false; SetStatus(e.Message); AppLog.Write("[discord] " + e.Message); return; }
                catch (Exception e) { problem = e.Message; }
                Connected = false;
                if (ct.IsCancellationRequested) return;
                SetStatus("Disconnected" + (problem != null ? " (" + problem + ")" : "") + ". Reconnecting…");
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); } catch { return; }
                backoff = Math.Min(60, backoff * 2);
            }
        }

        async Task RunOnce(CancellationToken ct)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;   // .NET 4 doesn't use TLS 1.2 by default; Discord requires it
            using (var sock = new ClientWebSocket())
            {
                ws = sock;
                await sock.ConnectAsync(new Uri(Gateway), ct);
                var hello = await Receive(sock, ct);
                if (Int(hello, "op") != 10) throw new Exception("unexpected first message from Discord");
                int interval = Int(Obj(hello, "d"), "heartbeat_interval");
                acked = true;
                seq = null;
                var hb = Heartbeat(sock, interval, ct);

                await Send(sock, new Dictionary<string, object>
                {
                    { "op", 2 },
                    { "d", new Dictionary<string, object>
                        {
                            { "token", Token },
                            { "intents", 1 },   // Guilds: roles and channels for the servers the bot is in
                            { "properties", new Dictionary<string, object> { { "os", "windows" }, { "browser", "ezpz" }, { "device", "ezpz" } } },
                        } },
                }, ct);

                while (sock.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var m = await Receive(sock, ct);
                    object s;
                    if (m.TryGetValue("s", out s) && s != null) seq = Convert.ToInt64(s);
                    switch (Int(m, "op"))
                    {
                        case 0: Dispatch(Str(m, "t"), Obj(m, "d")); break;
                        case 1: await Send(sock, new Dictionary<string, object> { { "op", 1 }, { "d", seq } }, ct); break;
                        case 7: throw new Exception("Discord asked to reconnect");
                        case 9: throw new Exception("session expired");
                        case 11: acked = true; break;
                    }
                }
            }
        }

        async Task Heartbeat(ClientWebSocket sock, int interval, CancellationToken ct)
        {
            try
            {
                await Task.Delay(new Random().Next(interval), ct);
                while (sock.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    if (!acked) { sock.Abort(); return; }   // zombie connection: reconnect
                    acked = false;
                    await Send(sock, new Dictionary<string, object> { { "op", 1 }, { "d", seq } }, ct);
                    await Task.Delay(interval, ct);
                }
            }
            catch { }
        }

        async Task Send(ClientWebSocket sock, object payload, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(Json.Serialize(payload));
            await sendLock.WaitAsync(ct);
            try { await sock.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct); }
            finally { sendLock.Release(); }
        }

        static async Task<Dictionary<string, object>> Receive(ClientWebSocket sock, CancellationToken ct)
        {
            var buf = new byte[16384];
            using (var ms = new MemoryStream())
            {
                while (true)
                {
                    var r = await sock.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        int code = sock.CloseStatus.HasValue ? (int)sock.CloseStatus.Value : 0;
                        if (code == 4004) throw new FatalError("Discord rejected the bot token. Check it on the Discord page.");
                        if (code == 4013 || code == 4014) throw new FatalError("Discord refused the bot's permissions (intents).");
                        throw new Exception("connection closed (" + code + ")");
                    }
                    ms.Write(buf, 0, r.Count);
                    if (r.EndOfMessage) break;
                }
                return (Json.DeserializeObject(Encoding.UTF8.GetString(ms.ToArray())) as Dictionary<string, object>) ?? new Dictionary<string, object>();
            }
        }

        void Dispatch(string t, Dictionary<string, object> d)
        {
            switch (t)
            {
                case "READY":
                    AppId = Str(Obj(d, "application"), "id");
                    BotName = Str(Obj(d, "user"), "username");
                    Connected = true;
                    SetStatus("Connected as " + BotName + ".");
                    Task.Run(() => RegisterCommands());
                    break;
                case "GUILD_CREATE":
                case "GUILD_UPDATE":
                {
                    var g = guilds.GetOrAdd(Str(d, "id"), id => new Guild { Id = id });
                    g.Name = Str(d, "name");
                    var roles = Arr(d, "roles");
                    if (roles != null) lock (g) { g.Roles.Clear(); foreach (var r in roles) g.Roles[Str(r, "id")] = Str(r, "name"); }
                    var chans = Arr(d, "channels");
                    if (chans != null) lock (g) { g.Channels.Clear(); foreach (var c in chans) if (Str(c, "type") == "0") g.Channels[Str(c, "id")] = Str(c, "name"); }
                    break;
                }
                case "GUILD_DELETE": { Guild g; guilds.TryRemove(Str(d, "id"), out g); break; }
                case "GUILD_ROLE_CREATE":
                case "GUILD_ROLE_UPDATE":
                {
                    Guild g;
                    if (guilds.TryGetValue(Str(d, "guild_id"), out g)) lock (g) g.Roles[Str(Obj(d, "role"), "id")] = Str(Obj(d, "role"), "name");
                    break;
                }
                case "GUILD_ROLE_DELETE":
                {
                    Guild g;
                    if (guilds.TryGetValue(Str(d, "guild_id"), out g)) lock (g) g.Roles.Remove(Str(d, "role_id"));
                    break;
                }
                case "CHANNEL_CREATE":
                case "CHANNEL_UPDATE":
                case "CHANNEL_DELETE":
                {
                    Guild g;
                    if (!guilds.TryGetValue(Str(d, "guild_id"), out g)) break;
                    lock (g)
                    {
                        g.Channels.Remove(Str(d, "id"));
                        if (t != "CHANNEL_DELETE" && Str(d, "type") == "0") g.Channels[Str(d, "id")] = Str(d, "name");
                    }
                    break;
                }
                case "INTERACTION_CREATE":
                {
                    var h = Interaction;
                    if (h != null) Task.Run(() => { try { h(d); } catch (Exception ex) { AppLog.Error("a Discord command", ex); } });
                    break;
                }
            }
        }

        // ---------- slash commands

        public static readonly object[] Commands =
        {
            Cmd("status", "Is the server up, and who's playing?"),
            Cmd("start", "Start the server (admins)"),
            Cmd("restart", "Restart the server, warning players in game first (admins)",
                Opt(4, "minutes", "Minutes of warning before the restart (default 5, 0 = now)", false)),
            Cmd("backup", "Back up the world now (admins)"),
            Cmd("whitelist", "Ask for an account on the server",
                Opt(3, "username", "The username you want to log in with", true)),
        };

        static object Cmd(string name, string desc, params object[] options)
        {
            return new Dictionary<string, object> { { "name", name }, { "description", desc }, { "type", 1 }, { "options", options }, { "dm_permission", false } };
        }

        static object Opt(int type, string name, string desc, bool required)
        {
            var o = new Dictionary<string, object> { { "type", type }, { "name", name }, { "description", desc }, { "required", required } };
            if (type == 4) { o["min_value"] = 0; o["max_value"] = 60; }
            if (type == 3) { o["min_length"] = 2; o["max_length"] = 32; }
            return o;
        }

        // One at a time (upsert by name), so commands that aren't ezpz's are left alone.
        async Task RegisterCommands()
        {
            foreach (var c in Commands)
            {
                try { await Rest("POST", "/applications/" + AppId + "/commands", c); }
                catch (Exception ex) { AppLog.Write("[discord] Couldn't register a slash command: " + ex.Message); }
            }
        }

        // ---------- REST

        public async Task<object> Rest(string method, string path, object body)
        {
            for (int attempt = 0; ; attempt++)
            {
                double wait = -1;
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.Authorization] = "Bot " + Token;
                        wc.Headers[HttpRequestHeader.UserAgent] = "DiscordBot (https://github.com/kruchone/ezpz, " + Program.Version + ")";
                        wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                        wc.Encoding = Encoding.UTF8;
                        string resp = method == "GET" ? await wc.DownloadStringTaskAsync(Api + path)
                            : await wc.UploadStringTaskAsync(Api + path, method, body == null ? "" : Json.Serialize(body));
                        return string.IsNullOrWhiteSpace(resp) ? null : Json.DeserializeObject(resp);
                    }
                }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    if (r == null || (int)r.StatusCode != 429 || attempt >= 2) throw new Exception(Describe(ex));
                    double.TryParse(r.Headers["Retry-After"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out wait);
                }
                // rate-limited: wait as long as Discord asks, then try again
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Math.Min(30, wait))));
            }
        }

        static string Describe(WebException ex)
        {
            var r = ex.Response as HttpWebResponse;
            if (r == null) return ex.Message;
            string detail = "";
            try { using (var sr = new StreamReader(r.GetResponseStream())) detail = Str(Json.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>, "message"); } catch { }
            return (int)r.StatusCode + " " + r.StatusDescription + (detail != "" ? ": " + detail : "");
        }

        // Interaction replies. Types: 4 = message, 5 = "thinking…", 6 = acknowledge a button, 7 = update the button's message.
        public Task Reply(Dictionary<string, object> it, int type, Dictionary<string, object> data)
        {
            var body = new Dictionary<string, object> { { "type", type } };
            if (data != null) body["data"] = data;
            return Rest("POST", "/interactions/" + Str(it, "id") + "/" + Str(it, "token") + "/callback", body);
        }

        // Changes the reply (or, for a button press, the message the button is on).
        public Task EditReply(Dictionary<string, object> it, Dictionary<string, object> data)
        {
            return Rest("PATCH", "/webhooks/" + AppId + "/" + Str(it, "token") + "/messages/@original", data);
        }

        public static Dictionary<string, object> Message(string content, Dictionary<string, object> embed, bool privateReply, object[] components)
        {
            var d = new Dictionary<string, object> { { "allowed_mentions", new Dictionary<string, object> { { "parse", new object[0] } } } };
            if (content != null) d["content"] = content;
            if (embed != null) d["embeds"] = new object[] { embed };
            if (privateReply) d["flags"] = 64;
            if (components != null) d["components"] = components;
            return d;
        }

        public static object[] Buttons(params object[][] buttons)
        {
            return new object[] { new Dictionary<string, object>
            {
                { "type", 1 },
                { "components", buttons.Select(b => (object)new Dictionary<string, object> { { "type", 2 }, { "style", b[0] }, { "label", b[1] }, { "custom_id", b[2] } }).ToArray() },
            } };
        }

        // Private message to a Discord user. Returns null, or why it couldn't be sent.
        public async Task<string> DirectMessage(string userId, Dictionary<string, object> embed)
        {
            try
            {
                var ch = await Rest("POST", "/users/@me/channels", new Dictionary<string, object> { { "recipient_id", userId } }) as Dictionary<string, object>;
                await Rest("POST", "/channels/" + Str(ch, "id") + "/messages", Message(null, embed, false, null));
                return null;
            }
            catch (Exception ex) { return ex.Message.StartsWith("403") ? "they don't accept private messages from this server" : ex.Message; }
        }

        public async Task<string> PostToChannel(string channelId, Dictionary<string, object> embed, object[] components)
        {
            var m = await Rest("POST", "/channels/" + channelId + "/messages", Message(null, embed, false, components)) as Dictionary<string, object>;
            return Str(m, "id");
        }

        public Task EditChannelMessage(string channelId, string messageId, Dictionary<string, object> embed, object[] components)
        {
            return Rest("PATCH", "/channels/" + channelId + "/messages/" + messageId, Message(null, embed, false, components ?? new object[0]));
        }

        // A text channel by name, preferring the given server (guild).
        public string FindChannel(string guildId, string name)
        {
            name = (name ?? "").Trim().TrimStart('#');
            if (name == "") return null;
            foreach (var g in guilds.Values.OrderBy(x => x.Id == guildId ? 0 : 1))
                lock (g)
                    foreach (var kv in g.Channels)
                        if (kv.Value.Equals(name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            return null;
        }

        public List<string> RoleNames(string guildId, IEnumerable<string> roleIds)
        {
            var list = new List<string>();
            Guild g;
            if (guildId == null || !guilds.TryGetValue(guildId, out g)) return list;
            lock (g) foreach (var id in roleIds) { string n; if (g.Roles.TryGetValue(id, out n)) list.Add(n); }
            return list;
        }

        public string GuildNames() { return string.Join(", ", guilds.Values.Select(g => g.Name)); }

        // ---------- JSON helpers (JavaScriptSerializer gives dictionaries and object arrays)

        public static Dictionary<string, object> Obj(object o, string k)
        {
            var d = o as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
        }

        public static object[] Arr(object o, string k)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return null;
            var a = v as object[];
            if (a != null) return a;
            var l = v as System.Collections.ArrayList;
            return l != null ? l.ToArray() : null;
        }

        public static string Str(object o, string k)
        {
            var d = o as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : "";
        }

        static int Int(object o, string k) { int i; return int.TryParse(Str(o, k), out i) ? i : -1; }
    }
}
