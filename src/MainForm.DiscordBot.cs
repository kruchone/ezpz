using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // Discord, part 2: ezpz's own bot (slash commands and whitelist requests), the live status message, the daily
    // summary, and death and survival-milestone announcements.
    public partial class MainForm
    {
        DiscordBot bot;
        DiscordState dstate = new DiscordState();
        PerkFeed perkFeed = new PerkFeed();
        DateTime? botRestartAt;
        readonly HashSet<int> botRestartWarned = new HashSet<int>();
        bool statusPosting;
        string statusLastKey = "";
        DateTime statusLastAt = DateTime.MinValue;
        Button btnRequests;

        static readonly int[] MilestoneDays = { 7, 14, 30, 60, 100, 180, 365 };
        static readonly Regex UsernameOk = new Regex(@"^[A-Za-z0-9_.\-]{2,32}$");

        void LoadDiscordState()
        {
            dstate = DiscordState.Load(S.ProfileId);
            perkFeed = new PerkFeed();
            statusLastKey = "";
            botRestartAt = null;
            UpdateRequestsButton();
        }

        void SaveDiscordState() { dstate.Save(S.ProfileId); }

        // Runs fn on the UI thread from a background thread and returns its result.
        T OnUi<T>(Func<T> fn)
        {
            if (!InvokeRequired) return fn();
            T r = default(T);
            Invoke((Action)(() => r = fn()));
            return r;
        }

        // ================================================================== the bot connection

        string BotToken()
        {
            try { return IniFile.Load(S.IniPath).Get("DiscordToken", "").Trim(); } catch { return ""; }
        }

        // Starts, restarts or stops the bot to match the settings (checked every few seconds and after saving).
        void EnsureBot()
        {
            string token = S.DiscordBotEnable ? BotToken() : "";
            if (bot != null && bot.Token == token) return;
            if (bot != null) { bot.Stop(); bot = null; Log("[ezpz] ezpz's Discord bot disconnected."); }
            if (token != "")
            {
                bot = new DiscordBot(token);
                bot.Interaction += OnBotInteraction;
                bot.StatusChanged += st => Ui(UpdateBotLabel);
                bot.Start();
                Log("[ezpz] Connecting ezpz's Discord bot…");
            }
            UpdateBotLabel();
        }

        void UpdateBotLabel()
        {
            if (lblBotState == null) return;
            if (!S.DiscordBotEnable) { lblBotState.Text = "Off."; lblBotState.ForeColor = Theme.Muted; return; }
            if (bot == null) { lblBotState.Text = "Needs the bot token from section 1."; lblBotState.ForeColor = Theme.Warn; return; }
            lblBotState.Text = bot.Status + (bot.Connected && bot.GuildNames() != "" ? " In: " + bot.GuildNames() + "." : "");
            lblBotState.ForeColor = bot.Connected ? Theme.Accent : Theme.Warn;
        }

        bool IsBotAdmin(Dictionary<string, object> it)
        {
            var member = DiscordBot.Obj(it, "member");
            long perms;
            if (long.TryParse(DiscordBot.Str(member, "permissions"), out perms) && (perms & 8) != 0) return true;   // Administrator
            string role = S.DiscordAdminRole.Trim();
            if (role == "" || bot == null) return false;
            var ids = (DiscordBot.Arr(member, "roles") ?? new object[0]).Select(x => Convert.ToString(x));
            return bot.RoleNames(DiscordBot.Str(it, "guild_id"), ids).Any(n => n.Equals(role, StringComparison.OrdinalIgnoreCase));
        }

        static string WhoIs(Dictionary<string, object> it)
        {
            var user = DiscordBot.Obj(DiscordBot.Obj(it, "member"), "user") ?? DiscordBot.Obj(it, "user");
            string g = DiscordBot.Str(user, "global_name");
            return g != "" ? g : DiscordBot.Str(user, "username");
        }

        string NotAdminText()
        {
            return "Only admins can do that" + (S.DiscordAdminRole.Trim() != "" ? " (the **" + S.DiscordAdminRole.Trim() + "** role)" : "") + ".";
        }

        // Called on a background thread for every slash command and button press.
        void OnBotInteraction(Dictionary<string, object> it)
        {
            var b = bot;
            if (b == null) return;
            int type;
            int.TryParse(DiscordBot.Str(it, "type"), out type);
            var data = DiscordBot.Obj(it, "data");
            string who = WhoIs(it);

            if (type == 2)
            {
                string name = DiscordBot.Str(data, "name");
                var opts = (DiscordBot.Arr(data, "options") ?? new object[0]).ToDictionary(o => DiscordBot.Str(o, "name"), o => DiscordBot.Str(o, "value"));
                OnUi(() => { Log("[ezpz] Discord: " + who + " used /" + name + (opts.Count > 0 ? " " + string.Join(" ", opts.Values) : "") + "."); return 0; });
                switch (name)
                {
                    case "status":
                        b.Reply(it, 4, DiscordBot.Message(null, OnUi(() => StatusEmbed()), false, null)).Wait();
                        return;
                    case "ladder":
                        b.Reply(it, 4, DiscordBot.Message(null, OnUi(() => LadderEmbed(who)), false, null)).Wait();
                        return;
                    case "whitelist":
                        string want;
                        opts.TryGetValue("username", out want);
                        WhitelistAsk(b, it, (want ?? "").Trim());
                        return;
                    case "start":
                    case "restart":
                    case "backup":
                        if (!IsBotAdmin(it)) { b.Reply(it, 4, DiscordBot.Message(NotAdminText(), null, true, null)).Wait(); return; }
                        if (name == "backup")
                        {
                            b.Reply(it, 5, null).Wait();   // "ezpz is thinking…" while the backup runs
                            OnUi(() =>
                            {
                                Ui(async () =>
                                {
                                    bool ok = await BackupFlow("manual");
                                    try { await b.EditReply(it, DiscordBot.Message(ok ? "💾 Backup done (asked by " + who + ")." : "✕ The backup didn't work. Check ezpz on the server PC.", null, false, null)); } catch { }
                                });
                                return 0;
                            });
                            return;
                        }
                        string text = OnUi(() => name == "start" ? BotStart(who) : BotRestart(who, opts.ContainsKey("minutes") ? int.Parse(opts["minutes"]) : 5));
                        b.Reply(it, 4, DiscordBot.Message(text, null, false, null)).Wait();
                        return;
                }
                return;
            }

            if (type == 3)
            {
                string id = DiscordBot.Str(data, "custom_id");
                var m = Regex.Match(id, "^wl:(a|d):(\\w+)$");
                if (!m.Success) return;
                if (!IsBotAdmin(it)) { b.Reply(it, 4, DiscordBot.Message(NotAdminText(), null, true, null)).Wait(); return; }
                b.Reply(it, 6, null).Wait();   // acknowledge the click; the message is edited when it's done
                var req = OnUi(() => dstate.Requests.FirstOrDefault(r => r.Id == m.Groups[2].Value));
                if (req == null)
                {
                    b.EditReply(it, DiscordBot.Message(null, DiscordWebhook.Embed("Whitelist request", "This request was already handled.", DcBlue, "ezpz · " + S.ServerName), false, new object[0])).Wait();
                    return;
                }
                var outcome = OnUi(() => DecideRequest(req, m.Groups[1].Value == "a", who)).Result;
                if (outcome.Private != null)
                    b.Rest("POST", "/webhooks/" + b.AppId + "/" + DiscordBot.Str(it, "token"), DiscordBot.Message(outcome.Private, null, true, null)).Wait();
            }
        }

        string BotStart(string who)
        {
            if (server.State != ServerState.Stopped) return "The server is already " + server.State.ToString().ToLower() + ".";
            Log("[ezpz] Starting the server (asked on Discord by " + who + ").");
            Ui(async () => await StartFlow());
            return "▶️ Starting **" + ServerTitle() + "** (asked by " + who + "). It takes a minute or two.";
        }

        string BotRestart(string who, int minutes)
        {
            if (server.State != ServerState.Running) return "The server isn't running. Use /start.";
            if (minutes <= 0)
            {
                Log("[ezpz] Restarting now (asked on Discord by " + who + ").");
                Ui(async () => await RestartFlow(false));
                return "🔁 Restarting **" + ServerTitle() + "** now (asked by " + who + ").";
            }
            botRestartAt = DateTime.Now.AddMinutes(minutes);
            botRestartWarned.Clear();
            botRestartWarned.Add(minutes);
            Cmd("servermsg \"Server restart in " + minutes + " minute" + (minutes == 1 ? "" : "s") + ". Find somewhere safe!\"");
            Log("[ezpz] Restarting in " + minutes + " minutes (asked on Discord by " + who + "). Players were warned.");
            return "🔁 **" + ServerTitle() + "** restarts in " + minutes + " minute" + (minutes == 1 ? "" : "s") + " (asked by " + who + "). Players were warned in game.";
        }

        void BotRestartTick()
        {
            if (botRestartAt == null) return;
            if (server.State != ServerState.Running) { botRestartAt = null; return; }
            var left = botRestartAt.Value - DateTime.Now;
            foreach (int m in new[] { 5, 1 })
                if (left.TotalMinutes <= m && left.TotalMinutes > m - 1 && botRestartWarned.Add(m))
                    Cmd("servermsg \"Server restart in " + m + " minute" + (m == 1 ? "" : "s") + ". Find somewhere safe!\"");
            if (left.TotalSeconds <= 0 && busy == null)
            {
                botRestartAt = null;
                Log("[ezpz] Restarting (asked on Discord).");
                var _ = RestartFlow(false);
            }
        }

        // ================================================================== whitelist requests

        void WhitelistAsk(DiscordBot b, Dictionary<string, object> it, string want)
        {
            Func<string, Task> say = t => b.Reply(it, 4, DiscordBot.Message(t, null, true, null));
            if (!UsernameOk.IsMatch(want)) { say("Usernames can be 2 to 32 letters, numbers, dots, dashes and underscores, with no spaces.").Wait(); return; }
            var user = DiscordBot.Obj(DiscordBot.Obj(it, "member"), "user") ?? DiscordBot.Obj(it, "user");
            string uid = DiscordBot.Str(user, "id"), who = WhoIs(it), guild = DiscordBot.Str(it, "guild_id");

            string problem = OnUi(() =>
            {
                var ini = IniFile.Load(S.IniPath);
                if (!ini.Get("Open", "true").Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
                    return "**" + ServerTitle() + "** is open to everyone, so there's no whitelist. Just join with the username you want.";
                if (roster.ContainsKey(want)) return "The username **" + want + "** is already taken on the server.";
                var mine = dstate.Requests.FirstOrDefault(r => r.DiscordUserId == uid);
                if (mine != null) return "You already asked for **" + mine.Username + "**. An admin will get to it.";
                if (dstate.Requests.Any(r => r.Username.Equals(want, StringComparison.OrdinalIgnoreCase))) return "Someone already asked for **" + want + "**. Pick another username.";
                return null;
            });
            if (problem != null) { say(problem).Wait(); return; }

            var req = new WhitelistRequest
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 10), Username = want, DiscordUserId = uid, DiscordName = who,
                GuildId = guild, At = DateTime.Now.ToString("o"),
            };
            string channel = b.FindChannel(guild, S.DiscordRequestChannel);
            if (channel != null)
            {
                try
                {
                    req.ChannelId = channel;
                    req.MessageId = b.PostToChannel(channel, RequestEmbed(req, null),
                        DiscordBot.Buttons(new object[] { 3, "Approve", "wl:a:" + req.Id }, new object[] { 4, "Deny", "wl:d:" + req.Id })).Result;
                }
                catch (Exception ex) { AppLog.Write("[discord] Couldn't post the whitelist request to #" + S.DiscordRequestChannel + ": " + ex.Message); req.ChannelId = ""; }
            }
            OnUi(() =>
            {
                dstate.Requests.Add(req);
                SaveDiscordState();
                Log("[ezpz] Whitelist request on Discord: " + who + " asks for the username \"" + want + "\". Approve or deny it on the Players page" + (channel != null ? " or in #" + S.DiscordRequestChannel : "") + ".");
                UpdateRequestsButton();
                FlashWindow();
                return 0;
            });
            say("Thanks! Your request for **" + want + "** went to the admins. This bot will send you a private message when they decide, so keep DMs from server members turned on.").Wait();
        }

        Dictionary<string, object> RequestEmbed(WhitelistRequest r, string outcome)
        {
            string text = "**" + r.DiscordName + "** asks for the username **" + r.Username + "**.";
            if (outcome != null) text += "\n\n" + outcome;
            return DiscordWebhook.Embed("📝 Whitelist request", text, outcome == null ? DcAmber : outcome.StartsWith("✅") ? DcGreen : DcRed, "ezpz · " + S.ServerName);
        }

        class Decision { public string Public, Private; }

        static string NewPassword()
        {
            const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
            var bytes = new byte[10];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            return new string(bytes.Select(x => chars[x % chars.Length]).ToArray());
        }

        // Approves (creates the account with a fresh password and messages it to the player) or denies a request.
        // Runs on the UI thread. Public goes on the admin post; Private is for the approver only.
        async Task<Decision> DecideRequest(WhitelistRequest r, bool approve, string by)
        {
            var d = new Decision();
            var b = bot;
            string title = ServerTitle();
            if (approve)
            {
                if (server.State != ServerState.Running)
                {
                    d.Private = "The server needs to be running to create the account. Start it and approve again.";
                    return d;
                }
                string pw = NewPassword();
                try { server.SendCommand("adduser \"" + Q(r.Username) + "\" \"" + pw + "\"", false); }
                catch (Exception ex) { d.Private = "Couldn't create the account: " + ex.Message; return d; }
                Log("> adduser \"" + r.Username + "\" \"********\"   (whitelist request from " + r.DiscordName + ", approved by " + by + ")");
                var _ = Task.Delay(3000).ContinueWith(t => Ui(() => RefreshPlayers(true)));   // the new account shows as "never joined"
                string dm = b == null ? "ezpz's Discord bot isn't connected" : await b.DirectMessage(r.DiscordUserId, DiscordWebhook.Embed("✅ You're whitelisted on " + title,
                    "Log in with:\n**Username:** `" + r.Username + "`\n**Password:** `" + pw + "`\n\nYou can change the password in game from the server's login screen (Manage account). Keep it to yourself.", DcGreen, "ezpz · " + S.ServerName));
                d.Public = "✅ Approved by " + by + "." + (dm == null ? " The login details were sent privately." : " Couldn't message them (" + dm + "), so the approver has the password.");
                if (dm != null) d.Private = "Couldn't message " + r.DiscordName + " (" + dm + "). Pass these on yourself:\nUsername: `" + r.Username + "`\nPassword: `" + pw + "`";
            }
            else
            {
                string dm = b == null ? "not connected" : await b.DirectMessage(r.DiscordUserId, DiscordWebhook.Embed("Whitelist request declined",
                    "Your request for the username **" + r.Username + "** on " + title + " was declined.", DcRed, "ezpz · " + S.ServerName));
                d.Public = "✕ Denied by " + by + "." + (dm == null ? " They were told privately." : "");
                Log("[ezpz] Whitelist request from " + r.DiscordName + " for \"" + r.Username + "\" denied by " + by + ".");
            }
            dstate.Requests.Remove(r);
            SaveDiscordState();
            UpdateRequestsButton();
            if (b != null && r.ChannelId != "" && r.MessageId != "")
            {
                try { await b.EditChannelMessage(r.ChannelId, r.MessageId, RequestEmbed(r, d.Public), new object[0]); }
                catch (Exception ex) { AppLog.Write("[discord] Couldn't update the request post: " + ex.Message); }
            }
            return d;
        }

        void UpdateRequestsButton()
        {
            if (btnRequests == null) return;
            int n = dstate.Requests.Count;
            btnRequests.Text = "Whitelist requests (" + n + ")";
            btnRequests.Visible = n > 0;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool FlashWindow(IntPtr hwnd, bool invert);
        void FlashWindow() { if (!ContainsFocus) try { FlashWindow(Handle, true); } catch { } }

        // The list of open requests, from the Players page.
        void ShowRequests()
        {
            var f = new Form
            {
                Text = "Whitelist requests", Size = new Size(640, 380), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Bg,
                ForeColor = Theme.Text, Font = Theme.Base, MinimizeBox = false, MaximizeBox = false, ShowIcon = false, ShowInTaskbar = false,
            };
            var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None };
            lv.Columns.Add("Discord user", 180);
            lv.Columns.Add("Wants the username", 180);
            lv.Columns.Add("Asked", 200);
            Action fill = () =>
            {
                lv.Items.Clear();
                foreach (var r in dstate.Requests)
                {
                    DateTime at;
                    DateTime.TryParse(r.At, null, DateTimeStyles.RoundtripKind, out at);
                    lv.Items.Add(new ListViewItem(new[] { r.DiscordName, r.Username, at == DateTime.MinValue ? "" : FriendlyTime(at) }) { Tag = r });
                }
                if (lv.Items.Count > 0) lv.Items[0].Selected = true;
            };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(8, 10, 8, 0) };
            var hint = new Label { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 8, 8, 0), ForeColor = Theme.Muted,
                Text = "Approving creates the account with a new password and sends it to them privately on Discord. The server must be running." };
            Func<bool, Task> decide = async approve =>
            {
                if (lv.SelectedItems.Count != 1) return;
                var r = (WhitelistRequest)lv.SelectedItems[0].Tag;
                if (!approve && MessageBox.Show(f, "Deny " + r.DiscordName + "'s request for \"" + r.Username + "\"?", "ezpz", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                var d = await DecideRequest(r, approve, "an admin in ezpz");
                if (d.Private != null)
                {
                    var choice = MessageBox.Show(f, d.Private.Replace("`", "") + (d.Private.Contains("Password") ? "\n\nCopy the password?" : ""), "ezpz",
                        d.Private.Contains("Password") ? MessageBoxButtons.YesNo : MessageBoxButtons.OK);
                    if (choice == DialogResult.Yes) try { Clipboard.SetText(Regex.Match(d.Private, "Password: `([^`]*)`").Groups[1].Value); } catch { }
                }
                fill();
                if (dstate.Requests.Count == 0) f.Close();
            };
            var ok = Theme.Btn("Approve", Theme.AccentBtn);
            ok.Click += async (s, e) => await decide(true);
            var no = Theme.Btn("Deny", Theme.DangerBtn);
            no.Click += async (s, e) => await decide(false);
            var close = Theme.Btn("Close");
            close.Click += (s, e) => f.Close();
            bottom.Controls.AddRange(new Control[] { ok, no, close });
            f.Controls.Add(lv);
            f.Controls.Add(hint);
            f.Controls.Add(bottom);
            fill();
            f.ShowDialog(this);
        }

        // ================================================================== live status message

        Dictionary<string, object> StatusEmbed() { string key; return StatusEmbed(out key); }

        Dictionary<string, object> StatusEmbed(out string key)
        {
            var st = server.State;
            string label; int color;
            switch (st)
            {
                case ServerState.Running: label = "🟢 Online"; color = DcGreen; break;
                case ServerState.Starting: label = "🟡 Starting…"; color = DcAmber; break;
                case ServerState.Stopping: label = "🟠 Shutting down…"; color = DcAmber; break;
                default: label = "🔴 Offline"; color = DcRed; break;
            }
            var lines = new List<string> { "**" + label + "**" };
            string names = "", up = "", restart = "";
            if (st == ServerState.Running)
            {
                var online = onlineNow.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                names = string.Join(", ", online);
                lines.Add("**Players:** " + online.Count + (online.Count > 0 ? " — " + DiscordWebhook.Clip(names, 900) : ""));
                var u = DateTime.Now - server.StartedAt;
                int mins = (int)u.TotalMinutes / 10 * 10;   // to the 10 minutes, so the message isn't edited every minute
                up = mins < 10 ? "just started" : (mins / 60 > 0 ? mins / 60 + "h " : "") + (mins % 60) + "m";
                lines.Add("**Up:** " + up);
                if (S.RestartHours > 0)
                {
                    var left = server.StartedAt.AddHours(S.RestartHours) - DateTime.Now;
                    int lm = Math.Max(0, (int)left.TotalMinutes / 10 * 10);
                    restart = (lm / 60 > 0 ? lm / 60 + "h " : "") + (lm % 60) + "m";
                    lines.Add("**Next restart:** in about " + restart);
                }
            }
            string wipe = NextWipeText();
            if (wipe != null) lines.Add("**Next wipe:** " + wipe.Substring(0, wipe.IndexOf(" (")));
            key = label + "|" + names + "|" + up + "|" + restart + "|" + wipe;
            return DiscordWebhook.Embed(ServerTitle(), string.Join("\n", lines), color, "Live status · ezpz · " + S.ServerName);
        }

        void StatusTick()
        {
            if (statusPosting || !DiscordWants(DiscordEvent.Status)) return;
            string key;
            var embed = StatusEmbed(out key);
            if (key == statusLastKey && (DateTime.Now - statusLastAt).TotalMinutes < 30) return;
            statusPosting = true;
            string url = S.DiscordWebhook, hook = Hash(url), id = dstate.StatusWebhook == hook ? dstate.StatusMessageId : "";
            string profile = S.ProfileId;
            Task.Run(async () =>
            {
                DiscordWebhook.Result r = null;
                if (id != "") { r = await DiscordWebhook.Edit(url, id, embed); if (r.Gone) id = ""; }
                if (id == "") r = await DiscordWebhook.Post(url, embed);
                Ui(() =>
                {
                    statusPosting = false;
                    if (profile != S.ProfileId) return;
                    if (r.Error != null) { Log("[ezpz] Couldn't update the Discord status message: " + r.Error); statusLastAt = DateTime.Now; statusLastKey = key; return; }
                    statusLastKey = key;
                    statusLastAt = DateTime.Now;
                    if (r.Id != null) { dstate.StatusMessageId = r.Id; dstate.StatusWebhook = hook; SaveDiscordState(); }
                });
            });
        }

        static string Hash(string s)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""))).Substring(0, 12);
        }

        // On exit: the window is already gone; disconnect the bot and mark the status message stale, in parallel and
        // off the UI thread (waiting on the UI thread for a network call that needs it was what made closing hang).
        void DiscordShutdown()
        {
            Hide();
            SaveDiscordState();
            var work = new List<Task>();
            var b = bot;
            bot = null;
            if (b != null) work.Add(Task.Run(() => b.Stop(true)));
            if (DiscordWants(DiscordEvent.Status) && dstate.StatusMessageId != "" && dstate.StatusWebhook == Hash(S.DiscordWebhook))
            {
                var embed = DiscordWebhook.Embed(ServerTitle(), "**⚪ Status unknown**\nezpz was closed at " + DateTime.Now.ToString("HH:mm") + ", so this isn't being updated.", 0x99AAB5, "Live status · ezpz · " + S.ServerName);
                string url = S.DiscordWebhook, id = dstate.StatusMessageId;
                work.Add(Task.Run(() => DiscordWebhook.Edit(url, id, embed)));
            }
            try { Task.WaitAll(work.ToArray(), 3000); } catch { }
        }

        // ================================================================== daily summary

        void DayTick()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (dstate.Day.Date != today)
            {
                var old = dstate.Day;
                if (old.Date != "" && !old.Posted && DiscordWants(DiscordEvent.Summary)) PostSummary(old);   // ezpz was closed at summary time
                dstate.Day = new DayStats { Date = today };
                // started after today's summary time: there's too little of today to summarize
                TimeSpan sumAt;
                if (TimeSpan.TryParse(S.SummaryTime, CultureInfo.InvariantCulture, out sumAt) && DateTime.Now.TimeOfDay >= sumAt) dstate.Day.Posted = true;
                foreach (var n in onlineNow) dstate.Day.Players.Add(n);
                SaveDiscordState();
            }
            var d = dstate.Day;
            if (server.State == ServerState.Running) d.UptimeSeconds++;
            d.PeakPlayers = Math.Max(d.PeakPlayers, onlineNow.Count);
            if (server.State == ServerState.Running && ticks % 60 == 0)
            {
                if (currentPage != PHome && DiscordWants(DiscordEvent.Summary)) PollLiveStats();
                double k;
                bool have;
                lock (liveStats) have = liveStats.TryGetValue("zombies-killed-today", out k);
                if (have)
                {
                    // the game's counter restarts with the server, so add up what it gained
                    if (d.LastKillCounter >= 0) d.Kills += k >= d.LastKillCounter ? k - d.LastKillCounter : k;
                    d.LastKillCounter = k;
                }
            }
            if (ticks % 60 == 30) SaveDiscordState();

            TimeSpan at;
            if (!d.Posted && DiscordWants(DiscordEvent.Summary) && TimeSpan.TryParse(S.SummaryTime, CultureInfo.InvariantCulture, out at) && DateTime.Now.TimeOfDay >= at)
                PostSummary(d);
        }

        void PostSummary(DayStats d)
        {
            d.Posted = true;
            SaveDiscordState();
            DateTime date;
            if (!DateTime.TryParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) date = DateTime.Today;
            var lines = new List<string>();
            if (d.UptimeSeconds == 0 && d.Players.Count == 0) lines.Add("The server was offline all day.");
            else
            {
                lines.Add("**Peak players:** " + d.PeakPlayers);
                lines.Add("**Played today:** " + d.Players.Count + (d.Players.Count > 0 ? " — " + DiscordWebhook.Clip(string.Join(", ", d.Players.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)), 900) : ""));
                lines.Add("**Zombies killed:** " + ((long)d.Kills).ToString("N0"));
                lines.Add("**Deaths:** " + d.Deaths);
                lines.Add("**Uptime:** " + (d.UptimeSeconds / 3600) + "h " + (d.UptimeSeconds / 60 % 60) + "m");
                if (d.Crashes > 0) lines.Add("**Crashes:** " + d.Crashes);
            }
            Notify(DiscordEvent.Summary, "📊 " + ServerTitle() + " — " + date.ToString("dddd, MMMM d", CultureInfo.InvariantCulture), string.Join("\n", lines), DcBlue);
            Log("[ezpz] Posted the daily summary to Discord.");
        }

        // ================================================================== deaths and survival milestones

        void PerkTick()
        {
            bool silent;
            var events = perkFeed.Poll(S, out silent);
            if (events.Count == 0) return;
            foreach (var e in events)
            {
                string key = e.Name.ToLowerInvariant();
                int days = e.Hours / 24;
                if (e.Died)
                {
                    dstate.Milestones[key] = 0;
                    if (silent) continue;
                    dstate.Day.Deaths++;
                    Notify(DiscordEvent.Deaths, "💀 " + e.Name + " died", "They survived " + Survived(e.Hours) + " on " + ServerTitle() + ".", DcRed);
                    continue;
                }
                int best = MilestoneDays.Where(m => m <= days).DefaultIfEmpty(0).Max();
                int done;
                // first time we see someone: remember where they are without announcing old achievements
                if (!dstate.Milestones.TryGetValue(key, out done) || days < done) { dstate.Milestones[key] = best; continue; }
                if (best > done)
                {
                    dstate.Milestones[key] = best;
                    if (!silent) Notify(DiscordEvent.Milestones, "🏆 " + e.Name + " has survived " + best + " days", "On " + ServerTitle() + ", and counting.", DcGreen);
                }
            }
            SaveDiscordState();
        }

        static string Survived(int hours)
        {
            int d = hours / 24, h = hours % 24;
            if (d == 0) return h + " hour" + (h == 1 ? "" : "s");
            return d + " day" + (d == 1 ? "" : "s") + (h > 0 ? " and " + h + " hour" + (h == 1 ? "" : "s") : "");
        }
    }
}
