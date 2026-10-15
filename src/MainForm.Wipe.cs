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
            S.Save();
            var next = WipeSchedule.Next(S, DateTime.Now);
            Log("[ezpz] Saved the wipe schedule: " + (next == null ? "off." : WipeSchedule.Presets[S.WipePreset].ToLower() + ", next " + next.Value.ToString("dddd d MMMM HH:mm", CultureInfo.InvariantCulture) + "."));
            UpdateWipeLabel();
            return true;
        }

        void UpdateWipeLabel()
        {
            if (lblWipeNext == null) return;
            var next = WipeSchedule.Next(S, DateTime.Now);
            if (next == null) { lblWipeNext.Text = "Scheduled wipes are off."; lblWipeNext.ForeColor = Theme.Muted; return; }
            lblWipeNext.Text = "Next wipe: " + next.Value.ToString("dddd d MMMM 'at' HH:mm", CultureInfo.InvariantCulture) + " (" + WipeSchedule.Until(next.Value, DateTime.Now) + "), "
                + WipeSchedule.Presets[S.WipePreset].ToLower() + ". " + (S.WipeStartAfter ? "The server starts again afterwards." : "The server stays stopped afterwards.")
                + (WipeSchedule.Last(S) != DateTime.MinValue ? "\nLast scheduled wipe: " + WipeSchedule.Last(S).ToString("d MMMM yyyy HH:mm", CultureInfo.InvariantCulture) + "." : "");
            lblWipeNext.ForeColor = Theme.Warn;
        }

        // "Next wipe: Saturday 1 November" for status messages; null when off
        string NextWipeText()
        {
            var next = WipeSchedule.Next(S, DateTime.Now);
            return next == null ? null : next.Value.ToString("dddd d MMMM, HH:mm", CultureInfo.InvariantCulture) + " (" + WipeSchedule.Until(next.Value, DateTime.Now) + ")";
        }

        // ---------- running it (called every second)

        void WipeTick()
        {
            if (wipeRunning || !S.WipeEnabled) return;
            var next = WipeSchedule.Next(S, DateTime.Now.AddSeconds(-1));
            if (next == null) return;
            double left = (next.Value - DateTime.Now).TotalMinutes;
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

        async Task RunScheduledWipe(DateTime due)
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
                    if (server.State != ServerState.Stopped) { WipeFailed(due, "the server didn't stop", false); return; }
                }
                if (!await BackupFlow("pre-reset", true)) { WipeFailed(due, "the backup failed", wasRunning); return; }
                await FinalizeSeason(due, "scheduled wipe");
                if (!BeginBusy("Wiping…")) { WipeFailed(due, "ezpz was busy", wasRunning); return; }
                try
                {
                    var plan = WipeSchedule.Plan(S);
                    await Task.Run(() => plan.Execute(S, Log));
                    Log("[reset] Scheduled wipe done.");
                }
                catch (Exception ex)
                {
                    AppLog.Error("the scheduled wipe", ex);
                    WipeFailed(due, "deleting files failed partway (" + ex.Message + "). The pre-wipe backup can be restored from the Backups page", false);
                    return;
                }
                finally { EndBusy(); }
                S.WipeLast = due.ToString("o");
                S.Save();
                UpdateWipeLabel();
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
        void WipeFailed(DateTime due, string why, bool restart)
        {
            S.WipeLast = due.ToString("o");
            S.Save();
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
