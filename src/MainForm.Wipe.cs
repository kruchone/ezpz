using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // Scheduled wipes: reset the server on a schedule (monthly, or every few weeks), with warnings in game and
    // on Discord, a backup first, and the server started again afterwards.
    public partial class MainForm
    {
        CheckBox chkWipe, chkWipeStart;
        ComboBox cbWipeRepeat, cbWipeWeekday, cbWipePreset;
        NumericUpDown nWipeDay, nWipeWeeks;
        TextBox tbWipeTime;
        Label lblWipeNext;
        Panel pnlWipeMonthly, pnlWipeWeekly;
        bool wipeRunning, wipeAnnounceOnline;

        // Warnings before a wipe: (minutes before, in game?, on Discord?)
        static readonly int[][] WipeWarnings =
        {
            new[] { 3 * 24 * 60, 0, 1 }, new[] { 24 * 60, 1, 1 }, new[] { 60, 1, 1 }, new[] { 15, 1, 0 }, new[] { 5, 1, 0 }, new[] { 1, 1, 0 },
        };

        void BuildWipeSection(Page p)
        {
            p.H2("Scheduled wipes");
            p.P("Reset this server automatically on a schedule, for example a fresh world every month. Players are warned in game beforehand, and on Discord if that's turned on. A backup is always made first; if it fails, the wipe is cancelled.");
            chkWipe = Theme.Check("Wipe this server on a schedule", false);
            p.Add(chkWipe, 24);

            cbWipeRepeat = p.Row("Repeat", Combo(new[] { "Every month", "Every few weeks" }), 200);
            int rowY = p.Y;
            pnlWipeMonthly = new Panel { Location = new Point(Page.X, rowY), Size = new Size(Page.Wide, 32), BackColor = Color.Transparent };
            pnlWipeMonthly.Controls.Add(new Label { Text = "On day of the month", Location = new Point(0, 3), Size = new Size(Page.LabelW - 8, 24), ForeColor = Theme.Text });
            nWipeDay = Theme.Num(1, 28, 1); nWipeDay.Location = new Point(Page.LabelW, 0); nWipeDay.Width = 70;
            pnlWipeMonthly.Controls.Add(nWipeDay);
            pnlWipeMonthly.Controls.Add(new Label { Text = "(1–28, so every month has it)", Location = new Point(Page.LabelW + 80, 3), AutoSize = true, ForeColor = Theme.Muted });
            pnlWipeWeekly = new Panel { Location = new Point(Page.X, rowY), Size = new Size(Page.Wide, 32), BackColor = Color.Transparent };
            pnlWipeWeekly.Controls.Add(new Label { Text = "Every", Location = new Point(0, 3), Size = new Size(Page.LabelW - 8, 24), ForeColor = Theme.Text });
            nWipeWeeks = Theme.Num(1, 12, 2); nWipeWeeks.Location = new Point(Page.LabelW, 0); nWipeWeeks.Width = 60;
            pnlWipeWeekly.Controls.Add(nWipeWeeks);
            pnlWipeWeekly.Controls.Add(new Label { Text = "weeks, on", Location = new Point(Page.LabelW + 68, 3), AutoSize = true, ForeColor = Theme.Text });
            cbWipeWeekday = Combo(CultureInfo.InvariantCulture.DateTimeFormat.DayNames);
            cbWipeWeekday.Location = new Point(Page.LabelW + 140, 0); cbWipeWeekday.Width = 140;
            pnlWipeWeekly.Controls.Add(cbWipeWeekday);
            p.Controls.Add(pnlWipeMonthly);
            p.Controls.Add(pnlWipeWeekly);
            p.Y += 40;
            tbWipeTime = p.Row("At (24-hour time)", Theme.Box("18:00"), 70);
            cbWipePreset = p.Row("Wipe", Combo(WipeSchedule.Presets), 260);
            p.Hint("New world keeps settings, mods, accounts, admins, whitelist and bans. See the presets below for exactly what each deletes.");
            chkWipeStart = Theme.Check("Start the server after the wipe", true);
            p.Add(chkWipeStart, 24);
            var save = Theme.Btn("Save schedule", Theme.AccentBtn);
            save.Click += (s, e) => SaveWipeSchedule();
            p.Buttons(save);
            lblWipeNext = Fixed(p, 44, Theme.Muted);

            cbWipeRepeat.SelectedIndexChanged += (s, e) => ShowWipeRepeat();
            LoadWipeControls();
        }

        ComboBox Combo(string[] items)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.Input, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat };
            c.Items.AddRange(items);
            c.SelectedIndex = 0;
            return c;
        }

        void ShowWipeRepeat()
        {
            pnlWipeMonthly.Visible = cbWipeRepeat.SelectedIndex == 0;
            pnlWipeWeekly.Visible = cbWipeRepeat.SelectedIndex == 1;
        }

        // also called when switching servers
        void LoadWipeControls()
        {
            if (chkWipe == null) return;
            chkWipe.Checked = S.WipeEnabled;
            cbWipeRepeat.SelectedIndex = Math.Max(0, Math.Min(1, S.WipeRepeat));
            nWipeDay.Value = Math.Max(1, Math.Min(28, S.WipeDay));
            nWipeWeeks.Value = Math.Max(1, Math.Min(12, S.WipeWeeks));
            cbWipeWeekday.SelectedIndex = Math.Max(0, Math.Min(6, S.WipeWeekday));
            tbWipeTime.Text = S.WipeTime;
            cbWipePreset.SelectedIndex = Math.Max(0, Math.Min(2, S.WipePreset));
            chkWipeStart.Checked = S.WipeStartAfter;
            ShowWipeRepeat();
            UpdateWipeLabel();
        }

        bool WipeDirty()
        {
            if (chkWipe == null) return false;
            return chkWipe.Checked != S.WipeEnabled || cbWipeRepeat.SelectedIndex != S.WipeRepeat || (int)nWipeDay.Value != S.WipeDay
                || (int)nWipeWeeks.Value != S.WipeWeeks || cbWipeWeekday.SelectedIndex != S.WipeWeekday || tbWipeTime.Text.Trim() != S.WipeTime
                || cbWipePreset.SelectedIndex != S.WipePreset || chkWipeStart.Checked != S.WipeStartAfter;
        }

        bool SaveWipeSchedule()
        {
            TimeSpan t;
            string time = tbWipeTime.Text.Trim();
            if (!Regex.IsMatch(time, @"^\d{1,2}:\d\d$") || !TimeSpan.TryParse(time, CultureInfo.InvariantCulture, out t) || t.TotalHours >= 24)
            {
                Warn("The wipe time should look like 18:00 (24-hour).");
                return false;
            }
            if (chkWipe.Checked && cbWipePreset.SelectedIndex == 2 && S.AdminPassword == "")
            {
                Warn("Wiping accounts recreates the admin account from your admin password. Set one on the Setup page first.");
                return false;
            }
            bool turningOn = chkWipe.Checked && !S.WipeEnabled;
            S.WipeEnabled = chkWipe.Checked;
            S.WipeRepeat = cbWipeRepeat.SelectedIndex;
            S.WipeDay = (int)nWipeDay.Value;
            S.WipeWeeks = (int)nWipeWeeks.Value;
            S.WipeWeekday = cbWipeWeekday.SelectedIndex;
            S.WipeTime = time;
            S.WipePreset = cbWipePreset.SelectedIndex;
            S.WipeStartAfter = chkWipeStart.Checked;
            if (turningOn) S.WipeLast = "";   // a fresh schedule counts from now
            var next = WipeSchedule.Next(S, DateTime.Now);
            S.WipeDue = next == null ? "" : next.Value.ToString("o");   // saving the schedule also settles a missed wipe
            S.Save();
            Log("[ezpz] Saved the wipe schedule: " + (next == null ? "off." : WipeSchedule.Presets[S.WipePreset].ToLower() + ", next " + next.Value.ToString("dddd d MMMM HH:mm", CultureInfo.InvariantCulture) + "."));
            UpdateWipeLabel();
            return true;
        }

        void UpdateWipeLabel()
        {
            if (lblWipeNext == null) return;
            var missed = MissedWipe();
            if (missed != null)
            {
                lblWipeNext.Text = "The wipe due " + missed.Value.ToString("dddd d MMMM 'at' HH:mm", CultureInfo.InvariantCulture) + " didn't happen because ezpz wasn't running (or was busy).\nChoose what to do on the Home page: wipe now, or skip to the next one.";
                lblWipeNext.ForeColor = Theme.Danger;
                return;
            }
            var next = UpcomingWipe();
            if (next == null) { lblWipeNext.Text = "Scheduled wipes are off."; lblWipeNext.ForeColor = Theme.Muted; return; }
            lblWipeNext.Text = "Next wipe: " + next.Value.ToString("dddd d MMMM 'at' HH:mm", CultureInfo.InvariantCulture) + " (" + WipeSchedule.Until(next.Value, DateTime.Now) + "), "
                + WipeSchedule.Presets[S.WipePreset].ToLower() + ". " + (S.WipeStartAfter ? "The server starts again afterwards." : "The server stays stopped afterwards.")
                + (WipeSchedule.Last(S) != DateTime.MinValue ? "\nLast scheduled wipe: " + WipeSchedule.Last(S).ToString("d MMMM yyyy HH:mm", CultureInfo.InvariantCulture) + "." : "");
            lblWipeNext.ForeColor = Theme.Warn;
        }

        // "Next wipe: Saturday 1 November" for status messages; null when off
        string NextWipeText()
        {
            var next = UpcomingWipe();
            return next == null ? null : next.Value.ToString("dddd d MMMM, HH:mm", CultureInfo.InvariantCulture) + " (" + WipeSchedule.Until(next.Value, DateTime.Now) + ")";
        }

        // ---------- running it (called every second)

        // The wipe ezpz is waiting for is kept in WipeDue, so a wipe that passed while ezpz was closed (or busy)
        // is noticed instead of silently moving on to the next date. A missed wipe waits for an admin's choice.
        const int WipeGraceMinutes = 2;

        DateTime? MissedWipe()
        {
            if (!S.WipeEnabled || wipeRunning) return null;
            var due = WipeSchedule.Due(S);
            return due != DateTime.MinValue && (DateTime.Now - due).TotalMinutes >= WipeGraceMinutes ? due : (DateTime?)null;
        }

        // The next wipe that will actually happen on its own: null when wipes are off or one was missed.
        DateTime? UpcomingWipe()
        {
            if (!S.WipeEnabled || MissedWipe() != null) return null;
            var due = WipeSchedule.Due(S);
            return due != DateTime.MinValue ? due : WipeSchedule.Next(S, DateTime.Now);
        }

        void WipeTick()
        {
            if (wipeRunning || !S.WipeEnabled) return;
            var due = WipeSchedule.Due(S);
            if (due == DateTime.MinValue)
            {
                var first = WipeSchedule.Next(S, DateTime.Now);
                if (first == null) return;
                S.WipeDue = first.Value.ToString("o");
                S.Save();
                return;
            }
            double left = (due - DateTime.Now).TotalMinutes;
            if (left <= -WipeGraceMinutes) { WipeMissedTick(due); return; }
            var next = (DateTime?)due;
            if (ticks % 60 == 0 && currentPage == PReset) UpdateWipeLabel();

            string key = next.Value.ToString("o");
            foreach (var w in WipeWarnings)
            {
                if (left > w[0] || left <= w[0] - 1 || dstate.WipeWarned.Contains(key + "|" + w[0])) continue;
                dstate.WipeWarned.Add(key + "|" + w[0]);
                if (dstate.WipeWarned.Count > 40) dstate.WipeWarned.RemoveRange(0, dstate.WipeWarned.Count - 40);
                SaveDiscordState();
                string when = w[0] >= 24 * 60 ? (w[0] / (24 * 60)) + " day" + (w[0] >= 48 * 60 ? "s" : "") : w[0] >= 60 ? (w[0] / 60) + " hour" + (w[0] >= 120 ? "s" : "") : w[0] + " minute" + (w[0] == 1 ? "" : "s");
                if (w[1] == 1 && server.State == ServerState.Running)
                    Cmd("servermsg \"Server wipe in " + when + ". The world starts fresh after that.\"");
                if (w[2] == 1)
                    Notify(DiscordEvent.Wipes, "🧹 " + ServerTitle() + " wipes in " + when,
                        WipeSchedule.Presets[S.WipePreset] + " on " + next.Value.ToString("dddd d MMMM 'at' HH:mm", CultureInfo.InvariantCulture) + ".", DcAmber);
                Log("[ezpz] Wipe warning: " + when + " to go.");
            }
            if (left <= 0 && busy == null) { var _ = RunScheduledWipe(next.Value); }
        }

        // Once per missed wipe: tell the admins. The Home page asks what to do.
        void WipeMissedTick(DateTime due)
        {
            string key = due.ToString("o") + "|missed";
            if (dstate.WipeWarned.Contains(key)) return;
            dstate.WipeWarned.Add(key);
            SaveDiscordState();
            Log("[ezpz] The scheduled wipe due " + due.ToString("dddd d MMMM HH:mm", CultureInfo.InvariantCulture) + " didn't happen (ezpz wasn't running or was busy). Choose on the Home page: wipe now or skip it.");
            Notify(DiscordEvent.Wipes, "⏸️ " + ServerTitle() + ": scheduled wipe missed",
                "The wipe due " + due.ToString("dddd d MMMM 'at' HH:mm", CultureInfo.InvariantCulture) + " didn't happen because ezpz wasn't running. An admin will decide whether to wipe now or wait for the next one.", DcAmber);
            UpdateWipeLabel();
            RefreshHome();
        }

        async Task WipeMissedNow()
        {
            var due = MissedWipe();
            if (due == null) return;
            int online = server.State == ServerState.Running ? onlineNow.Count : 0;
            if (!Confirm("Wipe the server now?\n\n" + WipeSchedule.Presets[S.WipePreset] + ". A backup is made first." + (online > 0 ? "\n" + online + " player" + (online == 1 ? " is" : "s are") + " online and will be disconnected." : "")
                + (Season != null ? "\n\nThis ends Season " + Season.Number + "." : ""))) return;
            Log("[ezpz] Running the missed wipe now (chosen by " + Environment.UserName + ").");
            await RunScheduledWipe(DateTime.Now, due.Value);
        }

        void WipeMissedSkip()
        {
            var due = MissedWipe();
            if (due == null) return;
            var next = WipeSchedule.Next(S, DateTime.Now);
            if (!Confirm("Skip the missed wipe?\n\nNothing is deleted. " + (next != null ? "The next scheduled wipe is " + next.Value.ToString("dddd d MMMM 'at' HH:mm", CultureInfo.InvariantCulture) + "." : "")
                + (Season != null ? " Season " + Season.Number + " keeps running until then." : ""))) return;
            S.WipeLast = due.Value.ToString("o");
            S.WipeDue = next == null ? "" : next.Value.ToString("o");
            S.Save();
            Log("[ezpz] Skipped the missed wipe due " + due.Value.ToString("d MMMM HH:mm", CultureInfo.InvariantCulture) + " (chosen by " + Environment.UserName + ")" + (next != null ? ". Next: " + next.Value.ToString("d MMMM HH:mm", CultureInfo.InvariantCulture) : "") + ".");
            Notify(DiscordEvent.Wipes, "⏭️ " + ServerTitle() + ": missed wipe skipped",
                next != null ? "The next wipe is " + next.Value.ToString("dddd d MMMM 'at' HH:mm", CultureInfo.InvariantCulture) + "." : "No more wipes are scheduled.", DcBlue);
            UpdateWipeLabel();
            RefreshHome();
        }

        Task RunScheduledWipe(DateTime due) { return RunScheduledWipe(due, due); }

        // due: when it runs (the season ends then); slot: the schedule entry it settles.
        async Task RunScheduledWipe(DateTime due, DateTime slot)
        {
            wipeRunning = true;
            bool wasRunning = server.State != ServerState.Stopped;
            try
            {
                Log("[reset] Scheduled wipe starting (" + WipeSchedule.Presets[S.WipePreset].ToLower() + ").");
                Notify(DiscordEvent.Wipes, "🧹 " + ServerTitle() + " is being wiped", "Back soon with a fresh world.", DcAmber);
                if (wasRunning)
                {
                    Cmd("servermsg \"Server wipe now. See you in the new world!\"");
                    await Task.Delay(3000);
                    await StopFlow();
                    if (server.State != ServerState.Stopped) { WipeFailed(slot, "the server didn't stop", false); return; }
                }
                if (!await BackupFlow("pre-reset", true)) { WipeFailed(slot, "the backup failed", wasRunning); return; }
                await FinalizeSeason(due, "scheduled wipe");
                if (!BeginBusy("Wiping…")) { WipeFailed(slot, "ezpz was busy", wasRunning); return; }
                try
                {
                    var plan = WipeSchedule.Plan(S);
                    await Task.Run(() => plan.Execute(S, Log));
                    Log("[reset] Scheduled wipe done.");
                }
                catch (Exception ex)
                {
                    AppLog.Error("the scheduled wipe", ex);
                    WipeFailed(slot, "deleting files failed partway (" + ex.Message + "). The pre-wipe backup can be restored from the Backups page", false);
                    return;
                }
                finally { EndBusy(); }
                SettleWipe(slot);
                RefreshChecklist();
                if (S.WipeStartAfter)
                {
                    wipeAnnounceOnline = true;
                    await StartFlow();
                }
                else Notify(DiscordEvent.Wipes, "🌱 " + ServerTitle() + " has been wiped", "It starts with a fresh world next time an admin starts it.", DcGreen);
            }
            finally { wipeRunning = false; }
        }

        // Skip this occurrence (so it doesn't retry in a loop), tell the admins, and bring the server back if it was up.
        // Marks a schedule entry as done (or given up on) and moves on to the next one.
        void SettleWipe(DateTime slot)
        {
            S.WipeLast = slot.ToString("o");
            var next = WipeSchedule.Next(S, DateTime.Now);
            S.WipeDue = next == null ? "" : next.Value.ToString("o");
            S.Save();
            UpdateWipeLabel();
        }

        void WipeFailed(DateTime due, string why, bool restart)
        {
            SettleWipe(due);
            Log("[reset] Scheduled wipe cancelled: " + why + ".");
            Notify(DiscordEvent.Wipes, "⚠️ " + ServerTitle() + ": wipe cancelled", "The scheduled wipe didn't happen because " + why + ". An admin should check ezpz.", DcRed);
            UpdateWipeLabel();
            if (restart) { var _ = StartFlow(); }
        }

        // after the server comes up following a wipe
        void WipeOnState(ServerState st)
        {
            if (st == ServerState.Running && wipeAnnounceOnline)
            {
                wipeAnnounceOnline = false;
                Notify(DiscordEvent.Wipes, "🌱 A fresh world is up on " + ServerTitle(), "The wipe is done. Come and make it to day one.", DcGreen);
            }
            if (st == ServerState.Stopped && !wipeRunning) wipeAnnounceOnline = false;
        }
    }
}
