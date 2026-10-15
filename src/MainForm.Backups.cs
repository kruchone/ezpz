using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // Backups page: choose who makes backups (ezpz, Project Zomboid's built-in system, or both), set each
    // side's options, and see / restore / delete all of them in one list.
    public partial class MainForm
    {
        ListView lvBackups;
        RadioButton rbBkEzpz, rbBkPz, rbBkBoth;
        NumericUpDown numBackupEveryHours, numPzPeriod, numPzCount;
        CheckBox chkPzOnStart, chkPzOnVersion;
        Label lblPzNote;
        Panel grpEzpz, grpPz;
        bool loadingBackupSettings;
        DateTime lastBackupAt = DateTime.MinValue;

        void BuildBackups()
        {
            var p = pgBackups = NewPage();
            p.H1("Backups");
            p.P("A backup is a .zip of this server's world, its account database and its config. Whatever you choose below, ezpz always makes a safety backup before a reset or a restore.");

            p.H2("Who makes backups");
            rbBkEzpz = BackupModeRadio(p, "ezpz (recommended)", "ezpz backs up when the server starts, and optionally every few hours while it runs. Project Zomboid's own start and timed backups are switched off, so the world isn't copied twice.");
            rbBkPz = BackupModeRadio(p, "Project Zomboid's built-in backups", "The game backs up on start, on a timer and when the game version changes, keeping the last few of each in the data folder. ezpz's own automatic backups are switched off.");
            rbBkBoth = BackupModeRadio(p, "Both, for redundancy", "Two independent copies. Uses twice the disk space and a little longer to start.");

            p.H2("ezpz backups");
            grpEzpz = new Panel { Size = new Size(Page.Wide, 120), BackColor = Theme.Bg };
            p.Add(grpEzpz, 120);
            int gy = 0;
            chkBackupOnStart = Theme.Check("Back up when the server starts", S.BackupOnStart);
            chkBackupOnStart.Location = new Point(0, gy); grpEzpz.Controls.Add(chkBackupOnStart); gy += 30;
            numBackupEveryHours = GroupNum(grpEzpz, ref gy, "Also every N hours while it runs (0 = off)", 0, 48, S.BackupEveryHours);
            numKeepBackups = GroupNum(grpEzpz, ref gy, "Keep this many automatic backups", 1, 500, S.KeepAutoBackups);
            grpEzpz.Controls.Add(new Label { Text = "Manual, pre-reset and pre-restore backups are never deleted automatically.", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(0, gy + 2) });

            p.H2("Project Zomboid's backups");
            grpPz = new Panel { Size = new Size(Page.Wide, 150), BackColor = Theme.Bg };
            p.Add(grpPz, 150);
            gy = 0;
            chkPzOnStart = Theme.Check("Back up when the server starts", true);
            chkPzOnStart.Location = new Point(0, gy); grpPz.Controls.Add(chkPzOnStart); gy += 30;
            numPzPeriod = GroupNum(grpPz, ref gy, "Also every N minutes while it runs (0 = off)", 0, 1500, 0);
            chkPzOnVersion = Theme.Check("Back up the config when the game version changes", true);
            chkPzOnVersion.Location = new Point(0, gy); grpPz.Controls.Add(chkPzOnVersion); gy += 30;
            numPzCount = GroupNum(grpPz, ref gy, "Keep this many of each kind", 1, 300, 5);
            lblPzNote = new Label { ForeColor = Theme.Muted, AutoSize = true, MaximumSize = new Size(Page.Wide, 0), Location = new Point(0, gy + 2) };
            grpPz.Controls.Add(lblPzNote);

            foreach (Control c in new Control[] { chkBackupOnStart, numBackupEveryHours, numKeepBackups, chkPzOnStart, numPzPeriod, chkPzOnVersion, numPzCount })
            {
                if (c is CheckBox) ((CheckBox)c).CheckedChanged += (s, e) => SaveBackupSettings();
                else ((NumericUpDown)c).ValueChanged += (s, e) => SaveBackupSettings();
            }

            p.H2("Your backups");
            var now = Theme.Btn("Back up now", Theme.AccentBtn);
            now.Click += async (s, e) => await BackupFlow("manual");
            var restore = Theme.Btn("Restore selected…");
            restore.Click += async (s, e) => await RestoreFlow();
            var del = Theme.Btn("Delete selected", Theme.DangerBtn);
            del.Click += (s, e) => DeleteBackups();
            var openE = Theme.Btn("Open ezpz backups");
            openE.Click += (s, e) => OpenFolder(S.BackupDir);
            var openP = Theme.Btn("Open PZ backups");
            openP.Click += (s, e) => OpenFolder(Path.Combine(S.DataDir, "backups"));
            var refresh = Theme.Btn("Refresh");
            refresh.Click += (s, e) => RefreshBackupList();
            p.Buttons(now, restore, del, openE, openP, refresh);

            lvBackups = new ListView
            {
                View = View.Details, FullRowSelect = true, Size = new Size(Page.Wide, 360), BackColor = Theme.Input,
                ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, HideSelection = false
            };
            lvBackups.Columns.Add("When", 150);
            lvBackups.Columns.Add("Made by", 80);
            lvBackups.Columns.Add("Kind", 120);
            lvBackups.Columns.Add("Contains", 170);
            lvBackups.Columns.Add("World ver.", 70);
            lvBackups.Columns.Add("Size", 80);
            lvBackups.Columns.Add("File", 210);
            lvBackups.DoubleClick += async (s, e) => await RestoreFlow();
            p.Add(lvBackups, 360);
            p.P("Double-click a backup to restore it. Restoring only replaces what the backup contains.");
            p.End();
        }

        RadioButton BackupModeRadio(Page p, string title, string desc)
        {
            var r = new RadioButton { Text = title, AutoSize = true, Font = Theme.Bold, ForeColor = Theme.Text };
            p.Add(r, 20);
            var d = new Label { Text = desc, AutoSize = true, MaximumSize = new Size(Page.Wide - 24, 0), ForeColor = Theme.Muted, Location = new Point(Page.X + 20, p.Y - 6) };
            p.Controls.Add(d);
            p.Y += d.GetPreferredSize(new Size(Page.Wide - 24, 0)).Height + 6;
            r.CheckedChanged += (s, e) => { if (r.Checked) OnBackupModeChosen(); };
            return r;
        }

        static NumericUpDown GroupNum(Panel g, ref int y, string label, int min, int max, int value)
        {
            g.Controls.Add(new Label { Text = label, AutoSize = false, Size = new Size(320, 22), Location = new Point(0, y + 3), ForeColor = Theme.Text });
            var n = Theme.Num(min, max, value);
            n.Width = 80;
            n.Location = new Point(330, y);
            g.Controls.Add(n);
            y += 32;
            return n;
        }

        int CurrentBackupMode { get { return rbBkPz.Checked ? 1 : rbBkBoth.Checked ? 2 : 0; } }

        // Shows the saved settings (ezpz's own, and PZ's from the server .ini).
        void LoadBackupSettings()
        {
            if (rbBkEzpz == null) return;
            loadingBackupSettings = true;
            try
            {
                var ini = IniFile.Load(S.IniPath);
                bool hasIni = File.Exists(S.IniPath);
                chkPzOnStart.Checked = ini.Get("BackupsOnStart", "true").Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                chkPzOnVersion.Checked = ini.Get("BackupsOnVersionChange", "true").Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                numPzPeriod.Value = Clamp(numPzPeriod, ini.Get("BackupsPeriod", "0"));
                numPzCount.Value = Clamp(numPzCount, ini.Get("BackupsCount", "5"));
                chkBackupOnStart.Checked = S.BackupOnStart;
                numBackupEveryHours.Value = Math.Max(0, Math.Min(48, S.BackupEveryHours));
                numKeepBackups.Value = Math.Max(1, Math.Min(500, S.KeepAutoBackups));

                int mode = S.BackupMode;
                if (mode < 0)   // not chosen yet: describe what's actually on
                {
                    bool pz = chkPzOnStart.Checked || numPzPeriod.Value > 0, ez = S.BackupOnStart || S.BackupEveryHours > 0;
                    mode = pz && ez ? 2 : pz ? 1 : 0;
                }
                (mode == 1 ? rbBkPz : mode == 2 ? rbBkBoth : rbBkEzpz).Checked = true;
                ApplyBackupModeEnables();
                lblPzNote.Text = hasIni
                    ? "Saved in the server's config. Project Zomboid applies these the next time the server starts. Its backups are kept in " + Path.Combine(S.DataDir, "backups") + "."
                    : "There's no server config yet, so these can't be saved until you generate it on the Setup page.";
            }
            finally { loadingBackupSettings = false; }
        }

        static decimal Clamp(NumericUpDown n, string v)
        {
            decimal d;
            if (!decimal.TryParse(v.Trim(), out d)) d = n.Minimum;
            return Math.Max(n.Minimum, Math.Min(n.Maximum, d));
        }

        // Picking a mode switches the relevant "on start" backups on or off; the rest stays as you set it.
        void OnBackupModeChosen()
        {
            if (loadingBackupSettings) return;
            loadingBackupSettings = true;
            try
            {
                int mode = CurrentBackupMode;
                bool ez = mode != 1, pz = mode != 0;
                chkBackupOnStart.Checked = ez;
                if (!ez) numBackupEveryHours.Value = 0;
                chkPzOnStart.Checked = pz;
                if (!pz) numPzPeriod.Value = 0;   // the game-version config backup stays on: it's tiny and rare
            }
            finally { loadingBackupSettings = false; }
            ApplyBackupModeEnables();
            SaveBackupSettings();
        }

        void ApplyBackupModeEnables()
        {
            int mode = CurrentBackupMode;
            grpEzpz.Enabled = mode != 1;
            grpPz.Enabled = mode != 0 && File.Exists(S.IniPath);
        }

        void SaveBackupSettings()
        {
            if (loadingBackupSettings || rbBkEzpz == null) return;
            S.BackupMode = CurrentBackupMode;
            S.BackupOnStart = chkBackupOnStart.Checked;
            S.BackupEveryHours = (int)numBackupEveryHours.Value;
            S.KeepAutoBackups = (int)numKeepBackups.Value;
            S.Save();
            if (File.Exists(S.IniPath))
            {
                var ini = IniFile.Load(S.IniPath);
                ini.Set("BackupsOnStart", chkPzOnStart.Checked ? "true" : "false");
                ini.Set("BackupsOnVersionChange", chkPzOnVersion.Checked ? "true" : "false");
                ini.Set("BackupsPeriod", ((int)numPzPeriod.Value).ToString());
                ini.Set("BackupsCount", ((int)numPzCount.Value).ToString());
                ini.Save();
            }
        }

        // ezpz's "every N hours while it runs" backups. Called every second.
        void BackupTick()
        {
            if (S.BackupEveryHours <= 0 || S.BackupMode == 1 || server.State != ServerState.Running || busy != null) return;
            DateTime since = lastBackupAt > server.StartedAt ? lastBackupAt : server.StartedAt;
            if ((DateTime.Now - since).TotalHours >= S.BackupEveryHours)
            {
                lastBackupAt = DateTime.Now;   // set first so a slow backup doesn't trigger twice
                Log("[backup] Scheduled backup (every " + S.BackupEveryHours + " h).");
                var _ = BackupFlow("auto");
            }
        }

        void RefreshBackupList()
        {
            if (lvBackups == null) return;
            List<Backups.BackupInfo> list;
            try { list = Backups.ListAll(S); }
            catch (Exception ex) { Log("[backup] Couldn't list backups: " + ex.Message); list = new List<Backups.BackupInfo>(); }
            lvBackups.BeginUpdate();
            lvBackups.Items.Clear();
            foreach (var b in list)
            {
                var it = new ListViewItem(new[]
                {
                    b.Created.ToString("yyyy-MM-dd HH:mm"), b.ByPZ ? "PZ" : "ezpz", b.Kind, b.Contents, b.WorldVersion, Util.Size(b.Size), Path.GetFileName(b.Path)
                });
                it.Tag = b;
                if (b.ByPZ) it.ForeColor = Theme.Info;
                lvBackups.Items.Add(it);
            }
            lvBackups.EndUpdate();
            LoadBackupSettings();
        }

        async Task<bool> BackupFlow(string reason, bool quiet = false)
        {
            if (!BeginBusy("Backing up…")) return false;
            try
            {
                if (server.State == ServerState.Running)
                {
                    Log("[backup] Server is running. Saving the world first...");
                    try { server.SendCommand("save"); } catch { }
                    await Task.Delay(8000);
                }
                await Task.Run(() => Backups.Create(S, reason, Log));
                if (reason == "auto") Backups.PruneAuto(S, Log);
                lastBackupAt = DateTime.Now;
                return true;
            }
            catch (Exception ex) { Log("[backup] ERROR: " + ex.Message); if (!quiet) Warn("Backup failed: " + ex.Message); return false; }
            finally { EndBusy(); if (currentPage == PBackups) RefreshBackupList(); }
        }

        async Task RestoreFlow()
        {
            if (lvBackups.SelectedItems.Count != 1) { Warn("Select one backup to restore."); return; }
            var b = (Backups.BackupInfo)lvBackups.SelectedItems[0].Tag;
            string what = b.Contents;
            if (!Confirm("Restore the " + (b.ByPZ ? "Project Zomboid" : "ezpz") + " " + b.Kind + " backup from " + b.Created.ToString("yyyy-MM-dd HH:mm") + "?\n\n"
                + "It contains: " + what + ". Those parts of '" + S.ServerName + "' will be replaced; anything else is left alone.\n"
                + (b.HasWorld ? "Players lose anything they did in the world after this backup.\n" : "")
                + "\nezpz makes a 'pre-restore' backup of the current state first, so you can undo this.")) return;
            if (!await EnsureStopped("restore")) return;
            if (!await BackupFlow("pre-restore")) return;
            if (!BeginBusy("Restoring…")) return;
            try { await Task.Run(() => Backups.Restore(S, b.Path, Log)); Info("Restore complete. Start the server when you're ready."); }
            catch (Exception ex) { Log("[backup] ERROR: " + ex.Message); Warn(ex.Message); }
            finally { EndBusy(); RefreshBackupList(); }
        }

        void DeleteBackups()
        {
            if (lvBackups.SelectedItems.Count == 0) return;
            if (!Confirm("Move " + lvBackups.SelectedItems.Count + " backup(s) to the Recycle Bin?")) return;
            foreach (ListViewItem it in lvBackups.SelectedItems)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(((Backups.BackupInfo)it.Tag).Path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                catch (Exception ex) { Log("[backup] " + ex.Message); }
            }
            RefreshBackupList();
        }
    }
}
