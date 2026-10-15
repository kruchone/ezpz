using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // Several servers ("profiles"), one running at a time. The header's server name opens the switcher.
    public partial class MainForm
    {
        CheckBox chkAutoRestart, chkUpdateOnRestart, chkBackupOnStart;
        NumericUpDown numRestartHours, numKeepBackups;
        Action stateHandler, crashHandler;

        void WireServer(ServerController c, bool on)
        {
            if (stateHandler == null) { stateHandler = () => Ui(OnStateChanged); crashHandler = () => Ui(OnCrash); }
            if (on) { c.Output += OnServerOutput; c.StateChanged += stateHandler; c.UnexpectedExit += crashHandler; }
            else { c.Output -= OnServerOutput; c.StateChanged -= stateHandler; c.UnexpectedExit -= crashHandler; }
        }

        // ---------- switcher menu

        void ShowServerMenu()
        {
            var m = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
            var ids = AppSettings.ProfileIds();
            if (!ids.Contains(S.ProfileId, StringComparer.OrdinalIgnoreCase)) ids.Insert(0, S.ProfileId);
            foreach (var id in ids)
            {
                var p = id.Equals(S.ProfileId, StringComparison.OrdinalIgnoreCase) ? S : AppSettings.Load(id);
                var item = new ToolStripMenuItem(p.ServerName + "      " + p.DataDir) { Checked = p == S, Tag = id };
                string target = id;
                item.Click += async (s, e) => await SwitchServer(target);
                m.Items.Add(item);
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Add server…", null, async (s, e) => await AddServer());
            var remove = new ToolStripMenuItem("Remove \"" + S.ServerName + "\" from this list…") { Enabled = ids.Count > 1 };
            remove.Click += async (s, e) => await RemoveCurrentServer();
            m.Items.Add(remove);
            m.Show(btnServerSwitch, new Point(0, btnServerSwitch.Height));
        }

        // ---------- switching

        async Task SwitchServer(string id)
        {
            if (id.Equals(S.ProfileId, StringComparison.OrdinalIgnoreCase)) return;
            if (busy != null) { Warn("Please wait: " + busy); return; }
            if (!LeaveCheck()) return;
            if (modsDirty && !Confirm("You have unsaved changes on the Mods page. Discard them and switch servers?")) return;
            if (SandboxDirty && !Confirm("You have unsaved changes on the Sandbox page. Discard them and switch servers?")) return;
            if (server.State != ServerState.Stopped)
            {
                if (!Confirm("\"" + S.ServerName + "\" is running. Only one server runs at a time.\n\nStop it (saving the world) and switch?")) return;
                await StopFlow();
                if (server.State != ServerState.Stopped) return;
            }

            WireServer(server, false);
            S = AppSettings.Load(id);
            AppSettings.ActiveProfileId = id;
            server = new ServerController(S);
            WireServer(server, true);

            ResetServerState();
            LoadSettingsIntoControls();
            Log("[ezpz] ---------- Switched to server \"" + S.ServerName + "\" (data: " + S.DataDir + ") ----------");
            RefreshAll();
            ShowPage(currentPage < 0 ? PHome : currentPage);
            await Task.Run(() => server.DetectExisting());
        }

        // Forget everything cached about the previous server.
        void ResetServerState()
        {
            roster = new Dictionary<string, PlayerInfo>(StringComparer.OrdinalIgnoreCase);
            onlineNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            roleSetHere.Clear();
            wsOrder.Clear(); enabledMods.Clear(); wsMods.Clear();
            modsLoaded = false; modsDirty = false;
            lastRun = new ModConfig.RunStatus();
            gcReader = new GcLogReader();
            lock (liveStats) liveStats.Clear();
            lastStatsPoll = DateTime.MinValue;
            ebsKhz = null; ebsLookedUp = false;
            latestBuild = null; updateError = null; lastUpdateCheck = DateTime.MinValue;
            crashTimes.Clear();
            restartWarned.Clear();
            serverCpuPct = -1; lastCpuAt = DateTime.MinValue;
            sandbox = null;
            dcLoaded = false;
            feed.Reset();
            logData = new LogViewerData(); logAll = new List<LogEntry>(); logLoadedAt = DateTime.MinValue;
            FilterLogs();   // empties the Logs list too, so it doesn't point past the new (empty) data
            LoadDiscordState();
            chars = new Dictionary<string, CharacterSheet>(StringComparer.OrdinalIgnoreCase); traffic = null; trafficAt = DateTime.MinValue;
            playersBaseline = false; notifiedBuild = null; healthNotifiedRun = DateTime.MinValue; lastNotifiedState = ServerState.Stopped;
            modsOutdated = new List<string>(); modsMissing = new List<string>();
            lastModCheck = DateTime.MinValue; modRestartAt = null; modRestartWhenEmpty = false; modCheckError = null;
        }

        // Controls that were filled from the old server's settings.
        void LoadSettingsIntoControls()
        {
            tbInstall.Text = S.InstallDir;
            tbSteam.Text = S.SteamCmdDir;
            tbData.Text = S.DataDir;
            tbBackup.Text = S.BackupDir;
            tbName.Text = S.ServerName;
            tbAdminPw.Text = S.AdminPassword;
            cbBranch.Text = S.Branch;
            nMinRam.Value = Math.Max(nMinRam.Minimum, Math.Min(nMinRam.Maximum, S.MinRamGB));
            nMaxRam.Value = Math.Max(nMaxRam.Minimum, Math.Min(nMaxRam.Maximum, S.MaxRamGB));
            chkAutoRestart.Checked = S.AutoRestart;
            numRestartHours.Value = Math.Max(numRestartHours.Minimum, Math.Min(numRestartHours.Maximum, S.RestartHours));
            chkUpdateOnRestart.Checked = S.UpdateOnRestart;
            chkBackupOnStart.Checked = S.BackupOnStart;
            numKeepBackups.Value = Math.Max(numKeepBackups.Minimum, Math.Min(numKeepBackups.Maximum, S.KeepAutoBackups));
            cbModAction.SelectedIndex = Math.Max(0, Math.Min(2, S.ModUpdateAction));
            LoadBackupSettings();
            lastBackupAt = DateTime.MinValue;
            LoadDiscordControls();
        }

        // ---------- add / remove

        async Task AddServer()
        {
            AppSettings created;
            using (var d = new AddServerDialog(S))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                created = d.Result;
            }
            created.ProfileId = AppSettings.NewProfileId(created.ServerName);
            created.Save();
            Log("[ezpz] Added server \"" + created.ServerName + "\".");
            await SwitchServer(created.ProfileId);
            if (S.ProfileId == created.ProfileId)
            {
                ShowPage(PSetup);
                Info("Server \"" + S.ServerName + "\" added. Set its admin password and work through the Setup checklist."
                    + (File.Exists(S.StartBat) ? "" : " It needs its own install, so start with 'Install / Update server'."));
            }
        }

        async Task RemoveCurrentServer()
        {
            var others = AppSettings.ProfileIds().Where(i => !i.Equals(S.ProfileId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (others.Count == 0) return;
            if (!Confirm("Remove \"" + S.ServerName + "\" from ezpz's list?\n\nOnly ezpz's settings for it are removed. Its install, world, config and backups stay on disk, so you can add it back later with the same folders.")) return;
            string old = S.ProfileId;
            await SwitchServer(others[0]);
            if (S.ProfileId == old) return;   // switch was cancelled
            AppSettings.RemoveProfile(old);
            Log("[ezpz] Removed a server from the list (files kept).");
        }

        // ---------- only one server at a time

        // Any Project Zomboid server running on this PC other than the one we manage.
        List<string> OtherRunningServers()
        {
            var found = new List<string>();
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='java.exe'"))
                    foreach (ManagementObject mo in q.Get())
                    {
                        string cl = Convert.ToString(mo["CommandLine"]);
                        if (cl.IndexOf("zombie.network.GameServer", StringComparison.Ordinal) < 0) continue;
                        int pid = Convert.ToInt32(mo["ProcessId"]);
                        if (pid == server.Pid) continue;
                        try { if (System.Diagnostics.Process.GetProcessById(pid).HasExited) continue; } catch { continue; }   // already gone
                        var m = Regex.Match(cl, @"-servername\s+""?([^""\s]+)");
                        found.Add(m.Success ? m.Groups[1].Value : "unknown (PID " + pid + ")");
                    }
            }
            catch { }
            return found;
        }
    }

    // Collects what a new server needs: its ID, which install it uses, and its own data folder.
    class AddServerDialog : Form
    {
        public AppSettings Result;
        readonly AppSettings current;
        readonly TextBox tbId, tbInstall, tbData;
        readonly RadioButton rbShare, rbNew;
        readonly ComboBox cbBranch;
        bool dataEdited, installEdited;

        public AddServerDialog(AppSettings current)
        {
            this.current = current;
            Text = "Add server";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Base;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(640, 420);

            int y = 16;
            Func<string, int, Label> label = (t, h) =>
            {
                var l = new Label { Text = t, Location = new Point(16, y), AutoSize = true, MaximumSize = new Size(608, 0), UseMnemonic = false };
                Controls.Add(l); y += h; return l;
            };
            label("Server ID (letters, numbers, - and _). It names this server's config and world.", 24);
            tbId = Theme.Box(""); tbId.Location = new Point(16, y); tbId.Width = 260; Controls.Add(tbId); y += 40;

            label("Game files", 24);
            rbShare = new RadioButton { Text = "Share the existing install (" + current.InstallDir + ")", Location = new Point(16, y), AutoSize = true, Checked = true, UseMnemonic = false };
            Controls.Add(rbShare); y += 26;
            rbNew = new RadioButton { Text = "Use a separate install, for example to run a different game version", Location = new Point(16, y), AutoSize = true, UseMnemonic = false };
            Controls.Add(rbNew); y += 28;
            tbInstall = Theme.Box(""); tbInstall.Location = new Point(36, y); tbInstall.Width = 420; tbInstall.Enabled = false; Controls.Add(tbInstall);
            cbBranch = new ComboBox { Location = new Point(466, y - 1), Width = 150, DropDownStyle = ComboBoxStyle.DropDown, BackColor = Theme.Input, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Enabled = false };
            cbBranch.Items.AddRange(new object[] { "public", "unstable" });
            cbBranch.Text = current.Branch;
            Controls.Add(cbBranch); y += 30;
            var branchHint = new Label { Text = "Folder for the new install, and its Steam branch", Location = new Point(36, y), AutoSize = true, ForeColor = Theme.Muted };
            Controls.Add(branchHint); y += 34;

            label("Data folder (world, config, players and logs). Each server needs its own.", 24);
            tbData = Theme.Box(""); tbData.Location = new Point(16, y); tbData.Width = 600; Controls.Add(tbData); y += 40;
            label("Memory, backups folder and restart options are copied from \"" + current.ServerName + "\". You'll set an admin password on the Setup page.", 44);

            var ok = Theme.Btn("Add server", Theme.AccentBtn);
            ok.Location = new Point(420, 372);
            ok.Click += (s, e) => Submit();
            var cancel = Theme.Btn("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(540, 372);
            Controls.AddRange(new Control[] { ok, cancel });
            CancelButton = cancel;

            tbId.TextChanged += (s, e) => SuggestPaths();
            tbData.TextChanged += (s, e) => { if (tbData.Focused) dataEdited = true; };
            tbInstall.TextChanged += (s, e) => { if (tbInstall.Focused) installEdited = true; };
            rbNew.CheckedChanged += (s, e) => { tbInstall.Enabled = cbBranch.Enabled = rbNew.Checked; };
            SuggestPaths();
        }

        void SuggestPaths()
        {
            string id = tbId.Text.Trim() == "" ? "server2" : tbId.Text.Trim();
            string root = Path.GetDirectoryName(current.DataDir.TrimEnd('\\')) ?? @"C:\PZServer";
            if (!dataEdited) tbData.Text = Path.Combine(root, "data-" + id);
            if (!installEdited) tbInstall.Text = Path.Combine(Path.GetDirectoryName(current.InstallDir.TrimEnd('\\')) ?? root, "server-" + id);
        }

        void Submit()
        {
            string id = tbId.Text.Trim();
            string data = tbData.Text.Trim();
            if (!AppSettings.ValidServerName(id)) { Say("The server ID may only contain letters, numbers, - and _ (max 40 characters)."); return; }
            if (data == "") { Say("Choose a data folder."); return; }
            foreach (var pid in AppSettings.ProfileIds())
            {
                var other = AppSettings.Load(pid);
                if (other.ServerName.Equals(id, StringComparison.OrdinalIgnoreCase)) { Say("There's already a server with the ID \"" + id + "\"."); return; }
                if (SamePath(other.DataDir, data)) { Say("\"" + other.ServerName + "\" already uses that data folder. Each server needs its own, so their logs, players and health data don't mix."); return; }
            }
            if (rbNew.Checked && tbInstall.Text.Trim() == "") { Say("Choose a folder for the separate install."); return; }

            var n = current.CopyForNewServer();
            n.ServerName = id;
            n.DataDir = data;
            if (rbNew.Checked)
            {
                n.InstallDir = tbInstall.Text.Trim();
                n.Branch = cbBranch.Text.Trim() == "" ? "public" : cbBranch.Text.Trim();
            }
            Result = n;
            DialogResult = DialogResult.OK;
        }

        static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        void Say(string m) { MessageBox.Show(this, m, "Add server", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
