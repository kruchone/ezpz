using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // Logs page: one searchable timeline of chat, admin actions, console commands, PvP, player actions,
    // joins and skill level-ups, filterable by player, type and time. For "who said or did what, and when".
    public partial class MainForm
    {
        Panel pgLogs;
        ListView lvLogs;
        TextBox tbLogSearch, tbLogDetail;
        ComboBox cbLogPlayer, cbLogRange;
        Label lblLogCount;
        readonly Dictionary<string, CheckBox> logKindChecks = new Dictionary<string, CheckBox>();
        LogViewerData logData = new LogViewerData();
        List<LogEntry> logAll = new List<LogEntry>(), logShown = new List<LogEntry>();
        bool logLoading;
        DateTime logLoadedAt = DateTime.MinValue;

        static readonly Dictionary<string, Color> KindColors = new Dictionary<string, Color>
        {
            { "Chat", Theme.Text }, { "Admin", Theme.Warn }, { "Console", Theme.Warn }, { "PvP", Theme.Danger },
            { "Action", Theme.Muted }, { "Join", Theme.Info }, { "Skill", Theme.Accent },
        };

        void BuildLogs()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false, Padding = new Padding(20, 14, 20, 14) };
            content.Controls.Add(p);
            pgLogs = p;

            lvLogs = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
                VirtualMode = true, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9.5f)
            };
            lvLogs.Columns.Add("Time", 120);
            lvLogs.Columns.Add("Type", 70);
            lvLogs.Columns.Add("Player", 130);
            lvLogs.Columns.Add("What", 420);
            lvLogs.Columns.Add("Where", 120);
            lvLogs.RetrieveVirtualItem += (s, e) => e.Item = MakeLogItem(logShown[e.ItemIndex]);
            lvLogs.SelectedIndexChanged += (s, e) => ShowLogDetail();
            lvLogs.ContextMenuStrip = BuildLogMenu();
            lvLogs.DoubleClick += (s, e) => OpenLogOnMap();
            lvLogs.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var hit = lvLogs.HitTest(e.Location);
                if (hit.Item != null) { lvLogs.SelectedIndices.Clear(); lvLogs.SelectedIndices.Add(hit.Item.Index); }
            };
            p.Controls.Add(lvLogs);

            tbLogDetail = new TextBox
            {
                Dock = DockStyle.Bottom, Height = 64, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.Side, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Theme.Mono
            };
            p.Controls.Add(tbLogDetail);

            // filters: row 2 = types and time range, row 1 = search and player
            var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            foreach (var k in LogViewerData.Kinds)
            {
                var c = Theme.Check(k, true);
                c.ForeColor = KindColors[k];
                c.Margin = new Padding(0, 4, 12, 0);
                c.CheckedChanged += (s, e) => FilterLogs();
                logKindChecks[k] = c;
                row2.Controls.Add(c);
            }
            cbLogRange = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, BackColor = Theme.Input, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Margin = new Padding(12, 0, 0, 0) };
            cbLogRange.Items.AddRange(new object[] { "Last hour", "Today", "Last 3 days", "Last 7 days", "Everything" });
            cbLogRange.SelectedIndex = 3;
            cbLogRange.SelectedIndexChanged += (s, e) => FilterLogs();
            row2.Controls.Add(cbLogRange);
            p.Controls.Add(row2);

            var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false };
            row1.Controls.Add(new Label { Text = "Search", AutoSize = true, Margin = new Padding(0, 9, 6, 0) });
            tbLogSearch = Theme.Box("");
            tbLogSearch.Width = 300;
            tbLogSearch.Margin = new Padding(0, 6, 16, 0);
            tbLogSearch.TextChanged += (s, e) => FilterLogs();
            row1.Controls.Add(tbLogSearch);
            row1.Controls.Add(new Label { Text = "Player", AutoSize = true, Margin = new Padding(0, 9, 6, 0) });
            cbLogPlayer = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 180, BackColor = Theme.Input, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 5, 10, 0) };
            cbLogPlayer.TextChanged += (s, e) => FilterLogs();
            row1.Controls.Add(cbLogPlayer);
            var refresh = Theme.Btn("Refresh");
            refresh.Click += (s, e) => LoadLogs(true);
            var open = Theme.Btn("Open logs folder");
            open.Click += (s, e) => OpenFolder(S.LogsDir);
            lblLogCount = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(10, 10, 0, 0) };
            row1.Controls.AddRange(new Control[] { refresh, open, lblLogCount });
            p.Controls.Add(row1);

            p.Controls.Add(new Label
            {
                Text = "Who said or did what, and when, from the server's own logs. Double-click a line with a location to see it on pzmap.org; right-click for more.",
                Dock = DockStyle.Top, Height = 26, ForeColor = Theme.Muted
            });
            p.Controls.Add(new Label { Text = "Logs", Font = Theme.H1, Dock = DockStyle.Top, Height = 44, ForeColor = Theme.Text });
        }

        ListViewItem MakeLogItem(LogEntry e)
        {
            var it = new ListViewItem(new[] { e.Time.ToString("MM-dd HH:mm:ss"), e.Kind, e.Player, e.Text, e.Where });
            Color c;
            // PvP safety records are routine (every login/logout); keep red for real PvP events
            bool routine = e.Kind == "PvP" && e.Text.StartsWith("PvP safety");
            it.ForeColor = e.Important ? Theme.Danger : routine ? Theme.Muted : KindColors.TryGetValue(e.Kind, out c) ? c : Theme.Text;
            return it;
        }

        ContextMenuStrip BuildLogMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Show only this player", null, (s, e) => { var x = SelectedLog(); if (x != null && x.Player != "" && !x.Player.StartsWith("(")) cbLogPlayer.Text = x.Player; });
            m.Items.Add("Open this player on the Players page", null, (s, e) =>
            {
                var x = SelectedLog();
                if (x == null || x.Player == "" || x.Player.StartsWith("(")) return;
                ShowPage(PPlayers);
                var item = lvPlayers.Items.Cast<ListViewItem>().FirstOrDefault(i => string.Equals((string)i.Tag, x.Player, StringComparison.OrdinalIgnoreCase));
                if (item != null) { item.Selected = true; item.EnsureVisible(); }
            });
            m.Items.Add("Show everything around this time", null, (s, e) =>
            {
                var x = SelectedLog();
                if (x == null) return;
                cbLogPlayer.Text = ""; tbLogSearch.Text = "";
                foreach (var c in logKindChecks.Values) c.Checked = true;
                int i = logShown.IndexOf(x);
                if (i >= 0) { lvLogs.SelectedIndices.Clear(); lvLogs.SelectedIndices.Add(i); lvLogs.EnsureVisible(i); }
            });
            var map = m.Items.Add("View this spot on pzmap.org", null, (s, e) => OpenLogOnMap());
            m.Opening += (s, e) => { var x = SelectedLog(); map.Enabled = x != null && Util.MapUrl(x.Where) != null; };
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Copy line", null, (s, e) => { var x = SelectedLog(); if (x != null) Clipboard.SetText(x.Raw); });
            m.Items.Add("Open the log file", null, (s, e) => { var x = SelectedLog(); if (x != null) Process.Start("notepad.exe", Util.QuoteArg(x.File)); });
            return m;
        }

        // Opens the selected line's location on the community map in the default browser.
        void OpenLogOnMap()
        {
            var x = SelectedLog();
            var url = x == null ? null : Util.MapUrl(x.Where);
            if (url != null) Util.OpenUrl(url);
        }

        LogEntry SelectedLog()
        {
            return lvLogs.SelectedIndices.Count == 1 && lvLogs.SelectedIndices[0] < logShown.Count ? logShown[lvLogs.SelectedIndices[0]] : null;
        }

        void ShowLogDetail()
        {
            var x = SelectedLog();
            tbLogDetail.Text = x == null ? "" : x.Raw + Environment.NewLine + "from " + System.IO.Path.GetFileName(x.File);
        }

        // Reads the logs in the background; unchanged files come from the cache.
        void LoadLogs(bool force)
        {
            if (logLoading || (!force && (DateTime.Now - logLoadedAt).TotalSeconds < 10)) return;
            logLoading = true;
            if (perkNamesFor != S.InstallDir) { perkNames = Characters.LoadNames(S.InstallDir); perkNamesFor = S.InstallDir; }
            var settings = S;
            var names = perkNames;
            Task.Run(() => logData.Load(settings, names)).ContinueWith(t => Ui(() =>
            {
                logLoading = false;
                logLoadedAt = DateTime.Now;
                if (t.Exception != null || settings != S) return;
                logAll = t.Result;
                // player list for the filter: everyone who appears as the actor
                string cur = cbLogPlayer.Text;
                var players = logAll.Select(x => x.Player).Where(n => n != "" && !n.StartsWith("(")).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToArray();
                cbLogPlayer.BeginUpdate();
                cbLogPlayer.Items.Clear();
                cbLogPlayer.Items.AddRange(players);
                cbLogPlayer.EndUpdate();
                cbLogPlayer.Text = cur;
                FilterLogs();
            }));
        }

        void FilterLogs()
        {
            if (lvLogs == null) return;
            string q = tbLogSearch.Text.Trim();
            string who = cbLogPlayer.Text.Trim().ToLowerInvariant();
            var kinds = new HashSet<string>(logKindChecks.Where(kv => kv.Value.Checked).Select(kv => kv.Key));
            DateTime since = DateTime.MinValue;
            switch (cbLogRange.SelectedIndex)
            {
                case 0: since = DateTime.Now.AddHours(-1); break;
                case 1: since = DateTime.Today; break;
                case 2: since = DateTime.Today.AddDays(-2); break;
                case 3: since = DateTime.Today.AddDays(-6); break;
            }
            logShown = logAll.Where(x => x.Time >= since && kinds.Contains(x.Kind)
                    && (who == "" || x.Player.ToLowerInvariant() == who || x.Mentions.Contains(who))
                    && (q == "" || x.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || x.Player.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                        || x.Where.Contains(q))).ToList();
            lvLogs.VirtualListSize = logShown.Count;
            lvLogs.Invalidate();
            lblLogCount.Text = logShown.Count + " of " + logAll.Count + " lines";
            ShowLogDetail();
        }

        // From the Players page: jump here filtered to one player.
        void ShowLogsFor(string player)
        {
            ShowPage(PLogs);
            tbLogSearch.Text = "";
            cbLogRange.SelectedIndex = 4;
            cbLogPlayer.Text = player;
        }
    }
}
