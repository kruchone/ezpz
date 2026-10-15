using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // Seasons: a ladder of the best all-round survivors between scheduled wipes, THE END IS NEAR before the
    // season-ending wipe, and symbolic rewards for the winners next season. Scoring lives in Seasons.cs.
    public partial class MainForm
    {
        SeasonStore seasons = new SeasonStore();
        List<Standing> standings;
        DateTime standingsAt = DateTime.MinValue;
        bool standingsBusy, seasonsDirty;
        readonly Dictionary<string, DateTime> onlineSince = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // ---------- store

        void LoadSeasons()
        {
            seasons = SeasonStore.Load(S.ProfileId);
            standings = null;
            standingsAt = DateTime.MinValue;
            seasonsDirty = false;
            onlineSince.Clear();
            LoadSeasonControls();
        }

        void SaveSeasons() { seasons.Save(S.ProfileId); seasonsDirty = false; }

        SeasonRecord Season { get { return S.SeasonsEnabled ? seasons.Current : null; } }

        // The wipe that ends the current season, if one is scheduled.
        DateTime? SeasonEndAt() { return S.SeasonsEnabled && S.WipeEnabled ? WipeSchedule.Next(S, DateTime.Now) : null; }

        bool EndIsNear()
        {
            var end = SeasonEndAt();
            return Season != null && end != null && (end.Value - DateTime.Now).TotalHours <= S.SeasonEndHours;
        }

        bool LadderVisible() { return S.SeasonShowLadder || EndIsNear(); }

        void Audit(SeasonRecord season, string what, string before, string after, string reason, string who)
        {
            season.Audit.Add(new AuditEntry
            {
                At = DateTime.Now.ToString("o"), Who = who ?? Environment.UserName, What = what, Before = before ?? "", After = after ?? "", Reason = reason ?? ""
            });
            Log("[seasons] " + what + (before != null || after != null ? " (" + (before ?? "") + " → " + (after ?? "") + ")" : "") + (string.IsNullOrEmpty(reason) ? "" : ". Reason: " + reason) + ".");
            SaveSeasons();
        }

        // ---------- standings (recomputed in the background; reading the logs takes a moment)

        void RefreshStandings(bool force)
        {
            var season = Season;
            if (season == null || standingsBusy) return;
            if (!force && (DateTime.Now - standingsAt).TotalSeconds < 60) return;
            standingsBusy = true;
            var settings = S;
            Task.Run(() =>
            {
                var alive = new HashSet<string>(ServerDb.ReadCharacters(settings).Values.Where(c => !c.Dead).Select(c => c.Username), StringComparer.OrdinalIgnoreCase);
                var roles = ServerDb.ReadAccounts(settings).GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Role, StringComparer.OrdinalIgnoreCase);
                return Ladder.Compute(settings, season, DateTime.Now, alive, roles);
            }).ContinueWith(t => Ui(() =>
            {
                standingsBusy = false;
                if (settings != S || season != Season) return;
                if (t.Exception != null) { AppLog.Error("working out the season standings", t.Exception.InnerException ?? t.Exception); return; }
                standings = t.Result;
                standingsAt = DateTime.Now;
                if (currentPage == PSeasons) ShowStandings();
            }));
        }

        // ---------- every 10 seconds

        void SeasonTick()
        {
            if (server.State != ServerState.Running) { onlineSince.Clear(); WriteSeasonFile(); return; }
            foreach (var n in onlineNow) if (!onlineSince.ContainsKey(n)) onlineSince[n] = DateTime.Now;
            foreach (var n in onlineSince.Keys.Where(k => !onlineNow.Contains(k)).ToList()) onlineSince.Remove(n);

            var season = Season;
            if (season != null)
            {
                TrackModData(season);
                RefreshStandings(false);
                EndNearTick(season);
                WeeklyTick(season);
                if (seasonsDirty && ticks % 60 < 10) SaveSeasons();
            }
            DeliverRewards();
            WriteSeasonFile();
        }

        // Kills (per character, banked across deaths) and map areas visited, from EZPZ Server Tools.
        void TrackModData(SeasonRecord season)
        {
            if (live == null || !live.Fresh) return;
            if (!season.ModSeen) { season.ModSeen = true; seasonsDirty = true; }
            foreach (var lp in live.Players)
            {
                var sp = season.Player(lp.Username);
                if (sp.FirstKills < 0) { sp.FirstKills = season.FreshStart ? 0 : lp.Kills; sp.LastKills = lp.Kills; seasonsDirty = true; }
                else if (lp.Kills < sp.LastKills)
                {
                    // a new character: bank what the old one killed this season and count the new one from zero
                    sp.KillsBanked += Math.Max(0, sp.LastKills - Math.Max(0, sp.FirstKills));
                    sp.FirstKills = 0;
                    sp.LastKills = lp.Kills;
                    seasonsDirty = true;
                }
                else if (lp.Kills != sp.LastKills) { sp.LastKills = lp.Kills; seasonsDirty = true; }
                string cell = (int)(lp.X / 100) + "," + (int)(lp.Y / 100);
                if ((lp.X != 0 || lp.Y != 0) && !sp.Cells.Contains(cell)) { sp.Cells.Add(cell); seasonsDirty = true; }
            }
        }

        // THE END IS NEAR: announced when it starts, then 6 hours and 1 hour before the wipe.
        void EndNearTick(SeasonRecord season)
        {
            var end = SeasonEndAt();
            if (end == null) return;
            double left = (end.Value - DateTime.Now).TotalHours;
            if (left > S.SeasonEndHours || left <= 0) return;
            string key = end.Value.ToString("o");
            var due = new[] { S.SeasonEndHours, 6, 1 }.Distinct().Where(h => h <= S.SeasonEndHours && left <= h && !season.EndNearSteps.Contains(key + "|" + h)).ToList();
            if (due.Count == 0) return;
            foreach (var h in due) season.EndNearSteps.Add(key + "|" + h);
            SaveSeasons();
            int step = due.Min();   // turned on late, or ezpz was closed: only the latest one
            bool first = step == S.SeasonEndHours;
            string when = Hours(left);
            Cmd("servermsg \"THE END IS NEAR. Season " + season.Number + " ends with the wipe in " + when + ".\"");
            Log("[seasons] THE END IS NEAR: " + when + " left in Season " + season.Number + ".");
            if (first)
                Notify(DiscordEvent.Seasons, "☠️ THE END IS NEAR on " + ServerTitle(), when.Substring(0, 1).ToUpper() + when.Substring(1) + " left in Season " + season.Number
                    + ". The world is wiped " + end.Value.ToString("dddd 'at' HH:mm", CultureInfo.InvariantCulture) + "." + TopText(3), DcRed);
            RefreshHome();
        }

        static string Hours(double h)
        {
            if (h >= 1.5) return (int)Math.Round(h) + " hours";
            if (h >= 0.95) return "1 hour";
            int m = Math.Max(1, (int)Math.Round(h * 60));
            return m + " minute" + (m == 1 ? "" : "s");
        }

        // "\n\n🥇 gib 85.7 · 🥈 Pat 14.3" for Discord posts; empty while the ladder is hidden or nobody qualifies
        string TopText(int n)
        {
            if (standings == null || !LadderVisible()) return "";
            var top = standings.Where(x => x.Qualified).Take(n).ToList();
            if (top.Count == 0) return "";
            return "\n\n" + string.Join("\n", top.Select(x => Medal(x.Rank) + " **" + x.Name + "** " + x.Score.ToString("0.0")));
        }

        static string Medal(int rank) { return rank == 1 ? "🥇" : rank == 2 ? "🥈" : rank == 3 ? "🥉" : "#" + rank; }

        // The weekly standings post, at the chosen day and time. Not made up later if ezpz was closed.
        void WeeklyTick(SeasonRecord season)
        {
            if (!DiscordWants(DiscordEvent.Seasons) || !LadderVisible() || standings == null) return;
            TimeSpan at;
            if (!TimeSpan.TryParse(S.SeasonWeeklyTime, CultureInfo.InvariantCulture, out at)) return;
            var occ = DateTime.Today.Add(at);
            while ((int)occ.DayOfWeek != S.SeasonWeeklyDay || occ > DateTime.Now) occ = occ.AddDays(-1);
            if ((DateTime.Now - occ).TotalHours > 1 || occ < season.StartTime) return;
            DateTime last;
            if (DateTime.TryParse(seasons.LastWeeklyPost, null, DateTimeStyles.RoundtripKind, out last) && last >= occ) return;
            seasons.LastWeeklyPost = occ.ToString("o");
            SaveSeasons();
            var end = SeasonEndAt();
            int day = (int)(DateTime.Now - season.StartTime).TotalDays + 1;
            Notify(DiscordEvent.Seasons, "🏆 Season " + season.Number + " standings on " + ServerTitle(),
                "Day " + day + (end != null ? ", ends " + WipeSchedule.Until(end.Value, DateTime.Now) : "") + "." + (TopText(10) == "" ? "\n\nNobody qualifies yet." : TopText(10)), DcBlue);
        }

        // Rewards go out once the winner has been online in the new season for a minute.
        void DeliverRewards()
        {
            if (server.State != ServerState.Running) return;
            int current = seasons.Current != null ? seasons.Current.Number : int.MaxValue;
            foreach (var r in seasons.Rewards.Where(x => x.Delivered == "" && x.Season < current))
            {
                DateTime since;
                if (!onlineSince.TryGetValue(r.Player, out since) || (DateTime.Now - since).TotalSeconds < 60) continue;
                Cmd("additem \"" + Q(r.Player) + "\" \"" + Q(r.Item) + "\" 1");
                Cmd("servermsg \"" + Q(r.Player) + " receives " + Q(ItemName(r.Item)) + ": " + Q(r.Title) + " of Season " + r.Season + ".\"");
                r.Delivered = DateTime.Now.ToString("o");
                Log("[seasons] Gave " + r.Player + " " + r.Item + " (" + r.Title + ", Season " + r.Season + ").");
                SaveSeasons();
                if (currentPage == PSeasons) ShowSeasonLists();
            }
        }

        string ItemName(string id)
        {
            var c = catalog.FindItem(id);
            return c != null ? c.Name : id;
        }

        // <data>\Lua\ezpz_season.txt, read by the EZPZ Server Tools server half: countdowns, standings, notices.
        string seasonFileLast;
        void WriteSeasonFile()
        {
            string dir = Path.Combine(S.DataDir, "Lua");
            if (!Directory.Exists(dir)) return;
            DateTime? restart = null;
            if (server.State == ServerState.Running)
            {
                if (S.RestartHours > 0) restart = server.StartedAt.AddHours(S.RestartHours);
                if (botRestartAt != null && (restart == null || botRestartAt < restart)) restart = botRestartAt;
                if (modRestartAt != null && (restart == null || modRestartAt < restart)) restart = modRestartAt;
            }
            var season = Season;
            var end = S.WipeEnabled ? WipeSchedule.Next(S, DateTime.Now) : null;
            var notices = seasons.Rewards.Where(r => r.Delivered != "" && (DateTime.Now - DateTime.Parse(r.Delivered, null, DateTimeStyles.RoundtripKind)).TotalMinutes < 15)
                .Select(r => r.Player + "|" + ItemName(r.Item) + "|" + r.Title + " of Season " + r.Season);
            string text = Ladder.ModFile(S, season, season != null && LadderVisible() ? standings : null, end, restart, season != null && LadderVisible(), notices);
            string body = Regex.Replace(text, "^written=.*\n", "", RegexOptions.Multiline);
            if (body == seasonFileLast && ticks % 60 >= 10) return;   // unchanged: rewrite once a minute so the mod knows ezpz is alive
            try
            {
                File.WriteAllText(Path.Combine(dir, "ezpz_season.txt"), text, new UTF8Encoding(false));
                seasonFileLast = body;
            }
            catch (Exception ex) { AppLog.Error("writing ezpz_season.txt", ex); }
        }

        // ---------- the season's end (called before a wipe deletes anything)

        async Task FinalizeSeason(DateTime end, string reason)
        {
            if (!S.SeasonsEnabled) return;
            var season = seasons.Current;
            if (season == null)
            {
                seasons.Current = new SeasonRecord { Number = 1, Start = end.ToString("o"), FreshStart = true };
                SaveSeasons();
                Log("[seasons] Season 1 begins with this wipe.");
                Notify(DiscordEvent.Seasons, "🏁 Season 1 begins on " + ServerTitle(), "The ladder starts with the fresh world. Survive long, learn a lot, kill plenty, see the map.", DcGreen);
                return;
            }
            var settings = S;
            List<Standing> final;
            try
            {
                final = await Task.Run(() =>
                {
                    var alive = new HashSet<string>(ServerDb.ReadCharacters(settings).Values.Where(c => !c.Dead).Select(c => c.Username), StringComparer.OrdinalIgnoreCase);
                    var roles = ServerDb.ReadAccounts(settings).GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Role, StringComparer.OrdinalIgnoreCase);
                    return Ladder.Compute(settings, season, end, alive, roles);
                });
            }
            catch (Exception ex) { AppLog.Error("the final season standings", ex); final = standings ?? new List<Standing>(); }
            if (settings != S) return;

            season.Final = final;
            season.Titles = Ladder.TitleHolders(S, season, final);
            season.End = end.ToString("o");
            season.EndReason = reason;
            seasons.Archive.Add(season);

            // rewards: medals for the podium, a trophy per category title
            var items = (S.SeasonRewards ?? "").Split(';').Select(x => x.Trim()).ToList();
            while (items.Count < 4) items.Add("");
            string[] places = { "1st place", "2nd place", "3rd place" };
            foreach (var st in final.Where(x => x.Qualified && x.Rank >= 1 && x.Rank <= 3))
                if (items[st.Rank - 1] != "")
                    seasons.Rewards.Add(new SeasonReward { Season = season.Number, Player = st.Name, Item = items[st.Rank - 1], Title = st.Rank == 1 ? Ladder.ChampionTitle : places[st.Rank - 1] });
            if (items[3] != "")   // one trophy per player, however many titles they hold
                foreach (var g in season.Titles.Where(x => x.Key != Ladder.ChampionTitle).GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase))
                    seasons.Rewards.Add(new SeasonReward { Season = season.Number, Player = g.Key, Item = items[3], Title = string.Join(", ", g.Select(x => x.Key)) });

            seasons.Current = new SeasonRecord { Number = season.Number + 1, Start = end.ToString("o"), FreshStart = true };
            standings = null;
            SaveSeasons();

            var podium = final.Where(x => x.Qualified).Take(3).ToList();
            Log("[seasons] Season " + season.Number + " is over (" + reason + "). " + (podium.Count > 0 ? "The Survivor: " + podium[0].Name + "." : "Nobody qualified."));
            string titles = string.Join("\n", season.Titles.Where(x => x.Key != Ladder.ChampionTitle).Select(x => "🏆 " + x.Key + ": **" + x.Value + "**"));
            Notify(DiscordEvent.Seasons, "🏁 Season " + season.Number + " is over on " + ServerTitle(),
                (podium.Count == 0 ? "Nobody played enough to qualify." : string.Join("\n", podium.Select(x => Medal(x.Rank) + " **" + x.Name + "** " + x.Score.ToString("0.0") + (x.Rank == 1 ? " · " + Ladder.ChampionTitle : ""))))
                + (titles != "" ? "\n\n" + titles : "") + "\n\nSeason " + (season.Number + 1) + " starts with the fresh world. Winners get their reward when they first join.", 0xF1C40F);
            if (currentPage == PSeasons) ShowSeasonPage();
        }

        // A manual reset from the Reset & wipes page: does it end the season? (null = cancel the reset)
        bool? AskEndSeason(ResetPlan plan)
        {
            if (Season == null || !plan.World) return false;
            var r = MessageBox.Show(this, "End Season " + Season.Number + " here?\n\nYes: this reset ends the season. The final standings are saved to the Hall of Fame, rewards are queued, and Season "
                + (Season.Number + 1) + " starts with the fresh world.\nNo: the season keeps running through the reset.", "Seasons", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return null;
            return r == DialogResult.Yes;
        }

        // ================================================================== the page

        Page pgSeasons;
        CheckBox chkSeasons, chkSeasonStaff, chkSeasonCheats, chkSeasonLadder;
        readonly NumericUpDown[] nWeights = new NumericUpDown[7];
        NumericUpDown nSeasonMinHours, nSeasonKillCap, nSeasonEndHours;
        ComboBox cbSeasonWeekday;
        TextBox tbSeasonWeekly;
        readonly IdPicker[] rewardPickers = new IdPicker[4];
        readonly Label[] rewardNames = new Label[4];
        Label lblSeasonStatus, lblSeasonActive, lblWeightsTotal;
        ListView lvStandings, lvRewards, lvHall, lvAudit;
        Button btnSeasonStart, btnSeasonReset, btnSeasonAdjust, btnSeasonDq;

        void BuildSeasons()
        {
            var p = pgSeasons = NewPage();
            p.H1("Seasons");
            p.P("A season runs from one scheduled wipe to the next. Players are ranked on a ladder by how well they survived all round, the last hours before the wipe are THE END IS NEAR, and the winners get a symbolic reward in the next season. With EZPZ Server Tools, players see the countdown and the ladder in game.");
            chkSeasons = Theme.Check("Run seasons on this server", false);
            p.Add(chkSeasons, 24);
            lblSeasonStatus = Fixed(p, 48, Theme.Text);

            p.H2("Standings");
            lvStandings = SeasonList(new[] { "#", "Player", "Score" }.Concat(Ladder.CategoryNames).Concat(new[] { "Played", "Note" }).ToArray(),
                new[] { 34, 120, 56, 74, 56, 64, 72, 82, 76, 44, 60, 150 }, 260);
            p.Add(lvStandings, 260);
            lblSeasonActive = Fixed(p, 40, Theme.Muted);
            var refresh = Theme.Btn("Refresh");
            refresh.Click += (s, e) => RefreshStandings(true);
            btnSeasonAdjust = Theme.Btn("Adjust score…");
            btnSeasonAdjust.Click += (s, e) => AdjustPlayer();
            btnSeasonDq = Theme.Btn("Disqualify…");
            btnSeasonDq.Click += (s, e) => ToggleDisqualify();
            btnSeasonReset = Theme.Btn("Reset this season…", Theme.DangerBtn);
            btnSeasonReset.Click += (s, e) => ResetSeason();
            btnSeasonStart = Theme.Btn("Start Season 1 now…");
            btnSeasonStart.Click += (s, e) => StartSeasonNow();
            p.Buttons(refresh, btnSeasonAdjust, btnSeasonDq, btnSeasonReset, btnSeasonStart);
            lvStandings.SelectedIndexChanged += (s, e) => UpdateSeasonButtons();
            p.P("Every change made here is written to the audit log below, with your reason.");

            p.H2("Rules");
            var wf = new FlowLayoutPanel { Size = new Size(640, 70), BackColor = Color.Transparent, WrapContents = true };
            for (int i = 0; i < 7; i++)
            {
                wf.Controls.Add(new Label { Text = Ladder.CategoryNames[i], AutoSize = false, Size = new Size(84, 26), TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Text, Margin = new Padding(0, 0, 4, 6) });
                nWeights[i] = Theme.Num(0, 100, 0);
                nWeights[i].Width = 56;
                nWeights[i].Margin = new Padding(0, 2, 12, 6);
                nWeights[i].ValueChanged += (s, e) => UpdateWeightsTotal();
                wf.Controls.Add(nWeights[i]);
            }
            p.Add(new Label { Text = "Weights: how much each category counts toward the Survivor Score", AutoSize = true, ForeColor = Theme.Text }, 20);
            p.Add(wf, 70);
            lblWeightsTotal = Fixed(p, 40, Theme.Muted);
            nSeasonMinHours = p.Row("Hours played to qualify", Theme.Num(0, 200, 3), 70);
            nSeasonKillCap = p.Row("Kills counted per hour", Theme.Num(10, 5000, 300), 80);
            p.Hint("Kills above this many per hour played don't count, which blunts AFK zombie farms.");
            chkSeasonStaff = Theme.Check("Leave out admins, moderators and GMs", true);
            p.Add(chkSeasonStaff, 24);
            chkSeasonCheats = Theme.Check("Leave out anyone who had god mode, invisibility or no-clip this season", true);
            p.Add(chkSeasonCheats, 24);
            chkSeasonLadder = Theme.Check("Show the ladder to players during the season (otherwise only from THE END IS NEAR)", true);
            p.Add(chkSeasonLadder, 24);
            nSeasonEndHours = p.Row("THE END IS NEAR (hours)", Theme.Num(1, 72, 24), 70);
            p.Hint("How long before the season-ending wipe the countdown turns red and players are warned.");
            cbSeasonWeekday = p.Row("Weekly Discord standings", Combo(CultureInfo.InvariantCulture.DateTimeFormat.DayNames), 140);
            tbSeasonWeekly = Theme.Box("20:00");
            tbSeasonWeekly.Location = new Point(Page.X + Page.LabelW + 150, cbSeasonWeekday.Top);
            tbSeasonWeekly.Width = 60;
            p.Controls.Add(tbSeasonWeekly);
            p.Hint("Posted with the Seasons notification on the Discord page. Skipped while the ladder is hidden.");

            p.H2("Rewards");
            p.P("A symbolic item for the winners, given when they first join in the next season: a medal for the top three, and a trophy for each player holding a category title. Pick any item; the defaults are for show only.");
            string[] rewardLabels = { "1st place (The Survivor)", "2nd place", "3rd place", "Each category title" };
            for (int i = 0; i < 4; i++)
            {
                var pk = rewardPickers[i] = new IdPicker("") { Source = () => catalog.Items };
                p.Row(rewardLabels[i], pk.Box, 300);
                var name = rewardNames[i] = new Label { AutoSize = true, ForeColor = Theme.Muted, Location = new Point(pk.Box.Right + 10, pk.Box.Top + 3) };
                p.Controls.Add(name);
                pk.Box.TextChanged += (s, e) => ShowRewardNames();
            }
            var save = Theme.Btn("Save seasons settings", Theme.AccentBtn);
            save.Click += (s, e) => SaveSeasonSettings();
            p.Buttons(save);
            p.Add(new Label { Text = "Rewards so far", AutoSize = true, ForeColor = Theme.Text, Font = Theme.Bold }, 20);
            lvRewards = SeasonList(new[] { "Season", "Player", "For", "Item", "Given" }, new[] { 60, 140, 180, 220, 160 }, 140);
            p.Add(lvRewards, 140);

            p.H2("Hall of Fame");
            lvHall = SeasonList(new[] { "Season", "Dates", "The Survivor", "Titles", "Ended by" }, new[] { 60, 190, 120, 380, 120 }, 140);
            p.Add(lvHall, 140);

            p.H2("Audit log");
            lvAudit = SeasonList(new[] { "When", "Season", "Who", "What", "Before → after", "Reason" }, new[] { 120, 56, 110, 220, 160, 220 }, 140);
            p.Add(lvAudit, 140);
            p.End();

            chkSeasons.CheckedChanged += (s, e) => UpdateSeasonButtons();
            LoadSeasonControls();
        }

        ListView SeasonList(string[] cols, int[] widths, int height)
        {
            var lv = new ListView
            {
                View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Size = new Size(Page.Wide, height),
                BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9.75f), ShowItemToolTips = true,
            };
            for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], widths[i]);
            return lv;
        }

        // also called when switching servers
        void LoadSeasonControls()
        {
            if (chkSeasons == null) return;
            chkSeasons.Checked = S.SeasonsEnabled;
            var w = Ladder.Weights(S);
            for (int i = 0; i < 7; i++) nWeights[i].Value = w[i];
            nSeasonMinHours.Value = Math.Max(0, Math.Min(200, S.SeasonMinHours));
            nSeasonKillCap.Value = Math.Max(10, Math.Min(5000, S.SeasonKillCap));
            chkSeasonStaff.Checked = S.SeasonExcludeStaff;
            chkSeasonCheats.Checked = S.SeasonExcludeCheats;
            chkSeasonLadder.Checked = S.SeasonShowLadder;
            nSeasonEndHours.Value = Math.Max(1, Math.Min(72, S.SeasonEndHours));
            cbSeasonWeekday.SelectedIndex = Math.Max(0, Math.Min(6, S.SeasonWeeklyDay));
            tbSeasonWeekly.Text = S.SeasonWeeklyTime;
            var items = (S.SeasonRewards ?? "").Split(';');
            for (int i = 0; i < 4; i++) rewardPickers[i].Box.Text = i < items.Length ? items[i].Trim() : "";
            UpdateWeightsTotal();
            if (currentPage == PSeasons) ShowSeasonPage();
        }

        string WeightsText() { return string.Join(",", nWeights.Select(n => ((int)n.Value).ToString())); }
        string RewardsText() { return string.Join(";", rewardPickers.Select(r => r.Box.Text.Trim())); }

        bool SeasonsSettingsDirty()
        {
            if (chkSeasons == null) return false;
            return chkSeasons.Checked != S.SeasonsEnabled || WeightsText() != string.Join(",", Ladder.Weights(S)) || (int)nSeasonMinHours.Value != S.SeasonMinHours
                || (int)nSeasonKillCap.Value != S.SeasonKillCap || chkSeasonStaff.Checked != S.SeasonExcludeStaff || chkSeasonCheats.Checked != S.SeasonExcludeCheats
                || chkSeasonLadder.Checked != S.SeasonShowLadder || (int)nSeasonEndHours.Value != S.SeasonEndHours || cbSeasonWeekday.SelectedIndex != S.SeasonWeeklyDay
                || tbSeasonWeekly.Text.Trim() != S.SeasonWeeklyTime || RewardsText() != S.SeasonRewards;
        }

        void UpdateWeightsTotal()
        {
            int total = nWeights.Sum(n => (int)n.Value);
            lblWeightsTotal.Text = "Total " + total + (total == 100 ? "." : " (should add up to 100).")
                + " Slayer and Explorer need EZPZ Server Tools; without it they're left out and the others count for more. PvP only matters with PvP on.";
            lblWeightsTotal.ForeColor = total == 100 ? Theme.Muted : Theme.Warn;
        }

        bool SaveSeasonSettings()
        {
            if (nWeights.Sum(n => (int)n.Value) != 100) { Warn("The weights should add up to 100."); return false; }
            TimeSpan t;
            string time = tbSeasonWeekly.Text.Trim();
            if (!Regex.IsMatch(time, @"^\d{1,2}:\d\d$") || !TimeSpan.TryParse(time, CultureInfo.InvariantCulture, out t) || t.TotalHours >= 24)
            {
                Warn("The weekly post time should look like 20:00 (24-hour).");
                return false;
            }
            if (chkSeasons.Checked && !S.WipeEnabled)
            {
                Warn("Seasons run between scheduled wipes. Turn on scheduled wipes on the Reset & wipes page first.");
                return false;
            }
            bool turningOn = chkSeasons.Checked && !S.SeasonsEnabled;
            S.SeasonsEnabled = chkSeasons.Checked;
            S.SeasonWeights = WeightsText();
            S.SeasonMinHours = (int)nSeasonMinHours.Value;
            S.SeasonKillCap = (int)nSeasonKillCap.Value;
            S.SeasonExcludeStaff = chkSeasonStaff.Checked;
            S.SeasonExcludeCheats = chkSeasonCheats.Checked;
            S.SeasonShowLadder = chkSeasonLadder.Checked;
            S.SeasonEndHours = (int)nSeasonEndHours.Value;
            S.SeasonWeeklyDay = cbSeasonWeekday.SelectedIndex;
            S.SeasonWeeklyTime = time;
            S.SeasonRewards = RewardsText();
            S.Save();
            Log("[seasons] Saved the seasons settings" + (turningOn ? ": seasons are on." : "."));
            standingsAt = DateTime.MinValue;
            RefreshStandings(true);
            ShowSeasonPage();
            return true;
        }

        void ShowSeasonPage()
        {
            if (pgSeasons == null) return;
            var season = Season;
            var end = SeasonEndAt();
            if (!S.SeasonsEnabled)
            {
                lblSeasonStatus.Text = S.WipeEnabled ? "Seasons are off. Tick the box above and save to start Season 1 at the next scheduled wipe."
                    : "Seasons are off. They run between scheduled wipes, so turn those on first (Reset & wipes page).";
                lblSeasonStatus.ForeColor = Theme.Muted;
            }
            else if (season == null)
            {
                lblSeasonStatus.Text = end != null ? "Season 1 starts with the next wipe, " + end.Value.ToString("dddd d MMMM 'at' HH:mm", CultureInfo.InvariantCulture) + " (" + WipeSchedule.Until(end.Value, DateTime.Now) + "). Or start it now with the button below."
                    : "Season 1 starts with the next scheduled wipe. Scheduled wipes are off at the moment.";
                lblSeasonStatus.ForeColor = Theme.Info;
            }
            else
            {
                int day = (int)(DateTime.Now - season.StartTime).TotalDays + 1;
                lblSeasonStatus.Text = "Season " + season.Number + "  ·  day " + day + "  ·  started " + season.StartTime.ToString("d MMMM HH:mm", CultureInfo.InvariantCulture)
                    + (end != null ? "\nEnds with the wipe on " + end.Value.ToString("dddd d MMMM 'at' HH:mm", CultureInfo.InvariantCulture) + " (" + WipeSchedule.Until(end.Value, DateTime.Now) + ")" : "\nNo wipe scheduled, so the season has no end yet.")
                    + (EndIsNear() ? "  ·  THE END IS NEAR" : "");
                lblSeasonStatus.ForeColor = EndIsNear() ? Theme.Danger : Theme.Accent;
            }
            ShowStandings();
            ShowSeasonLists();
            UpdateSeasonButtons();
            if (season != null) RefreshStandings(false);
        }

        void ShowStandings()
        {
            if (lvStandings == null) return;
            var season = Season;
            string sel = lvStandings.SelectedItems.Count == 1 ? (string)lvStandings.SelectedItems[0].Tag : null;
            lvStandings.BeginUpdate();
            lvStandings.Items.Clear();
            if (season != null && standings != null)
                foreach (var st in standings)
                {
                    var cells = new List<string> { st.Qualified ? st.Rank.ToString() : "–", st.Name, st.Score.ToString("0.0") };
                    cells.AddRange(Ladder.Categories.Select(c => Ladder.Show(c, st.Raw[c])));
                    cells.Add(st.HoursPlayed.ToString("0.0") + "h");
                    SeasonPlayer sp;
                    string note = st.Note;
                    if (season.Players.TryGetValue(st.Name, out sp) && sp.Adjust != 0) note = (note == "" ? "" : note + "; ") + "adjusted " + sp.Adjust.ToString("+0.#;-0.#");
                    cells.Add(note);
                    var it = new ListViewItem(cells.ToArray()) { Tag = st.Name, ForeColor = st.Qualified ? Theme.Text : Theme.Muted };
                    it.ToolTipText = st.Name + ": " + string.Join(", ", Ladder.Categories.Select((c, i) => Ladder.CategoryNames[i] + " " + Math.Round(st.Pct[c]) + "%")) + " (percentile among qualifying players)";
                    lvStandings.Items.Add(it);
                    if (st.Name == sel) it.Selected = true;
                }
            lvStandings.EndUpdate();
            if (season == null) lblSeasonActive.Text = "";
            else
            {
                var active = Ladder.Active(S, season);
                lblSeasonActive.Text = "Counting: " + string.Join(", ", Ladder.CategoryNames.Where((c, i) => active[i])) + "."
                    + (!season.ModSeen ? " Slayer and Explorer start counting once EZPZ Server Tools reports." : "")
                    + (standings == null ? "  Working out the standings…" : "  Updated " + standingsAt.ToString("HH:mm:ss") + ". Hover a player for percentiles.");
            }
        }

        void ShowRewardNames()
        {
            for (int i = 0; i < 4; i++)
            {
                if (rewardNames[i] == null) continue;
                string id = rewardPickers[i].Box.Text.Trim();
                var c = catalog.FindItem(id);
                rewardNames[i].Text = id == "" ? "nothing" : c != null ? c.Name : catalog.Items.Count > 0 ? "not an item this server knows" : "";
                rewardNames[i].ForeColor = c != null || id == "" ? Theme.Muted : Theme.Warn;
            }
        }

        void ShowSeasonLists()
        {
            if (lvRewards == null) return;
            ShowRewardNames();
            lvRewards.BeginUpdate();
            lvRewards.Items.Clear();
            foreach (var r in seasons.Rewards.AsEnumerable().Reverse())
                lvRewards.Items.Add(new ListViewItem(new[] { r.Season.ToString(), r.Player, r.Title, ItemName(r.Item) + " (" + r.Item + ")",
                    r.Delivered != "" ? DateTime.Parse(r.Delivered, null, DateTimeStyles.RoundtripKind).ToString("d MMM HH:mm") : "waiting for them to join" })
                    { ForeColor = r.Delivered != "" ? Theme.Muted : Theme.Warn });
            lvRewards.EndUpdate();

            lvHall.BeginUpdate();
            lvHall.Items.Clear();
            foreach (var a in seasons.Archive.AsEnumerable().Reverse())
            {
                string champ;
                a.Titles.TryGetValue(Ladder.ChampionTitle, out champ);
                lvHall.Items.Add(new ListViewItem(new[] { a.Number.ToString(), a.StartTime.ToString("d MMM yyyy") + " – " + a.EndTime.ToString("d MMM yyyy"), champ ?? "(nobody)",
                    string.Join(", ", a.Titles.Where(x => x.Key != Ladder.ChampionTitle).Select(x => x.Key + ": " + x.Value)), a.EndReason })
                    { ToolTipText = string.Join("\n", a.Final.Where(x => x.Qualified).Take(10).Select(x => "#" + x.Rank + " " + x.Name + " " + x.Score.ToString("0.0"))) });
            }
            lvHall.EndUpdate();

            lvAudit.BeginUpdate();
            lvAudit.Items.Clear();
            var all = new[] { seasons.Current }.Concat(seasons.Archive).Where(x => x != null)
                .SelectMany(x => x.Audit.Select(a => new { Season = x.Number, A = a }))
                .OrderByDescending(x => x.A.At, StringComparer.Ordinal);
            foreach (var x in all)
            {
                DateTime at;
                DateTime.TryParse(x.A.At, null, DateTimeStyles.RoundtripKind, out at);
                lvAudit.Items.Add(new ListViewItem(new[] { at.ToString("d MMM HH:mm"), x.Season.ToString(), x.A.Who, x.A.What,
                    x.A.Before == "" && x.A.After == "" ? "" : x.A.Before + " → " + x.A.After, x.A.Reason }) { ToolTipText = x.A.What + "\n" + x.A.Reason });
            }
            lvAudit.EndUpdate();
        }

        void UpdateSeasonButtons()
        {
            if (btnSeasonStart == null) return;
            var season = Season;
            bool sel = lvStandings.SelectedItems.Count == 1;
            btnSeasonStart.Visible = S.SeasonsEnabled && seasons.Current == null;
            btnSeasonReset.Enabled = season != null;
            btnSeasonAdjust.Enabled = btnSeasonDq.Enabled = season != null && sel;
            SeasonPlayer sp = null;
            if (season != null && sel) season.Players.TryGetValue((string)lvStandings.SelectedItems[0].Tag, out sp);
            btnSeasonDq.Text = sp != null && sp.Disqualified ? "Reinstate…" : "Disqualify…";
        }

        string AskReason(string what)
        {
            var r = Prompt.Ask(this, "Seasons", what + "\nReason (kept in the audit log):", "", false);
            if (r == null) return null;
            if (r.Trim() == "") { Warn("Give a reason, so the audit log explains it later."); return null; }
            return r.Trim();
        }

        void StartSeasonNow()
        {
            if (!S.SeasonsEnabled || seasons.Current != null) return;
            if (!Confirm("Start Season 1 now, without a wipe?\n\nEveryone's playtime, levels and lives from now on count. Characters keep what they already have, but only what happens from now scores.")) return;
            string why = AskReason("Starting Season 1 without a wipe.");
            if (why == null) return;
            seasons.Current = new SeasonRecord { Number = 1, Start = DateTime.Now.ToString("o") };
            Audit(seasons.Current, "Started Season 1 without a wipe", null, null, why, null);
            Notify(DiscordEvent.Seasons, "🏁 Season 1 begins on " + ServerTitle(), "The ladder starts now. Survive long, learn a lot, kill plenty, see the map.", DcGreen);
            RefreshStandings(true);
            ShowSeasonPage();
        }

        void ResetSeason()
        {
            var season = Season;
            if (season == null) return;
            if (!Confirm("Reset Season " + season.Number + "?\n\nIt starts again from now: scores so far, kills and explored areas are cleared and adjustments and disqualifications are dropped. No wipe happens.")) return;
            string why = AskReason("Resetting Season " + season.Number + ".");
            if (why == null) return;
            string before = season.Start;
            season.Start = DateTime.Now.ToString("o");
            season.FreshStart = false;
            season.Players.Clear();
            season.EndNearSteps.Clear();
            standings = null;
            Audit(season, "Reset the season (it restarts from now)", before, season.Start, why, null);
            RefreshStandings(true);
            ShowSeasonPage();
        }

        void AdjustPlayer()
        {
            var season = Season;
            if (season == null || lvStandings.SelectedItems.Count != 1) return;
            string name = (string)lvStandings.SelectedItems[0].Tag;
            var sp = season.Player(name);
            var v = Prompt.Ask(this, "Adjust score", "Points to add to " + name + "'s Survivor Score (negative to take away).\nCurrent adjustment: " + sp.Adjust.ToString("+0.#;-0.#;0"), "", false);
            if (v == null) return;
            double d;
            if (!double.TryParse(v.Trim().Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out d) || d == 0 || Math.Abs(d) > 100) { Warn("Type a number of points, like 5 or -2.5."); return; }
            string why = AskReason("Adjusting " + name + " by " + d.ToString("+0.#;-0.#") + " points.");
            if (why == null) return;
            double before = sp.Adjust;
            sp.Adjust = Math.Round(sp.Adjust + d, 1);
            Audit(season, "Adjusted " + name + "'s score by " + d.ToString("+0.#;-0.#"), before.ToString("+0.#;-0.#;0"), sp.Adjust.ToString("+0.#;-0.#;0"), why, null);
            RefreshStandings(true);
            ShowSeasonLists();
        }

        void ToggleDisqualify()
        {
            var season = Season;
            if (season == null || lvStandings.SelectedItems.Count != 1) return;
            string name = (string)lvStandings.SelectedItems[0].Tag;
            var sp = season.Player(name);
            bool dq = !sp.Disqualified;
            string why = AskReason((dq ? "Disqualifying " : "Reinstating ") + name + " for Season " + season.Number + ".");
            if (why == null) return;
            sp.Disqualified = dq;
            sp.DqReason = dq ? why : "";
            Audit(season, (dq ? "Disqualified " : "Reinstated ") + name, dq ? "ranked" : "disqualified", dq ? "disqualified" : "ranked", why, null);
            RefreshStandings(true);
            ShowSeasonLists();
            UpdateSeasonButtons();
        }

        // ---------- Discord /ladder

        Dictionary<string, object> LadderEmbed(string who)
        {
            var season = Season;
            string title = "🏆 " + ServerTitle() + ": " + (season != null ? "Season " + season.Number : "Seasons");
            string text;
            if (season == null) text = S.SeasonsEnabled ? "Season 1 starts with the next wipe." : "This server doesn't run seasons.";
            else if (!LadderVisible()) text = "The ladder is secret until THE END IS NEAR. Keep surviving.";
            else if (standings == null) text = "The standings are still being worked out. Try again in a minute.";
            else
            {
                var q = standings.Where(x => x.Qualified).ToList();
                text = q.Count == 0 ? "Nobody qualifies yet (" + S.SeasonMinHours + " hours played)." : string.Join("\n", q.Take(10).Select(x => Medal(x.Rank) + " **" + x.Name + "** " + x.Score.ToString("0.0")));
                var mine = standings.FirstOrDefault(x => x.Name.Equals(who ?? "", StringComparison.OrdinalIgnoreCase));
                if (mine != null && (!mine.Qualified || mine.Rank > 10))
                    text += "\n\nYou: " + (mine.Qualified ? "#" + mine.Rank + " with " + mine.Score.ToString("0.0") : mine.Note.ToLower()) + ".";
                var end = SeasonEndAt();
                if (end != null) text += "\n\n" + (EndIsNear() ? "☠️ THE END IS NEAR: " : "Ends ") + WipeSchedule.Until(end.Value, DateTime.Now) + ".";
            }
            return DiscordWebhook.Embed(title, text, EndIsNear() ? DcRed : 0xF1C40F, "ezpz · " + S.ServerName);
        }
    }
}
