using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
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
        TextBox tbDcToken, tbDcChat, tbDcLog, tbDcCmd, tbWebhook;
        Label lblDcCheck, lblWebhookState;
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
            Step(p, "4.  In OAuth2 → URL Generator, tick the scope \"bot\", then the permissions View Channels, Send Messages and Read Message History. Open the generated link, pick your Discord server and click Authorize.", null, null);
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
            p.Buttons(saveBot, check);
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
            p.Y += 6;
            var saveHook = Theme.Btn("Save", Theme.AccentBtn);
            saveHook.Click += (s, e) => SaveDiscordAll();
            var test = Theme.Btn("Send test message");
            test.Click += async (s, e) => await TestWebhook();
            p.Buttons(saveHook, test);
            lblWebhookState = Fixed(p, 30, Theme.Muted);

            // ---------------------------------------------------------------- ideas
            p.H2("Ideas for later");
            p.P("• A live status message that edits itself in place (online or offline, players, uptime) instead of new posts.\n"
              + "• Control from Discord: /status, /restart and /backup through an ezpz bot, limited to an admin role.\n"
              + "• A daily summary: peak players, zombies killed, deaths, uptime.\n"
              + "• Whitelist requests made in Discord and approved in ezpz.\n"
              + "• Death announcements and milestones (\"Rick survived 30 days\") from the game's player logs.");
            p.End();
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

        bool ReadWebhookForm(out string url)
        {
            url = tbWebhook.Text.Trim();
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
            S.Save();
            LoadDiscordControls();
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

        bool DiscordWants(DiscordEvent ev) { return S.DiscordWebhook != "" && (S.DiscordEvents & (int)ev) != 0; }

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
            if (S.DiscordWebhook == "" || server.State != ServerState.Running) return;
            if (DiscordWants(DiscordEvent.Players) && ticks % 30 == 0 && currentPage != PPlayers && currentPage != PWorld) PollOnline();
            if (DiscordWants(DiscordEvent.Health) && ticks % 60 == 0)
            {
                var gc = gcReader.Read(S.GcLogPath);
                if ((gc.Stalls >= 3 || gc.OutOfMemory) && healthNotifiedRun != gc.RunStart)
                {
                    healthNotifiedRun = gc.RunStart;
                    Notify(DiscordEvent.Health, "⚠️ " + ServerTitle() + " is short of memory",
                        "The game paused " + gc.Stalls + " times waiting for memory, which players feel as lag. Raise the max memory in ezpz (Setup page).", DcAmber);
                }
            }
        }

        // Called by PollOnline with the previous and current sets of online players.
        void DiscordPlayersChanged(HashSet<string> before, HashSet<string> now)
        {
            if (!playersBaseline) { playersBaseline = true; return; }
            if (!DiscordWants(DiscordEvent.Players)) return;
            foreach (var n in now.Except(before, StringComparer.OrdinalIgnoreCase))
                Notify(DiscordEvent.Players, "👋 " + n + " joined", now.Count + " online on " + ServerTitle() + ".", DcGreen);
            foreach (var n in before.Except(now, StringComparer.OrdinalIgnoreCase))
                Notify(DiscordEvent.Players, n + " left", now.Count + " online on " + ServerTitle() + ".", DcBlue);
        }
    }
}
