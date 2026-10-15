using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // Character sheet under the Players list: skills, days survived, deaths, last position and level-ups from
    // PZ's PerkLog, plus this session's network traffic per player from the metrics server when it's on.
    public partial class MainForm
    {
        Panel sheetPanel;
        Label lblSheetHead, lblSheetUps, lblSheetNet;
        LinkLabel lblSheetSub;   // the position in it links to pzmap.org
        Dictionary<string, ServerDb.Character> dbChars = new Dictionary<string, ServerDb.Character>(StringComparer.OrdinalIgnoreCase);
        SkillsView skillsView;
        Panel skillsScroll;
        CheckBox chkAllSkills;
        Dictionary<string, CharacterSheet> chars = new Dictionary<string, CharacterSheet>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> perkNames;
        string perkNamesFor;
        Dictionary<string, Metrics.Traffic> traffic;
        DateTime trafficAt = DateTime.MinValue;
        bool trafficBusy;

        Control BuildSheet()
        {
            sheetPanel = new Panel { Dock = DockStyle.Bottom, Height = 330, BackColor = Theme.Side, Padding = new Padding(14, 10, 14, 8) };
            var head = new Panel { Dock = DockStyle.Top, Height = 112, BackColor = Theme.Side };
            // name row: the name on the left, the "untrained" toggle on the right
            var nameRow = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Theme.Side };
            chkAllSkills = new CheckBox { Text = "Show untrained skills", Dock = DockStyle.Right, Width = 170, ForeColor = Theme.Muted, CheckAlign = ContentAlignment.MiddleLeft };
            chkAllSkills.CheckedChanged += (s, e) => UpdateSheet();
            lblSheetHead = new Label { Dock = DockStyle.Fill, Font = Theme.H2, ForeColor = Theme.Text, UseMnemonic = false };
            nameRow.Controls.Add(lblSheetHead);
            nameRow.Controls.Add(chkAllSkills);
            lblSheetSub = new LinkLabel
            {
                Dock = DockStyle.Top, Height = 24, ForeColor = Theme.Muted, UseMnemonic = false, LinkColor = Theme.Info,
                ActiveLinkColor = Theme.Accent, VisitedLinkColor = Theme.Info, LinkBehavior = LinkBehavior.HoverUnderline
            };
            lblSheetSub.LinkClicked += (s, e) => Util.OpenUrl((string)e.Link.LinkData);
            lblSheetLive = new Label { Dock = DockStyle.Top, Height = 58, ForeColor = Theme.Accent, UseMnemonic = false, AutoEllipsis = true };   // three short lines
            head.Controls.Add(lblSheetLive);   // live condition from EZPZ Server Tools (empty without it)
            head.Controls.Add(lblSheetSub);
            head.Controls.Add(nameRow);
            lblSheetNet = new Label { Dock = DockStyle.Bottom, Height = 36, ForeColor = Theme.Muted, UseMnemonic = false };
            lblSheetUps = new Label { Dock = DockStyle.Bottom, Height = 36, ForeColor = Theme.Text, UseMnemonic = false };
            skillsScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Side };
            skillsView = new SkillsView { Location = new Point(0, 0) };
            skillsView.HeightChanged += SizeSkills;
            skillsScroll.Controls.Add(skillsView);
            skillsScroll.Resize += (s, e) => SizeSkills();
            sheetPanel.Controls.Add(skillsScroll);
            sheetPanel.Controls.Add(lblSheetUps);
            sheetPanel.Controls.Add(lblSheetNet);
            sheetPanel.Controls.Add(head);
            UpdateSheet();
            return sheetPanel;
        }

        void SizeSkills()
        {
            if (skillsScroll == null) return;
            int w = skillsScroll.ClientSize.Width - (skillsScroll.VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth);
            skillsView.Size = new Size(Math.Max(200, w), Math.Max(skillsView.NeededHeight, 20));
            skillsView.Invalidate();
        }

        void LoadCharacters()
        {
            if (perkNamesFor != S.InstallDir) { perkNames = Characters.LoadNames(S.InstallDir); perkNamesFor = S.InstallDir; }
            var settings = S;
            Task.Run(() => Characters.Read(settings)).ContinueWith(t =>
            {
                if (t.Exception == null) Ui(() => { if (settings == S) { chars = t.Result; UpdateSheet(); } });
            });
            // character name, alive or dead, and last saved position from the world save
            Task.Run(() => ServerDb.ReadCharacters(settings)).ContinueWith(t =>
            {
                if (t.Exception == null) Ui(() => { if (settings == S) { dbChars = t.Result; UpdateSheet(); if (currentPage == PMap) RefreshMapMarkers(); } });
            });
        }

        // Called every second from PlayersTick while the Players page is open.
        void SheetTick()
        {
            if (Sel == null || server.State != ServerState.Running || server.MetricsPort <= 0) return;
            if (trafficBusy || (DateTime.Now - trafficAt).TotalSeconds < 10) return;
            trafficBusy = true;
            int port = server.MetricsPort;
            Task.Run(() => Metrics.PlayerTraffic(Metrics.Fetch(port))).ContinueWith(t => Ui(() =>
            {
                trafficBusy = false;
                trafficAt = DateTime.Now;
                if (t.Exception == null) { traffic = t.Result; UpdateSheet(); }
            }));
        }

        // The sheet's summary line, with one part (the position) as a link.
        void SetSheetSub(string text, string linkPart, string url)
        {
            lblSheetSub.Text = text;
            lblSheetSub.Links.Clear();
            int at = linkPart != null && url != null ? text.IndexOf(linkPart, StringComparison.Ordinal) : -1;
            if (at >= 0) lblSheetSub.Links.Add(at, linkPart.Length, url);
        }

        void UpdateSheet()
        {
            if (sheetPanel == null) return;
            string n = Sel;
            if (n == null)
            {
                lblSheetHead.Text = "Character sheet";
                SetSheetSub("Select a player in the list to see their character.", null, null);
                skillsView.SetData(null, perkNames);
                SizeSkills();
                lblSheetUps.Text = ""; lblSheetNet.Text = ""; lblSheetLive.Text = "";
                return;
            }
            CharacterSheet c;
            chars.TryGetValue(n, out c);
            bool on = onlineNow.Contains(n);
            ServerDb.Character dbc;
            dbChars.TryGetValue(n, out dbc);
            lblSheetHead.Text = n + (dbc != null && dbc.Name != "" ? "   ·   " + dbc.Name : "") + (dbc != null && dbc.Dead ? "   ✝ dead" : "") + (on ? "   ● online" : "");
            lblSheetHead.ForeColor = dbc != null && dbc.Dead ? Theme.Danger : on ? Theme.Accent : Theme.Text;
            string savedAt = dbc != null && (dbc.X != 0 || dbc.Y != 0) ? (int)dbc.X + ", " + (int)dbc.Y : null;
            if (c == null)
            {
                if (savedAt != null) SetSheetSub("No skill data yet   ·   last saved at " + savedAt + (dbc.Z >= 1 ? " (floor " + (int)dbc.Z + ")" : ""), savedAt, Util.MapUrl(savedAt));
                else SetSheetSub("No character data yet. The server records skills when a player logs in or levels up.", null, null);
                skillsView.SetData(null, perkNames);
                lblSheetUps.Text = "";
            }
            else
            {
                int days = c.HoursSurvived / 24;
                var parts = new List<string>();
                parts.Add("Survived " + (days > 0 ? days + " day" + (days == 1 ? "" : "s") + ", " : "") + (c.HoursSurvived % 24) + " h (in-game)");
                parts.Add(c.Characters == 1 ? "first character" : "character #" + c.Characters + " (" + (c.Characters - 1) + " earlier one" + (c.Characters == 2 ? "" : "s") + " died)");
                string spot = null;
                if (savedAt != null)
                {
                    // the world save is newer than the log's login/level-up snapshots
                    spot = savedAt;
                    parts.Add((on ? "around " : "last saved at ") + spot + (dbc.Z >= 1 ? " (floor " + (int)dbc.Z + ")" : ""));
                }
                else if (c.Position != "")
                {
                    var xyz = c.Position.Split(',');
                    spot = xyz[0] + ", " + (xyz.Length > 1 ? xyz[1] : "");
                    parts.Add((on ? "last logged at " : "last seen at ") + spot + (xyz.Length > 2 && xyz[2] != "0" ? " (floor " + xyz[2] + ")" : "") + ", " + FriendlyTime(c.LastSeen));
                }
                SetSheetSub(string.Join("   ·   ", parts), spot, spot != null ? Util.MapUrl(spot) : null);
                skillsView.ShowAll = chkAllSkills.Checked;
                skillsView.SetData(c.Skills, perkNames);
                var ups = c.LevelUps.AsEnumerable().Reverse().Take(6).ToList();
                lblSheetUps.Text = ups.Count == 0 ? "No level-ups yet for this character."
                    : "Recent level-ups:  " + string.Join("   ·   ", ups.Select(u => Characters.Name(perkNames, u.Perk) + " " + u.Level + " (" + FriendlyTime(u.When) + ")"));
            }

            Metrics.Traffic tr;
            if (on && traffic != null && traffic.TryGetValue(n, out tr))
            {
                var busiest = tr.SentByType.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key + " " + Util.Size((long)kv.Value));
                lblSheetNet.Text = "Network this session: the server sent them " + Util.Size((long)tr.SentBytes) + " and received " + Util.Size((long)tr.ReceivedBytes)
                    + ".\nBusiest: " + string.Join(", ", busiest) + ".";
            }
            else if (on && server.MetricsPort <= 0)
                lblSheetNet.Text = "Per-player network figures appear once the server has been started by this version of ezpz.";
            else lblSheetNet.Text = "";
            UpdateSheetLive();
            lblSheetNet.Visible = lblSheetNet.Text != "";
            lblSheetUps.Visible = lblSheetUps.Text != "";
            SizeSkills();
        }
    }

    // Skills in the game's groups, each with 10 level pips. By default only trained skills (level 1+).
    class SkillsView : Control
    {
        public bool ShowAll;
        Dictionary<string, int> skills;
        Dictionary<string, string> names;
        static readonly Color Pip = Theme.Accent, PipOff = Color.FromArgb(62, 64, 70);

        public SkillsView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Theme.Side;
            Font = new Font("Segoe UI", 8.5f);
        }

        public int NeededHeight = 40;
        public event Action HeightChanged;

        public void SetData(Dictionary<string, int> s, Dictionary<string, string> n) { skills = s; names = n ?? new Dictionary<string, string>(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (skills == null) return;
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int cols = Width >= 640 ? 3 : 2;
            float colW = Width / (float)cols, rowH = Font.Height + 3, pip = 7, gap = 2, pipsW = 10 * (pip + gap);
            // groups in the game's order; each goes into whichever column is shortest so far
            var known = new HashSet<string>(Characters.Groups.SelectMany(x => x.Value), StringComparer.OrdinalIgnoreCase);
            Func<string, bool> visible = k => { int lv; return ShowAll || (skills.TryGetValue(k, out lv) && lv > 0); };
            var groups = Characters.Groups.Select(x => new KeyValuePair<string, string[]>(Characters.Name(names, x.Key), x.Value.Where(visible).ToArray())).ToList();
            var other = skills.Keys.Where(k => !known.Contains(k) && visible(k)).ToArray();
            if (other.Length > 0) groups.Add(new KeyValuePair<string, string[]>("Other", other));
            groups = groups.Where(x => x.Value.Length > 0).ToList();
            if (groups.Count == 0)
            {
                using (var dimB = new SolidBrush(Theme.Muted)) g.DrawString("No skills trained yet.", Font, dimB, 0, 2);
                NeededHeight = (int)rowH + 4;
                return;
            }
            var heights = new float[cols];
            using (var head = new SolidBrush(Theme.Accent))
            using (var text = new SolidBrush(Theme.Text))
            using (var dim = new SolidBrush(Theme.Muted))
            using (var on = new SolidBrush(Pip))
            using (var off = new SolidBrush(PipOff))
            using (var bold = new Font(Font, FontStyle.Bold))
            {
                foreach (var grp in groups)
                {
                    int c = Array.IndexOf(heights, heights.Min());
                    float x = c * colW, y = heights[c];
                    g.DrawString(grp.Key, bold, head, x, y);
                    y += rowH + 1;
                    foreach (var k in grp.Value)
                    {
                        int lv;
                        skills.TryGetValue(k, out lv);
                        g.DrawString(Characters.Name(names, k), Font, lv > 0 ? text : dim, new RectangleF(x, y, colW - pipsW - 30, rowH));
                        float px = x + colW - pipsW - 26, py = y + (rowH - pip) / 2;
                        for (int i = 0; i < 10; i++) g.FillRectangle(i < lv ? on : off, px + i * (pip + gap), py, pip, pip);
                        g.DrawString(lv.ToString(), Font, lv > 0 ? text : dim, px + pipsW + 2, y);
                        y += rowH;
                    }
                    heights[c] = y + rowH * 0.6f;
                }
            }
            int need = (int)Math.Ceiling(heights.Max());
            if (need != NeededHeight) { NeededHeight = need; var h = HeightChanged; if (h != null) BeginInvoke(h); }
        }
    }
}
