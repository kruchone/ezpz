using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ezpz
{
    // Live player condition from the EZPZ Server Tools mod: a "Players right now" list on Home and a "Right now"
    // line on the character sheet.
    public partial class MainForm
    {
        LiveSnapshot live;
        bool liveReading;
        ListView lvLive;
        Label lblLiveNote, lblSheetLive;

        // Home's "Players right now": each online player's condition, from EZPZ Server Tools.
        Control BuildLiveSection()
        {
            var box = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(0, 0, 16, 0) };
            lvLive = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
                BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9.75f),
                ShowItemToolTips = true,   // hovering a row shows everything, since long lists get cut off in the columns
            };
            lvLive.Columns.Add("Player", 105);
            lvLive.Columns.Add("Health", 55);
            lvLive.Columns.Add("Injuries", 150);
            lvLive.Columns.Add("Moodles", 170);
            lvLive.Columns.Add("Where", 100);
            lvLive.DoubleClick += (s, e) => { var u = LiveSelectedMapUrl(); if (u != null) Util.OpenUrl(u); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open on the Players page", null, (s, e) => { if (lvLive.SelectedItems.Count == 1) ShowPlayer((string)lvLive.SelectedItems[0].Tag); });
            menu.Items.Add("Show on the Map page", null, (s, e) => { if (lvLive.SelectedItems.Count == 1) { var n = (string)lvLive.SelectedItems[0].Tag; ShowPage(PMap); MapFind(n); } });
            menu.Items.Add("View this spot on pzmap.org", null, (s, e) => { var u = LiveSelectedMapUrl(); if (u != null) Util.OpenUrl(u); });
            lvLive.ContextMenuStrip = menu;
            box.Controls.Add(lvLive);
            lblLiveNote = new Label { Dock = DockStyle.Bottom, Height = 40, ForeColor = Theme.Muted, Padding = new Padding(0, 6, 0, 0) };
            box.Controls.Add(lblLiveNote);
            box.Controls.Add(new Label { Text = "Players right now", Dock = DockStyle.Top, Height = 30, Font = Theme.H2, ForeColor = Theme.Accent });
            return box;
        }

        string LiveSelectedMapUrl()
        {
            if (lvLive.SelectedItems.Count != 1 || live == null) return null;
            var lp = live.Find((string)lvLive.SelectedItems[0].Tag);
            return lp == null ? null : Util.MapUrl((int)lp.X + "," + (int)lp.Y);
        }

        void ShowPlayer(string name)
        {
            ShowPage(PPlayers);
            var item = lvPlayers.Items.Cast<ListViewItem>().FirstOrDefault(i => string.Equals((string)i.Tag, name, StringComparison.OrdinalIgnoreCase));
            if (item != null) { item.Selected = true; item.EnsureVisible(); }
        }

        // Every 5 seconds while the server runs.
        void LiveTick()
        {
            if (liveReading) return;
            if (server.State != ServerState.Running) { if (live != null) { live = null; RefreshLive(); } return; }
            liveReading = true;
            var settings = S;
            Task.Run(() => LiveFeed.Read(settings)).ContinueWith(t => Ui(() =>
            {
                liveReading = false;
                if (settings != S) return;
                live = t.Exception == null ? t.Result : null;
                RefreshLive();
            }));
        }

        void RefreshLive()
        {
            if (currentPage == PPlayers) UpdateSheetLive();
            if (currentPage == PMap) RefreshMapMarkers();
            if (lvLive == null) return;
            lvLive.BeginUpdate();
            lvLive.Items.Clear();
            bool fresh = live != null && live.Fresh && server.State == ServerState.Running;
            if (fresh)
            {
                foreach (var lp in live.Players.OrderBy(x => x.Username, StringComparer.OrdinalIgnoreCase))
                {
                    var it = new ListViewItem(new[]
                    {
                        lp.Username, LiveFeed.HealthText(lp), LiveFeed.InjuryText(lp), LiveFeed.MoodleText(lp),
                        lp.Vehicle != "" ? VehicleName(lp.Vehicle) : (int)lp.X + ", " + (int)lp.Y,
                    }) { Tag = lp.Username, UseItemStyleForSubItems = false };
                    it.ToolTipText = lp.Username + ": " + LiveFeed.HealthText(lp) + " health\n" + LiveFeed.InjuryText(lp) + "\n" + LiveFeed.MoodleText(lp) + "\n" + LiveFeed.NeedsText(lp);
                    it.SubItems[1].ForeColor = lp.Health < 50 ? Theme.Danger : lp.Health < 80 ? Theme.Warn : Theme.Accent;
                    if (lp.Infected || lp.Bitten > 0 || lp.Bleeding > 0) it.SubItems[2].ForeColor = Theme.Danger;
                    else if (lp.Deep + lp.Scratched + lp.Fractures + lp.Burns > 0) it.SubItems[2].ForeColor = Theme.Warn;
                    else it.SubItems[2].ForeColor = Theme.Muted;
                    if (lp.Moodles.Count == 0) it.SubItems[3].ForeColor = Theme.Muted;
                    else if (lp.Moodles.Values.Max() >= 3) it.SubItems[3].ForeColor = Theme.Warn;
                    lvLive.Items.Add(it);
                }
            }
            lvLive.EndUpdate();

            if (server.State != ServerState.Running) lblLiveNote.Text = "Shown while the server is running.";
            else if (live == null) lblLiveNote.Text = "Add the EZPZ Server Tools mod (Mods page) to see each player's health, injuries and moodles here.";
            else if (!live.Fresh) lblLiveNote.Text = "No report from EZPZ Server Tools since " + live.Written.ToString("HH:mm:ss") + ". Is it still in the server's mod list?";
            else lblLiveNote.Text = live.Players.Count == 0 ? "Nobody online." : "Hover a player for everything; double-click to see where they are; right-click for more.";
        }

        string VehicleName(string script)
        {
            var c = catalog.FindVehicle(script);
            return c != null ? c.Name : script;
        }

        // The character sheet's "Right now" line for the selected player.
        void UpdateSheetLive()
        {
            if (lblSheetLive == null) return;
            var lp = Sel != null && live != null && live.Fresh && server.State == ServerState.Running ? live.Find(Sel) : null;
            if (lp == null) { lblSheetLive.Text = ""; return; }
            string where = lp.Vehicle != "" ? (lp.Driving ? "driving a " : "in a ") + VehicleName(lp.Vehicle) : lp.Asleep ? "asleep" : "on foot";
            double temp;
            string t = lp.Stats.TryGetValue("TEMPERATURE", out temp) ? "  ·  " + temp.ToString("0.0") + " °C" : "";
            lblSheetLive.Text = "Right now:  ❤ " + LiveFeed.HealthText(lp) + "  ·  " + where + t
                + "\n" + LiveFeed.InjuryText(lp)
                + "\n" + LiveFeed.MoodleText(lp) + "  ·  " + LiveFeed.NeedsText(lp);
            lblSheetLive.ForeColor = lp.Infected || lp.Health < 50 ? Theme.Danger : lp.Health < 80 || lp.Bleeding > 0 ? Theme.Warn : Theme.Accent;
        }
    }
}
