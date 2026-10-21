using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Ezpz
{
    // Home's "Server" tiles and "Needs attention" list.
    public partial class MainForm
    {
        // caption, the page a click opens
        static readonly object[][] HomeTileDefs =
        {
            new object[] { "Players", PPlayers }, new object[] { "Server FPS", PPerf },
            new object[] { "Zombies loaded", PPerf }, new object[] { "Memory", PPerf },
            new object[] { "Next restart", PConsole }, new object[] { "Next wipe", PReset },
            new object[] { "Last backup", PBackups }, new object[] { "EBS radio", PWorld },
        };

        static readonly Font HomeTileFont = new Font("Segoe UI Semibold", 13f);
        string attentionSig = "";
        DateTime botDownSince = DateTime.MinValue;

        Control BuildHomeTiles()
        {
            var box = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            homeTiles = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, BackColor = Color.Transparent };
            foreach (var d in HomeTileDefs)
            {
                int page = (int)d[1];
                var t = new Panel { Size = new Size(150, 58), BackColor = Theme.Card, Margin = new Padding(0, 0, 8, 8), Cursor = Cursors.Hand };
                var v = new Label { Text = "—", AutoSize = false, Location = new Point(10, 5), Size = new Size(136, 26), Font = HomeTileFont, ForeColor = Theme.Text, AutoEllipsis = true };
                var k = new Label { Text = (string)d[0], AutoSize = false, Location = new Point(10, 33), Size = new Size(136, 18), ForeColor = Theme.Muted };
                t.Controls.Add(v);
                t.Controls.Add(k);
                EventHandler go = (s, e) => ShowPage(page);
                t.Click += go; v.Click += go; k.Click += go;
                homeTiles.Controls.Add(t);
            }
            box.Controls.Add(homeTiles);
            box.Controls.Add(new Label { Text = "Server", Dock = DockStyle.Top, Height = 30, Font = Theme.H2, ForeColor = Theme.Accent });
            return box;
        }

        void SetHomeTile(int i, string value, Color color)
        {
            if (homeTiles == null || homeTiles.IsDisposed || homeTiles.Controls.Count <= i) return;
            var v = (Label)homeTiles.Controls[i].Controls[0];
            v.Text = value;
            v.ForeColor = color;
        }

        // Fills Home's tiles. v is the memory verdict (may be null).
        void RefreshGlance(HealthVerdict v)
        {
            if (homeTiles == null) return;
            if (ebsKhz == null && !ebsLookedUp) LookupEbs();
            bool running = server.State == ServerState.Running;
            Dictionary<string, double> st;
            lock (liveStats) st = new Dictionary<string, double>(liveStats);
            Func<string, string> num = k => { double x; return running && st.TryGetValue(k, out x) ? x.ToString("0") : "—"; };

            string max = IniFile.Load(S.IniPath).Get("MaxPlayers", "").Trim();
            double pl;
            // the game's count, else the mod's list, else the connection log
            int count = st.TryGetValue("players", out pl) ? (int)pl : live != null && live.Fresh ? live.Players.Count : onlineNow.Count;
            string players = running ? count + (max != "" ? " / " + max : "") : "—";
            SetHomeTile(0, players, Theme.Text);
            SetHomeTile(1, num("fps"), Theme.Text);
            SetHomeTile(2, num("zombies-loaded"), Theme.Text);

            string mem = "—";
            Color memColor = Theme.Text;
            if (v != null && (v.Level >= 2 || v.Title.StartsWith("Memory looks") || v.Title.StartsWith("Plenty") || v.Title.StartsWith("Tight")))
            {
                mem = v.Title.StartsWith("Memory looks") ? "Looks right" : v.Title.Split(':')[0];
                memColor = v.Level == 0 ? Theme.Accent : v.Level == 1 ? Theme.Info : v.Level == 2 ? Theme.Warn : Theme.Danger;
            }
            SetHomeTile(3, mem, memColor);

            string nextRestart = "off";
            if (S.RestartHours > 0)
            {
                if (running)
                {
                    var left = server.StartedAt.AddHours(S.RestartHours) - DateTime.Now;
                    nextRestart = left.TotalSeconds > 0 ? (int)left.TotalHours + "h " + left.Minutes.ToString("00") + "m" : "now";
                }
                else nextRestart = "every " + S.RestartHours + "h";
            }
            SetHomeTile(4, nextRestart, Theme.Text);

            var wipe = UpcomingWipe();
            if (MissedWipe() != null) SetHomeTile(5, "missed", Theme.Danger);
            else if (EndIsNear()) SetHomeTile(5, "THE END IS NEAR", Theme.Danger);
            else SetHomeTile(5, wipe == null ? "off" : wipe.Value.ToString("ddd d MMM", CultureInfo.InvariantCulture), wipe != null && (wipe.Value - DateTime.Now).TotalHours < 24 ? Theme.Warn : Theme.Text);

            string lastBackup = "none";
            try
            {
                if (Directory.Exists(S.BackupDir))
                {
                    var f = new DirectoryInfo(S.BackupDir).GetFiles(S.ServerName + "_*.zip").OrderByDescending(x => x.CreationTime).FirstOrDefault();
                    if (f != null) lastBackup = FriendlyTime(f.CreationTime);
                }
            }
            catch { }
            SetHomeTile(6, lastBackup, lastBackup == "none" ? Theme.Warn : Theme.Text);
            SetHomeTile(7, ebsKhz != null ? GameData.FormatFreq(ebsKhz) : "—", Theme.Text);
        }

        class Attention { public string Text; public Color Color; public string Button; public Action Act; }

        // Everything that needs a decision or a look, newest-first priorities at the top. Empty = all good.
        List<Attention> ComputeAttention(HealthVerdict v, List<string> problems)
        {
            var list = new List<Attention>();
            Action<string, Color, string, Action> add = (t, c, b, a) => list.Add(new Attention { Text = t, Color = c, Button = b, Act = a });

            // setup isn't finished
            bool[] setup = { AppSettings.ValidServerName(S.ServerName) && S.AdminPassword.Length > 0 && S.IsSaved, File.Exists(S.StartBat), File.Exists(S.IniPath), firewallOk };
            int missing = Array.IndexOf(setup, false);
            if (missing >= 0) add("Finish setup: " + Steps[missing] + ".", Theme.Warn, "Open Setup", () => ShowPage(PSetup));
            else if (!S.FirstStartDone && server.State == ServerState.Stopped)
                add("Last setup step: start the server for the first time.", Theme.Warn, "Start", () => { var _ = StartFlow(); });

            if (dstate.Requests.Count > 0)
                add(dstate.Requests.Count + " whitelist request" + (dstate.Requests.Count == 1 ? "" : "s") + " waiting, from " + string.Join(", ", dstate.Requests.Select(r => r.DiscordName).Take(3)) + ".", Theme.Warn, "Review", ShowRequests);
            var open = tickets.Where(t => t.Answers.Count == 0).ToList();
            if (open.Count > 0)
                add(open.Count + " unanswered ticket" + (open.Count == 1 ? "" : "s") + ". " + open[0].Author + ": \"" + DiscordWebhook.Clip(open[0].Message, 80) + "\"", Theme.Warn, "Open", ShowTickets);

            string installed = SteamCmd.InstalledBuildId(S);
            if (installed != null && latestBuild != null && installed != latestBuild)
                add("Server update available: build " + latestBuild + " (you have " + installed + "). Players' games update through Steam, so update soon.", Theme.Warn, "Update now", () => { var _ = UpdateServerFlow(); });

            if (modsOutdated.Count > 0)
            {
                string plan = modRestartAt != null ? " Restarting in " + Math.Max(0, (int)(modRestartAt.Value - DateTime.Now).TotalMinutes) + " min." : modRestartWhenEmpty ? " Restarting when nobody is online." : "";
                if (modRestartAt != null || modRestartWhenEmpty)
                    add(modsOutdated.Count + " mod update" + (modsOutdated.Count == 1 ? "" : "s") + " to install." + plan, Theme.Warn, "Cancel restart", CancelModRestart);
                else
                    add(modsOutdated.Count + " mod update" + (modsOutdated.Count == 1 ? "" : "s") + " to install. Restart the server so players with the new version can join.", Theme.Warn, "Mods", () => ShowPage(PMods));
            }
            int modProblems = ComputeModIssues().Count(l => l.StartsWith("⚠"));
            if (modProblems > 0) add(modProblems + " mod problem" + (modProblems == 1 ? "" : "s") + ", such as a missing required mod.", Theme.Warn, "Mods", () => ShowPage(PMods));

            if (v != null && v.Level >= 2) add("Memory: " + v.Title + ".", v.Level == 3 ? Theme.Danger : Theme.Warn, "Performance", () => ShowPage(PPerf));
            foreach (var pr in problems.Take(3)) add(pr, Theme.Warn, "Performance", () => ShowPage(PPerf));

            if (S.DiscordBotEnable && bot != null && !bot.Connected)
            {
                if (botDownSince == DateTime.MinValue) botDownSince = DateTime.Now;
                if ((DateTime.Now - botDownSince).TotalSeconds > 60) add("ezpz's Discord bot isn't connected: " + bot.Status, Theme.Warn, "Discord", () => ShowPage(PDiscord));
            }
            else botDownSince = DateTime.MinValue;

            if (EndIsNear())
            {
                var end = SeasonEndAt().Value;
                add("THE END IS NEAR: Season " + Season.Number + " ends with the wipe " + WipeSchedule.Until(end, DateTime.Now) + " (" + end.ToString("HH:mm") + ").", Theme.Danger, "Seasons", () => ShowPage(PSeasons));
            }
            var missedWipe = MissedWipe();
            if (missedWipe != null)
            {
                add("The scheduled wipe due " + missedWipe.Value.ToString("dddd d MMMM 'at' HH:mm", CultureInfo.InvariantCulture) + " didn't happen because ezpz wasn't running. Wipe now, or skip it and wait for the next one?",
                    Theme.Danger, "Wipe now", () => { var _ = WipeMissedNow(); });
                add("Or skip the missed wipe: nothing is deleted" + (Season != null ? " and Season " + Season.Number + " keeps running" : "") + ".", Theme.Muted, "Skip it", WipeMissedSkip);
            }
            var wipe = UpcomingWipe();
            if (wipe != null && (wipe.Value - DateTime.Now).TotalHours < 24)
                add("Scheduled wipe " + WipeSchedule.Until(wipe.Value, DateTime.Now) + " (" + wipe.Value.ToString("HH:mm") + "): " + WipeSchedule.Presets[S.WipePreset].ToLower() + ".", Theme.Info, "Reset & wipes", () => ShowPage(PReset));
            return list;
        }

        void RefreshAttention(HealthVerdict v, List<string> problems)
        {
            if (attention == null) return;
            var items = ComputeAttention(v, problems);
            string sig = string.Join("|", items.Select(i => i.Text));
            if (sig == attentionSig && attention.Controls.Count > 0) return;   // unchanged: don't rebuild (no flicker)
            attentionSig = sig;
            attention.SuspendLayout();
            foreach (Control c in attention.Controls.Cast<Control>().ToList()) c.Dispose();
            attention.Controls.Clear();
            if (items.Count == 0)
                attention.Controls.Add(new Label { Text = "✓  Nothing needs attention.", AutoSize = true, ForeColor = Theme.Accent, Margin = new Padding(0, 4, 0, 4) });
            else
            {
                attention.Controls.Add(new Label { Text = "Needs attention", AutoSize = true, Font = Theme.H2, ForeColor = Theme.Warn, Margin = new Padding(0, 0, 0, 4) });
                foreach (var it in items)
                {
                    var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Side, Margin = new Padding(0, 0, 0, 4), Padding = new Padding(8, 4, 4, 4) };
                    var text = new Label { Text = it.Text, AutoSize = true, MaximumSize = new Size(Math.Max(300, attention.Parent.ClientSize.Width - 220), 0), ForeColor = it.Color, Margin = new Padding(0, 6, 12, 0), UseMnemonic = false };
                    row.Controls.Add(text);
                    if (it.Button != null)
                    {
                        var b = Theme.Btn(it.Button);
                        b.UseMnemonic = false;   // show "&" in "Reset & wipes"
                        b.Margin = new Padding(0);
                        b.Padding = new Padding(6, 2, 6, 2);
                        var act = it.Act;
                        b.Click += (s, e) => act();
                        row.Controls.Add(b);
                    }
                    attention.Controls.Add(row);
                }
            }
            attention.ResumeLayout();
        }
    }
}
