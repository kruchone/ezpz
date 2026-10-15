using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    public partial class MainForm : Form
    {
        const string FirewallRule = "ezpz - Project Zomboid";
        const string LegacyFirewallRule = "PZ Server Manager - Project Zomboid";
        // One per install, since it targets that install's java.exe.
        string PrivateTcpRule { get { return "ezpz - Project Zomboid (keep TCP private) " + S.InstallDir.TrimEnd('\\'); } }

        AppSettings S;                // the server being managed; swapped by SwitchServer
        ServerController server;
        readonly ConcurrentQueue<string> logQ = new ConcurrentQueue<string>();
        readonly Timer uiTimer = new Timer();
        readonly Timer detectTimer = new Timer();

        Panel content, nav;
        readonly List<Button> navButtons = new List<Button>();
        readonly List<Page> pages = new List<Page>();
        Page pgSetup, pgConfig, pgWorld, pgBackups, pgReset;
        Panel pgMods;
        Panel pgConsole, pgPlayers;

        // header
        Label lblStatus, lblStats, lblBusy, lblHeaderName, lblNavData;
        Button btnServerSwitch;
        Button btnStart, btnStop, btnRestart;

        string busy;                 // non-null while a long task runs
        bool closingConfirmed;
        bool firewallOk;
        readonly List<DateTime> crashTimes = new List<DateTime>();
        readonly HashSet<int> restartWarned = new HashSet<int>();

        public MainForm()
        {
            S = AppSettings.Load();
            server = new ServerController(S);
            WireServer(server, true);

            UpdateTitle();
            Font = Theme.Base;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1220, 820);
            MinimumSize = new Size(1000, 650);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            Controls.Add(content);
            nav = new Panel { Dock = DockStyle.Left, Width = 220, BackColor = Theme.Side, Padding = new Padding(10, 14, 10, 10) };
            Controls.Add(nav);
            BuildHeader();

            BuildSetup();
            BuildConfig();
            BuildSandbox();
            BuildMods();
            BuildConsole();
            BuildLogs();
            BuildHome();
            BuildPlayers();
            BuildWorld();
            BuildDiscord();
            BuildBackups();
            BuildReset();
            BuildNav();
            NoMnemonics(this);

            uiTimer.Interval = 200;
            uiTimer.Tick += (s, e) => { FlushLog(); };
            uiTimer.Start();
            detectTimer.Interval = 1000;
            detectTimer.Tick += (s, e) => OnSecond();
            detectTimer.Start();

            Shown += (s, e) =>
            {
                Log("[ezpz] ezpz " + Program.Version + " ready. Data folder: " + S.DataDir);
                Task.Run(() => server.DetectExisting());
                RefreshAll();
                ShowPage(StartPage);
            };
            FormClosing += OnClosingForm;
        }

        void Ui(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(a); } catch { } }
            else a();
        }

        // Shows a line on the Console page and keeps it in ezpz's log file (see AppLog).
        void Log(string s)
        {
            logQ.Enqueue(s);
            if (!s.StartsWith("[ezpz] (harmless)")) AppLog.Write(s);
        }

        // ================================================================== HEADER / NAV

        void BuildHeader()
        {
            var h = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Side };
            Controls.Add(h);
            // Server switcher: click the server name to switch, add or remove servers.
            btnServerSwitch = new Button { Font = Theme.H2, ForeColor = Theme.Text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(10, 4), FlatStyle = FlatStyle.Flat, BackColor = Theme.Side, Cursor = Cursors.Hand, Padding = new Padding(2, 0, 2, 0) };
            btnServerSwitch.FlatAppearance.BorderSize = 0;
            btnServerSwitch.FlatAppearance.MouseOverBackColor = Theme.Card;
            btnServerSwitch.Click += (s, e) => ShowServerMenu();
            new ToolTip().SetToolTip(btnServerSwitch, "Switch servers, or add one");
            var title = btnServerSwitch;
            lblHeaderName = new Label { ForeColor = Theme.Muted, AutoSize = true, Location = new Point(18, 38) };
            lblStatus = new Label { Font = Theme.H2, AutoSize = true, Location = new Point(430, 10) };
            lblStats = new Label { ForeColor = Theme.Muted, AutoSize = true, Location = new Point(432, 36) };
            lblBusy = new Label { ForeColor = Theme.Warn, AutoSize = true, Location = new Point(640, 22) };
            h.Controls.AddRange(new Control[] { title, lblHeaderName, lblStatus, lblStats, lblBusy });

            var f = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right, WrapContents = false, BackColor = Color.Transparent };
            btnStart = Theme.Btn("▶  Start", Theme.AccentBtn);
            btnStop = Theme.Btn("■  Stop");
            btnRestart = Theme.Btn("↻  Restart");
            btnStart.Click += async (s, e) => await StartFlow();
            btnStop.Click += async (s, e) => await StopFlow();
            btnRestart.Click += async (s, e) => await RestartFlow(false);
            f.Controls.AddRange(new Control[] { btnStart, btnStop, btnRestart });
            h.Controls.Add(f);
            h.Layout += (s, e) => { f.Location = new Point(h.ClientSize.Width - f.Width - 12, 14); };
        }

        void BuildNav()
        {
            string[] names = { "Home", "Setup", "Configure", "Sandbox", "Mods", "Console", "Logs", "Players & Admin", "World & Weather", "Discord", "Backups", "Reset" };
            int y = 14;
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                var b = new Button
                {
                    Text = (i == PHome ? "⌂   " : i + "   ") + names[i], FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
                    Size = new Size(200, 36), Location = new Point(10, y), ForeColor = Theme.Text, Font = Theme.Bold,
                    BackColor = Theme.Side, Cursor = Cursors.Hand, Padding = new Padding(8, 0, 0, 0)
                };
                b.FlatAppearance.BorderSize = 0;
                b.Click += (s, e) => ShowPage(idx);
                nav.Controls.Add(b);
                navButtons.Add(b);
                y += i == PHome ? 48 : 39;
            }
            navButtons[PReset].ForeColor = Theme.Danger;
            lblNavData = new Label { Text = "Data:\n" + S.DataDir, ForeColor = Theme.Muted, AutoSize = true, MaximumSize = new Size(200, 0), Location = new Point(14, y + 20) };
            nav.Controls.Add(lblNavData);
        }

        // Page numbers, in nav order.
        const int PHome = 0, PSetup = 1, PConfig = 2, PSandbox = 3, PMods = 4, PConsole = 5, PLogs = 6, PPlayers = 7, PWorld = 8, PDiscord = 9, PBackups = 10, PReset = 11;

        public int StartPage;
        int currentPage = -1;
        void ShowPage(int i)
        {
            if (i != currentPage && !LeaveCheck()) return;
            currentPage = i;
            Control[] all = { pgHome, pgSetup, pgConfig, pgSandbox, pgMods, pgConsole, pgLogs, pgPlayers, pgWorld, pgDiscord, pgBackups, pgReset };
            for (int k = 0; k < all.Length; k++)
            {
                all[k].Visible = k == i;
                navButtons[k].BackColor = k == i ? Theme.Card : Theme.Side;
            }
            if (i == PSetup) RefreshChecklist();
            if (i == PConfig) LoadConfig();
            if (i == PSandbox) RefreshSandbox(false);
            if (i == PMods) RefreshMods(false);
            if (i == PHome) RefreshHome();
            if (i == PLogs) LoadLogs(false);
            if (i == PPlayers) { RefreshPlayers(true); RefreshCatalog(); }
            if (i == PWorld) RefreshWorld();
            if (i == PDiscord) RefreshDiscord();
            if (i == PBackups) RefreshBackupList();
            if (i == PReset) UpdateResetPreview();
        }

        Page NewPage()
        {
            var p = new Page();
            content.Controls.Add(p);
            return p;
        }

        // ================================================================== SETUP

        TextBox tbInstall, tbSteam, tbData, tbBackup, tbName, tbAdminPw;
        ComboBox cbBranch;
        NumericUpDown nMinRam, nMaxRam;
        readonly List<Label> checkLabels = new List<Label>();
        Label lblNextStep, lblBuild, lblIps;

        static readonly string[] Steps =
        {
            "Choose folders, a server name and an admin password, then Save",
            "Install the server files with SteamCMD",
            "Generate the default config (first start of the server)",
            "Set up Windows Firewall (open the game ports, keep the server's TCP ports private)",
            "Customize your server on the Configure page (optional)",
            "Forward the UDP ports on your router (only for players outside your home network)",
            "Start the server",
        };

        void BuildSetup()
        {
            var p = pgSetup = NewPage();
            p.H1("Setup");
            p.P("Work through these steps from top to bottom. Each step lights up green when it's done.");

            foreach (var s in Steps)
            {
                var l = new Label { AutoSize = true, MaximumSize = new Size(Page.Wide, 0), Text = "○   " + s, ForeColor = Theme.Muted };
                p.Add(l, 20);
                checkLabels.Add(l);
            }
            p.Y += 4;
            lblNextStep = p.P("", Theme.Warn);
            lblNextStep.Font = Theme.Bold;

            p.H2("Folders, server name & admin");
            tbInstall = p.PathRow("Server install folder", S.InstallDir, BrowseFolder);
            p.Hint("Where SteamCMD puts the server program (about 4 GB).");
            tbSteam = p.PathRow("SteamCMD folder", S.SteamCmdDir, BrowseFolder);
            tbData = p.PathRow("Server data folder", S.DataDir, BrowseFolder);
            p.Hint("World saves, configs, player database and logs. Kept separate from your own game's Zomboid folder so a reset never touches your single-player saves.");
            tbBackup = p.PathRow("Backups folder", S.BackupDir, BrowseFolder);
            tbName = p.Row("Server name (ID)", Theme.Box(S.ServerName), 240);
            p.Hint("Internal ID: letters, numbers, - and _ only. It names the config files and save folder. (The name players see is set on the Configure page.)");
            tbAdminPw = p.Row("Admin password", Theme.Box(S.AdminPassword), 240);
            tbAdminPw.UseSystemPasswordChar = true;
            var show = Theme.Check("Show", false);
            show.Location = new Point(Page.X + Page.LabelW + 250, tbAdminPw.Top + 2);
            show.CheckedChanged += (s, e) => tbAdminPw.UseSystemPasswordChar = !show.Checked;
            p.Controls.Add(show);
            p.Hint("Password for the built-in 'admin' account. Log in with username admin to get admin powers in-game. Saved encrypted on this PC.");

            cbBranch = p.Row("Steam branch", new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, BackColor = Theme.Input, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat }, 240);
            cbBranch.Items.AddRange(new object[] { "public", "unstable" });
            cbBranch.Text = S.Branch;
            p.Hint("'public' = current stable Build 42 (recommended). 'unstable' = test builds. Every player must be on the same branch and version as the server.");

            nMinRam = p.Row("Memory: start (GB)", Theme.Num(1, 128, S.MinRamGB), 80);
            nMaxRam = p.Row("Memory: max (GB)", Theme.Num(2, 128, S.MaxRamGB), 80);
            p.Hint("8 GB is a sensible max for a few friends, and 10+ GB for bigger servers or lots of mods. Leave several GB for Windows.");

            var save = Theme.Btn("Save settings", Theme.AccentBtn);
            save.Click += (s, e) => SaveSetup(true);
            p.Buttons(save);

            p.H2("Install / update");
            lblBuild = p.P("");
            var install = Theme.Btn("Install / Update server", Theme.AccentBtn);
            install.Click += async (s, e) => await InstallFlow(false);
            var validate = Theme.Btn("Verify files");
            validate.Click += async (s, e) => await InstallFlow(true);
            var openInst = Theme.Btn("Open install folder");
            openInst.Click += (s, e) => OpenFolder(S.InstallDir);
            p.Buttons(install, validate, openInst);
            p.P("Uses Valve's SteamCMD with anonymous login (no Steam account needed). Progress shows on the Console page. Stop the server before updating.");

            p.H2("First start & network");
            var gen = Theme.Btn("Generate default config");
            gen.Click += async (s, e) => await GenerateConfigFlow();
            var fw = Theme.Btn("Set up Windows Firewall");
            fw.Click += async (s, e) => await FirewallFlow();
            var ip = Theme.Btn("Show my public IP");
            ip.Click += async (s, e) => await ShowPublicIp();
            var openData = Theme.Btn("Open data folder");
            openData.Click += (s, e) => OpenFolder(S.DataDir);
            p.Buttons(gen, fw, ip, openData);
            lblIps = p.P("");
            p.P("Generate default config: starts the server once so Project Zomboid writes its default config files, then stops it. The first start can take several minutes.");
            p.P("Router: for players outside your home network, forward UDP ports 16261 and 16262 (or whatever DefaultPort/UDPPort you set) to this PC's local IP. Friends then join with your public IP and port 16261.");
            p.End();
        }

        void BrowseFolder(TextBox tb)
        {
            using (var d = new FolderBrowserDialog())
            {
                d.SelectedPath = tb.Text;
                d.ShowNewFolderButton = true;
                if (d.ShowDialog(this) == DialogResult.OK) tb.Text = d.SelectedPath;
            }
        }

        bool SaveSetup(bool announce)
        {
            if (!AppSettings.ValidServerName(tbName.Text.Trim()))
            {
                Warn("The server name may only contain letters, numbers, - and _ (max 40 characters).");
                return false;
            }
            if (nMaxRam.Value < nMinRam.Value) { Warn("The max memory must be at least the start memory."); return false; }
            string newName = tbName.Text.Trim();
            if (newName != S.ServerName && server.State != ServerState.Stopped)
            {
                Warn("Stop the server before changing its name.");
                return false;
            }
            S.InstallDir = tbInstall.Text.Trim();
            S.SteamCmdDir = tbSteam.Text.Trim();
            S.DataDir = tbData.Text.Trim();
            S.BackupDir = tbBackup.Text.Trim();
            S.ServerName = newName;
            S.AdminPassword = tbAdminPw.Text;
            S.Branch = string.IsNullOrWhiteSpace(cbBranch.Text) ? "public" : cbBranch.Text.Trim();
            S.MinRamGB = (int)nMinRam.Value;
            S.MaxRamGB = (int)nMaxRam.Value;
            S.Save();
            if (announce) Log("[ezpz] Settings saved.");
            RefreshAll();
            return true;
        }

        void RefreshAll()
        {
            btnServerSwitch.Text = S.ServerName + "  ▾";
            lblHeaderName.Text = S.DataDir;
            lblNavData.Text = "Data:\n" + S.DataDir;
            UpdateTitle();
            RefreshChecklist();
            OnStateChanged();
        }

        void RefreshChecklist()
        {
            string bi = SteamCmd.InstalledBuildInfo(S);
            lblBuild.Text = bi != null ? "Installed: " + bi + " in " + S.InstallDir : "Not installed yet.";
            bool[] done =
            {
                AppSettings.ValidServerName(S.ServerName) && S.AdminPassword.Length > 0 && S.IsSaved,
                File.Exists(S.StartBat),
                File.Exists(S.IniPath),
                firewallOk,
                File.Exists(S.IniPath) && IniFile.Load(S.IniPath).Get("PublicName", "") != "",
                false,
                server.State == ServerState.Running,
            };
            int next = -1;
            for (int i = 0; i < checkLabels.Count; i++)
            {
                bool info = i == 5;
                checkLabels[i].Text = (done[i] ? "●   " : info ? "◌   " : "○   ") + Steps[i];
                checkLabels[i].ForeColor = done[i] ? Theme.Accent : info ? Theme.Info : Theme.Text;
                if (!done[i] && next < 0 && i != 4 && i != 5) next = i;
            }
            lblNextStep.Text = next < 0 ? "All set. Your server is running." : "Next:  " + Steps[next];
            Task.Run(() =>
            {
                bool ok = (RunHidden("netsh", "advfirewall firewall show rule name=\"" + FirewallRule + "\"") == 0
                        || RunHidden("netsh", "advfirewall firewall show rule name=\"" + LegacyFirewallRule + "\"") == 0)
                       && RunHidden("netsh", "advfirewall firewall show rule name=\"" + PrivateTcpRule + "\"") == 0;
                if (ok != firewallOk) { firewallOk = ok; Ui(RefreshChecklist); }
            });
            lblIps.Text = "This PC's local IP: " + string.Join(", ", LocalIps());
        }

        static IEnumerable<string> LocalIps()
        {
            try
            {
                return Dns.GetHostAddresses(Dns.GetHostName())
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    .Select(a => a.ToString()).ToList();
            }
            catch { return new[] { "?" }; }
        }

        static int RunHidden(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.WaitForExit(10000); return p.ExitCode; }
            }
            catch { return -1; }
        }

        async Task InstallFlow(bool validate)
        {
            if (!SaveSetup(false) || !BeginBusy("Installing / updating server files…")) return;
            try
            {
                if (server.State != ServerState.Stopped) { Warn("Stop the server before installing or updating."); return; }
                ShowPage(PConsole);
                await SteamCmd.InstallOrUpdate(S, validate, Log);
            }
            catch (Exception ex) { Log("[steamcmd] ERROR: " + ex.Message); Warn(ex.Message); }
            finally { EndBusy(); RefreshChecklist(); }
        }

        async Task GenerateConfigFlow()
        {
            if (!SaveSetup(false)) return;
            if (File.Exists(S.IniPath) &&
                MessageBox.Show(this, "A config already exists for '" + S.ServerName + "'. Start the server once anyway? (Existing settings are kept.)", "Generate config", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            server.StopWhenStarted = true;
            await StartFlow();
        }

        async Task FirewallFlow()
        {
            var ini = IniFile.Load(S.IniPath);
            int p1, p2;
            if (!int.TryParse(ini.Get("DefaultPort", "16261"), out p1)) p1 = 16261;
            if (!int.TryParse(ini.Get("UDPPort", "16262"), out p2)) p2 = 16262;
            string ports = p2 == p1 + 1 ? p1 + "-" + p2 : p1 + "," + p2;
            string java = Path.Combine(S.InstallDir, "jre64", "bin", "java.exe");
            // Two rules: allow the game's UDP ports, and block inbound TCP to this install's Java from other machines.
            // PZ only needs UDP; its TCP listeners (the metrics server ezpz reads, and RCON) are for this PC only.
            // Block rules win over the allow rule Windows created for java.exe on first launch. Local connections aren't filtered.
            string cmd = "/c netsh advfirewall firewall delete rule name=\"" + FirewallRule + "\" >nul 2>&1 & " +
                         "netsh advfirewall firewall delete rule name=\"" + LegacyFirewallRule + "\" >nul 2>&1 & " +
                         "netsh advfirewall firewall add rule name=\"" + FirewallRule + "\" dir=in action=allow protocol=UDP localport=" + ports + " & " +
                         "netsh advfirewall firewall delete rule name=\"" + PrivateTcpRule + "\" >nul 2>&1 & " +
                         "netsh advfirewall firewall add rule name=\"" + PrivateTcpRule + "\" dir=in action=block protocol=TCP program=\"" + java + "\"";
            if (MessageBox.Show(this, "This sets up two Windows Firewall rules:\n\n"
                + "• Allow UDP " + ports + " in, so players can connect.\n"
                + "• Block TCP from other machines to the server's Java. Players don't use TCP; it keeps the server's stats and remote-command ports private to this PC.\n\n"
                + "Windows will ask for administrator permission.", "Firewall", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", cmd) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                var p = Process.Start(psi);
                await Task.Run(() => p.WaitForExit());
                Log("[ezpz] Firewall rule '" + FirewallRule + "' set for UDP " + ports + ".");
            }
            catch (Exception ex) { Log("[ezpz] Firewall rule not added: " + ex.Message); }
            RefreshChecklist();
        }

        async Task ShowPublicIp()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                string ip;
                using (var wc = new WebClient()) ip = (await wc.DownloadStringTaskAsync("https://api.ipify.org")).Trim();
                lblIps.Text = "This PC's local IP: " + string.Join(", ", LocalIps()) + "     ·     Public IP: " + ip + "  (friends connect to " + ip + " port 16261)";
            }
            catch (Exception ex) { Warn("Couldn't look up public IP: " + ex.Message); }
        }

        // ================================================================== CONFIGURE

        class Field { public string Key, Label, Kind, Hint; public Field(string k, string l, string kind, string h) { Key = k; Label = l; Kind = kind; Hint = h; } }

        static readonly Field[] Fields =
        {
            new Field("PublicName", "Server name (shown to players)", "text", null),
            new Field("PublicDescription", "Description", "text", null),
            new Field("ServerWelcomeMessage", "Welcome message", "text", null),
            new Field("Public", "List in public server browser", "bool", null),
            new Field("Password", "Join password", "text", "Leave empty for no password."),
            new Field("Open", "Anyone can join", "bool", "Unchecked = whitelist only (add players on the Players page)."),
            new Field("MaxPlayers", "Max players", "int", null),
            new Field("PVP", "PvP enabled", "bool", null),
            new Field("PauseEmpty", "Pause when nobody is online", "bool", null),
            new Field("SaveWorldEveryMinutes", "Autosave every (minutes)", "int", "0 = only save on shutdown."),
            new Field("DefaultPort", "Game port (UDP)", "int", null),
            new Field("UDPPort", "Second UDP port", "int", null),
            new Field("RCONPort", "RCON port (TCP)", "int", "Lets the manager control a server it didn't start. No need to forward this port."),
            new Field("RCONPassword", "RCON password", "text", null),
        };

        readonly Dictionary<string, Control> cfgCtl = new Dictionary<string, Control>();
        Label lblCfgMissing;
        DataGridView grid;
        TextBox tbFilter;

        void BuildConfig()
        {
            var p = pgConfig = NewPage();
            p.H1("Configure");
            lblCfgMissing = p.P("No config file yet. Click 'Generate default config' on the Setup page first, so you start from Project Zomboid's full default settings.", Theme.Warn);
            p.P("Most settings take effect after a restart. Click 'Apply live' to reload settings on a running server. Mods are set up on the Mods page.");

            var reload = Theme.Btn("Reload from disk");
            reload.Click += (s, e) => LoadConfig();
            var save = Theme.Btn("Save config", Theme.AccentBtn);
            save.Click += (s, e) => SaveConfig();
            var live = Theme.Btn("Apply live (reloadoptions)");
            live.Click += (s, e) => Cmd("reloadoptions");
            p.Buttons(save, reload, live);

            p.H2("Common settings");
            foreach (var f in Fields)
            {
                Control c;
                if (f.Kind == "bool") c = p.Row(f.Label, Theme.Check("", false), 0);
                else c = p.Row(f.Label, Theme.Box(""), f.Kind == "int" ? 100 : 560);
                cfgCtl[f.Key] = c;
                if (f.Hint != null) p.Hint(f.Hint);
            }

            p.H2("World rules (sandbox), spawns & imports");
            p.P("Zombie count, loot, XP, day length and similar world rules are in the SandboxVars file. Edit it directly, or set up a game with the in-game Host menu and import its files here.");
            var sb = Theme.Btn("Sandbox (world rules)");
            sb.Click += (s, e) => ShowPage(PSandbox);
            var sr = Theme.Btn("Edit spawn regions");
            sr.Click += (s, e) => EditFile(S.SpawnRegionsPath);
            var ini = Theme.Btn("Edit .ini in Notepad");
            ini.Click += (s, e) => EditFile(S.IniPath);
            var imp = Theme.Btn("Import .ini / .lua file…");
            imp.Click += (s, e) => ImportConfigFile();
            var of = Theme.Btn("Open config folder");
            of.Click += (s, e) => OpenFolder(S.ServerConfigDir);
            p.Buttons(sb, sr, ini, imp, of);

            p.H2("All settings");
            tbFilter = p.Row("Filter", Theme.Box(""), 300);
            tbFilter.TextChanged += (s, e) => FilterGrid();
            grid = new DataGridView
            {
                Size = new Size(Page.Wide, 420), BackgroundColor = Theme.Input, ForeColor = Color.Black,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None,
                SelectionMode = DataGridViewSelectionMode.CellSelect, EnableHeadersVisualStyles = false
            };
            grid.DefaultCellStyle.BackColor = Theme.Input;
            grid.DefaultCellStyle.ForeColor = Theme.Text;
            grid.DefaultCellStyle.SelectionBackColor = Theme.AccentBtn;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Card;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Text;
            grid.GridColor = Theme.Card;
            grid.Columns.Add("k", "Setting");
            grid.Columns.Add("v", "Value");
            grid.Columns[0].ReadOnly = true;
            grid.Columns[0].FillWeight = 35;
            p.Add(grid, 420);
            p.End();
        }

        void LoadConfig()
        {
            var ini = IniFile.Load(S.IniPath);
            lblCfgMissing.Visible = !File.Exists(S.IniPath);
            foreach (var f in Fields)
            {
                var c = cfgCtl[f.Key];
                string v = ini.Get(f.Key, "");
                c.Tag = v;
                var cb = c as CheckBox;
                if (cb != null) cb.Checked = v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                else c.Text = v;
            }
            grid.Rows.Clear();
            foreach (var kv in ini.All())
            {
                int r = grid.Rows.Add(kv.Key, kv.Value);
                grid.Rows[r].Tag = kv.Value;
            }
            FilterGrid();
        }

        void FilterGrid()
        {
            string f = tbFilter.Text.Trim();
            foreach (DataGridViewRow r in grid.Rows)
                r.Visible = f == "" || Convert.ToString(r.Cells[0].Value).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                                   || Convert.ToString(r.Cells[1].Value).IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // True if what's on the Configure page differs from the file.
        bool ConfigDirty()
        {
            if (grid == null || !File.Exists(S.IniPath)) return false;
            grid.EndEdit();
            foreach (DataGridViewRow r in grid.Rows)
                if (Convert.ToString(r.Cells[1].Value) != Convert.ToString(r.Tag)) return true;
            foreach (var f in Fields)
            {
                var c = cfgCtl[f.Key];
                var cb = c as CheckBox;
                string v = cb != null ? (cb.Checked ? "true" : "false") : c.Text.Trim();
                string old = Convert.ToString(c.Tag);
                bool same = cb != null ? old.Trim().Equals(v, StringComparison.OrdinalIgnoreCase) || (old == "" && !cb.Checked) : old == v;
                if (!same) return true;
            }
            return false;
        }

        // Before leaving a page (or the server, or the app): offer to save edits instead of silently dropping them.
        bool LeaveCheck()
        {
            if (currentPage == PDiscord && DiscordDirty()) return AskSave("Discord settings", SaveDiscordAll, RefreshDiscord);
            if (currentPage == PConfig && ConfigDirty()) return AskSave("server settings", SaveConfig, LoadConfig);
            return true;
        }

        bool AskSave(string what, Func<bool> save, Action discard)
        {
            var r = MessageBox.Show(this, "You have unsaved " + what + " on this page. Save them?", "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return save();
            discard();
            return true;
        }

        bool SaveConfig()
        {
            grid.EndEdit();
            var ini = IniFile.Load(S.IniPath);
            int changes = 0;
            foreach (DataGridViewRow r in grid.Rows)
            {
                string v = Convert.ToString(r.Cells[1].Value);
                if (v != Convert.ToString(r.Tag)) { ini.Set(Convert.ToString(r.Cells[0].Value), v); changes++; }
            }
            foreach (var f in Fields)
            {
                var c = cfgCtl[f.Key];
                var cb = c as CheckBox;
                string v = cb != null ? (cb.Checked ? "true" : "false") : c.Text.Trim();
                string old = Convert.ToString(c.Tag);
                bool same = cb != null ? old.Trim().Equals(v, StringComparison.OrdinalIgnoreCase) || (old == "" && !cb.Checked) : old == v;
                if (same) continue;
                if (f.Kind == "int") { int n; if (!int.TryParse(v, out n)) { Warn(f.Label + " must be a whole number."); return false; } }
                ini.Set(f.Key, v);
                changes++;
            }
            if (ini.Get("RCONPassword", "") == "")
            {
                ini.Set("RCONPassword", Util.RandomPassword(16));
                if (ini.Get("RCONPort", "") == "") ini.Set("RCONPort", "27015");
                changes++;
                Log("[ezpz] Generated an RCON password so the manager can always reach the server.");
            }
            ini.Save();
            Log("[ezpz] Saved " + changes + " change(s) to " + S.IniPath + (server.State != ServerState.Stopped ? "  (restart or 'Apply live' to take effect)" : ""));
            LoadConfig();
            RefreshChecklist();
            return true;
        }

        void EditFile(string path)
        {
            if (!File.Exists(path)) { Warn("That file doesn't exist yet:\n" + path + "\n\nGenerate the default config first (Setup page)."); return; }
            Process.Start("notepad.exe", Util.QuoteArg(path));
        }

        void ImportConfigFile()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = "Import a Project Zomboid server file";
                d.Filter = "PZ server files (*.ini;*.lua)|*.ini;*.lua";
                string hostDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Zomboid", "Server");
                if (Directory.Exists(hostDir)) d.InitialDirectory = hostDir;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string src = d.FileName, fn = Path.GetFileName(src), target;
                if (fn.EndsWith("_SandboxVars.lua", StringComparison.OrdinalIgnoreCase)) target = S.SandboxPath;
                else if (fn.EndsWith("_spawnregions.lua", StringComparison.OrdinalIgnoreCase)) target = S.SpawnRegionsPath;
                else if (fn.EndsWith("_spawnpoints.lua", StringComparison.OrdinalIgnoreCase)) target = S.SpawnPointsPath;
                else if (fn.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) target = S.IniPath;
                else { Warn("Pick a <name>.ini, <name>_SandboxVars.lua, <name>_spawnregions.lua or <name>_spawnpoints.lua file."); return; }
                if (MessageBox.Show(this, "Copy\n" + src + "\n\nover\n" + target + " ?\n(The current file is kept as .bak)", "Import", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                Directory.CreateDirectory(S.ServerConfigDir);
                if (File.Exists(target)) File.Copy(target, target + ".bak", true);
                File.Copy(src, target, true);
                Log("[ezpz] Imported " + fn + " -> " + target);
                LoadConfig();
            }
        }

        // ================================================================== CONSOLE

        RichTextBox rtb;
        TextBox tbCmd;
        CheckBox chkAutoScroll;
        readonly List<string> history = new List<string>();
        int histPos;

        void BuildConsole()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false, Padding = new Padding(20, 14, 20, 14) };
            content.Controls.Add(p);
            pgConsole = p;

            rtb = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(14, 15, 17), ForeColor = Theme.Text,
                Font = Theme.Mono, BorderStyle = BorderStyle.None, WordWrap = true, DetectUrls = false, HideSelection = false
            };
            p.Controls.Add(rtb);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.Bg, WrapContents = false };
            var save = Theme.Btn("Save world");
            save.Click += (s, e) => Cmd("save");
            var kill = Theme.Btn("Force kill", Theme.DangerBtn);
            kill.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Force-kill the server without saving? Use only if it's frozen.", "Force kill", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    server.Kill();
            };
            var clear = Theme.Btn("Clear");
            clear.Click += (s, e) => rtb.Clear();
            var logs = Theme.Btn("Open logs folder");
            logs.Click += (s, e) => OpenFolder(S.LogsDir);
            var appLog = Theme.Btn("ezpz log");
            appLog.Click += (s, e) => OpenAppLog();
            chkAutoScroll = Theme.Check("Auto-scroll", true);
            chkAutoScroll.Margin = new Padding(8, 8, 0, 0);
            top.Controls.AddRange(new Control[] { save, kill, clear, logs, appLog, chkAutoScroll });
            p.Controls.Add(top);
            var title = new Label { Text = "Console", Font = Theme.H1, Dock = DockStyle.Top, Height = 44, ForeColor = Theme.Text };
            p.Controls.Add(title);

            var input = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(0, 8, 0, 0) };
            tbCmd = Theme.Box("");
            tbCmd.Dock = DockStyle.Fill;
            tbCmd.Font = Theme.Mono;
            var send = Theme.Btn("Send", Theme.AccentBtn);
            send.Dock = DockStyle.Right;
            send.Margin = new Padding(0);
            send.Click += (s, e) => SendTyped();
            tbCmd.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { SendTyped(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Up && history.Count > 0) { histPos = Math.Max(0, histPos - 1); tbCmd.Text = history[histPos]; tbCmd.SelectionStart = tbCmd.TextLength; e.Handled = true; }
                else if (e.KeyCode == Keys.Down && history.Count > 0) { histPos = Math.Min(history.Count, histPos + 1); tbCmd.Text = histPos < history.Count ? history[histPos] : ""; e.Handled = true; }
            };
            input.Controls.Add(tbCmd);
            input.Controls.Add(send);

            var opts = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
            var auto = chkAutoRestart = Theme.Check("Auto-restart on crash", S.AutoRestart);
            auto.CheckedChanged += (s, e) => { S.AutoRestart = auto.Checked; S.Save(); };
            var lbl = new Label { Text = "Scheduled restart every", AutoSize = true, Margin = new Padding(16, 4, 4, 0) };
            var hrs = numRestartHours = Theme.Num(0, 168, S.RestartHours);
            hrs.Width = 60;
            hrs.ValueChanged += (s, e) => { S.RestartHours = (int)hrs.Value; S.Save(); };
            var lbl2 = new Label { Text = "hours (0 = off)", AutoSize = true, Margin = new Padding(4, 4, 16, 0) };
            var upd = chkUpdateOnRestart = Theme.Check("Update game on scheduled restart", S.UpdateOnRestart);
            upd.CheckedChanged += (s, e) => { S.UpdateOnRestart = upd.Checked; S.Save(); };
            opts.Controls.AddRange(new Control[] { auto, lbl, hrs, lbl2, upd });
            p.Controls.Add(opts);
            p.Controls.Add(input);
            rtb.BringToFront();
        }

        void SendTyped()
        {
            string c = tbCmd.Text.Trim();
            if (c == "") return;
            history.Add(c);
            histPos = history.Count;
            tbCmd.Clear();
            Cmd(c);
        }

        // ezpz's own log for today, for bug reports.
        void OpenAppLog()
        {
            AppLog.Write("(log opened from the Console page)");
            try { System.Diagnostics.Process.Start("notepad.exe", Util.QuoteArg(AppLog.TodayPath)); }
            catch { OpenFolder(AppLog.Dir); }
        }

        void Cmd(string c)
        {
            try { server.SendCommand(c); }
            catch (Exception ex) { Log("[ezpz] " + ex.Message); }
        }

        void FlushLog()
        {
            if (logQ.IsEmpty) return;
            string s;
            int n = 0;
            while (n < 400 && logQ.TryDequeue(out s))
            {
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionLength = 0;
                rtb.SelectionColor = ColorFor(s);
                rtb.AppendText(s + "\n");
                n++;
            }
            if (rtb.TextLength > 1500000)
            {
                rtb.Select(0, rtb.TextLength / 2);
                rtb.SelectedText = "";
            }
            if (chkAutoScroll.Checked) { rtb.SelectionStart = rtb.TextLength; rtb.ScrollToCaret(); }
        }

        static Color ColorFor(string s)
        {
            if (s.StartsWith("[ezpz] (harmless)")) return Theme.Muted;
            if (s.StartsWith("[ezpz]")) return Theme.Accent;
            if (s.StartsWith("[steamcmd]")) return Theme.Info;
            if (s.StartsWith("[backup]") || s.StartsWith("[reset]")) return Theme.Warn;
            if (s.StartsWith("> ")) return Color.White;
            if (s.IndexOf("ERROR", StringComparison.Ordinal) >= 0 || s.IndexOf("Exception", StringComparison.Ordinal) >= 0) return Theme.Danger;
            if (s.StartsWith("WARN")) return Theme.Warn;
            return Color.FromArgb(190, 194, 200);
        }

        // ================================================================== RESET

        RadioButton rbSoft, rbWorld, rbAccounts, rbFactory;
        CheckBox ckWorld, ckKeepChars, ckAccounts, ckConfig, ckLogs, ckBackupFirst, ckStartAfter;
        TextBox tbPreview, tbConfirm;
        Button btnReset;
        bool applyingPreset;
        int previewGen;

        void BuildReset()
        {
            var p = pgReset = NewPage();
            p.H1("Reset");
            p.P("Wipe this server back to a fresh state. Only files belonging to this server ID inside the data folder are touched. Your server install, mods, backups and your own single-player saves are never deleted.");

            p.H2("1. What kind of reset?");
            rbWorld = PresetRadio(p, "New world (recommended)", "Fresh map, fresh loot, everyone starts a new character. Keeps your settings, mods, admins, whitelist and bans.");
            rbSoft = PresetRadio(p, "Fresh map, keep characters (experimental)", "Regenerates the world (buildings, loot, zombies, vehicles, world time) but keeps players.db, so characters keep their skills and inventory. Characters may load in odd spots. Test it first.");
            rbAccounts = PresetRadio(p, "New world + wipe accounts", "Like a new world, and also deletes every account, whitelist entry, ban and admin role. The admin account is recreated from your admin password on the next start.");
            rbFactory = PresetRadio(p, "Factory reset", "Everything above, plus server settings, sandbox rules, spawn regions and logs. Back to Project Zomboid's defaults.");

            p.H2("2. Fine-tune");
            ckWorld = DetailCheck(p, "Delete world save (map, loot, zombies, vehicles, characters)");
            ckKeepChars = DetailCheck(p, "    …but keep players.db (characters)");
            ckAccounts = DetailCheck(p, "Delete accounts database (whitelist, admins, bans)");
            ckConfig = DetailCheck(p, "Delete config (.ini, SandboxVars, spawn regions/points)");
            ckLogs = DetailCheck(p, "Delete logs");
            p.Y += 6;
            ckBackupFirst = Theme.Check("Back up first (strongly recommended)", true);
            ckBackupFirst.ForeColor = Theme.Accent;
            p.Add(ckBackupFirst, 22);
            ckStartAfter = Theme.Check("Start the server when done", false);
            p.Add(ckStartAfter, 22);

            p.H2("3. Review");
            tbPreview = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Size = new Size(Page.Wide, 150), BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Mono, BorderStyle = BorderStyle.FixedSingle };
            p.Add(tbPreview, 150);

            p.H2("4. Confirm");
            var lbl = p.P("Type the server ID to confirm:", Theme.Text);
            tbConfirm = p.Row("Server ID", Theme.Box(""), 240);
            tbConfirm.TextChanged += (s, e) => UpdateResetButton();
            btnReset = Theme.Btn("RESET SERVER", Theme.DangerBtn);
            btnReset.Click += async (s, e) => await ResetFlow();
            p.Buttons(btnReset);
            p.End();

            rbWorld.Checked = true;
        }

        RadioButton PresetRadio(Page p, string title, string desc)
        {
            var r = new RadioButton { Text = title, AutoSize = true, Font = Theme.Bold, ForeColor = Theme.Text };
            p.Add(r, 20);
            var d = new Label { Text = desc, AutoSize = true, MaximumSize = new Size(Page.Wide - 24, 0), ForeColor = Theme.Muted };
            d.Location = new Point(Page.X + 20, p.Y - 6);
            p.Controls.Add(d);
            p.Y += d.GetPreferredSize(new Size(Page.Wide - 24, 0)).Height + 6;
            r.CheckedChanged += (s, e) => { if (r.Checked) ApplyPreset(); };
            return r;
        }

        CheckBox DetailCheck(Page p, string text)
        {
            var c = Theme.Check(text, false);
            p.Add(c, 20);
            c.CheckedChanged += (s, e) =>
            {
                if (applyingPreset) return;
                ckKeepChars.Enabled = ckWorld.Checked;
                UpdateResetPreview();
            };
            return c;
        }

        void ApplyPreset()
        {
            if (ckWorld == null) return;
            applyingPreset = true;
            ckWorld.Checked = true;
            ckKeepChars.Checked = rbSoft.Checked;
            ckAccounts.Checked = rbAccounts.Checked || rbFactory.Checked;
            ckConfig.Checked = rbFactory.Checked;
            ckLogs.Checked = rbFactory.Checked;
            ckKeepChars.Enabled = true;
            applyingPreset = false;
            UpdateResetPreview();
        }

        ResetPlan CurrentPlan()
        {
            return new ResetPlan
            {
                World = ckWorld.Checked, KeepCharacters = ckKeepChars.Checked && ckWorld.Checked,
                Accounts = ckAccounts.Checked, Config = ckConfig.Checked, Logs = ckLogs.Checked
            };
        }

        void UpdateResetPreview()
        {
            if (tbPreview == null) return;
            var plan = CurrentPlan();
            int gen = ++previewGen;
            tbPreview.Text = "Calculating…";
            Task.Run(() =>
            {
                var targets = plan.Targets(S);
                var lines = new List<string>();
                long total = 0;
                foreach (var t in targets)
                {
                    long sz = Util.DirSize(t);
                    total += sz;
                    lines.Add(Util.Size(sz).PadLeft(10) + "   " + t);
                }
                if (lines.Count == 0) lines.Add("Nothing to delete. This server has no data matching these options yet.");
                else lines.Insert(0, "Will delete " + targets.Count + " item(s), " + Util.Size(total) + " total:");
                if (plan.Accounts && S.AdminPassword == "") lines.Add("\r\n⚠ No admin password set on Setup. Set one so the admin account can be recreated.");
                Ui(() => { if (gen == previewGen) { tbPreview.Text = string.Join("\r\n", lines); UpdateResetButton(); } });
            });
        }

        void UpdateResetButton()
        {
            btnReset.Enabled = tbConfirm.Text.Trim() == S.ServerName && busy == null;
            btnReset.BackColor = btnReset.Enabled ? Theme.DangerBtn : Theme.Card;
        }

        async Task ResetFlow()
        {
            var plan = CurrentPlan();
            if (plan.Targets(S).Count == 0) { Info("Nothing to delete."); return; }
            if (!ckBackupFirst.Checked && !Confirm("You turned off the backup. This reset CANNOT be undone. Continue?")) return;
            if (!await EnsureStopped("reset")) return;
            if (ckBackupFirst.Checked && !await BackupFlow("pre-reset")) { Warn("Reset cancelled because the backup failed."); return; }
            if (!BeginBusy("Resetting…")) return;
            try
            {
                Log("[reset] Resetting server '" + S.ServerName + "'...");
                await Task.Run(() => plan.Execute(S, Log));
                Log("[reset] Done. The next start creates a fresh world.");
            }
            catch (Exception ex) { Log("[reset] ERROR: " + ex.Message); Warn("Reset failed partway: " + ex.Message + "\nYour pre-reset backup can be restored from the Backups page."); return; }
            finally { EndBusy(); tbConfirm.Clear(); UpdateResetPreview(); RefreshChecklist(); }
            if (ckStartAfter.Checked) await StartFlow();
            else Info("Reset complete. Start the server whenever you're ready.");
        }

        // ================================================================== SERVER LIFECYCLE

        async Task<bool> EnsureStopped(string why)
        {
            if (server.State == ServerState.Stopped) return true;
            if (!Confirm("The server is running. Stop it (saving the world) to continue with the " + why + "?")) return false;
            await StopFlow();
            return server.State == ServerState.Stopped;
        }

        async Task StartFlow()
        {
            if (server.State != ServerState.Stopped) { Info("The server is already " + server.State.ToString().ToLower() + "."); return; }
            if (!File.Exists(S.StartBat)) { Warn("The server isn't installed yet. Use 'Install / Update server' on the Setup page."); ShowPage(PSetup); return; }
            if (S.AdminPassword == "") { Warn("Set an admin password on the Setup page first."); ShowPage(PSetup); return; }
            if (busy != null) { Warn("Please wait: " + busy); return; }
            var others = await Task.Run(() => OtherRunningServers());
            // Right after a stop (e.g. a restart) the old process can take a few seconds to finish closing.
            // If what we found is this same server, wait for it rather than refusing to start.
            for (int wait = 0; wait < 20 && others.Count > 0 && others.All(o => o.Equals(S.ServerName, StringComparison.OrdinalIgnoreCase)); wait++)
            {
                if (wait == 0) Log("[ezpz] Waiting for the previous server process to finish closing…");
                await Task.Delay(1000);
                others = await Task.Run(() => OtherRunningServers());
            }
            if (others.Count > 0)
            {
                Warn("Another Project Zomboid server is already running on this PC (" + string.Join(", ", others) + ").\n\n"
                   + "ezpz runs one server at a time. Switch to that server with the server name at the top left and stop it first.");
                return;
            }
            restartWarned.Clear();
            if (S.BackupOnStart && Directory.Exists(S.SaveDir)) await BackupFlow("auto");
            try
            {
                ShowPage(PConsole);
                server.Start();
            }
            catch (Exception ex) { Log("[ezpz] ERROR: " + ex.Message); Warn(ex.Message); server.StopWhenStarted = false; }
        }

        async Task StopFlow()
        {
            if (server.State == ServerState.Stopped) return;
            await server.StopAsync();
        }

        async Task RestartFlow(bool scheduled)
        {
            if (server.State == ServerState.Stopped) { await StartFlow(); return; }
            await StopFlow();
            if (scheduled && S.UpdateOnRestart)
            {
                if (BeginBusy("Updating server…"))
                {
                    try { await SteamCmd.InstallOrUpdate(S, false, Log); }
                    catch (Exception ex) { Log("[steamcmd] ERROR: " + ex.Message); }
                    finally { EndBusy(); }
                }
            }
            await StartFlow();
        }

        void OnCrash()
        {
            lastCrashAt = DateTime.Now;
            dstate.Day.Crashes++;
            if (!S.AutoRestart)
            {
                Log("[ezpz] Server stopped unexpectedly. Auto-restart is off.");
                Notify(DiscordEvent.Crash, "💥 " + ServerTitle() + " crashed", "Auto-restart is off, so it stays down until someone starts it.", DcRed);
                return;
            }
            crashTimes.Add(DateTime.Now);
            crashTimes.RemoveAll(t => t < DateTime.Now.AddMinutes(-10));
            if (crashTimes.Count >= 3)
            {
                Log("[ezpz] Server crashed 3 times in 10 minutes. Auto-restart paused. Check the console and logs for the cause.");
                Notify(DiscordEvent.Crash, "💥 " + ServerTitle() + " keeps crashing", "3 crashes in 10 minutes, so automatic restarts are paused. An admin needs to check the server.", DcRed);
                return;
            }
            Log("[ezpz] Server stopped unexpectedly. Restarting in 15 seconds...");
            Notify(DiscordEvent.Crash, "💥 " + ServerTitle() + " crashed", "Restarting automatically in 15 seconds.", DcAmber);
            Task.Delay(15000).ContinueWith(t => Ui(async () =>
            {
                if (server.State == ServerState.Stopped) await StartFlow();
            }));
        }

        int ticks;
        volatile bool detecting;

        void OnSecond()
        {
            ticks++;
            if (!server.Attached)
            {
                if (ticks % 5 == 0 && !detecting)
                {
                    detecting = true;
                    Task.Run(() => { try { server.DetectExisting(); } finally { detecting = false; } });
                }
                server.TailConsoleLog();
            }
            UpdateStats();
            PlayersTick();
            ModWatchTick();
            DiscordTick();
            BackupTick();
            if (currentPage == PLogs && ticks % 10 == 0) LoadLogs(false);
            if (currentPage == PHome && ticks % 3 == 0) RefreshHome();

            // scheduled restarts with in-game warnings
            if (server.State == ServerState.Running && S.RestartHours > 0 && busy == null)
            {
                var left = server.StartedAt.AddHours(S.RestartHours) - DateTime.Now;
                foreach (int m in new[] { 15, 5, 1 })
                {
                    if (left.TotalMinutes <= m && left.TotalMinutes > m - 1 && restartWarned.Add(m))
                    {
                        Cmd("servermsg \"Scheduled server restart in " + m + " minute" + (m == 1 ? "" : "s") + ". Find somewhere safe!\"");
                        if (restartWarned.Count == 1)
                            Notify(DiscordEvent.RestartWarning, "🔁 " + ServerTitle() + " restarts in " + m + " minute" + (m == 1 ? "" : "s"), "Scheduled restart. It'll be back in a couple of minutes.", DcAmber);
                    }
                }
                if (left.TotalSeconds <= 0 && restartWarned.Add(0))
                {
                    Log("[ezpz] Scheduled restart.");
                    var _ = RestartFlow(true);
                }
            }
        }

        void UpdateStats()
        {
            if (server.State == ServerState.Stopped) { lblStats.Text = ""; return; }
            var up = DateTime.Now - server.StartedAt;
            string t = "Up " + (int)up.TotalHours + "h " + up.Minutes.ToString("00") + "m   ·   RAM " + Util.Size(server.MemoryBytes());
            if (server.Detached) t += "   ·   (started outside the manager)";
            if (S.RestartHours > 0 && server.State == ServerState.Running)
            {
                var left = server.StartedAt.AddHours(S.RestartHours) - DateTime.Now;
                if (left.TotalSeconds > 0) t += "   ·   restart in " + (int)left.TotalHours + "h " + left.Minutes.ToString("00") + "m";
            }
            lblStats.Text = t;
        }

        string StateText()
        {
            var st = server.State;
            return st == ServerState.Stopped ? "Stopped" : st == ServerState.Starting ? "Starting…" : st == ServerState.Running ? "Running" : "Stopping…";
        }

        // Window title: "<Server ID>: <status> — ezpz <version>"
        void UpdateTitle()
        {
            Text = S.ServerName + ": " + StateText() + " — ezpz " + Program.Version;
        }

        void OnStateChanged()
        {
            var st = server.State;
            lblStatus.Text = "● " + StateText();
            UpdateTitle();
            DiscordOnState(st);
            if (currentPage == PHome) RefreshHome();
            lblStatus.ForeColor = st == ServerState.Running ? Theme.Accent : st == ServerState.Stopped ? Theme.Muted : Theme.Warn;
            btnStart.Enabled = st == ServerState.Stopped;
            btnStart.BackColor = btnStart.Enabled ? Theme.AccentBtn : Theme.Card;
            btnStop.Enabled = st == ServerState.Running || st == ServerState.Starting;
            btnRestart.Enabled = st == ServerState.Running;
            if (currentPage == PSetup) RefreshChecklist();
            UpdateStats();
        }

        bool BeginBusy(string what)
        {
            if (busy != null) { Warn("Please wait: " + busy); return false; }
            busy = what;
            lblBusy.Text = "⏳ " + what;
            if (btnReset != null) UpdateResetButton();
            return true;
        }

        void EndBusy()
        {
            busy = null;
            lblBusy.Text = "";
            if (btnReset != null) UpdateResetButton();
        }

        async void OnClosingForm(object sender, FormClosingEventArgs e)
        {
            if (!closingConfirmed && !LeaveCheck()) { e.Cancel = true; return; }
            if (closingConfirmed || !server.Attached) return;
            e.Cancel = true;
            var r = MessageBox.Show(this, "The server is still running and needs the manager open.\n\nStop the server (saving the world) and exit?", "Exit", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;
            await StopFlow();
            closingConfirmed = true;
            Close();
        }

        // ================================================================== helpers

        // Show "&" literally in labels instead of treating it as a keyboard shortcut marker.
        static void NoMnemonics(Control c)
        {
            var l = c as Label; if (l != null) l.UseMnemonic = false;
            var b = c as ButtonBase; if (b != null) b.UseMnemonic = false;
            foreach (Control k in c.Controls) NoMnemonics(k);
        }

        void OpenFolder(string path)
        {
            try { Directory.CreateDirectory(path); Process.Start("explorer.exe", Util.QuoteArg(path)); }
            catch (Exception ex) { Warn(ex.Message); }
        }

        void Warn(string m) { MessageBox.Show(this, m, "ezpz", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        void Info(string m) { MessageBox.Show(this, m, "ezpz", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        bool Confirm(string m) { return MessageBox.Show(this, m, "ezpz", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes; }
    }
}
