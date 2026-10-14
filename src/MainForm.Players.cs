using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // Players & Admin page: a roster of everyone who has joined (from the connection logs),
    // live online status (from the "players" command), and per-player actions.
    public partial class MainForm
    {
        ListView lvPlayers;
        Label lblPlayerCounts, lblSel;
        CheckBox chkAutoPlayers, chkShowOffline;
        ComboBox cbRole, cbTpTarget, cbPerk;
        TextBox tbBroadcast;
        IdPicker itemPicker, vehiclePicker;
        Label lblItemName, lblVehicleName;
        ItemCatalog catalog = new ItemCatalog();
        bool catalogLoading;
        NumericUpDown nItemCount, nXp, nHorde;

        Dictionary<string, PlayerInfo> roster = new Dictionary<string, PlayerInfo>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> onlineNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> roleSetHere = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> captured = new List<string>();
        DateTime captureUntil = DateTime.MinValue, quietUntil = DateTime.MinValue, lastPoll = DateTime.MinValue, lastLogRead = DateTime.MinValue;
        bool pollPending;

        static readonly Regex PlayerLine = new Regex(@"^\s*-\s*(\S.*)$");

        void BuildPlayers()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false, Padding = new Padding(20, 14, 20, 14) };
            content.Controls.Add(p);
            pgPlayers = p;

            // --- big player list
            lvPlayers = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
                BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10.5f)
            };
            lvPlayers.Columns.Add("Player", 132);
            lvPlayers.Columns.Add("Role", 80);
            lvPlayers.Columns.Add("Last seen", 118);
            lvPlayers.Columns.Add("Steam ID", 138);
            lvPlayers.Columns.Add("IP", 100);
            // the IP column takes whatever width is left, so the list doesn't need a sideways scroll bar
            lvPlayers.ClientSizeChanged += (s, e) =>
            {
                int used = 0;
                for (int c = 0; c < lvPlayers.Columns.Count - 1; c++) used += lvPlayers.Columns[c].Width;
                lvPlayers.Columns[lvPlayers.Columns.Count - 1].Width = Math.Max(90, lvPlayers.ClientSize.Width - used - 1);
            };
            lvPlayers.SelectedIndexChanged += (s, e) => OnPlayerSelected();
            lvPlayers.ContextMenuStrip = BuildPlayerMenu();
            lvPlayers.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var hit = lvPlayers.HitTest(e.Location);
                if (hit.Item != null) hit.Item.Selected = true;
            };
            p.Controls.Add(lvPlayers);
            p.Controls.Add(BuildSheet());   // character sheet under the list

            // --- per-player action panel (added before the bottom row so the bottom row spans the full width)
            p.Controls.Add(BuildPlayerActions());

            // --- server-wide row under the list
            // sizes itself to the buttons and wraps when the window is narrow, so nothing gets cut off
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 10, 0, 0), WrapContents = true };
            tbBroadcast = Theme.Box("");
            tbBroadcast.Width = 330;
            tbBroadcast.Margin = new Padding(0, 4, 8, 0);
            tbBroadcast.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Broadcast(); e.SuppressKeyPress = true; } };
            var bc = Theme.Btn("Broadcast to all", Theme.AccentBtn);
            bc.Click += (s, e) => Broadcast();
            var mods = Theme.Btn("Check mod updates");
            mods.Click += (s, e) => Cmd("checkModsNeedUpdate");
            var help = Theme.Btn("All commands");
            help.Click += (s, e) => { Cmd("help"); ShowPage(PConsole); };
            bottom.Controls.AddRange(new Control[] { tbBroadcast, bc, mods, help });
            p.Controls.Add(bottom);

            // --- toolbar + title
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, WrapContents = false };
            var refresh = Theme.Btn("Refresh", Theme.AccentBtn);
            refresh.Click += (s, e) => { PollOnline(); RefreshPlayers(true); };
            chkAutoPlayers = Theme.Check("Auto-refresh", true);
            chkAutoPlayers.Visible = false;   // the list now updates live from the server's connection log
            chkAutoPlayers.Margin = new Padding(8, 8, 8, 0);
            chkShowOffline = Theme.Check("Show offline players", true);
            chkShowOffline.Margin = new Padding(8, 8, 8, 0);
            chkShowOffline.CheckedChanged += (s, e) => RebuildPlayerList();
            lblPlayerCounts = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(16, 9, 0, 0) };
            top.Controls.AddRange(new Control[] { refresh, chkAutoPlayers, chkShowOffline, lblPlayerCounts });
            p.Controls.Add(top);
            var title = new Label { Text = "Players & Admin", Font = Theme.H1, Dock = DockStyle.Top, Height = 44, ForeColor = Theme.Text };
            p.Controls.Add(title);

            // the character sheet shrinks on short windows so the player list always keeps some room
            EventHandler fit = (s, e) =>
            {
                int room = p.ClientSize.Height - p.Padding.Vertical - title.Height - top.Height - bottom.Height;
                sheetPanel.Height = Math.Max(150, Math.Min(330, room - 160));
            };
            p.Resize += fit;
            bottom.SizeChanged += fit;
        }

        Control BuildPlayerActions()
        {
            var host = new Panel { Dock = DockStyle.Right, Width = 390, Padding = new Padding(16, 0, 0, 0), BackColor = Theme.Bg };
            var f = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
                BackColor = Theme.Side, Padding = new Padding(14, 10, 8, 10)
            };
            host.Controls.Add(f);

            lblSel = new Label { AutoSize = true, Font = Theme.H2, ForeColor = Theme.Text, MaximumSize = new Size(330, 0), Margin = new Padding(0, 0, 0, 6) };
            f.Controls.Add(lblSel);

            Section(f, "Role");
            cbRole = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, BackColor = Theme.Input, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 3, 8, 6) };
            cbRole.Items.AddRange(GameData.Roles);
            cbRole.SelectedIndex = 0;
            var setRole = Btn("Set role", (s, e) => SetRole(cbRole.Text));
            Line(f, cbRole, setRole);
            Line(f, Btn("Make admin", (s, e) => SetRole("admin")), Btn("Moderator", (s, e) => SetRole("moderator")), Btn("Remove role", (s, e) => SetRole("user")));

            Section(f, "Moderation");
            Line(f, Btn("Kick…", (s, e) => Kick()), Btn("Ban…", (s, e) => Ban(false)), Btn("Ban + IP…", (s, e) => Ban(true)), Btn("Unban", (s, e) => PCmd("unbanuser \"{0}\"")));
            Line(f, Btn("Mute voice", (s, e) => PCmd("voiceban \"{0}\" -true")), Btn("Unmute voice", (s, e) => PCmd("voiceban \"{0}\" -false")));
            Line(f, Btn("View logs", (s, e) => { if (NeedSel()) ShowLogsFor(Sel); }), Btn("Set password…", (s, e) => SetPassword()));
            Line(f, Btn("Remove from whitelist", (s, e) => { if (ConfirmSel("Remove {0} from the whitelist? They won't be able to log in on a whitelist-only server.")) PCmd("removeuserfromwhitelist \"{0}\""); }));

            Section(f, "Admin powers");
            Line(f, Lbl("God mode"), Btn("On", (s, e) => PCmd("godmodeplayer \"{0}\" -true")), Btn("Off", (s, e) => PCmd("godmodeplayer \"{0}\" -false")));
            Line(f, Lbl("Invisible"), Btn("On", (s, e) => PCmd("invisibleplayer \"{0}\" -true")), Btn("Off", (s, e) => PCmd("invisibleplayer \"{0}\" -false")));
            Line(f, Lbl("No-clip"), Btn("On", (s, e) => PCmd("noclip \"{0}\" -true")), Btn("Off", (s, e) => PCmd("noclip \"{0}\" -false")));

            Section(f, "Teleport");
            cbTpTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, BackColor = Theme.Input, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 3, 8, 6) };
            Line(f, Lbl("Other player"), cbTpTarget);
            Line(f, Btn("Send to them", (s, e) => Teleport(false)), Btn("Bring them here", (s, e) => Teleport(true)));

            Section(f, "Give");
            itemPicker = Picker(f, "Base.Axe", () => catalog.Items, GiveItem, out lblItemName);
            nItemCount = Theme.Num(1, 999, 1);
            nItemCount.Width = 60; nItemCount.Margin = new Padding(0, 4, 6, 6);
            Line(f, Lbl("How many"), nItemCount, Btn("Give item", (s, e) => GiveItem()));
            cbPerk = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, BackColor = Theme.Input, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 3, 6, 6) };
            cbPerk.Items.AddRange(GameData.Perks);
            cbPerk.SelectedItem = "Woodwork";
            nXp = Theme.Num(1, 100000, 100);
            nXp.Width = 70; nXp.Margin = new Padding(0, 4, 6, 6);
            Line(f, cbPerk, nXp, Btn("Give XP", (s, e) => PCmd("addxp \"{0}\" " + cbPerk.Text + "=" + nXp.Value)));
            vehiclePicker = Picker(f, "Base.VanAmbulance", () => catalog.Vehicles, GiveVehicle, out lblVehicleName);
            Line(f, Btn("Spawn vehicle", (s, e) => GiveVehicle()));
            f.Controls.Add(Hint("Type part of a name or ID, like \"fire axe\" or \"ambulance\", and pick from the list. It comes from this server's own files and its enabled mods."));
            f.Scroll += (s, e) => { itemPicker.Hide(); vehiclePicker.Hide(); };
            f.MouseWheel += (s, e) => { itemPicker.Hide(); vehiclePicker.Hide(); };

            Section(f, "Trouble near them");
            nHorde = Theme.Num(1, 500, 25);
            nHorde.Width = 60; nHorde.Margin = new Padding(0, 4, 6, 6);
            Line(f, nHorde, Btn("Spawn horde", (s, e) => { if (ConfirmSel("Spawn " + nHorde.Value + " zombies next to {0}?")) PCmd("createhorde " + nHorde.Value + " \"{0}\""); }));
            Line(f, Btn("Lightning strike", (s, e) => PCmd("lightning \"{0}\"")), Btn("Thunder", (s, e) => PCmd("thunder \"{0}\"")));
            // a scrolling panel ignores its bottom padding, which clipped this last row; leave room after it
            f.Controls.Add(new Panel { Size = new Size(1, 20), Margin = new Padding(0), BackColor = Color.Transparent });

            OnPlayerSelected();
            return host;
        }

        // ---------- small layout helpers for the action panel
        void Section(FlowLayoutPanel f, string t)
        {
            f.Controls.Add(new Label { Text = t, Font = Theme.Bold, ForeColor = Theme.Accent, AutoSize = true, Margin = new Padding(0, 12, 0, 4) });
        }

        static void Line(FlowLayoutPanel f, params Control[] cs)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
            row.Controls.AddRange(cs);
            f.Controls.Add(row);
        }

        static Button Btn(string text, EventHandler click)
        {
            var b = Theme.Btn(text);
            b.Margin = new Padding(0, 0, 6, 6);
            b.Padding = new Padding(6, 3, 6, 3);
            b.Click += click;
            return b;
        }

        static Label Lbl(string t)
        {
            return new Label { Text = t, AutoSize = false, Size = new Size(90, 26), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text, Margin = new Padding(0, 0, 6, 6) };
        }

        // An item or vehicle box with suggestions, and the chosen entry's name under it.
        IdPicker Picker(FlowLayoutPanel f, string text, Func<List<CatalogEntry>> source, Action submit, out Label name)
        {
            var p = new IdPicker(text) { Source = source };
            p.Box.Width = 330;
            p.Box.Margin = new Padding(0, 4, 0, 2);
            p.Submit += submit;
            var l = Hint("");
            l.Margin = new Padding(0, 0, 0, 4);
            p.Box.TextChanged += (s, e) => ShowPickedName(p, l);
            f.Controls.Add(p.Box);
            f.Controls.Add(l);
            name = l;
            return p;
        }

        void ShowPickedName(IdPicker p, Label l)
        {
            bool vehicle = p == vehiclePicker;
            string id = p.Box.Text.Trim();
            var c = vehicle ? catalog.FindVehicle(id) : catalog.FindItem(id);
            if (id == "") l.Text = "";
            else if (c != null) { l.Text = c.Name + (c.Source != "" ? "  ·  from " + c.Source : ""); l.ForeColor = Theme.Accent; }
            else if ((vehicle ? catalog.Vehicles : catalog.Items).Count == 0) { l.Text = ""; }
            else { l.Text = "Not a known " + (vehicle ? "vehicle" : "item") + " ID on this server."; l.ForeColor = Theme.Warn; }
        }

        // Reads the item and vehicle lists in the background when the install or the mod list changed.
        void RefreshCatalog()
        {
            if (catalogLoading) return;
            string key;
            try { key = ItemCatalog.KeyFor(S); } catch { return; }
            if (key == catalog.Key) return;
            catalogLoading = true;
            var settings = S;
            Task.Run(() => ItemCatalog.Load(settings)).ContinueWith(t => Ui(() =>
            {
                catalogLoading = false;
                if (t.Exception != null || settings != S) return;
                catalog = t.Result;
                ShowPickedName(itemPicker, lblItemName);
                ShowPickedName(vehiclePicker, lblVehicleName);
            }));
        }

        static Label Hint(string t)
        {
            return new Label { Text = t, AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = Theme.Muted, Margin = new Padding(0, 0, 0, 6) };
        }

        ContextMenuStrip BuildPlayerMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Make admin", null, (s, e) => SetRole("admin"));
            m.Items.Add("Make moderator", null, (s, e) => SetRole("moderator"));
            m.Items.Add("Remove role", null, (s, e) => SetRole("user"));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Kick…", null, (s, e) => Kick());
            m.Items.Add("Ban…", null, (s, e) => Ban(false));
            m.Items.Add("Mute voice", null, (s, e) => PCmd("voiceban \"{0}\" -true"));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("God mode on", null, (s, e) => PCmd("godmodeplayer \"{0}\" -true"));
            m.Items.Add("God mode off", null, (s, e) => PCmd("godmodeplayer \"{0}\" -false"));
            m.Items.Add("Lightning strike", null, (s, e) => PCmd("lightning \"{0}\""));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("View this player's logs", null, (s, e) => { if (Sel != null) ShowLogsFor(Sel); });
            m.Items.Add("Copy name", null, (s, e) => { if (Sel != null) Clipboard.SetText(Sel); });
            m.Items.Add("Copy Steam ID", null, (s, e) =>
            {
                PlayerInfo pi;
                if (Sel != null && roster.TryGetValue(Sel, out pi) && pi.SteamId != "") Clipboard.SetText(pi.SteamId);
            });
            m.Opening += (s, e) => { if (Sel == null) e.Cancel = true; };
            return m;
        }

        // ---------- data

        string Sel
        {
            get { return lvPlayers.SelectedItems.Count == 1 ? (string)lvPlayers.SelectedItems[0].Tag : null; }
        }

        void RefreshPlayers(bool readLogs)
        {
            if (server.State != ServerState.Running) { onlineNow.Clear(); RebuildPlayerList(); }
            else RebuildPlayerList();
            if (readLogs || (DateTime.Now - lastLogRead).TotalSeconds > 60)
            {
                lastLogRead = DateTime.Now;
                LoadCharacters();
                Task.Run(() => GameData.ReadRoster(S)).ContinueWith(t =>
                {
                    if (t.Exception == null) Ui(() => { roster = t.Result; RebuildPlayerList(); });
                });
            }
        }

        void PollOnline()
        {
            if (pollPending) return;
            lock (captured) captured.Clear();
            captureUntil = quietUntil = DateTime.Now.AddSeconds(3);
            try { server.SendCommand("players", false); }
            catch { return; }
            pollPending = true;
            lastPoll = DateTime.Now;
            Task.Delay(1500).ContinueWith(t => Ui(() =>
            {
                pollPending = false;
                var before = onlineNow;
                lock (captured) onlineNow = new HashSet<string>(captured, StringComparer.OrdinalIgnoreCase);
                // the server's own answer wins: correct the log feed if it drifted (e.g. a missed timeout)
                feed.Online.Clear();
                feed.Online.UnionWith(onlineNow);
                DiscordPlayersChanged(before, onlineNow);
                RebuildPlayerList();
            }));
        }

        // Who is online comes from the server's connection log (no console commands, so nothing in PZ's
        // logs). A quiet "players" check every 5 minutes corrects anything the log missed.
        readonly ConnectionFeed feed = new ConnectionFeed();

        void PlayersTick()
        {
            if (server.State != ServerState.Running)
            {
                if (onlineNow.Count > 0 || ticks % 10 == 0) { feed.Reset(); if (onlineNow.Count > 0) { onlineNow.Clear(); if (currentPage == PPlayers || currentPage == PWorld) RebuildPlayerList(); } }
                return;
            }
            if (ticks % 2 == 0)
            {
                bool rebuilt;
                if (feed.Poll(S, out rebuilt))
                {
                    var before = onlineNow;
                    onlineNow = new HashSet<string>(feed.Online, StringComparer.OrdinalIgnoreCase);
                    DiscordPlayersChanged(before, onlineNow);
                    if (currentPage == PPlayers || currentPage == PWorld || currentPage == PHome) RebuildPlayerList();
                }
            }
            if ((DateTime.Now - lastPoll).TotalMinutes >= 5 && (DateTime.Now - server.StartedAt).TotalMinutes >= 2) PollOnline();
            if ((currentPage == PPlayers) && (DateTime.Now - lastLogRead).TotalSeconds > 60) RefreshPlayers(true);
            if (currentPage == PPlayers) SheetTick();
        }

        // All server output comes through here: hide the manager's own polling, pick up facts, then log.
        void OnServerOutput(string text)
        {
            foreach (var raw in text.Split('\n'))
            {
                string l = raw.TrimEnd('\r');
                if (CollapseHarmless(l)) continue;
                var ebs = GameData.EbsLine.Match(l);
                if (ebs.Success) { ebsKhz = ebs.Groups[1].Value; ebsWhen = DateTime.Now; Ui(UpdateEbsLabel); }
                bool isPlayerLine = l.Contains("Players connected") || PlayerLine.IsMatch(l);
                if (DateTime.Now < captureUntil)
                {
                    var m = PlayerLine.Match(l);
                    if (m.Success) lock (captured) captured.Add(m.Groups[1].Value.Trim());
                }
                if (DateTime.Now < quietUntil && isPlayerLine) continue;
                if (CaptureStats(l)) continue;
                Log(l);
            }
        }

        // B42 prints a full stack trace for every mod that has no animation folders. That is normal
        // (most mods have none), so collapse each one into a single grey line.
        bool collapsingTrace;
        static readonly System.Text.RegularExpressions.Regex MissingModFolder =
            new System.Text.RegularExpressions.Regex(@"NoSuchFileException: (.*?[\\/]mods[\\/][^\\/]+)[\\/].*?[\\/]media[\\/](\w+)");

        bool CollapseHarmless(string l)
        {
            if (l.Contains("AdvancedAnimator$1.visitFileFailed")) { collapsingTrace = true; return true; }
            if (!collapsingTrace) return false;
            if (l.StartsWith("\t") || l.StartsWith(" "))
            {
                var m = MissingModFolder.Match(l);
                if (m.Success) Log("[ezpz] (harmless) Mod " + System.IO.Path.GetFileName(m.Groups[1].Value) + " has no " + m.Groups[2].Value + " folder.");
                return true;
            }
            collapsingTrace = false;
            return false;
        }

        void RebuildPlayerList()
        {
            if (lvPlayers == null) return;
            string keep = Sel;
            var names = new HashSet<string>(roster.Keys, StringComparer.OrdinalIgnoreCase);
            names.UnionWith(onlineNow);
            var ordered = names.OrderByDescending(n => onlineNow.Contains(n))
                               .ThenByDescending(n => roster.ContainsKey(n) ? roster[n].LastSeen : DateTime.MaxValue)
                               .ToList();
            lvPlayers.BeginUpdate();
            lvPlayers.Items.Clear();
            foreach (var n in ordered)
            {
                bool on = onlineNow.Contains(n);
                if (!on && !chkShowOffline.Checked) continue;
                PlayerInfo pi;
                roster.TryGetValue(n, out pi);
                string role;
                if (!roleSetHere.TryGetValue(n, out role)) role = pi != null ? pi.Role : "";
                string seen = on ? "now" : pi != null && pi.LastSeen > DateTime.MinValue ? FriendlyTime(pi.LastSeen) : "";
                var it = new ListViewItem(new[] { (on ? "● " : "    ") + n, role, seen, pi != null ? pi.SteamId : "", pi != null ? pi.Ip : "" });
                it.Tag = n;
                it.UseItemStyleForSubItems = true;
                it.ForeColor = on ? Theme.Accent : Theme.Muted;
                lvPlayers.Items.Add(it);
                if (n.Equals(keep, StringComparison.OrdinalIgnoreCase)) it.Selected = true;
            }
            lvPlayers.EndUpdate();
            lblPlayerCounts.Text = onlineNow.Count + " online  ·  " + names.Count + " known" +
                (server.State == ServerState.Running ? "" : "  ·  server is stopped");

            // teleport + weather target lists show online players
            FillPlayerCombo(cbTpTarget, onlineNow.Where(x => !x.Equals(Sel, StringComparison.OrdinalIgnoreCase)));
            FillPlayerCombo(cbWxPlayer, onlineNow);
            OnPlayerSelected();
        }

        static void FillPlayerCombo(ComboBox cb, IEnumerable<string> items)
        {
            if (cb == null) return;
            string cur = cb.Text;
            cb.Items.Clear();
            foreach (var x in items.OrderBy(x => x)) cb.Items.Add(x);
            if (cur != "" && cb.Items.Contains(cur)) cb.SelectedItem = cur;
            else if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        }

        static string FriendlyTime(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalHours < 1) return (int)d.TotalMinutes + " min ago";
            if (t.Date == DateTime.Today) return "today " + t.ToString("HH:mm");
            if (t.Date == DateTime.Today.AddDays(-1)) return "yesterday " + t.ToString("HH:mm");
            return t.ToString("yyyy-MM-dd HH:mm");
        }

        void OnPlayerSelected()
        {
            UpdateSheet();
            if (lblSel == null) return;
            string n = Sel;
            if (n == null) { lblSel.Text = "Select a player on the left"; lblSel.ForeColor = Theme.Muted; return; }
            bool on = onlineNow.Contains(n);
            lblSel.Text = n + (on ? "   ● online" : "   (offline)");
            lblSel.ForeColor = on ? Theme.Accent : Theme.Text;
            string role;
            PlayerInfo pi;
            if (!roleSetHere.TryGetValue(n, out role)) role = roster.TryGetValue(n, out pi) ? pi.Role : "";
            if (cbRole.Items.Contains(role)) cbRole.SelectedItem = role;
            FillPlayerCombo(cbTpTarget, onlineNow.Where(x => !x.Equals(n, StringComparison.OrdinalIgnoreCase)));
        }

        // ---------- actions

        static string Q(string s) { return (s ?? "").Replace("\"", "'"); }

        bool NeedSel()
        {
            if (Sel != null) return true;
            Warn("Select a player in the list first.");
            return false;
        }

        void PCmd(string fmt)
        {
            if (!NeedSel()) return;
            Cmd(string.Format(fmt, Q(Sel)));
        }

        bool ConfirmSel(string fmt)
        {
            return NeedSel() && Confirm(string.Format(fmt, Sel));
        }

        void SetRole(string role)
        {
            if (!NeedSel()) return;
            if (role == "admin" && !Confirm("Give " + Sel + " full admin rights? Admins can change server settings and other players' roles.")) return;
            Cmd("setaccesslevel \"" + Q(Sel) + "\" \"" + role + "\"");
            roleSetHere[Sel] = role;
            RebuildPlayerList();
        }

        void Kick()
        {
            if (!NeedSel()) return;
            string r = Prompt.Ask(this, "Kick " + Sel, "Reason (optional, shown to the player):", "", false);
            if (r == null) return;
            Cmd("kickuser \"" + Q(Sel) + "\"" + (r.Trim() != "" ? " -r \"" + Q(r.Trim()) + "\"" : ""));
            Task.Delay(2000).ContinueWith(t => Ui(() => RefreshPlayers(false)));
        }

        void Ban(bool ip)
        {
            if (!NeedSel()) return;
            string r = Prompt.Ask(this, "Ban " + Sel, (ip ? "Ban " + Sel + " AND their IP address." : "Ban " + Sel + ".") + "\nReason (optional):", "", false);
            if (r == null) return;
            Cmd("banuser \"" + Q(Sel) + "\"" + (ip ? " -ip" : "") + (r.Trim() != "" ? " -r \"" + Q(r.Trim()) + "\"" : ""));
            Task.Delay(2000).ContinueWith(t => Ui(() => RefreshPlayers(false)));
        }

        void SetPassword()
        {
            if (!NeedSel()) return;
            string pw = Prompt.Ask(this, "New password for " + Sel, "Give this new password to " + Sel + ". They'll need it to log in.", "", false);
            if (string.IsNullOrWhiteSpace(pw)) return;
            server.SendCommand("setpassword \"" + Q(Sel) + "\" \"" + Q(pw) + "\"", false);
            Log("> setpassword \"" + Sel + "\" \"********\"");
        }

        void Teleport(bool bring)
        {
            if (!NeedSel()) return;
            string other = cbTpTarget.Text;
            if (other == "") { Warn("Pick the other (online) player first."); return; }
            if (bring) Cmd("teleportplayer \"" + Q(other) + "\" \"" + Q(Sel) + "\"");
            else Cmd("teleportplayer \"" + Q(Sel) + "\" \"" + Q(other) + "\"");
        }

        void GiveItem()
        {
            if (!NeedSel()) return;
            itemPicker.Hide();
            string id = itemPicker.Box.Text.Trim();
            if (id == "") { Warn("Type an item name or ID first."); return; }
            if (!id.Contains(".")) id = "Base." + id;
            if (catalog.Items.Count > 0 && catalog.FindItem(id) == null && !Confirm(id + " isn't an item this server knows. Send it anyway?")) return;
            Cmd("additem \"" + Q(Sel) + "\" \"" + Q(id) + "\" " + nItemCount.Value);
        }

        void GiveVehicle()
        {
            if (!NeedSel()) return;
            vehiclePicker.Hide();
            string id = vehiclePicker.Box.Text.Trim();
            if (id == "") { Warn("Type a vehicle name or ID first."); return; }
            if (!id.Contains(".")) id = "Base." + id;
            if (catalog.Vehicles.Count > 0 && catalog.FindVehicle(id) == null && !Confirm(id + " isn't a vehicle this server knows. Send it anyway?")) return;
            Cmd("addvehicle \"" + Q(id) + "\" \"" + Q(Sel) + "\"");
        }

        void Broadcast()
        {
            string m = tbBroadcast.Text.Trim();
            if (m == "") return;
            Cmd("servermsg \"" + Q(m) + "\"");
            tbBroadcast.Clear();
        }
    }
}
