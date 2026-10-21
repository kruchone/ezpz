using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // The setup wizard for a new server: the Setup page's steps one at a time, each showing only what that step
    // needs. It drives the same actions as the Setup page (save, install, generate config, firewall, start), so the
    // two can't drift apart. "Skip to the full app" closes it at any point.
    public partial class MainForm
    {
        Panel wizard, wizBody;
        Label wizTitle, wizStatus;
        readonly List<Label> wizSteps = new List<Label>();
        Button wizBack, wizNext, wizSkipStep;
        TextBox wizLog;
        int wizStep;

        // step 1 fields
        TextBox wzName, wzPw, wzInstall, wzData;
        NumericUpDown wzMem;
        ComboBox wzBranch;
        // step 5 fields
        TextBox wzPublic, wzJoinPw;
        NumericUpDown wzMax;
        CheckBox wzOpen, wzPvp;
        Label wzIps;

        static readonly string[] WizardTitles =
        {
            "Name your server", "Install the server", "Create its config", "Open the firewall",
            "Make it yours (optional)", "Let friends in from outside (optional)", "Start it",
        };

        void ShowWizard()
        {
            if (wizard == null) BuildWizard();
            wizStep = FirstUnfinishedStep();
            wizard.Visible = true;
            wizard.BringToFront();
            ShowWizardStep();
        }

        void CloseWizard(bool finished)
        {
            if (S.WizardPending) { S.WizardPending = false; if (S.IsSaved) S.Save(); }
            wizard.Visible = false;
            Log("[ezpz] " + (finished ? "Setup wizard finished." : "Setup wizard skipped. The Setup page has the same steps."));
            RefreshAll();
            ShowPage(PHome);
        }

        int FirstUnfinishedStep()
        {
            for (int i = 0; i < Steps.Length; i++) if (!WizardStepDone(i) && i != 4 && i != 5) return i;
            return Steps.Length - 1;
        }

        bool WizardStepDone(int i)
        {
            switch (i)
            {
                case 0: return AppSettings.ValidServerName(S.ServerName) && S.AdminPassword.Length > 0 && S.IsSaved;
                case 1: return File.Exists(S.StartBat);
                case 2: return File.Exists(S.IniPath);
                case 3: return firewallOk;
                case 5: return S.PortsForwarded;
                case 6: return S.FirstStartDone;
                default: return false;   // 4 and 5 are optional
            }
        }

        void BuildWizard()
        {
            // over the whole window, header and menu included (docking would only fill what's left)
            wizard = new Panel { Bounds = ClientRectangle, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, BackColor = Theme.Bg, Visible = false };
            Controls.Add(wizard);

            // ---- left: the steps
            var side = new Panel { Dock = DockStyle.Left, Width = 290, BackColor = Theme.Side, Padding = new Padding(24, 24, 16, 16) };
            var stepList = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
            for (int i = 0; i < WizardTitles.Length; i++)
            {
                var l = new Label { AutoSize = true, MaximumSize = new Size(240, 0), Margin = new Padding(0, 0, 0, 14), Font = Theme.Bold, ForeColor = Theme.Muted };
                stepList.Controls.Add(l);
                wizSteps.Add(l);
            }
            side.Controls.Add(stepList);
            side.Controls.Add(new Label { Text = "Set up your server", Dock = DockStyle.Top, Height = 54, Font = Theme.H1, ForeColor = Theme.Text });
            wizard.Controls.Add(side);

            // ---- bottom: navigation
            var nav2 = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Theme.Side, Padding = new Padding(24, 14, 24, 14) };
            var skip = new LinkLabel { Text = "Skip to the full app", AutoSize = true, LinkColor = Theme.Muted, ActiveLinkColor = Theme.Accent, Dock = DockStyle.Left, Padding = new Padding(0, 9, 0, 0), LinkBehavior = LinkBehavior.HoverUnderline };
            skip.LinkClicked += (s, e) => CloseWizard(false);
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
            wizSkipStep = Theme.Btn("Skip this step");
            wizSkipStep.Click += (s, e) => GoWizard(wizStep + 1);
            wizBack = Theme.Btn("Back");
            wizBack.Click += (s, e) => GoWizard(wizStep - 1);
            wizNext = Theme.Btn("Next", Theme.AccentBtn);
            wizNext.Click += async (s, e) => await WizardNext();
            right.Controls.AddRange(new Control[] { wizSkipStep, wizBack, wizNext });
            nav2.Controls.Add(right);
            nav2.Controls.Add(skip);
            wizard.Controls.Add(nav2);

            // ---- right: the current step
            var main = new Panel { Dock = DockStyle.Fill, Padding = new Padding(40, 30, 40, 16), BackColor = Theme.Bg };
            wizLog = new TextBox
            {
                Dock = DockStyle.Bottom, Height = 150, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Visible = false,
                BackColor = Theme.Input, ForeColor = Theme.Muted, BorderStyle = BorderStyle.None, Font = Theme.Mono, WordWrap = false,
            };
            main.Controls.Add(wizLog);
            wizStatus = new Label { Dock = DockStyle.Bottom, Height = 34, ForeColor = Theme.Muted, Padding = new Padding(0, 8, 0, 0), UseMnemonic = false };
            main.Controls.Add(wizStatus);
            wizBody = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true };
            main.Controls.Add(wizBody);
            wizTitle = new Label { Dock = DockStyle.Top, Height = 50, Font = Theme.H1, ForeColor = Theme.Text };
            main.Controls.Add(wizTitle);
            wizard.Controls.Add(main);
            main.BringToFront();
        }

        void GoWizard(int step)
        {
            wizStep = Math.Max(0, Math.Min(Steps.Length - 1, step));
            ShowWizardStep();
        }

        // ---- building each step's content

        int wy;
        Label WText(string t, Color? c = null)
        {
            var l = new Label { Text = t, AutoSize = true, MaximumSize = new Size(Math.Max(400, wizBody.ClientSize.Width - 30), 0), ForeColor = c ?? Theme.Text, Location = new Point(0, wy), UseMnemonic = false };
            wizBody.Controls.Add(l);
            wy += l.GetPreferredSize(new Size(l.MaximumSize.Width, 0)).Height + 12;
            return l;
        }

        T WRow<T>(string label, T c, int w) where T : Control
        {
            wizBody.Controls.Add(new Label { Text = label, Location = new Point(0, wy + 3), Size = new Size(190, 24), ForeColor = Theme.Text });
            c.Location = new Point(200, wy);
            if (w > 0) c.Width = w;
            wizBody.Controls.Add(c);
            wy += Math.Max(c.Height, 24) + 10;
            return c;
        }

        void WHint(string t)
        {
            var l = new Label { Text = t, AutoSize = true, MaximumSize = new Size(Math.Max(300, wizBody.ClientSize.Width - 230), 0), ForeColor = Theme.Muted, Location = new Point(200, wy - 6) };
            wizBody.Controls.Add(l);
            wy += l.GetPreferredSize(new Size(l.MaximumSize.Width, 0)).Height + 6;
        }

        Button WButton(string text, Color back, Func<Task> act)
        {
            var b = Theme.Btn(text, back);
            b.Location = new Point(0, wy);
            b.Click += async (s, e) => { await act(); WizardRefresh(); };
            wizBody.Controls.Add(b);
            wy += 48;
            return b;
        }

        void ShowWizardStep()
        {
            wizBody.SuspendLayout();
            foreach (Control c in wizBody.Controls.Cast<Control>().ToList()) c.Dispose();
            wizBody.Controls.Clear();
            wy = 0;
            wizTitle.Text = (wizStep + 1) + ".  " + WizardTitles[wizStep];
            wizLog.Visible = wizStep == 1 || wizStep == 2 || wizStep == 6;
            wizLog.Clear();
            var ini = File.Exists(S.IniPath) ? IniFile.Load(S.IniPath) : null;
            switch (wizStep)
            {
                case 0:
                    WText("Pick a short ID for this server and the password for its built-in admin account. The folders are fine as they are unless you want the server somewhere else.");
                    wzName = WRow("Server ID", Theme.Box(S.ServerName), 240);
                    WHint("Letters, numbers, - and _. It names the server's files. The name players see comes later.");
                    wzPw = WRow("Admin password", Theme.Box(S.AdminPassword), 240);
                    wzPw.UseSystemPasswordChar = true;
                    WHint("Log in as \"admin\" with this password to get admin powers in game. Stored encrypted on this PC.");
                    wzInstall = WRow("Install folder", Theme.Box(S.InstallDir), 420);
                    WHint("Where the server program goes (about 4 GB).");
                    wzData = WRow("Data folder", Theme.Box(S.DataDir), 420);
                    WHint("World saves, settings, accounts and logs.");
                    int suggest = S.IsSaved ? S.MaxRamGB : SuggestedMaxGB();
                    wzMem = WRow("Server memory (GB)", Theme.Num(2, 128, suggest), 80);
                    double pcGB = new Microsoft.VisualBasic.Devices.ComputerInfo().TotalPhysicalMemory / (1024.0 * 1024 * 1024);
                    WHint("The most memory the server may use. This PC has " + pcGB.ToString("0") + " GB; " + SuggestedMaxGB() + " GB leaves enough for Windows. 8 GB suits a few friends; big or modded worlds want more. You can change it later on the Performance page.");
                    break;
                case 1:
                    WText("ezpz downloads the free Project Zomboid dedicated server through Valve's SteamCMD, with an anonymous login: no Steam account needed. It's about 4 GB and can take a while.");
                    wzBranch = WRow("Steam branch", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.Input, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat }, 200);
                    wzBranch.Items.AddRange(new object[] { "public", "unstable" });
                    wzBranch.SelectedItem = S.Branch == "unstable" ? "unstable" : "public";
                    WHint("public is the current stable Build 42 (recommended). Players must be on the same branch.");
                    WButton("Install the server", Theme.AccentBtn, async () =>
                    {
                        cbBranch.Text = (string)wzBranch.SelectedItem;
                        await InstallFlow(false);
                    });
                    break;
                case 2:
                    WText("The server writes its settings files the first time it starts. ezpz starts it once, waits until it's up, then stops it. The first start can take a few minutes.");
                    WButton("Create the config", Theme.AccentBtn, GenerateConfigFlow);
                    break;
                case 3:
                    WText("Windows Firewall needs two rules: one lets players connect over UDP, the other keeps the server's private TCP ports (remote commands and live stats) closed to other machines.");
                    WText("Windows asks for administrator permission once.", Theme.Muted);
                    WButton("Set up the firewall", Theme.AccentBtn, FirewallFlow);
                    break;
                case 4:
                    WText("The basics players notice. Everything else is on the Settings page later.");
                    wzPublic = WRow("Server name", Theme.Box(ini != null ? ini.Get("PublicName", "") : ""), 360);
                    WHint("What players see in the server browser.");
                    wzJoinPw = WRow("Join password", Theme.Box(ini != null ? ini.Get("Password", "") : ""), 240);
                    WHint("Leave empty for no password.");
                    wzMax = WRow("Max players", Theme.Num(1, 100, ini != null ? ParseInt(ini.Get("MaxPlayers", "16"), 16) : 16), 80);
                    wzOpen = WRow("Anyone can join", Theme.Check("", ini == null || !ini.Get("Open", "true").Trim().Equals("false", StringComparison.OrdinalIgnoreCase)), 0);
                    WHint("Off makes it whitelist-only: only players with an account can join.");
                    wzPvp = WRow("PvP", Theme.Check("", ini != null && ini.Get("PVP", "true").Trim().Equals("true", StringComparison.OrdinalIgnoreCase)), 0);
                    if (ini == null) WText("Create the config first (step 3) to change these.", Theme.Warn);
                    break;
                case 5:
                    string port = ini != null ? ini.Get("DefaultPort", "16261") : "16261";
                    string port2 = ini != null ? ini.Get("UDPPort", "16262") : "16262";
                    WText("Players on your home network can already join. For friends over the internet, forward two UDP ports on your router to this PC.");
                    wzIps = WText("This PC: " + string.Join(" or ", LocalIps()) + "     Ports: UDP " + port + " and " + port2, Theme.Accent);
                    WText("In your router's settings, look for \"Port forwarding\", \"Virtual server\" or \"NAT\", and forward UDP " + port + " and " + port2 + " to this PC's address. Friends then join with your public IP and port " + port + ".");
                    WButton("Show my public IP", Theme.Card, async () => { await ShowPublicIp(); wzIps.Text = lblIps.Text; });
                    var fwd = WRow("Done?", Theme.Check("I've forwarded these ports on my router", S.PortsForwarded), 0);
                    fwd.CheckedChanged += (s, e) => { SetPortsForwarded(fwd.Checked); WizardRefresh(); };
                    break;
                case 6:
                    WText("Start the server. When it says it's up, it's ready for players, and setup is done.");
                    WButton("Start the server", Theme.AccentBtn, StartFlow);
                    break;
            }
            wizBody.ResumeLayout();
            WizardRefresh();
        }

        static int ParseInt(string s, int def) { int v; return int.TryParse(s, out v) ? v : def; }

        // ---- moving on

        async Task WizardNext()
        {
            if (wizStep == 0)
            {
                // the same save as the Setup page, through its fields
                tbName.Text = wzName.Text.Trim();
                tbAdminPw.Text = wzPw.Text;
                tbInstall.Text = wzInstall.Text.Trim();
                tbData.Text = wzData.Text.Trim();
                nMaxRam.Value = Math.Max(nMaxRam.Minimum, Math.Min(nMaxRam.Maximum, wzMem.Value));
                if (nMinRam.Value > nMaxRam.Value) nMinRam.Value = Math.Max(nMinRam.Minimum, Math.Min(4, nMaxRam.Value));
                if (wzPw.Text.Length == 0) { Warn("Choose an admin password."); return; }
                if (!SaveSetup(true)) return;
            }
            if (wizStep == 4 && File.Exists(S.IniPath))
            {
                var ini = IniFile.Load(S.IniPath);
                ini.Set("PublicName", wzPublic.Text.Trim());
                ini.Set("Password", wzJoinPw.Text);
                ini.Set("MaxPlayers", ((int)wzMax.Value).ToString());
                ini.Set("Open", wzOpen.Checked ? "true" : "false");
                ini.Set("PVP", wzPvp.Checked ? "true" : "false");
                ini.Save();
                Log("[ezpz] Saved the server's name, password, player limit, access and PvP from the setup wizard.");
                LoadConfig();
            }
            if (wizStep == Steps.Length - 1) { CloseWizard(true); return; }
            GoWizard(wizStep + 1);
            await Task.Yield();
        }

        // Called every second while the wizard is open, and after each action.
        void WizardRefresh()
        {
            if (wizard == null || !wizard.Visible) return;
            for (int i = 0; i < wizSteps.Count; i++)
            {
                bool done = WizardStepDone(i), optional = i == 4 || i == 5;
                wizSteps[i].Text = (i == wizStep ? "▶   " : done ? "●   " : "○   ") + WizardTitles[i];
                wizSteps[i].ForeColor = i == wizStep ? Theme.Text : done ? Theme.Accent : optional ? Theme.Info : Theme.Muted;
            }
            bool stepDone = WizardStepDone(wizStep);
            bool optionalStep = wizStep == 4 || wizStep == 5;
            wizBack.Enabled = wizStep > 0 && busy == null;
            wizNext.Text = wizStep == Steps.Length - 1 ? "Finish" : "Next";
            // step 1 saves on Next; the others need their action done first (optional ones don't)
            wizNext.Enabled = busy == null && (wizStep == 0 || optionalStep || stepDone);
            wizNext.BackColor = wizNext.Enabled ? Theme.AccentBtn : Theme.Card;
            wizSkipStep.Visible = wizStep == 3 && !stepDone;   // some can't or won't change the firewall here
            foreach (var b in wizBody.Controls.OfType<Button>()) b.Enabled = busy == null && !(wizStep == 6 && server.State != ServerState.Stopped);

            string st;
            if (busy != null) st = busy;
            else if (wizStep == 1) st = stepDone ? "✓  Installed: " + (SteamCmd.InstalledBuildInfo(S) ?? "the server") + "." : "Not installed yet.";
            else if (wizStep == 2) st = stepDone ? "✓  The config is ready: " + S.IniPath : server.State != ServerState.Stopped ? "Starting the server to create its config… (" + StateText().ToLower() + ")" : "No config yet.";
            else if (wizStep == 3) st = stepDone ? "✓  The firewall rules are in place." : "Not set up yet.";
            else if (wizStep == 6) st = stepDone ? "✓  The server is up and setup is finished. Click Finish." : server.State != ServerState.Stopped ? "Starting… (" + StateText().ToLower() + "). This can take a few minutes." : "Not started yet.";
            else st = "";
            wizStatus.Text = st;
            wizStatus.ForeColor = st.StartsWith("✓") ? Theme.Accent : Theme.Muted;
        }

        // Console lines go to the wizard's progress box too, while a step that runs something is open.
        void WizardLogLine(string line)
        {
            if (wizLog == null || !wizLog.Visible || wizard == null || !wizard.Visible) return;
            if (wizLog.TextLength > 60000) wizLog.Text = wizLog.Text.Substring(wizLog.TextLength - 30000);
            wizLog.AppendText(line + "\r\n");
        }
    }
}
