using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // Discord page: (1) the game's built-in bot (chat bridge, log and command channels), configured in the
    // server .ini, and (2) ezpz's own notifications posted through a webhook.
    public partial class MainForm
    {
        Page pgDiscord;
        CheckBox chkDcEnable;
        TextBox tbDcToken, tbDcChat, tbDcLog, tbDcCmd, tbWebhook, tbSummaryTime, tbAdminRole, tbReqChannel;
        Label lblDcCheck, lblWebhookState, lblBotState;
        CheckBox chkBotEnable;
        readonly Dictionary<DiscordEvent, CheckBox> dcEventChecks = new Dictionary<DiscordEvent, CheckBox>();

        static readonly KeyValuePair<DiscordEvent, string>[] DcEvents =
        {
            new KeyValuePair<DiscordEvent, string>(DiscordEvent.Online, "Server is online"),
            new KeyValuePair<DiscordEvent, string>(DiscordEvent.Stopped, "Server stopped"),
            new KeyValuePair<DiscordEvent, string>(DiscordEvent.Crash, "Crashes and automatic restarts"),
            new KeyValuePair<DiscordEvent, string>(DiscordEvent.RestartWarning, "Restart warnings (scheduled, mod and game updates)"),
            new KeyValuePair<DiscordEvent, string>(DiscordEvent.Updates, "Game or mod update available"),
            new KeyValuePair<DiscordEvent, string>(DiscordEvent.Players, "Players joining and leaving"),
            new KeyValuePair<DiscordEvent, string>(DiscordEvent.Health, "Memory problems"),
            new KeyValuePair<DiscordEvent, string>(DiscordEvent.Tickets, "New tickets (help requests players file in game)"),
            new KeyValuePair<DiscordEvent, string>(DiscordEvent.Wipes, "Scheduled wipes: warnings 3 days, 1 day and 1 hour before, and when the fresh world is up"),
            new KeyValuePair<DiscordEvent, string>(DiscordEvent.Seasons, "Seasons: weekly standings, THE END IS NEAR, and the final podium"),
            new KeyValuePair<DiscordEvent, string>(DiscordEvent.Deaths, "Player deaths"),
            new KeyValuePair<DiscordEvent, string>(DiscordEvent.Milestones, "Survival milestones (7, 14, 30, 60, 100, 180 and 365 days)"),
            new KeyValuePair<DiscordEvent, string>(DiscordEvent.Status, "A live status message that edits itself (online, players, uptime). Replaces the online, stopped and join posts"),
            new KeyValuePair<DiscordEvent, string>(DiscordEvent.Summary, "A daily summary: peak players, who played, zombies killed, deaths, uptime"),
        };

        void BuildDiscord()
        {
            var p = pgDiscord = NewPage();
            p.H1("Discord");
            p.P("Two separate things, use either or both. The chat bridge is Project Zomboid's own Discord bot: in-game chat and Discord chat flow both ways. Notifications are ezpz posting server news (online, crashes, updates, players) to a channel, and need no bot.");

            // ---------------------------------------------------------------- chat bridge
            p.H2("1. Chat bridge (the game's built-in Discord bot)");
            p.P("Relays the game's global chat (/all) to a Discord channel and back. It can also post server events to a log channel and take admin commands in a command channel. You set it up once in Discord's developer portal:");
            Step(p, "1.  Open the Discord Developer Portal and click New Application. Name it after your server.", "Discord Developer Portal", "https://discord.com/developers/applications");
            Step(p, "2.  In the Bot tab, click Reset Token, then Copy, and paste it into Bot token below. Treat it like a password: anyone with it controls the bot.", null, null);
            Step(p, "3.  Still in the Bot tab, under Privileged Gateway Intents, turn on Message Content Intent and click Save. Without it, chat from Discord can't reach the game.", null, null);
            Step(p, "4.  Click Invite bot below. ezpz builds the invite link with everything the bot needs and opens it in your browser: pick your Discord server and click Authorize.", null, null);
            Step(p, "5.  In your Discord server, create text channels for the bridge (for example pz-chat, pz-log and pz-commands) and type their names below exactly, without the #. Make the commands channel visible to admins only: messages there run as server commands.", null, null);
            Step(p, "6.  Save, click Check setup, then restart the server.", null, null);
            p.Y += 6;

            chkDcEnable = Theme.Check("Turn on the chat bridge", false);
            p.Add(chkDcEnable, 24);
            int y = p.Y;
            tbDcToken = p.Row("Bot token", Theme.Box(""), 420);
            tbDcToken.UseSystemPasswordChar = true;
            var show = Theme.Check("Show", false);
            show.Location = new Point(Page.X + Page.LabelW + 430, y + 2);
            show.CheckedChanged += (s, e) => tbDcToken.UseSystemPasswordChar = !show.Checked;
            p.Controls.Add(show);
            tbDcChat = p.Row("Chat channel name", Theme.Box(""), 240);
            tbDcLog = p.Row("Log channel name", Theme.Box(""), 240);
            p.Hint("Optional. Server events such as joins and deaths.");
            tbDcCmd = p.Row("Command channel name", Theme.Box(""), 240);
            p.Hint("Optional. Admins can type server commands here. Keep it private.");
            var saveBot = Theme.Btn("Save", Theme.AccentBtn);
            saveBot.Click += (s, e) => SaveDiscordAll();
            var check = Theme.Btn("Check setup");
            check.Click += async (s, e) => await CheckDiscordBot();
            var invite = Theme.Btn("Invite bot");
            invite.Click += async (s, e) => await InviteBot();
            p.Buttons(saveBot, check, invite);
            lblDcCheck = Fixed(p, 120, Theme.Muted);
            p.P("The game reads the token from the server's .ini file, so it's stored there in plain text. Don't share that file.");

            // ---------------------------------------------------------------- notifications
            p.H2("2. ezpz notifications (webhook)");
            p.P("ezpz posts short updates to one Discord channel. A webhook is a posting link Discord gives you, so there's no bot to set up:");
            Step(p, "1.  In Discord, hover the channel you want and click its gear (Edit Channel).", null, null);
            Step(p, "2.  Go to Integrations → Webhooks → New Webhook. Name it (for example \"ezpz\") and check the channel.", null, null);
            Step(p, "3.  Click Copy Webhook URL and paste it below. You need the Manage Webhooks permission on that server. Anyone with the URL can post to the channel, so keep it private.", null, null);
            p.Y += 6;
            y = p.Y;
            tbWebhook = p.Row("Webhook URL", Theme.Box(""), 520);
            tbWebhook.UseSystemPasswordChar = true;
            var showW = Theme.Check("Show", false);
            showW.Location = new Point(Page.X + Page.LabelW + 530, y + 2);
            showW.CheckedChanged += (s, e) => tbWebhook.UseSystemPasswordChar = !showW.Checked;
            p.Controls.Add(showW);
            p.P("Post when:", Theme.Text);
            foreach (var ev in DcEvents)
            {
                var c = Theme.Check(ev.Value, false);
                c.Location = new Point(Page.X + 16, p.Y);
                p.Controls.Add(c);
                p.Y += 26;
                dcEventChecks[ev.Key] = c;
            }
            // with the live status message on, online / stopped / joins and leaves are shown there instead
            dcEventChecks[DiscordEvent.Status].CheckedChanged += (s, e) => ShowCoveredByStatus();
            p.Y += 4;
            tbSummaryTime = p.Row("Daily summary time", Theme.Box(""), 70);
            p.Hint("24-hour time on this PC, like 23:59. Deaths and milestones need the server's PerkLogs setting on (it is by default).");
            var saveHook = Theme.Btn("Save", Theme.AccentBtn);
            saveHook.Click += (s, e) => SaveDiscordAll();
            var test = Theme.Btn("Send test message");
            test.Click += async (s, e) => await TestWebhook();
            p.Buttons(saveHook, test);
            lblWebhookState = Fixed(p, 30, Theme.Muted);

            // ---------------------------------------------------------------- ezpz's bot
            p.H2("3. Discord commands and whitelist requests (ezpz's bot)");
            p.P("ezpz connects with the same bot as the chat bridge (the bot token in section 1), so there's nothing new to create. It adds slash commands to your Discord server. Everyone can use /status, and players ask for an account with /whitelist. /start, /restart and /backup are for admins. ezpz has to be running for them to work, but the game server doesn't.");
            p.P("If you invited the bot before this was added, click Invite bot in section 1 again so the commands show up.");
            chkBotEnable = Theme.Check("Turn on ezpz's bot", false);
            p.Add(chkBotEnable, 24);
            tbAdminRole = p.Row("Admin role name", Theme.Box(""), 240);
            p.Hint("People with this Discord role can use the admin commands and approve requests. Discord server administrators always can.");
            tbReqChannel = p.Row("Requests channel name", Theme.Box(""), 240);
            p.Hint("Whitelist requests are posted here with Approve and Deny buttons, so make it admins-only. Don't use the command channel: the game runs everything posted there as a server command. You can also approve requests on the Players page.");
            var saveBot2 = Theme.Btn("Save", Theme.AccentBtn);
            saveBot2.Click += (s, e) => SaveDiscordAll();
            p.Buttons(saveBot2);
            lblBotState = Fixed(p, 40, Theme.Muted);
            p.P("Whitelist requests only apply when the server isn't open to everyone (\"Anyone can join\" off on the Settings page). Approving one creates the account with a random password, and the bot sends it to the player privately.");
            p.End();
            FormClosed += (s, e) => DiscordShutdown();
        }

        void ShowCoveredByStatus()
        {
            bool live = dcEventChecks[DiscordEvent.Status].Checked;
            foreach (var kv in dcEventChecks)
            {
                if ((CoveredByStatus & kv.Key) == 0) continue;
                string label = DcEvents.First(x => x.Key == kv.Key).Value;
                // locked rather than disabled: Windows draws disabled check boxes in a dark grey that's unreadable here
                kv.Value.AutoCheck = !live;
                kv.Value.ForeColor = live ? Theme.Muted : Theme.Text;
                kv.Value.Text = live ? label + "  (shown in the live status message instead)" : label;
            }
        }

        void Step(Page p, string text, string linkText, string url)
        {
            var l = new LinkLabel
            {
                Text = text, AutoSize = true, MaximumSize = new Size(Page.Wide - 20, 0), ForeColor = Theme.Text,
                LinkColor = Theme.Info, ActiveLinkColor = Theme.Accent, VisitedLinkColor = Theme.Info, UseMnemonic = false,
                Location = new Point(Page.X + 8, p.Y)
            };
            l.Links.Clear();
            if (linkText != null && text.Contains(linkText)) l.Links.Add(text.IndexOf(linkText, StringComparison.Ordinal), linkText.Length, url);
            l.LinkClicked += (s, e) => { try { Process.Start((string)e.Link.LinkData); } catch { } };
            p.Controls.Add(l);
            p.Y += l.GetPreferredSize(new Size(Page.Wide - 20, 0)).Height + 6;
        }

        // ---------- load / save

        bool dcLoaded;

        void RefreshDiscord()
        {
            dcLoaded = true;
            var ini = IniFile.Load(S.IniPath);
            chkDcEnable.Checked = ini.Get("DiscordEnable", "false").Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            tbDcToken.Text = ini.Get("DiscordToken", "");
            tbDcChat.Text = ini.Get("DiscordChatChannel", "");
            tbDcLog.Text = ini.Get("DiscordLogChannel", "");
            tbDcCmd.Text = ini.Get("DiscordCommandChannel", "");
            LoadDiscordControls();
        }

        // Per-server ezpz settings shown on this page (also called when switching servers).
        void LoadDiscordControls()
        {
            if (tbWebhook == null) return;
            tbWebhook.Text = S.DiscordWebhook;
            foreach (var kv in dcEventChecks) kv.Value.Checked = (S.DiscordEvents & (int)kv.Key) != 0;
            tbSummaryTime.Text = S.SummaryTime;
            ShowCoveredByStatus();
            chkBotEnable.Checked = S.DiscordBotEnable;
            tbAdminRole.Text = S.DiscordAdminRole;
            tbReqChannel.Text = S.DiscordRequestChannel;
            UpdateBotLabel();
            lblWebhookState.Text = S.DiscordWebhook == "" ? "Not set up." : "Set up. Posting " + dcEventChecks.Count(kv => kv.Value.Checked) + " kinds of events.";
        }

        // True if anything on the page differs from what's saved (server .ini for the bridge, ezpz settings for the webhook).
        bool DiscordDirty()
        {
            if (!dcLoaded) return false;
            var ini = IniFile.Load(S.IniPath);
            bool en = ini.Get("DiscordEnable", "false").Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            if (System.IO.File.Exists(S.IniPath) && (chkDcEnable.Checked != en
                || tbDcToken.Text.Trim() != ini.Get("DiscordToken", "").Trim()
                || tbDcChat.Text.Trim().TrimStart('#') != ini.Get("DiscordChatChannel", "").Trim()
                || tbDcLog.Text.Trim().TrimStart('#') != ini.Get("DiscordLogChannel", "").Trim()
                || tbDcCmd.Text.Trim().TrimStart('#') != ini.Get("DiscordCommandChannel", "").Trim())) return true;
            if (tbWebhook.Text.Trim() != S.DiscordWebhook) return true;
            if (tbSummaryTime.Text.Trim() != S.SummaryTime || chkBotEnable.Checked != S.DiscordBotEnable
                || tbAdminRole.Text.Trim() != S.DiscordAdminRole || tbReqChannel.Text.Trim().TrimStart('#') != S.DiscordRequestChannel) return true;
            int mask = 0;
            foreach (var kv in dcEventChecks) if (kv.Value.Checked) mask |= (int)kv.Key;
            return mask != S.DiscordEvents;
        }

        // One Save for the whole page: both the chat bridge and the notifications.
        bool SaveDiscordAll()
        {
            string url;
            if (!ReadWebhookForm(out url)) return false;
            bool bridgeChanged = false;
            if (System.IO.File.Exists(S.IniPath))
            {
                var before = IniFile.Load(S.IniPath);
                bridgeChanged = !before.Get("DiscordEnable", "").Trim().Equals(chkDcEnable.Checked ? "true" : "false", StringComparison.OrdinalIgnoreCase)
                    || before.Get("DiscordToken", "").Trim() != tbDcToken.Text.Trim()
                    || before.Get("DiscordChatChannel", "").Trim() != tbDcChat.Text.Trim().TrimStart('#')
                    || before.Get("DiscordLogChannel", "").Trim() != tbDcLog.Text.Trim().TrimStart('#')
                    || before.Get("DiscordCommandChannel", "").Trim() != tbDcCmd.Text.Trim().TrimStart('#');
                if (bridgeChanged && !SaveDiscordBot(false)) return false;
            }
            else if (chkDcEnable.Checked) { Warn("There's no server config yet. Click 'Generate default config' on the Setup page first."); return false; }
            SaveWebhook();
            lblWebhookState.Text = "Saved.";
            lblWebhookState.ForeColor = Theme.Accent;
            if (bridgeChanged && server.State == ServerState.Running && Confirm("Saved. The game connects to Discord when the server starts. Restart it now?"))
            {
                var _ = RestartFlow(false);
            }
            return true;
        }

        bool SaveDiscordBot(bool askRestart)
        {
            if (!System.IO.File.Exists(S.IniPath)) { Warn("There's no server config yet. Click 'Generate default config' on the Setup page first."); return false; }
            if (chkDcEnable.Checked && (tbDcToken.Text.Trim() == "" || tbDcChat.Text.Trim() == ""))
            {
                Warn("The chat bridge needs at least a bot token and a chat channel name.");
                return false;
            }
            var ini = IniFile.Load(S.IniPath);
            ini.Set("DiscordEnable", chkDcEnable.Checked ? "true" : "false");
            ini.Set("DiscordToken", tbDcToken.Text.Trim());
            ini.Set("DiscordChatChannel", tbDcChat.Text.Trim().TrimStart('#'));
            ini.Set("DiscordLogChannel", tbDcLog.Text.Trim().TrimStart('#'));
            ini.Set("DiscordCommandChannel", tbDcCmd.Text.Trim().TrimStart('#'));
            ini.Save();
            Log("[ezpz] Saved Discord chat bridge settings (" + (chkDcEnable.Checked ? "on" : "off") + ").");
            if (askRestart && server.State == ServerState.Running && Confirm("Saved. The game connects to Discord when the server starts. Restart it now?"))
            {
                var _ = RestartFlow(false);
            }
            return true;
        }

        async Task CheckDiscordBot()
        {
            lblDcCheck.ForeColor = Theme.Muted;
            lblDcCheck.Text = "Asking Discord…";
            var names = new[] { tbDcChat.Text, tbDcLog.Text, tbDcCmd.Text };
            string token = tbDcToken.Text;
            List<string> result;
            try { result = await Task.Run(() => DiscordBotCheck.Run(token, names)); }
            catch (Exception ex) { result = new List<string> { "✕ " + ex.Message }; }
            lblDcCheck.Text = string.Join("\n", result);
            lblDcCheck.ForeColor = result.Any(l => l.StartsWith("✕")) ? Theme.Warn : Theme.Accent;
        }

        // Opens (and copies) the invite link for the bot token in the box.
        async Task InviteBot()
        {
            lblDcCheck.ForeColor = Theme.Muted;
            lblDcCheck.Text = "Asking Discord for the bot's id…";
            string token = tbDcToken.Text;
            try
            {
                string url = await Task.Run(() => DiscordBotCheck.InviteUrl(token));
                try { Clipboard.SetText(url); } catch { }
                Util.OpenUrl(url);
                lblDcCheck.ForeColor = Theme.Accent;
                lblDcCheck.Text = "Opened the invite link in your browser (it's also copied). Pick your Discord server and click Authorize.\n"
                    + "It asks for View Channels, Send Messages, Read Message History and Embed Links, plus slash commands. If the bot is already in your server, this just updates what it's allowed to do.";
            }
            catch (Exception ex)
            {
                lblDcCheck.ForeColor = Theme.Warn;
                lblDcCheck.Text = "✕ " + ex.Message;
            }
        }

        bool ReadWebhookForm(out string url)
        {
            url = tbWebhook.Text.Trim();
            TimeSpan at;
            if (!Regex.IsMatch(tbSummaryTime.Text.Trim(), @"^\d{1,2}:\d\d$") || !TimeSpan.TryParse(tbSummaryTime.Text.Trim(), CultureInfo.InvariantCulture, out at) || at.TotalHours >= 24)
            {
                Warn("The daily summary time should look like 23:59 (24-hour).");
                return false;
            }
            string req = tbReqChannel.Text.Trim().TrimStart('#');
            if (req != "" && req.Equals(tbDcCmd.Text.Trim().TrimStart('#'), StringComparison.OrdinalIgnoreCase))
            {
                Warn("The requests channel can't be the command channel: the game runs everything posted there as a server command. Pick another channel.");
                return false;
            }
            if (chkBotEnable.Checked && tbDcToken.Text.Trim() == "")
            {
                Warn("ezpz's bot uses the bot token from section 1. Set that up first.");
                return false;
            }
            if (url != "" && !DiscordWebhook.UrlPattern.IsMatch(url))
            {
                Warn("That doesn't look like a Discord webhook URL. It should start with https://discord.com/api/webhooks/");
                return false;
            }
            return true;
        }

        void SaveWebhook()
        {
            string url;
            if (!ReadWebhookForm(out url)) return;
            S.DiscordWebhook = url;
            int mask = 0;
            foreach (var kv in dcEventChecks) if (kv.Value.Checked) mask |= (int)kv.Key;
            S.DiscordEvents = mask;
            S.SummaryTime = tbSummaryTime.Text.Trim();
            S.DiscordBotEnable = chkBotEnable.Checked;
            S.DiscordAdminRole = tbAdminRole.Text.Trim();
            S.DiscordRequestChannel = tbReqChannel.Text.Trim().TrimStart('#');
            S.Save();
            LoadDiscordControls();
            statusLastKey = "";
            EnsureBot();
            Log("[ezpz] Saved Discord notification settings.");
        }

        async Task TestWebhook()
        {
            string url;
            if (!ReadWebhookForm(out url)) return;
            if (url == "") { Warn("Paste a webhook URL first."); return; }
            lblWebhookState.Text = "Sending…";
            string err = await DiscordWebhook.Send(url, "👋 ezpz is connected", "Notifications for **" + ServerTitle() + "** will appear in this channel.", 0x5865F2, "ezpz · " + S.ServerName);
            lblWebhookState.Text = err ?? "Sent. Check the channel. (Click Save to keep this webhook.)";
            lblWebhookState.ForeColor = err == null ? Theme.Accent : Theme.Warn;
        }

        string ServerTitle()
        {
            string n = IniFile.Load(S.IniPath).Get("PublicName", "").Trim();
            return n != "" ? n : S.ServerName;
        }

        // ---------- posting events

        // The live status message already shows online / offline and who's playing, so those don't also get their own posts.
        const DiscordEvent CoveredByStatus = DiscordEvent.Online | DiscordEvent.Stopped | DiscordEvent.Players;

        bool DiscordWants(DiscordEvent ev)
        {
            if (S.DiscordWebhook == "" || (S.DiscordEvents & (int)ev) == 0) return false;
            return (CoveredByStatus & ev) == 0 || (S.DiscordEvents & (int)DiscordEvent.Status) == 0;
        }

        DateTime lastDiscordError = DateTime.MinValue;

        void Notify(DiscordEvent ev, string title, string text, int color)
        {
            if (!DiscordWants(ev)) return;
            string url = S.DiscordWebhook, footer = "ezpz · " + S.ServerName, full = title;
            Task.Run(async () =>
            {
                string err = await DiscordWebhook.Send(url, full, text, color, footer);
                if (err != null && (DateTime.Now - lastDiscordError).TotalMinutes > 10)
                {
                    lastDiscordError = DateTime.Now;
                    Log("[ezpz] Discord notification failed: " + err);
                }
            });
        }

        const int DcGreen = 0x57F287, DcRed = 0xED4245, DcAmber = 0xFEE75C, DcBlue = 0x5865F2;

        // ---------- event sources that need watching (called every second)

        ServerState lastNotifiedState = ServerState.Stopped;
        DateTime lastCrashAt = DateTime.MinValue;
        bool playersBaseline;
        DateTime healthNotifiedRun = DateTime.MinValue;
        string notifiedBuild;

        void DiscordOnState(ServerState st)
        {
            WipeOnState(st);
            var was = lastNotifiedState;
            lastNotifiedState = st;
            if (st == ServerState.Running && was != ServerState.Running)
                Notify(DiscordEvent.Online, "🟢 " + ServerTitle() + " is online", "The server is up and ready for players.", DcGreen);
            if (st == ServerState.Stopped && was != ServerState.Stopped)
            {
                playersBaseline = false;
                // A crash raises its own message right after this; give it a moment so we don't post both.
                Task.Delay(2000).ContinueWith(t => Ui(() =>
                {
                    if ((DateTime.Now - lastCrashAt).TotalSeconds > 5)
                        Notify(DiscordEvent.Stopped, "🔴 " + ServerTitle() + " stopped", "The server was shut down.", DcRed);
                }));
            }
            if (st == ServerState.Starting) playersBaseline = false;
        }

        void DiscordTick()
        {
            if (ticks % 10 == 1) EnsureBot();
            if (ticks % 10 == 2) UpdateBotLabel();
            BotRestartTick();
            WipeTick();
            if (ticks % 10 == 7) SeasonTick();
            DayTick();
            if (ticks % 10 == 5) PerkTick();
            if (ticks % 5 == 3) StatusTick();
            if (ticks % 30 == 20) TicketTick();
            if (ticks % 5 == 4) LiveTick();
            if (S.DiscordWebhook == "" || server.State != ServerState.Running) return;
            if (DiscordWants(DiscordEvent.Health) && ticks % 60 == 0)
            {
                var gc = ReadGc();
                if ((gc.RecentStalls(DateTime.Now, 6).Count >= 3 || gc.OutOfMemory) && healthNotifiedRun != gc.RunStart)
                {
                    healthNotifiedRun = gc.RunStart;
                    Notify(DiscordEvent.Health, "⚠️ " + ServerTitle() + " is short of memory",
                        "The game paused " + gc.RecentStalls(DateTime.Now, 6).Count + " times in the last 6 hours waiting for memory, which players feel as lag. Raise the max memory in ezpz (Setup page).", DcAmber);
                }
            }
        }

        // Called by PollOnline with the previous and current sets of online players.
        void DiscordPlayersChanged(HashSet<string> before, HashSet<string> now)
        {
            foreach (var n in now)
                if (!dstate.Day.Players.Contains(n, StringComparer.OrdinalIgnoreCase)) dstate.Day.Players.Add(n);
            if (!playersBaseline) { playersBaseline = true; return; }
            if (!DiscordWants(DiscordEvent.Players)) return;
            foreach (var n in now.Except(before, StringComparer.OrdinalIgnoreCase))
                Notify(DiscordEvent.Players, "👋 " + n + " joined", now.Count + " online on " + ServerTitle() + ".", DcGreen);
            foreach (var n in before.Except(now, StringComparer.OrdinalIgnoreCase))
                Notify(DiscordEvent.Players, n + " left", now.Count + " online on " + ServerTitle() + ".", DcBlue);
        }
    }
}
